using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.DISCOVER &lt;service&gt; → array of <c>[nodeId, secondsSinceLastSeen]</c> pairs.
///
/// <para>Returns the live nodes hosting a service. Empty array for an unknown
/// service or one whose hosts have all gone stale — never an error.</para>
///
/// <para><b>Read-only by contract.</b> Stale nodes are filtered out of the
/// results but not pruned here: pruning must requeue the node's unacknowledged
/// RPC work, and that belongs in the commands that already lock those keys
/// (<c>HW.DEQUEUE</c>, and <c>HW.HEARTBEAT ... BYE</c>). Filtering without
/// pruning is why a stale node disappears from discovery immediately, well
/// before its state is reclaimed.</para>
///
/// <para>Discovery is a lookup, not a scan: the registration form of
/// <c>HW.HEARTBEAT</c> maintains <c>hw:reg:svc:{service}</c> at write time, so
/// no node's catalog is deserialized here.</para>
/// </summary>
internal sealed class HwDiscoverCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;

    private string _service = null!;
    private string[] _candidates = [];

    public HwDiscoverCommand(HighwayServerOptions opts) => _opts = opts;

    protected override void ResetState() => _candidates = [];

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out _service))
            return true;

        api.GET(CreateArgSlice(HighwayKeys.RegistrationService(_service)), out PinnedSpanByte index);
        _candidates = SplitList(index);

        // Shared locks only: this command never mutates.
        foreach (var node in _candidates)
            AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(node)), LockType.Shared, StoreType.Main);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var now = DateTime.UtcNow.Ticks;
            var live = new List<(string NodeId, long AgeSeconds)>(_candidates.Length);

            foreach (var node in _candidates)
            {
                api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(node)), out PinnedSpanByte record);
                if (record.Length < NodeRegistration.HeaderSize)
                    continue; // pruned already, or index entry outlived its record

                NodeRegistration.Decode(record.ReadOnlySpan, out var seen, out _);
                if (NodeRegistration.IsStale(seen, now, _opts.NodeExpiry))
                    continue;

                live.Add((node, (long)NodeRegistration.Age(seen, now).TotalSeconds));
            }

            WritePairArray(ref output, live);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>Writes an array of 2-element <c>[nodeId, ageSeconds]</c> bulk-string arrays.</summary>
    private static unsafe void WritePairArray(
        ref MemoryResult<byte> output, List<(string NodeId, long AgeSeconds)> entries)
    {
        var encoded = new List<(byte[] Node, byte[] Age)>(entries.Count);
        foreach (var (nodeId, age) in entries)
            encoded.Add((Encoding.UTF8.GetBytes(nodeId), Encoding.UTF8.GetBytes(age.ToString())));

        var totalLen = ArrayHeaderLen(encoded.Count);
        foreach (var (node, age) in encoded)
        {
            totalLen += ArrayHeaderLen(2);
            totalLen += BulkStringLen(node.Length);
            totalLen += BulkStringLen(age.Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(encoded.Count, ref curr, end);
            foreach (var (node, age) in encoded)
            {
                RespWriteUtils.TryWriteArrayLength(2, ref curr, end);
                RespWriteUtils.TryWriteBulkString(node, ref curr, end);
                RespWriteUtils.TryWriteBulkString(age, ref curr, end);
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
