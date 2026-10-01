using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.HEARTBEAT — liveness and departure (5.0; registration moved to HW.SESSION).
/// <code>
/// HW.HEARTBEAT &lt;node&gt;            → +OK | +SESSION   (liveness)
/// HW.HEARTBEAT &lt;node&gt; BYE        → +OK              (departure)
/// HW.HEARTBEAT &lt;node&gt; BYE PURGE  → [groups, messages, bytes]  (retire forever)
/// </code>
/// <para><b>Liveness</b> writes 8 bytes (<c>reg:seen:{node}</c>) and nothing else (060). When the broker holds
/// no session for the node it answers <c>+SESSION</c> — a normal reply, not an error — and the client sends
/// HW.SESSION at once. That is what makes a wiped, fresh or re-bootstrapped broker self-healing: pruning or
/// losing a registration without asking for it back would leave the node alive but undiscoverable, and its
/// subscriptions and job schedules gone.</para>
///
/// <para><b>Departure</b> returns the node's claimed RPC work to each service it hosted (attempts + 1) and
/// removes it from discovery and the registry — the same teardown dead-node recovery runs. Subscriber groups
/// are untouched. <b>Purge</b> also retires every group the node is the last member of (its queue box
/// destroyed in one range delete, counted first), and leaves groups other members still back.</para>
/// </summary>
internal sealed class HwHeartbeatCommand : HighwayCommand
{
    private enum Form { Liveness, Departure, Purge }

    private string _nodeId = null!;
    private Form _form;
    private RegistrySupport.RetirementOutcome _retired;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId))
            return false;

        var second = input.Next(ref idx);
        if (second.Length == 0)
        {
            _form = Form.Liveness;
            return true;
        }
        if (!Encoding.ASCII.GetString(second).Equals("BYE", StringComparison.Ordinal))
            return Fail(HighwayErrors.InvalidArg, "expected no argument (liveness) or BYE [PURGE]; registration is HW.SESSION");

        var third = input.Next(ref idx);
        _form = third.Length > 0 && Encoding.ASCII.GetString(third).Equals("PURGE", StringComparison.Ordinal)
            ? Form.Purge
            : Form.Departure;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        switch (_form)
        {
            case Form.Liveness: RunLiveness(ctx, writer); break;
            case Form.Departure: Depart(ctx); writer.SimpleString("OK"); break;
            default: RunPurge(ctx, writer); break;
        }
    }

    private void RunLiveness(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_nodeId);
        using var snap = ctx.Store.Snapshot();
        var record = ctx.Store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(_nodeId)));
        if (record is null || record.Length == 0)
        {
            writer.SimpleString("SESSION");
            return;
        }
        using var batch = ctx.Store.NewBatch();
        RegistrySupport.TouchSeen(ctx.Store, batch, _nodeId, ctx.NowTicks);
        batch.Commit();
        writer.SimpleString("OK");
    }

    /// <summary>Returns the node's claimed RPC work, then removes it from discovery and the registry.</summary>
    private void Depart(CommandContext ctx)
    {
        string[] services;
        using (var snap = ctx.Store.Snapshot())
            services = RegistrySupport.StoredCatalogServices(ctx.Store, snap, _nodeId);

        foreach (var service in services)
        {
            if (BoxCommandSupport.Find(ctx, BoxKeys.Kind.Service, service) is not { } box) continue;
            using var _ = BoxCommandSupport.Lock(ctx, box);
            using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Service);
            wb.RequeueNode(_nodeId);
            wb.Commit();
        }

        using var _lock = ctx.Locks.Lock(_nodeId);
        using var batch = ctx.Store.NewBatch();
        foreach (var service in services)
            RegistrySupport.RemoveFromServiceIndex(ctx.Store, batch, service, _nodeId);
        RegistrySupport.RemoveRegistration(ctx.Store, batch, _nodeId);
        batch.Commit();
    }

    private void RunPurge(CommandContext ctx, RespWriter writer)
    {
        Depart(ctx);

        // Leave every group the node declared, under the node lock as well as the channel and group box
        // locks, re-reading the node's declarations under them (070 review F2). A session for the same node
        // is then either wholly before a pass (its groups are in the re-read and are left) or wholly after
        // the purge (a new registration whose record and memberships agree) — never half in between, which
        // would leave a membership its NodeSubs record no longer lists, one nothing could drain or retire.
        for (var attempt = 1; ; attempt++)
        {
            (string Channel, string Group)[] targets;
            using (var snap = ctx.Store.Snapshot())
                targets = PurgeTargets(ctx, snap);

            var held = LocksFor(ctx, targets);
            using (ctx.Locks.LockMany(held))
            {
                using var snap = ctx.Store.Snapshot();
                targets = PurgeTargets(ctx, snap);
                if (!LocksFor(ctx, targets).IsSubsetOf(held))
                {
                    if (attempt >= MaxPurgeAttempts)
                        throw new InvalidOperationException(
                            $"BYE PURGE for '{_nodeId}': its subscriptions kept changing under {MaxPurgeAttempts} lock attempts");
                    continue;   // a session joined a group on another channel meanwhile: lock that too
                }

                using var batch = ctx.Store.NewBatch();
                var retired = new List<(BoxKeys.Kind, string, uint)>();
                foreach (var (channel, group) in targets)
                    _retired += BoxCommandSupport.LeaveGroup(ctx, batch, snap, channel, group, _nodeId,
                        BoxCommandSupport.LastMember.Retire, retired).Retired;
                // Entries that are not a {channel}@{group} name are not memberships; clear them with the rest.
                ctx.Store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.NodeSubs(_nodeId)));
                batch.Commit();
                // Before the locks go (070 review F3): until the retired box id leaves the runtime cache, a
                // publish that re-resolves the group would get the deleted box and enqueue into it - lost.
                BoxCommandSupport.Forget(ctx, retired);
            }
            break;
        }
        writer.ThreeIntegers(_retired.Groups, _retired.Messages, _retired.Bytes);
    }

    private const int MaxPurgeAttempts = 16;

    /// <summary>The node lock plus each target's channel and existing group box lock.</summary>
    private HashSet<string> LocksFor(CommandContext ctx, IEnumerable<(string Channel, string Group)> targets)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { _nodeId };
        foreach (var byChannel in targets.GroupBy(t => t.Channel, StringComparer.Ordinal))
        {
            names.Add(byChannel.Key);
            names.UnionWith(BoxCommandSupport.GroupLockNames(ctx, byChannel.Key, byChannel.Select(t => t.Group)));
        }
        return names;
    }

    /// <summary>Every group this node declared (its <c>NodeSubs</c> record — 070: the only membership record).</summary>
    private (string Channel, string Group)[] PurgeTargets(CommandContext ctx, IStoreSnapshot snap)
    {
        var targets = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var entry in RegistrySupport.ReadSet(ctx.Store, snap, HighwayNames.NodeSubs(_nodeId)))
        {
            var at = entry.IndexOf('@');
            if (at > 0 && at < entry.Length - 1)
                targets[entry] = (entry[..at], entry[(at + 1)..]);
        }
        return [.. targets.Values];
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_form == Form.Liveness) return;
        if (_form == Form.Purge && _retired.Groups > 0)
        {
            ctx.Recorder.Record(
                HighwayEventType.GroupRetired, _nodeId,
                nodeId: _nodeId,
                count: (int)_retired.Messages,
                errorCode: $"retired {_retired.Groups} group(s), discarded {_retired.Messages} " +
                           $"message(s) / {_retired.Bytes} byte(s) by explicit BYE PURGE");
        }
        ctx.Recorder.Record(HighwayEventType.NodeDeparted, _nodeId, nodeId: _nodeId, errorCode: FailureCode);
    }
}
