using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// Shared base for all HW.* transaction procedures.
///
/// <para><b>Validation pattern (feature 004.1):</b> <c>Prepare</c> cannot write
/// RESP output in Garnet — returning <c>false</c> surfaces only the literal
/// <c>ERR Transaction failed.</c>, which is indistinguishable from a transient
/// watch-conflict abort. Validation therefore never fails <c>Prepare</c>:
/// errors are <em>captured</em> here and rendered by <c>Main</c>.</para>
///
/// <para>Contract for derived commands:</para>
/// <list type="number">
///   <item><c>Prepare</c> returns <c>true</c> in every case. On validation
///         failure it adds NO keys (nothing locked, watched, or mutated).</item>
///   <item><c>Main</c> calls <see cref="TryWriteError"/> as its FIRST statement
///         and returns when it yields <c>true</c>.</item>
///   <item><c>Finalize</c> returns early when <see cref="Failed"/> — a rejected
///         command must never ring a doorbell.</item>
/// </list>
///
/// <para>Running a transaction that locks zero keys is safe (verified in the
/// 004.1 Task 1 spike): <c>LockAllKeys</c> over an empty set is a no-op and
/// watch validation with no registered watches succeeds.</para>
/// </summary>
internal abstract partial class HighwayCommandBase : CustomTransactionProcedure
{
    private string? _error;
    private string? _errorCode;

    /// <summary>True when validation already failed; callers must stop.</summary>
    protected bool Failed => _error is not null;

    /// <summary>
    /// The error code captured by <see cref="Fail"/>, or null when validation
    /// passed. Surfaced so <c>Finalize</c> can record <em>why</em> a command was
    /// rejected — a flight recorder that only shows successes is worth little
    /// (feature 002).
    /// </summary>
    protected string? FailureCode => _errorCode;

    /// <summary>
    /// Resets per-invocation state, then runs the command's own Prepare.
    ///
    /// <para><b>Sealed on purpose.</b> Garnet caches one procedure instance per
    /// session (<c>CustomCommandManagerSession.sessionTransactionProcMap</c>) and
    /// reuses it for every invocation of that command on that connection, so
    /// instance fields survive between calls. Before this was sealed, a single
    /// validation failure left <c>_error</c> set and every subsequent invocation
    /// on the same connection replayed that stale error — a valid command
    /// answering with the previous command's rejection.</para>
    ///
    /// <para>Resetting here rather than asking each command to remember makes the
    /// bug structurally impossible rather than fixed once.</para>
    /// </summary>
    public sealed override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        _error = null;
        _errorCode = null;
        ResetState();

        return PrepareCore(api, ref procInput);
    }

    /// <summary>The command's own preparation. Never called with stale base state.</summary>
    protected abstract bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
        where TGarnetReadApi : IGarnetReadApi;

    /// <summary>
    /// Clears any command-specific field that is not unconditionally assigned in
    /// <see cref="PrepareCore"/>. Anything conditionally assigned — a captured
    /// result, a parsed form, a cached lookup — must be reset here, or it leaks
    /// into the next invocation on the same connection.
    /// </summary>
    protected virtual void ResetState() { }

    /// <summary>
    /// Captures a validation error. The first failure wins; later calls are
    /// ignored so the client sees one stable diagnosis.
    /// Always returns <c>false</c> so call sites read naturally:
    /// <c>if (!TryRead...) return true;</c>
    /// </summary>
    protected bool Fail(string code, string detail)
    {
        if (_error is null)
        {
            _error = HighwayErrors.Format(code, detail);
            _errorCode = code;
        }
        return false;
    }

    /// <summary>
    /// Reads the next argument and validates it as an identifier (non-empty,
    /// ≤ <paramref name="maxBytes"/> bytes, no C0 control characters, no DEL)
    /// on the raw bytes BEFORE any string decode — no key is ever derived from
    /// an invalid value.
    /// </summary>
    /// <returns>True when valid; false after capturing <c>HW_INVALID_ARG</c>.</returns>
    protected bool TryReadIdentifier(
        ref CustomProcedureInput input, ref int idx, string name, int maxBytes, out string value)
    {
        var arg = GetNextArg(ref input, ref idx);
        var raw = arg.ReadOnlySpan;

        if (!Identifier.IsValid(raw, maxBytes))
        {
            value = null!;
            return Fail(HighwayErrors.InvalidArg,
                $"{name} is blank, contains a control character, or exceeds {maxBytes} bytes");
        }

        value = Encoding.UTF8.GetString(raw);
        return true;
    }

    /// <summary>
    /// Overload that also surfaces the validated identifier's raw bytes, for
    /// call sites that need byte fidelity (doorbell payloads, byte-wise
    /// matching in <c>HW.ACK</c>).
    /// </summary>
    protected bool TryReadIdentifier(
        ref CustomProcedureInput input, ref int idx, string name, int maxBytes,
        out string value, out byte[] rawBytes)
    {
        var arg = GetNextArg(ref input, ref idx);
        var raw = arg.ReadOnlySpan;

        if (!Identifier.IsValid(raw, maxBytes))
        {
            value = null!;
            rawBytes = [];
            return Fail(HighwayErrors.InvalidArg,
                $"{name} is blank, contains a control character, or exceeds {maxBytes} bytes");
        }

        value = Encoding.UTF8.GetString(raw);
        rawBytes = raw.ToArray();
        return true;
    }

    /// <summary>
    /// Reads the next argument as an opaque payload, enforcing the size cap.
    /// </summary>
    /// <returns>True when within the cap; false after capturing <c>HW_PAYLOAD_TOO_LARGE</c>.</returns>
    protected bool TryReadPayload(
        ref CustomProcedureInput input, ref int idx, int maxBytes, out byte[] value)
    {
        var arg = GetNextArg(ref input, ref idx);
        var raw = arg.ReadOnlySpan;

        if (raw.Length > maxBytes)
        {
            value = [];
            return Fail(HighwayErrors.PayloadTooLarge, $"{raw.Length} > {maxBytes}");
        }

        value = raw.ToArray();
        return true;
    }

    /// <summary>
    /// Writes the captured validation error, if any. Must be called as the FIRST
    /// statement of every <c>Main</c> override; stop when it returns <c>true</c>.
    /// </summary>
    protected bool TryWriteError(ref MemoryResult<byte> output)
    {
        if (_error is null)
            return false;

        WriteError(ref output, _error);
        return true;
    }

    /// <summary>
    /// Writes a RESP integer reply. Lived as a private copy in <c>HwDlqCommand</c> and
    /// <c>HwQAckCommand</c> until <c>HW.FAIL</c> would have made it a third (015 T3).
    /// </summary>
    protected static unsafe void WriteInteger(ref MemoryResult<byte> output, int value)
    {
        const int len = 4; // :N

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(len);
        output.Length = len;
        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteInt32(value, ref curr, ptr + len);
        }
    }
}
