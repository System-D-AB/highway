using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.RECEIVE &lt;channel&gt; &lt;group&gt; [COUNT n] → [[messageId, payload], ...]
///
/// Pops up to COUNT messages from the group queue and moves each to the
/// processing list with a receive timestamp. Performs a lazy lease sweep first.
/// Returns an empty array when nothing is available.
/// </summary>
internal sealed class HwReceiveCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;

    private string _channel = null!;
    private string _group = null!;
    private int _count;

    public HwReceiveCommand(HighwayServerOptions opts)
    {
        _opts = opts;
    }

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "channel", _opts.MaxIdentifierBytes, out _channel))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "group", _opts.MaxIdentifierBytes, out _group))
            return true;

        _count = _opts.ReceiveDefaultCount;

        // Optional COUNT argument — may be "COUNT n" or just "n"
        var arg3 = GetNextArg(ref procInput, ref idx);
        if (arg3.Length > 0)
        {
            var span3 = arg3.ReadOnlySpan;
            if (IsCountKeyword(span3))
            {
                // "COUNT" keyword — next arg is the value
                var countValueArg = GetNextArg(ref procInput, ref idx);
                if (!TryReadCountValue(countValueArg.ReadOnlySpan))
                    return true;
            }
            else if (!TryReadCountValue(span3))
            {
                return true;
            }
        }

        if (_count > _opts.ReceiveMaxCount)
        {
            Fail(HighwayErrors.InvalidCount, $"COUNT {_count} exceeds maximum {_opts.ReceiveMaxCount}");
            return true;
        }

        AddKey(CreateArgSlice(HighwayKeys.GroupQueue(_channel, _group)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    /// <summary>
    /// Parses the COUNT value, capturing a distinct <c>HW_INVALID_COUNT</c>
    /// detail for every failure class: missing, negative, non-numeric,
    /// overflow, and zero. (The above-max check runs in Prepare after parsing.)
    /// Overflow-safe: accumulates in <see cref="long"/> and rejects before wrap.
    /// </summary>
    private bool TryReadCountValue(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return Fail(HighwayErrors.InvalidCount, "COUNT value is missing");

        if (span[0] == (byte)'-')
            return Fail(HighwayErrors.InvalidCount, "COUNT must not be negative");

        long value = 0;
        foreach (var b in span)
        {
            if (b < '0' || b > '9')
                return Fail(HighwayErrors.InvalidCount,
                    $"COUNT value '{Encoding.UTF8.GetString(span)}' is not numeric");

            // Reject before the multiplication/addition can wrap
            if (value > (long.MaxValue - (b - '0')) / 10)
                return Fail(HighwayErrors.InvalidCount, "COUNT overflows a 32-bit integer");

            value = value * 10 + (b - '0');
        }

        if (value == 0)
            return Fail(HighwayErrors.InvalidCount, "COUNT must be at least 1");

        if (value > int.MaxValue)
            return Fail(HighwayErrors.InvalidCount, "COUNT overflows a 32-bit integer");

        _count = (int)value;
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var groupQueueKey = CreateArgSlice(HighwayKeys.GroupQueue(_channel, _group));
            var groupProcKey  = CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group));

            // Lazy lease sweep: expired processing entries → re-queue at HEAD
            if (_opts.Lease > TimeSpan.Zero)
            {
                var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;
                var sweepStatus = api.ListLeftPop(groupProcKey, int.MaxValue, out var procEntries);
                if (sweepStatus == GarnetStatus.OK && procEntries is { Length: > 0 })
                {
                    var expired = new List<byte[]>();
                    var keep    = new List<byte[]>();

                    foreach (var entry in procEntries)
                    {
                        var span = entry.ReadOnlySpan;
                        if (span.Length < 16) continue;
                        Envelope.DecodeGroupProcessingEntry(span, out var receiveTicks, out _, out _);
                        if (receiveTicks < leaseExpiry)
                            expired.Add(span.ToArray());
                        else
                            keep.Add(span.ToArray());
                    }

                    // Restore non-expired to proc list
                    foreach (var e in keep)
                        api.ListRightPush(groupProcKey, CreateArgSlice(e), out _);

                    // Re-queue expired at HEAD (reversed to preserve order)
                    for (var i = expired.Count - 1; i >= 0; i--)
                    {
                        Envelope.DecodeGroupProcessingEntry(expired[i], out _, out var msgId, out var msgPayload);
                        var channelEntry = Envelope.EncodeChannelEntry(msgId, msgPayload);
                        api.ListLeftPush(groupQueueKey, CreateArgSlice(channelEntry), out _);
                    }
                }
            }

            // Pop up to _count messages from the group queue
            var results = new List<PinnedSpanByte[]>();

            for (var i = 0; i < _count; i++)
            {
                var popStatus = api.ListLeftPop(groupQueueKey, out var popped);
                if (popStatus != GarnetStatus.OK || popped.Length == 0) break;

                Envelope.DecodeChannelEntry(popped.ReadOnlySpan, out var messageId, out var msgPayload);

                // Push to proc list with receive timestamp
                var procEntry = Envelope.EncodeGroupProcessingEntry(DateTime.UtcNow.Ticks, messageId, msgPayload);
                api.ListRightPush(groupProcKey, CreateArgSlice(procEntry), out _);

                var msgIdSlice = CreateArgSlice(Encoding.UTF8.GetBytes(messageId.ToString()));
                var paySlice   = CreateArgSlice(msgPayload);
                results.Add([msgIdSlice, paySlice]);
            }

            WriteMessageArray(ref output, results);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>Writes a RESP array of 2-element bulk-string sub-arrays.</summary>
    private static unsafe void WriteMessageArray(ref MemoryResult<byte> output, List<PinnedSpanByte[]> pairs)
    {
        var totalLen = ArrayHeaderLen(pairs.Count);
        foreach (var pair in pairs)
        {
            totalLen += ArrayHeaderLen(2);
            totalLen += BulkStringLen(pair[0].Length);
            totalLen += BulkStringLen(pair[1].Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end  = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(pairs.Count, ref curr, end);
            foreach (var pair in pairs)
            {
                RespWriteUtils.TryWriteArrayLength(2, ref curr, end);
                RespWriteUtils.TryWriteBulkString(pair[0].Span, ref curr, end);
                RespWriteUtils.TryWriteBulkString(pair[1].Span, ref curr, end);
            }
        }
    }

    private static int ArrayHeaderLen(int count) => 1 + CountDigits(count) + 2;
    private static int BulkStringLen(int len)    => 1 + CountDigits(len) + 2 + len + 2;

    private static int CountDigits(int value)
    {
        if (value < 0) value = -value;
        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1_000) return 3;
        if (value < 10_000) return 4;
        if (value < 100_000) return 5;
        if (value < 1_000_000) return 6;
        if (value < 10_000_000) return 7;
        if (value < 100_000_000) return 8;
        if (value < 1_000_000_000) return 9;
        return 10;
    }

    private static bool IsCountKeyword(ReadOnlySpan<byte> span)
        => span.Length == 5
           && (span[0] == 'C' || span[0] == 'c')
           && (span[1] == 'O' || span[1] == 'o')
           && (span[2] == 'U' || span[2] == 'u')
           && (span[3] == 'N' || span[3] == 'n')
           && (span[4] == 'T' || span[4] == 't');
}
