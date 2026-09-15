using System.Globalization;
using System.Text;
using Highway.Abstractions.Scheduling;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.JOB SET &lt;queue&gt; &lt;job&gt; &lt;expression&gt; &lt;template&gt; → +OK
/// HW.JOB DEL &lt;queue&gt; &lt;job&gt;                              → :1 | :0
/// HW.JOB LIST                                                → [[queue, job, expr, nextFireTicks, lastFireTicks], ...]
///
/// <para>Recurring-job schedule management (feature 028). A schedule is a member of the
/// per-queue ordered set <c>job:{queue}:schedules</c> scored by <c>nextFireTicks</c>; firing
/// happens in <c>HW.QCLAIM</c>'s promotion sweep, not here.</para>
///
/// <para>Ported from Garnet. Two model changes: the <c>job:index</c> mirror is gone — LIST
/// enumerates queues with schedules straight from the <c>job:index</c> <b>set</b> via
/// <see cref="IHighwayStore.SetMembers"/> (T3.3), and the next-occurrence clock is
/// <see cref="CommandContext.NowTicks"/>, not <c>DateTime.UtcNow</c> (037 R5.1). SET stays
/// last-registration-wins loudly (OD5): preserves <c>lastFire</c>, records the expression
/// change.</para>
/// </summary>
internal sealed class HwJobCommand : HighwayCommand
{
    private enum Form { Set, Del, List }

    private Form _form;
    private string _queue = null!;
    private string _job = null!;
    private string _expression = null!;
    private JobExpression _parsed = null!;
    private byte[] _template = [];
    private string? _changeDetail;
    private bool _removed;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var sub = Encoding.ASCII.GetString(input.Next(ref idx)).ToUpperInvariant();

        if (sub == "LIST")
        {
            _form = Form.List;
            return true;
        }

        if (sub != "SET" && sub != "DEL")
            return Fail(HighwayErrors.InvalidArg, "first argument must be SET, DEL or LIST");

        _form = sub == "SET" ? Form.Set : Form.Del;

        if (!TryReadIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _queue))
            return false;
        if (!TryReadIdentifier(input, ref idx, "job", ctx.Options.MaxIdentifierBytes, out _job))
            return false;

        if (_form == Form.Set)
        {
            _expression = Encoding.UTF8.GetString(input.Next(ref idx));

            // Validated HERE so a bad expression is a permanent, classified error before any
            // key is touched — and the message teaches the grammar (R1.7).
            if (!JobExpression.TryParse(_expression, out _parsed!, out var reason))
                return Fail(HighwayErrors.InvalidArg,
                    $"schedule expression '{_expression}': {reason}. Accepted: {JobExpression.AcceptedForms}");

            var template = input.Next(ref idx);
            if (template.Length == 0)
                return Fail(HighwayErrors.InvalidArg, "a template payload is required (the occurrence message's bytes)");
            if (template.Length > ctx.Options.MaxPayloadBytes)
                return Fail(HighwayErrors.PayloadTooLarge, $"{template.Length} > {ctx.Options.MaxPayloadBytes}");

            _template = template.ToArray();
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        switch (_form)
        {
            case Form.Set: RunSet(ctx, writer); break;
            case Form.Del: RunDel(ctx, writer); break;
            default: RunList(ctx, writer); break;
        }
    }

    private void RunSet(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(HighwayNames.JobSchedules(_queue));
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        var schedName = HighwayNames.JobSchedules(_queue);
        var schedKey = HighwayKeyspace.SortedSetPrefix(schedName);

        // Find an existing member for this job; preserve its lastFire across the update.
        long lastFire = 0;
        if (FindMember(store, snap, schedName, _job, out var existing))
        {
            JobScheduleRecord.Decode(existing, out _, out var oldExpr, out lastFire, out _, out _);
            store.SortedSetRemove(batch, schedKey, existing);

            if (!string.Equals(oldExpr, _expression, StringComparison.Ordinal))
                _changeDetail = $"{oldExpr} => {_expression}";
        }
        else
        {
            _changeDetail = $"registered {_expression}";
        }

        var next = _parsed.NextOccurrence(new DateTime(ctx.NowTicks, DateTimeKind.Utc)).Ticks;
        var record = JobScheduleRecord.Encode(_job, _expression, lastFire, next, _template);

        store.SortedSetAdd(batch, schedKey, next, record);

        // The job index is a plain set now (mirror gone, T3.3).
        RegistrySupport.SetAdd(store, batch, HighwayNames.JobIndex, _queue);

        batch.Commit();
        writer.SimpleString("OK");
    }

    private void RunDel(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(HighwayNames.JobSchedules(_queue));
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        var schedName = HighwayNames.JobSchedules(_queue);
        var schedKey = HighwayKeyspace.SortedSetPrefix(schedName);

        _removed = FindMember(store, snap, schedName, _job, out var existing);
        if (_removed)
        {
            store.SortedSetRemove(batch, schedKey, existing);

            // Drop the queue from the index set when its last schedule goes. Length is read
            // from the snapshot, so it still counts the member we just staged for removal —
            // remaining == 1 means this was the last.
            var remaining = store.SortedSetLength(snap, schedKey);
            if (remaining <= 1)
                RegistrySupport.SetRemove(store, batch, HighwayNames.JobIndex, _queue);
        }

        batch.Commit();
        writer.Integer(_removed ? 1 : 0);
    }

    private void RunList(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;
        using var snap = store.Snapshot();

        // Enumerate queues-with-schedules straight from the index set (no mirror, T3.3).
        var queues = RegistrySupport.ReadSet(store, snap, HighwayNames.JobIndex);

        var rows = new List<IReadOnlyList<byte[]>>();
        foreach (var queue in queues)
        {
            var schedKey = HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules(queue));
            foreach (var member in store.SortedSetRangeByScore(snap, schedKey, long.MinValue, long.MaxValue, 1024))
            {
                JobScheduleRecord.Decode(member, out var job, out var expr, out var lastFire, out var nextFire, out _);
                rows.Add(
                [
                    Encoding.UTF8.GetBytes(queue),
                    Encoding.UTF8.GetBytes(job),
                    Encoding.UTF8.GetBytes(expr),
                    Encoding.UTF8.GetBytes(nextFire.ToString(CultureInfo.InvariantCulture)),
                    Encoding.UTF8.GetBytes(lastFire.ToString(CultureInfo.InvariantCulture)),
                ]);
            }
        }

        writer.ArrayOfArrays(rows);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (Failed) return;

        switch (_form)
        {
            case Form.Set when _changeDetail is not null:
                ctx.Recorder.Record(
                    HighwayEventType.JobScheduleChanged, _queue,
                    requestId: _job, errorCode: _changeDetail);
                break;

            case Form.Del when _removed:
                ctx.Recorder.Record(
                    HighwayEventType.JobScheduleRemoved, _queue,
                    requestId: _job);
                break;
        }
    }

    /// <summary>Scans the (small, topology-bounded) schedule set for a job by name.</summary>
    private static bool FindMember(
        IHighwayStore store, IStoreSnapshot snap, string schedName, string job, out byte[] member)
    {
        var schedKey = HighwayKeyspace.SortedSetPrefix(schedName);
        foreach (var candidate in store.SortedSetRangeByScore(snap, schedKey, long.MinValue, long.MaxValue, 1024))
        {
            if (JobScheduleRecord.PeekName(candidate) == job)
            {
                member = candidate;
                return true;
            }
        }
        member = [];
        return false;
    }
}
