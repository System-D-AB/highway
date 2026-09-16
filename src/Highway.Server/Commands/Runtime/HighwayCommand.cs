using System.Globalization;
using System.Text;
using Highway.Server.Internal;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// The base for a ported <c>HW.*</c> command (039 T2, the pattern all 22 others follow).
/// It replaces Garnet's <c>CustomTransactionProcedure</c> (Prepare/Main/Finalize) with one
/// method — <see cref="Execute"/> — that runs the 037 §3 shape:
///
/// <list type="number">
///   <item>parse + validate the arguments (capturing an error, never throwing — the
///         validate-in-Main pattern preserved);</item>
///   <item>if validation failed, render the error and stop — no lock, no batch, no
///         side effect;</item>
///   <item>otherwise take the per-name lock, open a snapshot, decide, stage one batch,
///         commit once (037 R4);</item>
///   <item>on a successful, non-refused run, do post-commit side effects — recorder +
///         doorbell (037 R7, guarded exactly as Finalize was).</item>
/// </list>
///
/// <para>The clock is read once (<see cref="CommandContext.NowTicks"/>) before the batch;
/// no command reads it again (037 R5.1). Errors stay data — a rejection is a RESP error
/// reply, never a thrown exception to the caller (037 design, "errors are data").</para>
/// </summary>
internal abstract class HighwayCommand
{
    private string? _error;
    private string? _errorCode;

    /// <summary>True once validation captured an error — callers stop.</summary>
    protected bool Failed => _error is not null;

    /// <summary>The captured error code, or null — so post-commit can record why (feature 002).</summary>
    protected string? FailureCode => _errorCode;

    /// <summary>
    /// Runs the command end-to-end against <paramref name="ctx"/>, reading
    /// <paramref name="input"/> and writing the reply to <paramref name="writer"/>. This is
    /// the one entry point the dispatcher (and 039's tests) call — no transport involved.
    /// </summary>
    public void Execute(CommandContext ctx, CommandInput input, RespWriter writer)
    {
        _error = null;
        _errorCode = null;

        // 1–2: parse + validate. A failure captures the error; Run is skipped — but
        // AfterCommit still runs, exactly as Garnet's Finalize did on a rejected command:
        // the commands record their event with errorCode = FailureCode and their own
        // `if (Failed) return;` guard stops doorbells and success-only effects. (Found by
        // the 040 fixture swap: rejected commands vanished from the flight recorder.)
        if (!Parse(ctx, input) || Failed)
        {
            writer.Error(_error!);
            AfterCommit(ctx);
            return;
        }

        // 3: the work — lock, snapshot, batch, commit — is the command's own Run.
        try
        {
            Run(ctx, writer);
        }
        catch (StorageFormatException ex)
        {
            writer.Error(HighwayErrors.StorageFormatError(ex.Message));
            return;
        }
        catch (Exception ex)
        {
            writer.Error(HighwayErrors.InternalError(ex.Message));
            return;
        }

        // 4: post-commit side effects (recorder + doorbell), guarded by the command.
        AfterCommit(ctx);
    }

    /// <summary>
    /// Parses and validates arguments. Returns false (or calls <see cref="Fail"/>) on a bad
    /// argument; returns true when ready to <see cref="Run"/>. Must not touch the store.
    /// </summary>
    protected abstract bool Parse(CommandContext ctx, CommandInput input);

    /// <summary>
    /// The transactional work: take the per-name lock, open a snapshot, decide, stage one
    /// batch, commit. Writes the success reply. Runs only when <see cref="Parse"/> succeeded.
    /// </summary>
    protected abstract void Run(CommandContext ctx, RespWriter writer);

    /// <summary>
    /// Post-run side effects — flight-recorder writes and doorbell rings. Default does
    /// nothing. Runs after a successful <see cref="Run"/> <b>and</b> after a validation
    /// rejection (the Garnet Finalize contract): overrides record first (with
    /// <see cref="FailureCode"/>), then guard success-only effects with
    /// <c>if (Failed) return;</c> — never ring a doorbell on a rejected run (037 R7).
    /// </summary>
    protected virtual void AfterCommit(CommandContext ctx) { }

    // ---- validation helpers (mirror the old HighwayCommandBase surface) ------

    /// <summary>Captures a validation error. First failure wins. Always returns false.</summary>
    protected bool Fail(string code, string detail)
    {
        if (_error is null)
        {
            _error = HighwayErrors.Format(code, detail);
            _errorCode = code;
        }
        return false;
    }

    /// <summary>Reads and validates the next argument as an identifier (no '@').</summary>
    protected bool TryReadIdentifier(CommandInput input, ref int idx, string name, int maxBytes, out string value)
        => TryReadIdentifier(input, ref idx, name, maxBytes, out value, out _);

    /// <summary>Identifier read that also surfaces the raw bytes (doorbell payloads, byte-wise ack matching).</summary>
    protected bool TryReadIdentifier(
        CommandInput input, ref int idx, string name, int maxBytes, out string value, out byte[] rawBytes)
    {
        var raw = input.Next(ref idx);
        if (!Identifier.IsValid(raw, maxBytes))
        {
            value = null!;
            rawBytes = [];
            Fail(HighwayErrors.InvalidArg, IdentifierErrorDetail(raw, name, maxBytes));
            return false;
        }
        value = Encoding.UTF8.GetString(raw);
        rawBytes = raw.ToArray();
        return true;
    }

    /// <summary>Reads an identifier that may contain '@' (derived group queues, feature 018).</summary>
    protected bool TryReadDerivedIdentifier(CommandInput input, ref int idx, string name, int maxBytes, out string value)
    {
        var raw = input.Next(ref idx);
        if (!Identifier.IsValidAllowingAt(raw, maxBytes))
        {
            value = null!;
            Fail(HighwayErrors.InvalidArg,
                raw.IsEmpty ? $"{name} is blank"
                    : $"{name} is blank, contains a control character, or exceeds {maxBytes} bytes");
            return false;
        }
        value = Encoding.UTF8.GetString(raw);
        return true;
    }

    /// <summary>Reads the next argument as an opaque payload, enforcing the size cap.</summary>
    protected bool TryReadPayload(CommandInput input, ref int idx, int maxBytes, out byte[] value)
    {
        var raw = input.Next(ref idx);
        if (raw.Length > maxBytes)
        {
            value = [];
            Fail(HighwayErrors.PayloadTooLarge, $"{raw.Length} > {maxBytes}");
            return false;
        }
        value = raw.ToArray();
        return true;
    }

    /// <summary>Reads the next argument as an unsigned integer (042 replication sequences).</summary>
    protected bool TryReadUInt64(CommandInput input, ref int idx, string name, out ulong value)
    {
        var raw = input.Next(ref idx);
        value = 0;
        if (raw.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, $"{name} is blank");
        var text = Encoding.UTF8.GetString(raw);
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return Fail(HighwayErrors.InvalidArg, $"{name} is not an unsigned integer");
        return true;
    }

    /// <summary>The specific rejection reason (control char, reserved '@', blank, or length).</summary>
    protected static string IdentifierErrorDetail(ReadOnlySpan<byte> raw, string name, int maxBytes)
    {
        if (raw.IsEmpty) return $"{name} is blank";
        if (Identifier.ContainsAtSign(raw))
            return $"{name} contains '@' which is reserved for internal group-queue routing (feature 018)";
        return $"{name} is blank, contains a control character, or exceeds {maxBytes} bytes";
    }
}
