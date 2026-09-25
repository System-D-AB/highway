using System.Globalization;
using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.IDEM — the <c>[Idempotent]</c> gate (5.0; replaces stock SET NX EX / GET / SET EX / DEL on
/// <c>hw:idem:{scope}:{id}</c>).
/// <code>
/// HW.IDEM BEGIN &lt;scope&gt; &lt;id&gt; &lt;windowSeconds&gt;          → CLAIMED | BUSY | [DONE, bytes]
/// HW.IDEM END   &lt;scope&gt; &lt;id&gt; &lt;windowSeconds&gt; &lt;bytes&gt;  → OK
/// HW.IDEM ABORT &lt;scope&gt; &lt;id&gt;                          → OK
/// </code>
/// <para>BEGIN claims the id for processing by writing an in-progress marker that lives for the window;
/// <c>BUSY</c> means another worker holds it (or held it and crashed — the marker expires with the window);
/// <c>DONE</c> returns the cached result of a completed run. END replaces the marker with the result for the
/// window; ABORT releases the claim so a retry can run. Markers and results are expiring keys, deleted by
/// the expiry sweep (065 T5) — before 3.0 they were never deleted.</para>
/// </summary>
internal sealed class HwIdemCommand : HighwayCommand
{
    private static readonly byte[] InProgress = " hw:in-progress"u8.ToArray();

    private enum Form { Begin, End, Abort }

    private Form _form;
    private string _scope = null!;
    private string _id = null!;
    private long _windowTicks;
    private byte[] _result = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var verb = Encoding.ASCII.GetString(input.Next(ref idx)).ToUpperInvariant();
        _form = verb switch
        {
            "BEGIN" => Form.Begin,
            "END" => Form.End,
            "ABORT" => Form.Abort,
            _ => (Form)(-1),
        };
        if ((int)_form < 0) return Fail(HighwayErrors.InvalidArg, "expected BEGIN, END or ABORT");
        if (!TryReadDerivedIdentifier(input, ref idx, "scope", ctx.Options.MaxIdentifierBytes, out _scope)) return false;
        if (!TryReadIdentifier(input, ref idx, "id", ctx.Options.MaxIdentifierBytes, out _id)) return false;
        if (_form == Form.Abort) return true;

        var window = Encoding.ASCII.GetString(input.Next(ref idx));
        if (!long.TryParse(window, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
            return Fail(HighwayErrors.InvalidArg, "windowSeconds must be a positive integer");
        _windowTicks = seconds * TimeSpan.TicksPerSecond;

        if (_form == Form.End && !TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _result))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var key = HighwayKeyspace.Kv($"idem:{_scope}:{_id}");
        using var _lock = ctx.Locks.Lock("idem:" + _scope + ":" + _id);
        using var batch = ctx.Store.NewBatch();
        long? floor = null;

        switch (_form)
        {
            case Form.Begin:
            {
                using var snap = ctx.Store.Snapshot();
                var existing = ctx.Store.GetLive(snap, key, ctx.NowTicks);
                if (existing is null)
                {
                    ExpirySupport.SetExpiring(ctx, batch, key, InProgress, ctx.NowTicks + _windowTicks);
                    floor = ExpirySupport.Sweep(ctx, batch);
                    batch.Commit();
                    writer.SimpleString("CLAIMED");
                }
                else if (existing.AsSpan().SequenceEqual(InProgress))
                {
                    writer.SimpleString("BUSY");
                }
                else
                {
                    writer.BulkStringArray("DONE"u8.ToArray(), existing);
                }
                break;
            }
            case Form.End:
                ExpirySupport.SetExpiring(ctx, batch, key, _result, ctx.NowTicks + _windowTicks);
                floor = ExpirySupport.Sweep(ctx, batch);
                batch.Commit();
                writer.SimpleString("OK");
                break;
            default:
                ctx.Store.Delete(batch, key);
                batch.Commit();
                writer.SimpleString("OK");
                break;
        }
        ExpirySupport.AfterCommit(ctx, floor);
    }
}
