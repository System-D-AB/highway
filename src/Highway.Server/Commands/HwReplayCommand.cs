using System.Buffers;
using System.Globalization;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Abstractions.Observability;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.REPLAY &lt;name&gt; [FROM ts] [TO ts] [LIMIT n] [NODE nodeId] → array of events (feature 002).
///
/// <para>Returns one name's recent operations in chronological order. An unknown
/// name, a disabled name, or an empty range returns an <b>empty array</b> — never
/// an error, matching <c>HW.DISCOVER</c>.</para>
///
/// <para><b>Locks nothing.</b> The flight recorder lives in process memory, not
/// in the Garnet keyspace, so this command is genuinely read-only with respect to
/// the store and cannot contend with traffic.</para>
///
/// <para>Each event is a flat field/value array — the same self-describing shape
/// <c>HW.STATS</c> uses — so fields can be appended later without breaking
/// readers. <c>payload</c> is null unless the name is captured at
/// <c>Full</c>; <c>payloadSize</c> is present either way.</para>
/// </summary>
internal sealed class HwReplayCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _name = null!;
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private int _limit;
    private string? _nodeFilter;
    private bool _disabled;

    public HwReplayCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _disabled = false;
        _nodeFilter = null;
        _limit = 0;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "name", _opts.MaxIdentifierBytes, out _name))
            return true;

        var obs = _opts.Observability;

        if (!obs.ReplayEnabled)
        {
            _disabled = true;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        _from = now - obs.ReplayDefaultWindow;
        _to = now;
        _limit = obs.ReplayDefaultLimit;

        // Optional keyword arguments, in any order.
        while (true)
        {
            var keyword = GetNextArg(ref procInput, ref idx);
            if (keyword.Length == 0) break;

            var word = Encoding.ASCII.GetString(keyword.ReadOnlySpan).ToUpperInvariant();
            var value = GetNextArg(ref procInput, ref idx);

            if (value.Length == 0)
            {
                Fail(HighwayErrors.InvalidArg, $"{word} requires a value");
                return true;
            }

            var raw = Encoding.UTF8.GetString(value.ReadOnlySpan);

            switch (word)
            {
                case "FROM":
                    if (!TryParseTimestamp(raw, now, out _from))
                    {
                        Fail(HighwayErrors.InvalidArg,
                            $"FROM '{raw}' is neither an ISO-8601 timestamp nor a relative offset such as -5min");
                        return true;
                    }
                    break;

                case "TO":
                    if (!TryParseTimestamp(raw, now, out _to))
                    {
                        Fail(HighwayErrors.InvalidArg,
                            $"TO '{raw}' is neither an ISO-8601 timestamp nor a relative offset such as -5min");
                        return true;
                    }
                    break;

                case "LIMIT":
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                        || limit < 1)
                    {
                        Fail(HighwayErrors.InvalidCount, $"LIMIT '{raw}' must be a positive integer");
                        return true;
                    }
                    if (limit > obs.ReplayMaxLimit)
                    {
                        Fail(HighwayErrors.InvalidCount,
                            $"LIMIT {limit} exceeds maximum {obs.ReplayMaxLimit}");
                        return true;
                    }
                    _limit = limit;
                    break;

                case "NODE":
                    if (!Identifier.IsValid(value.ReadOnlySpan, _opts.MaxIdentifierBytes))
                    {
                        Fail(HighwayErrors.InvalidArg, "NODE is blank or contains a control character");
                        return true;
                    }
                    _nodeFilter = raw;
                    break;

                default:
                    Fail(HighwayErrors.InvalidArg,
                        $"unknown argument '{word}'; expected FROM, TO, LIMIT or NODE");
                    return true;
            }
        }

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            if (_disabled)
            {
                WriteError(ref output, HighwayErrors.Format(
                    HighwayErrors.InvalidArg,
                    "HW.REPLAY is disabled on this server (Observability.ReplayEnabled = false)"));
                return;
            }

            var events = _recorder.Read(_name, _from, _to, _nodeFilter, _limit);
            WriteEvents(ref output, events);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>
    /// Parses an absolute ISO-8601 timestamp or a relative offset such as
    /// <c>-5min</c>, <c>-1h</c>, <c>-30s</c>. Relative offsets are what an
    /// operator actually types during an incident.
    /// </summary>
    internal static bool TryParseTimestamp(string raw, DateTimeOffset now, out DateTimeOffset result)
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

        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out result);
    }

    /// <summary>Writes an array of flat field/value event arrays.</summary>
    private static unsafe void WriteEvents(ref MemoryResult<byte> output, IReadOnlyList<HighwayEvent> events)
    {
        var encoded = new List<List<byte[]>>(events.Count);

        foreach (var evt in events)
        {
            var fields = new List<byte[]>(20);

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

            encoded.Add(fields);
        }

        var totalLen = ArrayHeaderLen(encoded.Count);
        foreach (var fields in encoded)
        {
            totalLen += ArrayHeaderLen(fields.Count);
            foreach (var f in fields) totalLen += BulkStringLen(f.Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(encoded.Count, ref curr, end);
            foreach (var fields in encoded)
            {
                RespWriteUtils.TryWriteArrayLength(fields.Count, ref curr, end);
                foreach (var f in fields)
                    RespWriteUtils.TryWriteBulkString(f, ref curr, end);
            }
        }
    }

    private static int ArrayHeaderLen(int count) => 1 + CountDigits(count) + 2;
    private static int BulkStringLen(int len) => 1 + CountDigits(len) + 2 + len + 2;

    private static int CountDigits(int value)
    {
        if (value < 0) value = -value;
        var digits = 1;
        while (value >= 10) { value /= 10; digits++; }
        return digits;
    }
}
