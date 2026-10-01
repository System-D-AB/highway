using System.Text;
using System.Text.Json;
using Highway.Abstractions.Scheduling;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.SESSION &lt;node&gt; &lt;sessionJson&gt; → +OK (5.0, 068 C3; replaces the heartbeat registration form,
/// HW.SUBSCRIBE and HW.JOB SET).
/// <code>
/// { "catalog":       { …the node's catalog, stored verbatim… },        (optional)
///   "subscriptions": [ { "channel": "…", "group": "…" } ],
///   "jobs":          [ { "queue": "…", "job": "…", "expression": "…", "template": "&lt;base64&gt;" } ] }
/// </code>
/// <para>Everything the broker holds on a client's behalf, declared in one idempotent command: the
/// registration and discovery index (as the registration form did — a changed catalog re-indexes), the
/// node's group memberships (as HW.SUBSCRIBE did), and job schedules (as HW.JOB SET did: last registration
/// wins, <c>lastFire</c> preserved). The client sends it at start, after any failover adoption, and whenever
/// a heartbeat answers <c>+SESSION</c> — so a broker that lost this state (wiped, fresh, re-bootstrapped)
/// regains all of it within one beat, instead of silently dropping subscriptions and schedules until the
/// client restarts (067). Jobs are upsert-only: a job removed from code is removed with HW.JOB DEL.</para>
///
/// <para><b>Subscriptions are the node's complete set</b> (070). A present <c>subscriptions</c> list replaces
/// the node's previous declaration: groups it no longer declares lose it as a member, and a group whose last
/// member left <b>drains</b> — it receives nothing new (so it can never fill and block its channel) and keeps
/// its backlog until the retirement threshold, when it retires; declaring it again first resumes it. An
/// absent list leaves memberships as they are. Every change takes the channel and group box locks, in the
/// same sorted order as publish and retirement.</para>
///
/// <para><b>The catalog is optional.</b> A client running with heartbeats disabled declares no catalog:
/// registering a node that never beats would make it look dead, and dead-node recovery would take its
/// work and retire its groups. Such a session declares only subscriptions and jobs — as before 5.0,
/// when those were sent independently of registration.</para>
/// </summary>
internal sealed class HwSessionCommand : HighwayCommand
{
    private const int MaxJobs = 256;
    private const int MaxSubscriptions = 1024;
    private const int MaxPlanAttempts = 16;   // 070 review F1: re-plan when the locked plan needs more locks

    private sealed record JobDecl(string Queue, string Job, string Expression, JobExpression Parsed, byte[] Template);

    private string _node = null!;
    private byte[]? _catalog;
    private string[] _services = [];
    private readonly List<(string Channel, string Group)> _subscriptions = [];
    private bool _subscriptionsDeclared;
    private readonly List<(string Channel, string Group)> _joined = [];
    private readonly List<(string Channel, string Group, BoxCommandSupport.LeaveOutcome Outcome)> _left = [];
    private readonly List<JobDecl> _jobs = [];
    private readonly List<(string Queue, string Job, string Detail)> _jobChanges = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "node", ctx.Options.MaxIdentifierBytes, out _node))
            return false;

        var raw = input.Next(ref idx);
        var limit = (long)ctx.Options.MaxCatalogBytes + 16L * ctx.Options.MaxPayloadBytes;
        if (raw.Length == 0) return Fail(HighwayErrors.InvalidArg, "session JSON is required");
        if (raw.Length > limit) return Fail(HighwayErrors.PayloadTooLarge, $"{raw.Length} > {limit}");

        try
        {
            using var doc = JsonDocument.Parse(raw.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(HighwayErrors.InvalidArg, "session JSON must be an object");

            if (root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind != JsonValueKind.Null)
            {
                if (catalog.ValueKind != JsonValueKind.Object)
                    return Fail(HighwayErrors.InvalidArg, "'catalog' must be an object");
                _catalog = Encoding.UTF8.GetBytes(catalog.GetRawText());
                if (_catalog.Length > ctx.Options.MaxCatalogBytes)
                    return Fail(HighwayErrors.PayloadTooLarge, $"catalog {_catalog.Length} > {ctx.Options.MaxCatalogBytes}");
                if (!RegistrySupport.TryParseCatalogServices(_catalog, out _services))
                    return Fail(HighwayErrors.InvalidArg, "catalog is not parseable catalog JSON");
            }

            if (root.TryGetProperty("subscriptions", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                _subscriptionsDeclared = true;
                foreach (var s in subs.EnumerateArray())
                {
                    if (!TryName(ctx, s, "channel", out var channel, out var why) || !TryName(ctx, s, "group", out var group, out why))
                        return Fail(HighwayErrors.InvalidArg, "subscription: " + why);
                    _subscriptions.Add((channel, group));
                }
                if (_subscriptions.Count > MaxSubscriptions)
                    return Fail(HighwayErrors.InvalidArg, $"at most {MaxSubscriptions} subscriptions per session");
            }

            if (root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
            {
                foreach (var j in jobs.EnumerateArray())
                {
                    if (!TryName(ctx, j, "queue", out var queue, out var why) || !TryName(ctx, j, "job", out var job, out why))
                        return Fail(HighwayErrors.InvalidArg, "job: " + why);
                    if (!j.TryGetProperty("expression", out var e) || e.GetString() is not { Length: > 0 } expression
                        || !j.TryGetProperty("template", out var t) || !t.TryGetBytesFromBase64(out var template))
                        return Fail(HighwayErrors.InvalidArg, "each job needs 'queue', 'job', 'expression' and a base64 'template'");
                    if (template.Length > ctx.Options.MaxPayloadBytes)
                        return Fail(HighwayErrors.PayloadTooLarge, $"job '{job}' template {template.Length} > {ctx.Options.MaxPayloadBytes}");
                    JobExpression parsed;
                    try { parsed = JobExpression.Parse(expression); }
                    catch (FormatException ex) { return Fail(HighwayErrors.InvalidArg, $"job '{job}': {ex.Message}"); }
                    _jobs.Add(new JobDecl(queue, job, expression, parsed, template));
                }
                if (_jobs.Count > MaxJobs)
                    return Fail(HighwayErrors.InvalidArg, $"at most {MaxJobs} jobs per session");
            }
        }
        catch (JsonException ex)
        {
            return Fail(HighwayErrors.InvalidArg, "session JSON does not parse: " + ex.Message);
        }
        return true;
    }

    /// <summary>
    /// Reads and validates one name from the session JSON. On failure <paramref name="why"/> says exactly why —
    /// the same detail a positional identifier argument gets (a reserved <c>@</c>, a control character, the
    /// length cap), so a rejected name is as diagnosable as it was under HW.SUBSCRIBE (018).
    /// </summary>
    private static bool TryName(CommandContext ctx, JsonElement obj, string property, out string value, out string why)
    {
        value = "";
        why = "";
        if (!obj.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { } s)
        {
            why = $"'{property}' is required";
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(s);
        if (!Identifier.IsValid(bytes, ctx.Options.MaxIdentifierBytes))
        {
            why = IdentifierErrorDetail(bytes, property, ctx.Options.MaxIdentifierBytes);
            return false;
        }
        value = s;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        // 1. Registration, under the node's lock (the registry's lock name, as before).
        if (_catalog is not null)
        {
            using var _ = ctx.Locks.Lock(_node);
            using var snap = store.Snapshot();
            using var batch = store.NewBatch();
            {
                foreach (var service in RegistrySupport.StoredCatalogServices(store, snap, _node))
                    if (Array.IndexOf(_services, service) < 0)
                        RegistrySupport.RemoveFromServiceIndex(store, batch, service, _node);

                store.Set(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(_node)), NodeRegistration.Encode(ctx.NowTicks, _catalog));
                RegistrySupport.TouchSeen(store, batch, _node, ctx.NowTicks);
                RegistrySupport.SetAdd(store, batch, HighwayNames.RegistrationNodeList, _node);
                foreach (var service in _services)
                    RegistrySupport.SetAdd(store, batch, HighwayNames.RegistrationService(service), _node);
            }
            batch.Commit();
        }

        // 2. Memberships (070): join what is declared and not yet held, leave what is no longer declared.
        ApplySubscriptions(ctx);

        // 2. Job schedules, each in its queue's box under that box's lock.
        foreach (var job in _jobs)
        {
            var box = BoxCommandSupport.Ensure(ctx, BoxKeys.Kind.Queue, job.Queue);
            using var _ = BoxCommandSupport.Lock(ctx, box);
            using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue);
            var previous = wb.UpsertSchedule(job.Job, job.Expression, job.Parsed, job.Template, ctx.NowTicks);
            wb.Commit();
            if (previous is null) _jobChanges.Add((job.Queue, job.Job, $"registered {job.Expression}"));
            else if (!string.Equals(previous, job.Expression, StringComparison.Ordinal))
                _jobChanges.Add((job.Queue, job.Job, $"{previous} => {job.Expression}"));
        }

        writer.SimpleString("OK");
    }

    /// <summary>
    /// The subscription diff (070). Plans from a snapshot, takes the node, channel and group box locks for
    /// the groups that change — one sorted <c>LockMany</c>, the order publish and retirement use — and
    /// plans again under them. It acts only when every change the locked plan needs is covered by the
    /// locks it holds; if the plan moved in between (a concurrent session for the same node, or a group
    /// retired), it releases them and starts over. Acting on the covered part alone would leave the node
    /// a member of groups it no longer declares — the very state this diff exists to remove (review F1).
    /// An unchanged session plans nothing and writes nothing.
    /// </summary>
    private void ApplySubscriptions(CommandContext ctx)
    {
        var store = ctx.Store;
        var declared = _subscriptions.Distinct().ToList();
        var declaredNames = declared.Select(s => BoxCommandSupport.GroupQueue(s.Channel, s.Group)).ToHashSet(StringComparer.Ordinal);

        (List<(string Channel, string Group)> Join, List<(string Channel, string Group)> Leave) Plan(IStoreSnapshot snap)
        {
            var join = declared.Where(s => !Holds(ctx, snap, s.Channel, s.Group)).ToList();
            var leave = new List<(string Channel, string Group)>();
            if (_subscriptionsDeclared)
                foreach (var entry in RegistrySupport.ReadSet(store, snap, HighwayNames.NodeSubs(_node)))
                    if (!declaredNames.Contains(entry) && Split(entry) is { } g)
                        leave.Add(g);
            return (join, leave);
        }

        HashSet<string> LocksFor(IEnumerable<(string Channel, string Group)> changes)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { _node };
            foreach (var byChannel in changes.GroupBy(s => s.Channel, StringComparer.Ordinal))
            {
                names.Add(byChannel.Key);
                names.UnionWith(BoxCommandSupport.GroupLockNames(ctx, byChannel.Key, byChannel.Select(s => s.Group)));
            }
            return names;
        }

        for (var attempt = 1; ; attempt++)
        {
            List<(string Channel, string Group)> join, leave;
            using (var snap = store.Snapshot())
                (join, leave) = Plan(snap);
            if (join.Count == 0 && leave.Count == 0) return;

            var held = LocksFor(join.Concat(leave));
            using (ctx.Locks.LockMany(held))
            {
                using var snap = store.Snapshot();
                (join, leave) = Plan(snap);   // re-read under the locks
                if (join.Count == 0 && leave.Count == 0) return;
                if (!LocksFor(join.Concat(leave)).IsSubsetOf(held))
                {
                    if (attempt >= MaxPlanAttempts)
                        throw new InvalidOperationException(
                            $"HW.SESSION for '{_node}': the subscription plan kept changing under {MaxPlanAttempts} lock attempts");
                    continue;   // the plan moved: release and plan again with the locks it now needs
                }

                var retired = new List<(BoxKeys.Kind, string, uint)>();
                using var batch = store.NewBatch();
                foreach (var (channel, group) in join)
                {
                    RegistrySupport.SetAdd(store, batch, HighwayNames.ChannelGroups(channel), group);
                    RegistrySupport.SetAdd(store, batch, HighwayNames.GroupMembers(channel, group), _node);
                    RegistrySupport.SetAdd(store, batch, HighwayNames.NodeSubs(_node), BoxCommandSupport.GroupQueue(channel, group));
                    if (Catalog.Find(store, batch, ctx.Boxes, BoxKeys.Kind.Queue, BoxCommandSupport.GroupQueue(channel, group)) is { } box)
                    {
                        using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue, batch);
                        if (wb.DrainingSince != 0) wb.SetDrainingSince(0);   // declared again: fan-out resumes, backlog kept
                    }
                    _joined.Add((channel, group));
                }
                foreach (var (channel, group) in leave)
                    _left.Add((channel, group, BoxCommandSupport.LeaveGroup(
                        ctx, batch, snap, channel, group, _node, BoxCommandSupport.LastMember.Drain, retired)));
                batch.Commit();

                // Before the locks go (review F3): a retired box id left in the runtime cache after they are
                // released would let a publish enqueue into the deleted box.
                BoxCommandSupport.Forget(ctx, retired);
            }
            return;
        }
    }

    /// <summary>True when the node is a declared member of a live, non-draining group.</summary>
    private bool Holds(CommandContext ctx, IStoreSnapshot snap, string channel, string group)
    {
        var store = ctx.Store;
        var derived = BoxCommandSupport.GroupQueue(channel, group);
        if (store.Get(snap, HighwayKeyspace.SetMember(HighwayNames.GroupMembers(channel, group), Encoding.UTF8.GetBytes(_node))) is null
            || store.Get(snap, HighwayKeyspace.SetMember(HighwayNames.ChannelGroups(channel), Encoding.UTF8.GetBytes(group))) is null
            || store.Get(snap, HighwayKeyspace.SetMember(HighwayNames.NodeSubs(_node), Encoding.UTF8.GetBytes(derived))) is null)
            return false;
        if (BoxCommandSupport.Find(ctx, BoxKeys.Kind.Queue, derived) is not { } box)
            return true;
        using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue);
        return wb.DrainingSince == 0;
    }

    private static (string Channel, string Group)? Split(string derived)
    {
        var at = derived.IndexOf('@');
        return at > 0 && at < derived.Length - 1 ? (derived[..at], derived[(at + 1)..]) : null;
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (Failed || _catalog is not null)
            ctx.Recorder.Record(HighwayEventType.NodeRegistered, _node ?? "?", nodeId: _node, errorCode: FailureCode);
        if (Failed) return;
        foreach (var (channel, group) in _joined)
            ctx.Recorder.Record(HighwayEventType.GroupRegistered, channel, nodeId: group);
        foreach (var (channel, group, outcome) in _left)
        {
            // Loud (070): a session that drops a subscription changes who receives a channel. Two processes
            // sharing one node name would flip memberships back and forth - this is where that shows (the
            // recorder also writes these events to the broker log at Warning).
            var who = $"node '{_node}' no longer declares {channel}@{group}";
            if (outcome.OthersRemain)
                ctx.Recorder.Record(HighwayEventType.MembershipRemoved, channel, nodeId: group, errorCode: who);
            else if (outcome.Draining)
                ctx.Recorder.Record(HighwayEventType.GroupDraining, channel, nodeId: group,
                    errorCode: who + "; it was the last member, so the group drains (no new messages; backlog kept until the retirement threshold)");
            else
                ctx.Recorder.Record(HighwayEventType.GroupRetired, channel, nodeId: group, count: 0,
                    errorCode: who + "; it was the last member and the group held nothing, so it was retired");
        }
        foreach (var (queue, job, detail) in _jobChanges)
            ctx.Recorder.Record(HighwayEventType.JobScheduleChanged, queue, requestId: job, errorCode: detail);
    }
}
