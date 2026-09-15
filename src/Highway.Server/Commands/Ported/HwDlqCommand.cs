using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

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
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";

    private string _action = null!;
    private string _dlqName = null!;
    private string _liveName = null!;
    private int _count;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;

        var actionArg = input.Next(ref idx);
        if (actionArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.DLQ requires an action: PEEK, REQUEUE or PURGE");

        _action = Encoding.ASCII.GetString(actionArg).ToUpperInvariant();
        if (_action is not (ActionPeek or ActionRequeue or ActionPurge))
            return Fail(HighwayErrors.InvalidArg, $"unknown action '{_action}'; expected PEEK, REQUEUE or PURGE");

        var kindArg = input.Next(ref idx);
        if (kindArg.Length == 0)
            return Fail(HighwayErrors.InvalidArg, "HW.DLQ requires a target: SVC <service> or Q <queue>");

        var kind = Encoding.ASCII.GetString(kindArg).ToUpperInvariant();
        switch (kind)
        {
            case TargetService:
                if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out var service)) return false;
                _dlqName = HighwayNames.ServiceDeadLetter(service);
                _liveName = HighwayNames.ServiceQueue(service);
                break;
            case TargetQueue:
                if (!TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out var queue)) return false;
                _dlqName = HighwayNames.QueueDeadLetter(queue);
                _liveName = HighwayNames.Queue(queue);
                break;
            default:
                return Fail(HighwayErrors.InvalidArg, $"unknown target '{kind}'; accepted forms are SVC <service> or Q <queue>");
        }

        _count = ctx.Options.ReceiveDefaultCount;

        var keyword = input.Next(ref idx);
        if (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
            if (word != "COUNT")
                return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected COUNT");

            var value = input.Next(ref idx);
            if (value.Length == 0 || !int.TryParse(Encoding.ASCII.GetString(value), out var count) || count < 1)
                return Fail(HighwayErrors.InvalidCount, "COUNT must be a positive integer");
            if (count > ctx.Options.ReceiveMaxCount)
                return Fail(HighwayErrors.InvalidCount, $"COUNT {count} exceeds maximum {ctx.Options.ReceiveMaxCount}");
            _count = count;
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_dlqName);
        using var batch = ctx.Store.NewBatch();

        // Drain the DLQ once; the head window is [0.._count). _dlqName is already the full
        // logical name (Queue/Service dead-letter), so the list prefix is built directly.
        var all = ctx.Store.ListDrain(batch, HighwayKeyspace.ListPrefix(_dlqName));

        switch (_action)
        {
            case ActionPeek:
                // Non-destructive: restore everything in order, report the head window.
                foreach (var e in all) HwQSendCommand.PushTail(ctx.Store, batch, _dlqName, e);
                batch.Commit();
                WritePeek(writer, all.Take(_count).ToList());
                break;

            case ActionRequeue:
            {
                var moved = 0;
                for (var i = 0; i < all.Count; i++)
                {
                    if (i < _count)
                    {
                        DeadLetter.Decode(all[i], out _, out _, out _, out var original);
                        Envelope.DecodeRpcEntry(original, out var requestId, out var payload, out _);
                        var revived = Envelope.EncodeRpcEntry(requestId.ToArray(), payload.ToArray(), attempts: 0);
                        HwQSendCommand.PushTail(ctx.Store, batch, _liveName, revived);
                        moved++;
                    }
                    else
                    {
                        HwQSendCommand.PushTail(ctx.Store, batch, _dlqName, all[i]); // beyond the window: keep in DLQ
                    }
                }
                batch.Commit();
                writer.Integer(moved);
                break;
            }

            default: // PURGE
            {
                var removed = System.Math.Min(_count, all.Count);
                // Restore everything beyond the window; the head window is dropped.
                for (var i = removed; i < all.Count; i++)
                    HwQSendCommand.PushTail(ctx.Store, batch, _dlqName, all[i]);
                batch.Commit();
                writer.Integer(removed);
                break;
            }
        }
    }

    private static void WritePeek(RespWriter writer, List<byte[]> entries)
    {
        var rows = new List<IReadOnlyList<byte[]>>(entries.Count);
        foreach (var raw in entries)
        {
            DeadLetter.Decode(raw, out var ticks, out var attempts, out var reason, out var original);
            var fields = new List<byte[]>(12);

            void Add(string name, string value)
            {
                fields.Add(Encoding.UTF8.GetBytes(name));
                fields.Add(Encoding.UTF8.GetBytes(value));
            }

            Add("deadLetteredAt", new DateTime(ticks, DateTimeKind.Utc).ToString("O"));
            Add("attempts", attempts.ToString());
            Add("reason", Encoding.UTF8.GetString(reason));

            Envelope.DecodeRpcEntry(original, out var requestId, out var payload, out _);
            Add("requestId", Encoding.UTF8.GetString(requestId));
            fields.Add("payload"u8.ToArray());
            fields.Add(payload.ToArray());

            if (Envelope.TryGetFailureBlock(original, out var block, out _))
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
}
