using System.Globalization;

namespace Highway.Abstractions.Scheduling;

/// <summary>
/// A recurring-job schedule expression (feature 028): parsing, validation, and the next
/// occurrence after a given instant. Shared by the client (fail-fast validation at startup)
/// and the server (computing <c>nextFire</c> inside the fire transaction), which is why it
/// lives in the zero-dependency contracts package.
///
/// <para><b>Wire grammar</b> — the string stored in the schedule record and carried by
/// <c>HW.JOB SET</c>:</para>
/// <code>
/// daily:HH:mm          fires every day at HH:mm UTC
/// every:{seconds}      fires every N seconds, anchored to the previous fire
/// cron:{m h dom mon dow}   standard 5-field cron, UTC
/// </code>
///
/// <para><b>UTC only, deliberately</b> (028 OD2). Time-zone and DST semantics are a real
/// feature with real edge cases, registered as deferred rather than half-shipped. Every
/// computation here is in UTC ticks.</para>
/// </summary>
public sealed class JobExpression
{
    private enum Kind { Daily, Every, Cron }

    private readonly Kind _kind;
    private readonly TimeOnly _dailyAt;
    private readonly long _everyTicks;
    private readonly CronField _minute, _hour, _dayOfMonth, _month, _dayOfWeek;

    /// <summary>The canonical wire form this instance was parsed from.</summary>
    public string Text { get; }

    private JobExpression(string text, Kind kind, TimeOnly dailyAt, long everyTicks,
        CronField minute = default, CronField hour = default,
        CronField dom = default, CronField mon = default, CronField dow = default)
    {
        Text = text;
        _kind = kind;
        _dailyAt = dailyAt;
        _everyTicks = everyTicks;
        _minute = minute; _hour = hour; _dayOfMonth = dom; _month = mon; _dayOfWeek = dow;
    }

    /// <summary>The forms named in every rejection message, so the error teaches the grammar.</summary>
    public const string AcceptedForms =
        "\"daily:HH:mm\" (e.g. daily:02:00), \"every:{seconds}\" (e.g. every:900), " +
        "or \"cron:{m h dom mon dow}\" (e.g. \"cron:0 3 * * SUN\"), all UTC";

    public static JobExpression Parse(string text)
        => TryParse(text, out var parsed, out var error)
            ? parsed
            : throw new FormatException($"'{text}' is not a valid job schedule: {error}. Accepted: {AcceptedForms}.");

    public static bool TryParse(string? text, out JobExpression parsed, out string error)
    {
        parsed = null!;
        error = "empty expression";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            error = "expected '{kind}:{value}'";
            return false;
        }

        var kind = text[..colon];
        var value = text[(colon + 1)..];

        switch (kind)
        {
            case "daily":
                if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var at))
                {
                    error = $"'{value}' is not a valid HH:mm time";
                    return false;
                }
                parsed = new JobExpression(text, Kind.Daily, at, 0);
                return true;

            case "every":
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    || seconds < 1)
                {
                    error = $"'{value}' is not a positive whole number of seconds";
                    return false;
                }
                parsed = new JobExpression(text, Kind.Every, default, seconds * TimeSpan.TicksPerSecond);
                return true;

            case "cron":
                var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 5)
                {
                    error = $"cron needs 5 fields (minute hour day-of-month month day-of-week), got {fields.Length}";
                    return false;
                }
                if (!CronField.TryParse(fields[0], 0, 59, null, out var minute, out error)
                    || !CronField.TryParse(fields[1], 0, 23, null, out var hour, out error)
                    || !CronField.TryParse(fields[2], 1, 31, null, out var dom, out error)
                    || !CronField.TryParse(fields[3], 1, 12, MonthNames, out var mon, out error)
                    || !CronField.TryParse(fields[4], 0, 6, DayNames, out var dow, out error))
                    return false;

                parsed = new JobExpression(text, Kind.Cron, default, 0, minute, hour, dom, mon, dow);
                return true;

            default:
                error = $"unknown schedule kind '{kind}'";
                return false;
        }
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="afterUtc"/>.
    ///
    /// <para>Always computed <b>from the given instant</b>, never from a stored previous fire:
    /// that is what implements OD3's catch-up-one — a schedule that was due five times while
    /// the system was down fires once, and the next occurrence is measured from now.</para>
    /// </summary>
    public DateTime NextOccurrence(DateTime afterUtc)
    {
        switch (_kind)
        {
            case Kind.Every:
                return new DateTime(afterUtc.Ticks + _everyTicks, DateTimeKind.Utc);

            case Kind.Daily:
            {
                var candidate = afterUtc.Date + _dailyAt.ToTimeSpan();
                if (candidate <= afterUtc) candidate = candidate.AddDays(1);
                return DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
            }

            default:
            {
                // Cron: walk minute by minute from the next whole minute. Bounded to 5 years —
                // past that the expression matches nothing real (e.g. Feb 30) and we refuse
                // rather than loop forever.
                var t = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day,
                    afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
                var limit = afterUtc.AddYears(5);

                while (t <= limit)
                {
                    if (_month.Matches(t.Month) && _minute.Matches(t.Minute) && _hour.Matches(t.Hour)
                        && MatchesDay(t))
                        return t;

                    // Skip in the largest safe steps.
                    if (!_month.Matches(t.Month))
                        t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                    else if (!MatchesDay(t))
                        t = t.Date.AddDays(1);
                    else if (!_hour.Matches(t.Hour))
                        t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                    else
                        t = t.AddMinutes(1);
                }

                throw new InvalidOperationException(
                    $"Cron expression '{Text}' has no occurrence within 5 years of {afterUtc:O}.");
            }
        }
    }

    /// <summary>
    /// Standard cron day semantics: when BOTH day-of-month and day-of-week are restricted,
    /// either matching fires (union); otherwise the restricted one decides.
    /// </summary>
    private bool MatchesDay(DateTime t)
    {
        var domMatch = _dayOfMonth.Matches(t.Day);
        var dowMatch = _dayOfWeek.Matches((int)t.DayOfWeek);

        if (_dayOfMonth.IsWildcard && _dayOfWeek.IsWildcard) return true;
        if (_dayOfMonth.IsWildcard) return dowMatch;
        if (_dayOfWeek.IsWildcard) return domMatch;
        return domMatch || dowMatch;
    }

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    /// <summary>One cron field as a 64-bit match mask. Supports <c>*</c>, N, N-M, lists, and /step.</summary>
    private readonly struct CronField
    {
        private readonly ulong _mask;
        public bool IsWildcard { get; }

        private CronField(ulong mask, bool wildcard) { _mask = mask; IsWildcard = wildcard; }

        public bool Matches(int value) => (_mask & (1UL << value)) != 0;

        public static bool TryParse(
            string field, int min, int max, string[]? names, out CronField parsed, out string error)
        {
            parsed = default;
            error = "";
            ulong mask = 0;
            var wildcard = false;

            foreach (var part in field.Split(','))
            {
                var body = part;
                var step = 1;

                var slash = part.IndexOf('/');
                if (slash >= 0)
                {
                    body = part[..slash];
                    if (!int.TryParse(part[(slash + 1)..], out step) || step < 1)
                    {
                        error = $"'{part}' has an invalid step";
                        return false;
                    }
                }

                int lo, hi;
                if (body == "*")
                {
                    lo = min; hi = max;
                    if (step == 1 && part == field) wildcard = true;
                }
                else
                {
                    var dash = body.IndexOf('-');
                    var loText = dash >= 0 ? body[..dash] : body;
                    var hiText = dash >= 0 ? body[(dash + 1)..] : loText;

                    if (!TryValue(loText, min, max, names, out lo) || !TryValue(hiText, min, max, names, out hi))
                    {
                        error = $"'{body}' is out of range {min}-{max}";
                        return false;
                    }
                    if (hi < lo)
                    {
                        error = $"'{body}' is an inverted range";
                        return false;
                    }
                }

                for (var v = lo; v <= hi; v += step)
                    mask |= 1UL << v;
            }

            parsed = new CronField(mask, wildcard);
            return true;
        }

        private static bool TryValue(string text, int min, int max, string[]? names, out int value)
        {
            if (names is not null)
            {
                var index = Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    value = min == 1 ? index + 1 : index;   // months are 1-based, days 0-based
                    return true;
                }
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                   && value >= min && value <= max;
        }
    }
}
