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

    // 074: the send of each call, so a withdrawal (HW.CANCEL) waits for the HW.CALL it withdraws — sent first, a
    // cancel would find nothing and the request would then be queued for nobody.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _sends = new();
    private readonly IHighwayConnection _connection;

    public PendingCallRegistry(IHighwayConnection connection)
    {
        _connection = connection;
    }

    /// <summary>The monotonic clock deadlines are measured on (074). A seam for tests; never wall-clock time.</summary>
    internal Func<long> Timestamp { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

    /// <summary>Number of calls currently awaiting a reply (diagnostics/tests).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Registers a pending call and returns the awaitable response task.
    /// <paramref name="serviceName"/>/<paramref name="envelope"/> are the replay data
    /// (042-1b B-T5): after a failover the same call is re-driven, same requestId, to the
    /// new master — the caller holds the truth for "unanswered" (parent R4).
    /// </summary>
    public Task<Output> Register(
        string requestId, Type responseType, TimeSpan timeout, CancellationToken callerToken,
        string? serviceName = null, byte[]? envelope = null)
    {
        var call = new PendingCall
        {
            Tcs = new TaskCompletionSource<Output>(TaskCreationOptions.RunContinuationsAsynchronously),
            ResponseType = responseType,
            RegisteredAtUtc = DateTime.UtcNow,
            Timeout = timeout,
            DeadlineTimestamp = Timestamp() + (long)(timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency),
            ServiceName = serviceName,
            Envelope = envelope,
        };

        // Linked source: timeout timer OR caller cancellation — whichever fires first.
        call.LinkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        call.LinkedCts.CancelAfter(timeout);
        call.CancellationRegistration = call.LinkedCts.Token.Register(() => OnLinkedCancellation(call, requestId, callerToken));

        _sends[requestId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            // HW.RESULT takes the reply: read and removed in one command (5.0), so there is no
            // separate delete to forget and no window where two readers both collect it.
            payload = await _connection.TakeReplyAsync(requestId, ct).ConfigureAwait(false);
        }
        catch (HighwayTransportException)
        {
            return; // transient connection trouble — the next doorbell/sweep retries
        }

        if (payload is null)
            return; // slot not written yet (doorbell raced ahead) — sweep will pick it up

        if (!_pending.TryRemove(requestId, out var call))
        {
            // Our own call timed out / was cancelled between the guard above and here. The
            // take already removed the slot (005 Req 3 AC6), so there is nothing to clean up.
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
    /// 042-1b B-T5 / parent R4: after the connection converges on a new master, re-drive
    /// every pending call with its ORIGINAL request id. A call whose effect the new
    /// master already holds (acked + replicated before the failover) becomes a counted
    /// at-least-once duplicate; one it never saw executes fresh. First the slots are
    /// probed — the reply may already be waiting on the new master.
    /// </summary>
    public async Task ReplayPendingAsync(CancellationToken ct = default)
    {
        foreach (var (requestId, call) in _pending)
        {
            if (ct.IsCancellationRequested) return;

            // The reply may have been written and replicated before the transition.
            await TryCompleteFromSlotAsync(requestId, ct).ConfigureAwait(false);
            if (!_pending.ContainsKey(requestId))
                continue;

            if (call.ServiceName is null || call.Envelope is null)
                continue;   // registered without replay data (a legacy path) — its timeout bounds it

            // 074: re-sent with the time its caller has LEFT, never its original timeout; a call with none left is
            // over — answered here, not sent for a worker to run for nobody.
            var remaining = System.Diagnostics.Stopwatch.GetElapsedTime(Timestamp(), call.DeadlineTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                TryFail(requestId, BuildErrorResponse(call.ResponseType, StatusCodes.Status504GatewayTimeout, "CALL_TIMEOUT",
                    $"The call timed out after {call.Timeout.TotalSeconds:0.#}s with no reply (its time ran out during a failover)."));
                continue;
            }

            try
            {
                await _connection.CallAsync(call.ServiceName, requestId, call.Envelope, remaining, ct).ConfigureAwait(false);
            }
            catch (HighwayTransportException)
            {
                // Still converging; the next Converged raise (or the call's own timeout)
                // bounds it. Replay is idempotent to repeat — the id is stable.
            }
        }
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
            // 074 (D5): the caller gave up — withdraw the request if it is still queued, so no worker spends time on
            // it. Fire-and-forget and best effort: it never fails the caller's cancellation.
            if (call.ServiceName is { } service) _ = WithdrawAsync(service, requestId);
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

    /// <summary>The call's HW.CALL has been sent (or failed): a withdrawal may now follow it (074).</summary>
    public void MarkSent(string requestId)
    {
        if (_sends.TryRemove(requestId, out var sent)) sent.TrySetResult();
    }

    private async Task WithdrawAsync(string service, string requestId)
    {
        try
        {
            if (_sends.TryGetValue(requestId, out var sent))
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var withdrawn = await _connection.CancelCallAsync(service, requestId).ConfigureAwait(false);
            Observability.HighwayClientMetrics.RecordRpcCancel(withdrawn ? "withdrawn" : "not_waiting");
        }
        catch
        {
            Observability.HighwayClientMetrics.RecordRpcCancel("failed");
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

    private sealed class PendingCall : IDisposable
    {
        public required TaskCompletionSource<Output> Tcs { get; init; }
        public required Type ResponseType { get; init; }
        public required DateTime RegisteredAtUtc { get; init; }
        public required TimeSpan Timeout { get; init; }
        public required long DeadlineTimestamp { get; init; }   // 074: Stopwatch-based, for remaining-time re-sends
        public string? ServiceName { get; init; }
        public byte[]? Envelope { get; init; }
        public CancellationTokenSource LinkedCts { get; set; } = null!;
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public void Dispose()
        {
            CancellationRegistration.Dispose();
            LinkedCts.Dispose();
        }
    }
}
