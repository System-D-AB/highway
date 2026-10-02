using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

using Highway.Abstractions.Observability;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.DLQ (PEEK | REQUEUE | PURGE) (SVC &lt;service&gt; | Q &lt;queue&gt;) [COUNT n] (feature 013/015).
/// PEEK → array of dead-letter field arrays; REQUEUE/PURGE → :n moved/removed.
///
/// Ported from Garnet. PEEK is now a true range read (drain, report the head window, restore
/// all in order) rather than pop-and-restore; REQUEUE moves entries back to the live queue
/// with attempts reset; PURGE drops the head window.
/// </summary>
internal sealed class HwDlqCommand : HighwayCommand
{
    private const string ActionPeek = "PEEK";
    private const string ActionRequeue = "REQUEUE";
    private const string ActionPurge = "PURGE";
    private const string ActionResolve = "RESOLVE";
    private const string ActionCount = "COUNT";
    private const string ResolveKeep = "KEEP";
    private const string ResolveDiscard = "DISCARD";
    private const string ResolveRequeue = "REQUEUE";
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";
    private const string KeywordCount = "COUNT";
    private const string KeywordId = "ID";
    private const string KeywordAfter = "AFTER";

    private string _action = null!;
    private BoxKeys.Kind _kind;
    private string _name = null!;
    private int _count;
    private bool _hasCount;
    private bool _hasId;
    private uint _deadBox;
    private ulong _deadSeq;
    private TimeSpan? _after;

    private uint _feedBox;
    private ulong _feedSeq;
    private string _resolveAction = null!;
    private string? _resolvedOutcome;
    private string? _resolvedDetail;
    private string? _resolvedTarget;
    private string? _resolvedDeadId;
    private int _operatorMoved;
    private string? _operatorTarget;
    private string? _operatorAction;
    private string? _operatorDeadId;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;

        var actionArg = input.Next(ref idx);
        if (actionArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.DLQ requires an action: PEEK, REQUEUE, PURGE, RESOLVE or COUNT");

        _action = Encoding.ASCII.GetString(actionArg).ToUpperInvariant();
        if (_action is not (ActionPeek or ActionRequeue or ActionPurge or ActionResolve or ActionCount))
            return Fail(HighwayErrors.InvalidArg, $"unknown action '{_action}'; expected PEEK, REQUEUE, PURGE, RESOLVE or COUNT");

        if (_action == ActionResolve)
        {
            var tokenArg = input.Next(ref idx);
            if (tokenArg.Length == 0 || !BoxCommandSupport.TryParseToken(tokenArg, out _feedBox, out _feedSeq))
                return Fail(HighwayErrors.InvalidToken, "a token is the value HW.CLAIM returned, echoed verbatim");

            var resolveActionArg = input.Next(ref idx);
            if (resolveActionArg.Length == 0)
                return Fail(HighwayErrors.InvalidArg, "HW.DLQ RESOLVE requires an action: KEEP, DISCARD or REQUEUE");

            _resolveAction = Encoding.ASCII.GetString(resolveActionArg).ToUpperInvariant();
            if (_resolveAction is not (ResolveKeep or ResolveDiscard or ResolveRequeue))
                return Fail(HighwayErrors.InvalidArg, $"unknown resolve action '{_resolveAction}'; expected KEEP, DISCARD or REQUEUE");

            var extra = input.Next(ref idx);
            while (extra.Length > 0)
            {
                var word = Encoding.ASCII.GetString(extra).ToUpperInvariant();
                if (word == KeywordAfter)
                {
                    if (_resolveAction != ResolveRequeue)
                        return Fail(HighwayErrors.InvalidArg, "AFTER is only valid with REQUEUE");
                    var afterVal = input.Next(ref idx);
                    if (afterVal.Length == 0 || !long.TryParse(Encoding.ASCII.GetString(afterVal), out var ms) || ms < 1)
                        return Fail(HighwayErrors.InvalidArg, "AFTER must be a positive integer in milliseconds");
                    _after = TimeSpan.FromMilliseconds(ms);
                }
                else
                {
                    return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'");
                }
                extra = input.Next(ref idx);
            }
            return true;
        }

        var kindArg = input.Next(ref idx);
        if (kindArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.DLQ requires a target: SVC <service> or Q <queue>");

        var kind = Encoding.ASCII.GetString(kindArg).ToUpperInvariant();
        switch (kind)
        {
            case TargetService:
                if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _name)) return false;
                _kind = BoxKeys.Kind.Service;
                break;
            case TargetQueue:
                if (!TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _name)) return false;
                _kind = BoxKeys.Kind.Queue;
                break;
            default:
                return Fail(HighwayErrors.InvalidArg, $"unknown target '{kind}'; accepted forms are SVC <service> or Q <queue>");
        }

        if (_action == ActionCount)
        {
            var extra = input.Next(ref idx);
            if (extra.Length > 0)
                return Fail(HighwayErrors.InvalidArg, $"unknown argument '{Encoding.ASCII.GetString(extra)}'");
            return true;
        }

        _count = ctx.Options.ReceiveDefaultCount;

        var keyword = input.Next(ref idx);
        while (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
            switch (word)
            {
                case KeywordCount:
                    if (_hasId)
                        return Fail(HighwayErrors.InvalidArg, "COUNT and ID are mutually exclusive");
                    var countVal = input.Next(ref idx);
                    if (countVal.Length == 0 || !int.TryParse(Encoding.ASCII.GetString(countVal), out var count) || count < 1)
                        return Fail(HighwayErrors.InvalidCount, "COUNT must be a positive integer");
                    if (count > ctx.Options.ReceiveMaxCount)
                        return Fail(HighwayErrors.InvalidCount, $"COUNT {count} exceeds maximum {ctx.Options.ReceiveMaxCount}");
                    _count = count;
                    _hasCount = true;
                    break;

                case KeywordId:
                    if (_hasCount)
                        return Fail(HighwayErrors.InvalidArg, "COUNT and ID are mutually exclusive");
                    if (_hasId)
                        return Fail(HighwayErrors.InvalidArg, "ID specified more than once");
                    if (_action == ActionPeek)
                        return Fail(HighwayErrors.InvalidArg, "HW.DLQ PEEK does not accept ID");
                    var idVal = input.Next(ref idx);
                    if (idVal.Length == 0 || !DeadLetter.TryParseDeadId(idVal, out _deadBox, out _deadSeq))
                        return Fail(HighwayErrors.InvalidArg, "malformed dead letter id");
                    _hasId = true;
                    break;

                case KeywordAfter:
                    if (_action != ActionRequeue)
                        return Fail(HighwayErrors.InvalidArg, "AFTER is only valid with REQUEUE");
                    var afterVal = input.Next(ref idx);
                    if (afterVal.Length == 0 || !long.TryParse(Encoding.ASCII.GetString(afterVal), out var ms) || ms < 1)
                        return Fail(HighwayErrors.InvalidArg, "AFTER must be a positive integer in milliseconds");
                    _after = TimeSpan.FromMilliseconds(ms);
                    break;

                default:
                    return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'");
            }
            keyword = input.Next(ref idx);
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (_action == ActionResolve)
        {
            RunResolve(ctx, writer);
            return;
        }

        if (_action == ActionCount)
        {
            using var _countLock = BoxCommandSupport.LockLive(ctx, _kind, _name, create: false, out var countResolved);
            if (countResolved is not { } countBox)
            {
                writer.Integer(0);
                return;
            }
            using var countWb = BoxCommandSupport.Open(ctx, countBox, _kind);
            writer.Integer(countWb.Dead);
            return;
        }

        // 065: dead letters live in the target's box, oldest first from a durable head — nothing already
        // requeued or purged is ever re-walked. An unknown target answers empty / :0, never an error.
        using var _lock = BoxCommandSupport.LockLive(ctx, _kind, _name, create: false, out var resolved);
        if (resolved is not { } box)
        {
            if (_action == ActionPeek) writer.EmptyArray();
            else writer.Integer(0);
            return;
        }

        if (_hasId && _deadBox != box)
        {
            // Box mismatch (retired box or cross-target handle): entry is gone from this live target
            writer.Integer(0);
            return;
        }

        using var wb = BoxCommandSupport.Open(ctx, box, _kind);
        if (_hasId)
        {
            switch (_action)
            {
                case ActionRequeue:
                {
                    var ok = wb.RequeueDead(_deadSeq, _after, ctx.NowTicks);
                    wb.Commit();
                    if (ok)
                    {
                        _operatorMoved = 1;
                        _operatorAction = "REQUEUE";
                        _operatorTarget = _name;
                        _operatorDeadId = DeadLetter.FormatDeadId(box, _deadSeq);
                        ctx.Metrics?.RecordDeadLetterResolved("requeue", "operator");
                        ctx.Metrics?.RecordDeadLetterRequeued(1);
                    }
                    writer.Integer(ok ? 1 : 0);
                    break;
                }
                case ActionPurge:
                {
                    var ok = wb.PurgeDead(_deadSeq);
                    wb.Commit();
                    if (ok)
                    {
                        _operatorMoved = 1;
                        _operatorAction = "PURGE";
                        _operatorTarget = _name;
                        _operatorDeadId = DeadLetter.FormatDeadId(box, _deadSeq);
                        ctx.Metrics?.RecordDeadLetterResolved("purge", "operator");
                    }
                    writer.Integer(ok ? 1 : 0);
                    break;
                }
            }
            return;
        }

        switch (_action)
        {
            case ActionPeek:
                WritePeek(writer, box, _kind, wb.PeekDead(_count));
                break;
            case ActionRequeue:
            {
                var moved = wb.RequeueDead(_count);
                wb.Commit();
                if (moved > 0)
                {
                    _operatorMoved = moved;
                    _operatorAction = "REQUEUE";
                    _operatorTarget = _name;
                    ctx.Metrics?.RecordDeadLetterResolved("requeue", "operator", moved);
                    ctx.Metrics?.RecordDeadLetterRequeued(moved);
                }
                writer.Integer(moved);
                break;
            }
            default:
            {
                var removed = wb.PurgeDead(_count);
                wb.Commit();
                if (removed > 0)
                {
                    _operatorMoved = removed;
                    _operatorAction = "PURGE";
                    _operatorTarget = _name;
                    ctx.Metrics?.RecordDeadLetterResolved("purge", "operator", removed);
                }
                writer.Integer(removed);
                break;
            }
        }
    }

    private static void WritePeek(RespWriter writer, uint box, BoxKeys.Kind kind, IReadOnlyList<(byte[] Key, byte[] Value)> entries)
    {
        var rows = new List<IReadOnlyList<byte[]>>(entries.Count);
        foreach (var (key, raw) in entries)
        {
            var seq = BoxKeys.TrailingSeq(key);
            var deadId = DeadLetter.FormatDeadId(box, seq);

            DeadLetter.Decode(raw, out var ticks, out var attempts, out var reason, out var original);
            Envelope.TryGetLineage(original, out var requeues, out var withoutLineage);

            var fields = new List<byte[]>(16);

            void Add(string name, string value)
            {
                fields.Add(Encoding.UTF8.GetBytes(name));
                fields.Add(Encoding.UTF8.GetBytes(value));
            }

            Add("deadId", deadId);
            Add("deadLetteredAt", new DateTime(ticks, DateTimeKind.Utc).ToString("O"));
            Add("attempts", attempts.ToString());
            Add("requeues", requeues.ToString());
            Add("reason", Encoding.UTF8.GetString(reason));

            Envelope.DecodeRpcEntry(withoutLineage, out var id, out var payload, out _);
            if (kind == BoxKeys.Kind.Service)
                Add("requestId", Encoding.UTF8.GetString(id));
            else
                Add("messageId", Encoding.UTF8.GetString(id));

            fields.Add("payload"u8.ToArray());
            fields.Add(payload.ToArray());

            if (Envelope.TryGetFailureBlock(withoutLineage, out var block, out _))
            {
                Envelope.DecodeFailureBlock(block, out var type, out var firstType, out var detail);
                Add("failureType", Encoding.UTF8.GetString(type));
                if (firstType.Length > 0)
                    Add("failureFirstType", Encoding.UTF8.GetString(firstType));
                fields.Add("failureDetail"u8.ToArray());
                fields.Add(detail.ToArray());
            }
            else
            {
                Add("failure", "not reported - the worker did not report a failure before this " +
                               "message exhausted its attempts");
            }

            rows.Add(fields);
        }

        if (rows.Count == 0) writer.EmptyArray();
        else writer.ArrayOfArrays(rows);
    }

    private void RunResolve(CommandContext ctx, RespWriter writer)
    {
        if (BoxCommandSupport.Describe(ctx, _feedBox) is not { } feedMeta || feedMeta.Kind != BoxKeys.Kind.DeadFeed)
        {
            writer.Integer(0);
            return;
        }

        string? deadIdString = null;
        uint targetBox = 0;
        ulong targetSeq = 0;

        using (BoxCommandSupport.Lock(ctx, _feedBox))
        {
            using var wbFeedProbe = BoxCommandSupport.Open(ctx, _feedBox, BoxKeys.Kind.DeadFeed);
            var inFlight = wbFeedProbe.ReadInFlight(_feedSeq);
            if (inFlight is null)
            {
                writer.Integer(0);
                return;
            }

            Envelope.DecodeRpcEntry(inFlight.Entry, out var notifId, out _, out _);
            deadIdString = Encoding.UTF8.GetString(notifId);
            if (!DeadLetter.TryParseDeadId(deadIdString, out targetBox, out targetSeq))
            {
                wbFeedProbe.Ack(_feedSeq);
                wbFeedProbe.Commit();
                _resolvedOutcome = "gone";
                _resolvedDetail = "malformed dead letter id";
                writer.BulkStringArray(Encoding.UTF8.GetBytes("gone"), Encoding.UTF8.GetBytes("malformed dead letter id"));
                return;
            }
        }

        var locks = new[] { WorkBox.LockName(_feedBox), WorkBox.LockName(targetBox) };
        using (ctx.Locks.LockMany(locks))
        {
            using var wbFeed = BoxCommandSupport.Open(ctx, _feedBox, BoxKeys.Kind.DeadFeed);
            var inFlight = wbFeed.ReadInFlight(_feedSeq);
            if (inFlight is null)
            {
                writer.Integer(0);
                return;
            }

            if (BoxCommandSupport.Describe(ctx, targetBox) is not { } targetMeta)
            {
                wbFeed.Ack(_feedSeq);
                wbFeed.Commit();
                _resolvedOutcome = "gone";
                _resolvedDetail = "target box retired";
                writer.BulkStringArray(Encoding.UTF8.GetBytes("gone"), Encoding.UTF8.GetBytes("target box retired"));
                return;
            }

            _resolvedTarget = BoxCommandSupport.FeedTargetName(targetMeta.Kind, targetMeta.Name);
            _resolvedDeadId = deadIdString;

            using var wbTarget = BoxCommandSupport.Open(ctx, targetBox, targetMeta.Kind, sharedBatch: wbFeed.Batch);
            var deadRecord = wbTarget.ReadDead(targetSeq);
            if (deadRecord is null)
            {
                wbFeed.Ack(_feedSeq);
                wbFeed.Commit();
                wbTarget.AfterCommit();
                _resolvedOutcome = "gone";
                _resolvedDetail = "entry missing or previously purged";
                writer.BulkStringArray(Encoding.UTF8.GetBytes("gone"), Encoding.UTF8.GetBytes("entry missing or previously purged"));
                return;
            }

            switch (_resolveAction)
            {
                case ResolveKeep:
                {
                    wbFeed.Ack(_feedSeq);
                    wbFeed.Commit();
                    wbTarget.AfterCommit();
                    _resolvedOutcome = "done";
                    _resolvedDetail = "kept";
                    ctx.Metrics?.RecordDeadLetterResolved("ack", "handler");
                    writer.BulkStringArray(Encoding.UTF8.GetBytes("done"), Encoding.UTF8.GetBytes("kept"));
                    return;
                }

                case ResolveDiscard:
                {
                    wbTarget.PurgeDead(targetSeq);
                    wbFeed.Ack(_feedSeq);
                    wbFeed.Commit();
                    wbTarget.AfterCommit();
                    _resolvedOutcome = "done";
                    _resolvedDetail = "discarded";
                    ctx.Metrics?.RecordDeadLetterResolved("purge", "handler");
                    writer.BulkStringArray(Encoding.UTF8.GetBytes("done"), Encoding.UTF8.GetBytes("discarded"));
                    return;
                }

                case ResolveRequeue:
                {
                    if (targetMeta.Kind == BoxKeys.Kind.Service)
                    {
                        wbFeed.Ack(_feedSeq);
                        wbFeed.Commit();
                        wbTarget.AfterCommit();
                        _resolvedOutcome = "refused";
                        _resolvedDetail = "rpc";
                        writer.BulkStringArray(Encoding.UTF8.GetBytes("refused"), Encoding.UTF8.GetBytes("rpc"));
                        return;
                    }

                    DeadLetter.Decode(deadRecord, out _, out _, out _, out var originalEntry);
                    Envelope.TryGetLineage(originalEntry, out var requeues, out var withoutLineage);

                    var maxRequeues = wbTarget.MaxRequeues;
                    if (requeues >= maxRequeues)
                    {
                        wbFeed.Ack(_feedSeq);
                        wbFeed.Commit();
                        wbTarget.AfterCommit();
                        _resolvedOutcome = "refused";
                        _resolvedDetail = "cap";
                        writer.BulkStringArray(Encoding.UTF8.GetBytes("refused"), Encoding.UTF8.GetBytes("cap"));
                        return;
                    }

                    if (wbTarget.TracksBytes && ctx.Options.MaxQueueBytes > 0)
                    {
                        var bare = Envelope.StripFailureBlock(withoutLineage);
                        if (wbTarget.Bytes + bare.Length + 3 > ctx.Options.MaxQueueBytes)
                        {
                            wbFeed.Ack(_feedSeq);
                            wbFeed.Commit();
                            wbTarget.AfterCommit();
                            _resolvedOutcome = "refused";
                            _resolvedDetail = "full";
                            writer.BulkStringArray(Encoding.UTF8.GetBytes("refused"), Encoding.UTF8.GetBytes("full"));
                            return;
                        }
                    }

                    wbTarget.RequeueDead(targetSeq, _after, ctx.NowTicks);
                    wbFeed.Ack(_feedSeq);
                    wbFeed.Commit();
                    wbTarget.AfterCommit();
                    _resolvedOutcome = "done";
                    _resolvedDetail = "requeued";
                    ctx.Metrics?.RecordDeadLetterResolved("requeue", "handler");
                    ctx.Metrics?.RecordDeadLetterRequeued(1);
                    writer.BulkStringArray(Encoding.UTF8.GetBytes("done"), Encoding.UTF8.GetBytes("requeued"));
                    return;
                }
            }
        }
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_action == ActionResolve && _resolvedOutcome is not null && _resolvedTarget is not null)
        {
            if (_resolvedOutcome == "refused")
            {
                ctx.Recorder.Record(
                    HighwayEventType.DeadLetterRequeueRefused,
                    _resolvedTarget,
                    nodeId: "handler",
                    requestId: _resolvedDeadId,
                    errorCode: _resolvedDetail);
            }
            else
            {
                ctx.Recorder.Record(
                    HighwayEventType.DeadLetterResolved,
                    _resolvedTarget,
                    nodeId: "handler",
                    requestId: _resolvedDeadId,
                    errorCode: _resolveAction);
            }
        }
        else if ((_action == ActionRequeue || _action == ActionPurge) && _operatorMoved > 0 && _operatorTarget is not null)
        {
            ctx.Recorder.Record(
                HighwayEventType.DeadLetterResolved,
                _operatorTarget,
                nodeId: "operator",
                requestId: _operatorDeadId,
                count: _operatorMoved,
                errorCode: _operatorAction);
        }
    }
}
