namespace Highway.Client.Engine;

/// <summary>
/// Outcome of trying to claim the right to run a handler for one delivery (feature 013).
/// </summary>
internal readonly struct IdempotencyClaim
{
    private IdempotencyClaim(IdempotencyOutcome outcome, byte[]? response)
    {
        Outcome = outcome;
        Response = response;
    }

    public IdempotencyOutcome Outcome { get; }

    /// <summary>The original response, present only when <see cref="Outcome"/> is Duplicate.</summary>
    public byte[]? Response { get; }

    public static IdempotencyClaim Claimed() => new(IdempotencyOutcome.Claimed, null);
    public static IdempotencyClaim InProgress() => new(IdempotencyOutcome.InProgress, null);
    public static IdempotencyClaim Duplicate(byte[] response) => new(IdempotencyOutcome.Duplicate, response);
}

internal enum IdempotencyOutcome
{
    /// <summary>Nobody has run this delivery. Run the handler.</summary>
    Claimed,

    /// <summary>
    /// Another attempt is running it now — or was running it when its process died.
    ///
    /// <para>The handler is <b>not</b> run and no reply is sent. Treating a stale marker as
    /// "probably crashed, run it again" would silently break the one promise
    /// <c>[Idempotent]</c> makes; the caller times out or the message is redelivered after
    /// the window instead.</para>
    /// </summary>
    InProgress,

    /// <summary>Already completed. Return the original response without re-running.</summary>
    Duplicate,
}
