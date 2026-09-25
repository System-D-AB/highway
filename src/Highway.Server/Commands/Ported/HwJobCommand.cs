using System.Globalization;
using System.Text;
using Highway.Abstractions.Scheduling;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

using Highway.Server.Storage.Boxes;

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

    // 065: a queue's schedules live in its box (schedules + a nextFire index), written under the box's
    // lock — the same lock HW.CLAIM holds while firing them (before 3.0, SET locked a different name).

    private void RunSet(CommandContext ctx, RespWriter writer)
    {
        var box = BoxCommandSupport.Ensure(ctx, BoxKeys.Kind.Queue, _queue);
        using var _lock = BoxCommandSupport.Lock(ctx, box);
        using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue);
        var previous = wb.UpsertSchedule(_job, _expression, _parsed, _template, ctx.NowTicks);
        wb.Commit();
        _changeDetail = previous is null ? $"registered {_expression}"
            : string.Equals(previous, _expression, StringComparison.Ordinal) ? null
            : $"{previous} => {_expression}";
        writer.SimpleString("OK");
    }

    private void RunDel(CommandContext ctx, RespWriter writer)
    {
        if (BoxCommandSupport.Find(ctx, BoxKeys.Kind.Queue, _queue) is { } box)
        {
            using var _lock = BoxCommandSupport.Lock(ctx, box);
            using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue);
            _removed = wb.RemoveSchedule(_job);
            wb.Commit();
        }
        writer.Integer(_removed ? 1 : 0);
    }

    private static void RunList(CommandContext ctx, RespWriter writer)
    {
        var rows = new List<IReadOnlyList<byte[]>>();
        using var batch = ctx.Store.NewBatch();
        foreach (var (_, queue, box) in Catalog.List(ctx.Store, batch, BoxKeys.Kind.Queue))
        {
            using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue, batch);
            foreach (var s in wb.Schedules())
            {
                rows.Add(
                [
                    Encoding.UTF8.GetBytes(queue),
                    Encoding.UTF8.GetBytes(s.Job),
                    Encoding.UTF8.GetBytes(s.Expression),
                    Encoding.UTF8.GetBytes(s.NextFire.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Encoding.UTF8.GetBytes(s.LastFire.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]);
            }
        }
        if (rows.Count == 0) writer.EmptyArray();
        else writer.ArrayOfArrays(rows);
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
}
