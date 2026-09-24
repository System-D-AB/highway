using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.DISCOVER &lt;service&gt; → array of <c>[nodeId, secondsSinceLastSeen]</c> pairs.
///
/// <para>Returns the live nodes hosting a service. Empty array for an unknown service or one
/// whose hosts have all gone stale — never an error.</para>
///
/// <para><b>Read-only by contract.</b> Stale nodes are filtered out of the results but not
/// pruned here: pruning must requeue the node's unacknowledged RPC work, and that belongs in
/// the commands that already lock those keys (<c>HW.DEQUEUE</c>, <c>HW.HEARTBEAT ... BYE</c>).</para>
///
/// <para>Ported from Garnet. The discovery index (<c>reg:svc:{service}</c>) is a plain set now
/// (mirror gone, T3.3) read with <see cref="RegistrySupport.ReadSet"/>; the clock is
/// <see cref="CommandContext.NowTicks"/>. No lock, no batch — a pure read against one snapshot.</para>
/// </summary>
internal sealed class HwDiscoverCommand : HighwayCommand
{
    private string _service = null!;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        return TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service);
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;
        using var snap = store.Snapshot();

        var candidates = RegistrySupport.ReadSet(store, snap, HighwayNames.RegistrationService(_service));

        var rows = new List<IReadOnlyList<byte[]>>(candidates.Length);
        foreach (var node in candidates)
        {
            var record = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(node)));
            if (record is null || record.Length < NodeRegistration.HeaderSize)
                continue; // pruned already, or index entry outlived its record

            var seen = RegistrySupport.SeenTicks(store, snap, node, record);   // 060: beat lives apart from the record
            if (NodeRegistration.IsStale(seen, ctx.NowTicks, ctx.Options.NodeExpiry))
                continue;

            var age = (long)NodeRegistration.Age(seen, ctx.NowTicks).TotalSeconds;
            rows.Add(
            [
                Encoding.UTF8.GetBytes(node),
                Encoding.UTF8.GetBytes(age.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]);
        }

        writer.ArrayOfArrays(rows);
    }
}
