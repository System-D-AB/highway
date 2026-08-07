using System.Collections.Concurrent;
using System.Text.Json;
using Highway.Abstractions;
using Highway.Client.Wire;

namespace Highway.Client.Engine;

/// <summary>
/// Correlates in-flight <c>ExecuteAsync</c> calls with their reply slots.
///
/// <para>Completion paths: doorbell → <see cref="TryCompleteFromSlotAsync"/>;
/// dropped doorbell → the backstop sweep GETs aged slots; timeout → 504 data;
/// caller cancellation → <see cref="OperationCanceledException"/> (the one
/// intentional exception path — errors are otherwise data).</para>
///
/// <para>Race rule: whichever path removes the entry from the dictionary first
/// completes it; the loser no-ops. Timed-out/cancelled calls that get a late
/// reply still clean the slot up (DEL) without surfacing anything.</para>
/// </summary>
internal sealed class PendingCallRegistry
{
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new();
    private readonly IHighwayConnection _connection;

    public PendingCallRegistry(IHighwayConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Number of calls currently awaiting a reply (diagnostics/tests).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Registers a pending call and returns the awaitable response task.
    /// </summary>
    public Task<Output> Register(string requestId, Type responseType, TimeSpan timeout, CancellationToken callerToken)
    {
        var call = new PendingCall
        {
            Tcs = new TaskCompletionSource<Output>(TaskCreationOptions.RunContinuationsAsynchronously),
            ResponseType = responseType,
            RegisteredAtUtc = DateTime.UtcNow,
            Timeout = timeout,
        };

        // Linked source: timeout timer OR caller cancellation — whichever fires first.
        call.LinkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        call.LinkedCts.CancelAfter(timeout);
        call.CancellationRegistration = call.LinkedCts.Token.Register(() => OnLinkedCancellation(call, requestId, callerToken));

        if (!_pending.TryAdd(requestId, call))
        {
            // A duplicate requestId would corrupt correlation; GUIDs make this unreachable.
            call.Dispose();
            throw new InvalidOperationException($"Duplicate pending call id '{requestId}'.");
        }

        return call.Tcs.Task;
    }

    /// <summary>
    /// Attempts to complete the call identified by <paramref name="requestId"/>
    /// from its reply slot (doorbell path and sweep path).
    /// </summary>
    public async Task TryCompleteFromSlotAsync(string requestId, CancellationToken ct = default)
    {
        // hw:door:rep is node-global: every engine connected to the server sees
        // EVERY reply doorbell, not just its own. Only the node that issued the
        // request may read or delete its slot — deleting a foreign slot destroys
        // another caller's reply and hangs it until its call timeout. This guard
        // also keeps reply-slot GET traffic O(1) per reply instead of O(nodes).
        if (!_pending.ContainsKey(requestId))
            return;

        byte[]? payload;
        try
        {
            payload = await _connection.GetReplySlotAsync(requestId, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException)
        {
            return; // transient connection trouble — the next doorbell/sweep retries
        }

        if (payload is null)
            return; // slot not written yet (doorbell raced ahead) — sweep will pick it up

        if (!_pending.TryRemove(requestId, out var call))
        {
            // Our own call timed out / was cancelled between the guard above and
            // here. The slot is still ours, so clean it up (005 Req 3 AC6).
            await SafeDeleteSlotAsync(requestId).ConfigureAwait(false);
            return;
        }

        try
        {
            var envelope = HighwayJson.DecodeEnvelope(payload);
            if (HighwayJson.DeserializeBody(envelope, call.ResponseType) is Output response)
            {
                response.StatusCode ??= StatusCodes.Status200OK;
                call.Tcs.TrySetResult(response);
            }
            else
            {
                call.Tcs.TrySetResult(BuildErrorResponse(
                    call.ResponseType,
                    StatusCodes.Status502BadGateway,
                    "BAD_REPLY",
                    "The server returned a reply that could not be read as the expected response type."));
            }
        }
        catch (JsonException)
        {
            call.Tcs.TrySetResult(BuildErrorResponse(
                call.ResponseType,
                StatusCodes.Status502BadGateway,
                "BAD_REPLY",
                "The server returned a malformed reply envelope."));
        }
        finally
        {
            call.Dispose();
            await SafeDeleteSlotAsync(requestId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes a pending call with a failure result before any reply could arrive
    /// (e.g. the send itself failed permanently). No-op when the entry is gone
    /// (a reply or timeout already won the race).
    /// </summary>
    public void TryFail(string requestId, Output failure)
    {
        if (!_pending.TryRemove(requestId, out var call))
            return;

        call.Dispose();
        call.Tcs.TrySetResult(failure);
    }

    /// <summary>
    /// Backstop sweep: GETs the slots of all calls older than <paramref name="minAge"/>
    /// so a dropped doorbell costs latency, never a hung call.
    /// </summary>
    public async Task SweepAsync(TimeSpan minAge, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - minAge;
        foreach (var (requestId, call) in _pending)
        {
            if (ct.IsCancellationRequested) return;
            if (call.RegisteredAtUtc <= cutoff)
                await TryCompleteFromSlotAsync(requestId, ct).ConfigureAwait(false);
        }
    }

    private void OnLinkedCancellation(PendingCall call, string requestId, CancellationToken callerToken)
    {
        // Dictionary-remove-before-complete: the race gate against late replies.
        if (!_pending.TryRemove(requestId, out _))
            return; // a reply already won

        call.Dispose();

        if (callerToken.IsCancellationRequested)
        {
            call.Tcs.TrySetException(new OperationCanceledException(callerToken));
        }
        else
        {
            call.Tcs.TrySetResult(BuildErrorResponse(
                call.ResponseType,
                StatusCodes.Status504GatewayTimeout,
                "CALL_TIMEOUT",
                $"The call timed out after {call.Timeout.TotalSeconds:0.#}s with no reply."));
        }
    }

    /// <summary>
    /// Constructs an error response instance of the caller's TResponse
    /// (scan-time validation guarantees a public parameterless constructor).
    /// </summary>
    internal static Output BuildErrorResponse(Type responseType, int statusCode, string code, string message)
    {
        var response = (Output)Activator.CreateInstance(responseType)!;
        response.StatusCode = statusCode;
        response.Error = new ErrorDetail { Code = code, Message = message };
        return response;
    }

    private async Task SafeDeleteSlotAsync(string requestId)
    {
        try
        {
            await _connection.DeleteReplySlotAsync(requestId).ConfigureAwait(false);
        }
        catch (HighwayTransportException)
        {
            // Slot leaks are bounded by the server-side TTL.
        }
    }

    private sealed class PendingCall : IDisposable
    {
        public required TaskCompletionSource<Output> Tcs { get; init; }
        public required Type ResponseType { get; init; }
        public required DateTime RegisteredAtUtc { get; init; }
        public required TimeSpan Timeout { get; init; }
        public CancellationTokenSource LinkedCts { get; set; } = null!;
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public void Dispose()
        {
            CancellationRegistration.Dispose();
            LinkedCts.Dispose();
        }
    }
}
