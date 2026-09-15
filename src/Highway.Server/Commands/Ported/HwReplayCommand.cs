using System.Globalization;
using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.REPLAY &lt;name&gt; [FROM t] [TO t] [LIMIT n] [NODE id] → array of event field arrays
/// (feature 002). Reads the flight recorder — no store, no lock, no batch: it is a pure
/// observability read. Ported unchanged; the recorder is already engine-agnostic.
/// </summary>
internal sealed class HwReplayCommand : HighwayCommand
{
    private string _name = null!;
    private bool _disabled;
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private string? _nodeFilter;
    private int _limit;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "name", ctx.Options.MaxIdentifierBytes, out _name))
            return false;

        var obs = ctx.Options.Observability;
        if (!obs.ReplayEnabled)
        {
            _disabled = true;
            return true;
        }

        // The window default is anchored to the command's single clock read (037 R5.1 —
        // even though this command opens no batch, it uses NowTicks rather than UtcNow).
        var now = new DateTimeOffset(ctx.NowTicks, TimeSpan.Zero);
        _from = now - obs.ReplayDefaultWindow;
        _to = now;
        _limit = obs.ReplayDefaultLimit;

        while (true)
        {
            var keyword = input.Next(ref idx);
            if (keyword.Length == 0) break;

            var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
            var value = input.Next(ref idx);
            if (value.Length == 0)
                return Fail(HighwayErrors.InvalidArg, $"{word} requires a value");

            var raw = Encoding.UTF8.GetString(value);
            switch (word)
            {
                case "FROM":
                    if (!HwReplayTime.TryParseTimestamp(raw, now, out _from))
                        return Fail(HighwayErrors.InvalidArg, $"FROM '{raw}' is neither an ISO-8601 timestamp nor a relative offset such as -5min");
                    break;
                case "TO":
                    if (!HwReplayTime.TryParseTimestamp(raw, now, out _to))
                        return Fail(HighwayErrors.InvalidArg, $"TO '{raw}' is neither an ISO-8601 timestamp nor a relative offset such as -5min");
                    break;
                case "LIMIT":
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit < 1)
                        return Fail(HighwayErrors.InvalidCount, $"LIMIT '{raw}' must be a positive integer");
                    if (limit > obs.ReplayMaxLimit)
                        return Fail(HighwayErrors.InvalidCount, $"LIMIT {limit} exceeds maximum {obs.ReplayMaxLimit}");
                    _limit = limit;
                    break;
                case "NODE":
                    if (!Identifier.IsValid(value, ctx.Options.MaxIdentifierBytes))
                        return Fail(HighwayErrors.InvalidArg, "NODE is blank or contains a control character");
                    _nodeFilter = raw;
                    break;
                default:
                    return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected FROM, TO, LIMIT or NODE");
            }
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (_disabled)
        {
            writer.Error(HighwayErrors.Format(HighwayErrors.InvalidArg,
                "HW.REPLAY is disabled on this server (Observability.ReplayEnabled = false)"));
            return;
        }

        var events = ctx.Recorder.Read(_name, _from, _to, _nodeFilter, _limit);
        var rows = new List<IReadOnlyList<byte[]>>(events.Count);
        foreach (var evt in events)
            rows.Add(EncodeEvent(evt));

        if (rows.Count == 0) writer.EmptyArray();
        else writer.ArrayOfArrays(rows);
    }

    private static IReadOnlyList<byte[]> EncodeEvent(HighwayEvent evt)
    {
        var fields = new List<byte[]>(22);
        void Add(string name, string? value)
        {
            fields.Add(Encoding.UTF8.GetBytes(name));
            fields.Add(value is null ? [] : Encoding.UTF8.GetBytes(value));
        }

        Add("timestamp", evt.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        Add("eventType", evt.EventType.ToString());
        Add("name", evt.Name);
        Add("nodeId", evt.NodeId);
        Add("requestId", evt.RequestId);
        Add("messageId", evt.MessageId?.ToString(CultureInfo.InvariantCulture));
        Add("payloadSize", evt.PayloadSize.ToString(CultureInfo.InvariantCulture));
        Add("errorCode", evt.ErrorCode);
        Add("statusCode", evt.StatusCode?.ToString(CultureInfo.InvariantCulture));
        Add("count", evt.Count?.ToString(CultureInfo.InvariantCulture));
        fields.Add("payload"u8.ToArray());
        fields.Add(evt.Payload ?? []);
        return fields;
    }
}

/// <summary>The timestamp parser shared with the Garnet original (relative offsets + ISO-8601).</summary>
internal static class HwReplayTime
{
    public static bool TryParseTimestamp(string raw, DateTimeOffset now, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (raw[0] == '-')
        {
            var span = raw.AsSpan(1);
            var digits = 0;
            while (digits < span.Length && char.IsAsciiDigit(span[digits])) digits++;
            if (digits == 0) return false;
            if (!long.TryParse(span[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                return false;

            var unit = span[digits..].ToString().ToLowerInvariant();
            var offset = unit switch
            {
                "s" or "sec" or "secs" => TimeSpan.FromSeconds(amount),
                "m" or "min" or "mins" => TimeSpan.FromMinutes(amount),
                "h" or "hr" or "hrs" => TimeSpan.FromHours(amount),
                "d" or "day" or "days" => TimeSpan.FromDays(amount),
                _ => TimeSpan.Zero,
            };
            if (offset == TimeSpan.Zero && amount != 0) return false;
            result = now - offset;
            return true;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out result);
    }
}
