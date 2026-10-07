using System.Collections.Concurrent;
using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Shared core of the two single-message worker loops — <see cref="RpcWorkerLoop"/> and
/// <see cref="QueueWorkerLoop"/>. Both wait for a wake, drain their source to nil under a
/// concurrency gate, and track in-flight work so shutdown can drain rather than abort. Only
/// <b>what</b> they claim and <b>what</b> they do with it differs.
///
/// <para><b>The claim/gate ordering is the point (015 T0).</b> The slot is taken
/// <i>before</i> the claim, because claiming starts the server-side lease: a message claimed
/// while the gate is full has its clock running on a node that cannot begin it, and if the
/// wait outlives the lease the server redelivers it elsewhere. Putting the ordering here
/// means a third loop cannot get it wrong.</para>
///
/// <para><b>Covers all worker loops.</b> <see cref="RpcWorkerLoop"/>,
/// <see cref="QueueWorkerLoop"/> and <see cref="SubscriptionWorkerLoop"/> all derive from
/// this class. They share the concurrency gate, the claim ordering, and the failure-reporting
/// seam; only <b>what</b> they claim and <b>what</b> they do with it differs.</para>
///
/// <para>Retry policy (004.1): the connection already retries the transient class with
/// bounded backoff. A surfaced permanent error is logged and the drain pass ends — never
/// retried, so the loop cannot spin on poisoned input. The loop itself never dies.</para>
/// </summary>
internal abstract class SingleMessageWorkerLoop
{
    protected readonly IHighwayConnection Connection;
    protected readonly ServiceExecutor Executor;
    protected readonly string NodeName;
    protected readonly ILogger Logger;

    /// <summary>The single seam through which a handler exception is reported (015 T2).</summary>
    protected readonly FailureReporter Reporter;

    private readonly int _concurrency;
    private readonly SemaphoreSlim _gate;
    private readonly LoopWake _wake;
    private readonly List<Task> _inflight = [];

    /// <summary>
    /// Window used when <c>[Idempotent]</c> names none. Matches the server's default
    /// <c>ReplySlotTtl</c>: a response nobody can collect any more is a response there is
    /// no point deduplicating against.
    /// </summary>
    internal static readonly TimeSpan DefaultIdempotencyWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deduplication window for this loop's contract, or <see langword="null"/> when the
    /// contract carries no <c>[Idempotent]</c> attribute — in which case every delivery takes
    /// exactly the path it always did.
    /// </summary>
    protected TimeSpan? IdempotencyWindow { get; }

    private readonly TimeSpan _renewalInterval;

    /// <summary>The handler cap (019; enforced on the handler since 074 Part B). Zero disables cap and renewal.</summary>
    protected readonly TimeSpan MaxProcessingTime;

    private readonly TimeSpan _abandonGrace;
    private readonly int _maxAbandonedHandlers;

    // Abandoned invocations, keyed by the invocation's own cancellation token — never by message id: a
    // message redelivered while its abandoned copy still hangs is a NEW invocation and must settle normally.
    private readonly ConcurrentDictionary<CancellationToken, (string Id, DateTime Since)> _abandoned = new();
    private DateTime _lastAbandonedLimitLog = DateTime.MinValue;

    /// <summary>
    /// Largest span a timer can hold (CancelAfter / Task.Delay take at most uint.MaxValue - 1 ms). A cap
    /// above it is treated as this — about 24 days, i.e. practically "no cap" — instead of throwing before
    /// the slot is protected.
    /// </summary>
    private static readonly TimeSpan MaxTimerSpan = TimeSpan.FromDays(24);

    protected SingleMessageWorkerLoop(
        Type contractType,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default,
        TimeSpan abandonGrace = default,
        int maxAbandonedHandlers = 0)
    {
        _renewalInterval = renewalInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : renewalInterval;
        MaxProcessingTime = maxProcessingTime > MaxTimerSpan ? MaxTimerSpan : maxProcessingTime;
        _abandonGrace = abandonGrace <= TimeSpan.Zero ? TimeSpan.FromSeconds(30)
            : abandonGrace > MaxTimerSpan ? MaxTimerSpan : abandonGrace;
        Connection = connection;
        Executor = executor;
        NodeName = nodeName;
        Logger = logger;
        Reporter = new FailureReporter(connection, logger);
        _concurrency = Math.Max(1, concurrency);
        _maxAbandonedHandlers = maxAbandonedHandlers > 0 ? maxAbandonedHandlers : _concurrency;
        _wake = wake;
        _gate = new SemaphoreSlim(_concurrency, _concurrency);

        var idempotent = contractType
            .GetCustomAttributes(typeof(IdempotentAttribute), inherit: false)
            .FirstOrDefault() as IdempotentAttribute;

        IdempotencyWindow = idempotent is null
            ? null
            : idempotent.Window ?? DefaultIdempotencyWindow;
    }

    public LoopWake Wake => _wake;

    /// <summary>Identifies what this loop serves, for logging and for failure reporting.</summary>
    protected abstract FailureTarget Target { get; }

    /// <summary>
    /// What happens to a message whose handler threw. The two loops answer differently — an
    /// RPC caller may be waiting on a reply that will never come, a queue message is simply
    /// redelivered — and that difference is the part an operator reading the log needs.
    /// </summary>
    protected abstract string FailureDisposition { get; }

    private string TargetName => Target.Name;
    private string TargetKind => Target.Kind;

    /// <summary>
    /// Takes the next message, or <see langword="null"/> when the source is drained. Called
    /// only while a concurrency slot is held.
    /// </summary>
    protected abstract Task<ClaimedWork?> ClaimAsync(CancellationToken stopToken);

    /// <summary>
    /// Handles one claimed message; settles it with <see cref="ClaimedWork.Token"/>. Exceptions are caught and
    /// logged by the base. <paramref name="ct"/> is this invocation's own token: cancelled at
    /// <c>MaxProcessingTime</c> or at shutdown, and the key <see cref="IsAbandoned"/> checks before settling.
    /// </summary>
    protected abstract Task ProcessAsync(ClaimedWork work, CancellationToken ct);

    /// <summary>
    /// The handler stopped because <c>MaxProcessingTime</c> cancelled it (074 Part B): queues and subscribers
    /// report a <c>ProcessingTimeExceeded</c> failure (not acknowledged), RPC answers 504.
    /// </summary>
    protected abstract Task OnProcessingTimeExceededAsync(ClaimedWork work, CancellationToken workToken);

    /// <summary>
    /// Two-token contract: <paramref name="stopToken"/> stops claiming new work;
    /// <paramref name="workToken"/> governs in-flight processing and is only cancelled after
    /// the drain timeout — so graceful shutdown drains rather than aborts.
    /// </summary>
    public async Task RunAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        Logger.LogInformation("Worker loop started for {Kind} '{Target}' (concurrency {Concurrency})",
            TargetKind, TargetName, _concurrency);

        // Initial drain on startup before waiting for wake signal
        try
        {
            await DrainAsync(selfHealTimeout, stopToken, workToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped during the first pass. Fall through to the in-flight wait below — returning here
            // reported the loop stopped while handlers claimed in this pass were still running (074 Part B).
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Initial drain failed for {Kind} '{Target}'", TargetKind, TargetName);
        }

        while (!stopToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(selfHealTimeout, stopToken).ConfigureAwait(false);
            if (stopToken.IsCancellationRequested) break;

            try
            {
                await DrainAsync(selfHealTimeout, stopToken, workToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Worker loop for {Kind} '{Target}' hit an unexpected error during drain; continuing",
                    TargetKind, TargetName);
            }
        }

        // Keep the loop "active" until every spawned processing task finishes — this is what
        // the engine's drain wait observes.
        Task[] snapshot;
        lock (_inflight) snapshot = [.. _inflight];
        try
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // Individual tasks log their own failures.
        }

        Logger.LogInformation("Worker loop stopped for {Kind} '{Target}'", TargetKind, TargetName);
    }

    private async Task DrainAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            // 074 Part B: while MaxAbandonedHandlers abandoned handlers still run, claim nothing more.
            if (_abandoned.Count >= _maxAbandonedHandlers)
            {
                LogAbandonedLimitReachedOncePerMinute();
                await _wake.WaitAsync(selfHealTimeout, stopToken).ConfigureAwait(false);
                if (stopToken.IsCancellationRequested) return;
                continue;
            }

            // Slot first, then claim. See the class remarks: claiming starts the lease, so a
            // claim taken while the gate is full is a lease running on work that cannot start.
            await _gate.WaitAsync(stopToken).ConfigureAwait(false);

            // The slot may have been freed by an abandonment: re-check the bound before claiming.
            if (_abandoned.Count >= _maxAbandonedHandlers)
            {
                _gate.Release();
                LogAbandonedLimitReachedOncePerMinute();
                await _wake.WaitAsync(selfHealTimeout, stopToken).ConfigureAwait(false);
                if (stopToken.IsCancellationRequested) return;
                continue;
            }

            ClaimedWork? item;
            try
            {
                item = await ClaimAsync(stopToken).ConfigureAwait(false);
            }
            catch (HighwayTransientException ex)
            {
                // Bounded retries already exhausted in the connection. Back off, then let the
                // next wake retry. This is the retryable class only.
                _gate.Release();
                Logger.LogWarning(ex, "Transient abort claiming from {Kind} '{Target}'; will retry on next wake",
                    TargetKind, TargetName);
                await Task.Delay(100, stopToken).ConfigureAwait(false);
                return;
            }
            catch (HighwayTransportException ex)
            {
                // Permanent failure: log and drop this drain pass. Never retry in a tight loop.
                _gate.Release();
                Logger.LogError(ex, "Permanent error claiming from {Kind} '{Target}'; ending drain pass",
                    TargetKind, TargetName);
                return;
            }
            catch
            {
                // Anything else must not leak the slot, or the loop starves itself.
                _gate.Release();
                throw;
            }

            if (item is null)
            {
                _gate.Release();
                return; // drained to nil
            }

            var work = item.Value;

            // Process on the thread pool so a synchronous-heavy handler cannot stall the
            // drain. The slot is already held and is released by ProcessAndReleaseAsync.
            var task = Task.Run(() => ProcessAndReleaseAsync(work, workToken), CancellationToken.None);
            lock (_inflight)
            {
                _inflight.Add(task);
                if (_inflight.Count > 64)
                    _inflight.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Renews this message's lease while its handler runs (019), until <paramref name="stop"/> fires: when the
    /// handler has stopped, or when it is abandoned (074 Part B).
    ///
    /// <para><b>Why it is still bounded.</b> Unbounded renewal deletes lease recovery: a handler stuck in a
    /// deadlock would hold its message forever — never redelivered, never dead-lettered, never visible. The
    /// bound is the handler's: it is cancelled at <c>MaxProcessingTime</c> and abandoned <c>AbandonGrace</c>
    /// later if it ignores that, which stops this renewal and lets lease recovery redeliver the message.
    /// Renewing past the cap until then is what keeps a second worker from starting the message while the
    /// first copy is still being waited for.</para>
    ///
    /// <para><b>A failed renewal is swallowed</b> (C7.1). A mechanism that exists to protect
    /// delivery must never be able to break it; the ordinary sweep still recovers the message.
    /// Individual renewals are not recorded — at this interval they would flood the recorder
    /// with the least interesting thing it could hold.</para>
    /// </summary>
    private async Task RenewWhileRunningAsync(ClaimedWork work, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_renewalInterval, stop).ConfigureAwait(false);
                await Connection.TouchAsync(work.Token, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The handler finished, was abandoned, or the engine is stopping. None is a problem.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Could not renew the lease for '{MessageId}' on {Kind} '{Target}'; the message is " +
                "unaffected and the ordinary sweep still recovers it, but a slow handler may now " +
                "be duplicated",
                work.Id, TargetKind, TargetName);
        }
    }

    private async Task ProcessAndReleaseAsync(ClaimedWork work, CancellationToken workToken)
    {
        if (MaxProcessingTime <= TimeSpan.Zero)
        {
            // Zero disables renewal and the cap, restoring pre-019 behaviour exactly.
            try
            {
                await ProcessAsync(work, workToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Engine stopping mid-message; server lease recovery handles redelivery.
            }
            catch (Exception ex)
            {
                await Reporter.ReportAsync(Target, work, ex, FailureDisposition, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
            return;
        }

        // Renewal is on by default (019 R2.2): a handler outliving the lease is a correctness failure with
        // silent duplicate execution. It continues past MaxProcessingTime until the handler stops or is
        // abandoned (074 Part B), so no second worker is handed a message whose first copy is still running.
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        var renewal = RenewWhileRunningAsync(work, renewalStop.Token);

        // The handler's token: cancelled at MaxProcessingTime or at shutdown. Owned by this method unless
        // the invocation is abandoned, when the abandoned handler's continuation disposes it.
        var cap = CancellationTokenSource.CreateLinkedTokenSource(workToken);

        // The abandonment timer is deliberately NOT linked to the shutdown token: stopping the engine cancels
        // the handler (through `cap`), it does not declare it abandoned. It is cancelled in `finally`, so no
        // timer outlives the message it guards.
        using var abandonTimer = new CancellationTokenSource();
        var abandoned = false;

        // Loud, and once, at the moment the cap is reached — whether the handler then stops or not.
        var started = DateTime.UtcNow;
        var activity = System.Diagnostics.Activity.Current;
        using var capReached = cap.Token.Register(() =>
        {
            if (workToken.IsCancellationRequested) return;   // shutdown, not the cap
            try
            {
                Observability.HighwayActivity.MarkProcessingCapExceeded(activity, work.Id, DateTime.UtcNow - started);
                Logger.LogWarning(
                    "Message '{MessageId}' on {Kind} '{Target}' reached MaxProcessingTime ({Cap}); its handler has " +
                    "been cancelled. Its lease is renewed until the handler stops, or until it is abandoned after " +
                    "AbandonGrace ({Grace}) if it ignores the cancellation. Either the handler is hung, or this work " +
                    "should be chunked rather than run in one message.",
                    work.Id, TargetKind, TargetName, MaxProcessingTime, _abandonGrace);
            }
            catch (Exception)
            {
                // Diagnostics must never break the loop (C7.1).
            }
        });

        try
        {
            cap.CancelAfter(MaxProcessingTime);

            // On the thread pool, so a handler that blocks synchronously before its first await
            // (Thread.Sleep, .Result, a lock) cannot stop this method reaching the abandonment check.
            var handler = Task.Run(() => ProcessAsync(work, cap.Token), CancellationToken.None);
            var deadline = Task.Delay(MaxProcessingTime + _abandonGrace, abandonTimer.Token);
            var finished = await Task.WhenAny(handler, deadline).ConfigureAwait(false);

            if (finished != handler)
            {
                abandoned = true;
                Abandon(work.Id, handler, cap);
                return;
            }

            await handler.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cap.IsCancellationRequested && !workToken.IsCancellationRequested)
        {
            Observability.HighwayClientMetrics.RecordHandlerCancelled(TargetKind);
            try
            {
                await OnProcessingTimeExceededAsync(work, workToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Reporting the cap must never fault this method: the slot and renewal below still have to
                // be released, and lease recovery still redelivers the message.
                Logger.LogWarning(ex,
                    "Could not report MaxProcessingTime for '{MessageId}' on {Kind} '{Target}'; lease recovery will redeliver it",
                    work.Id, TargetKind, TargetName);
            }
        }
        catch (OperationCanceledException)
        {
            // Engine stopping mid-message; server lease recovery handles redelivery.
        }
        catch (Exception ex)
        {
            // Reported with CancellationToken.None: a failure that happens during shutdown is
            // exactly the one worth recording, so the report must not be cancelled with the work.
            await Reporter.ReportAsync(Target, work, ex, FailureDisposition, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await abandonTimer.CancelAsync().ConfigureAwait(false);

            // Stops the moment the handler completes, throws or is abandoned. A completed or abandoned
            // message is never renewed.
            await renewalStop.CancelAsync().ConfigureAwait(false);
            try { await renewal.ConfigureAwait(false); } catch { /* already logged */ }

            if (!abandoned) cap.Dispose();
            _gate.Release();
        }
    }

    /// <summary>
    /// True when the invocation that owns <paramref name="invocationToken"/> (the token passed to
    /// <see cref="ProcessAsync"/>) was abandoned after MaxProcessingTime + AbandonGrace. An abandoned invocation
    /// must not acknowledge, reply, resolve or complete idempotency when it finally returns: its message was
    /// already given up. A redelivery of the same message is a different invocation.
    /// </summary>
    protected bool IsAbandoned(CancellationToken invocationToken) => _abandoned.ContainsKey(invocationToken);

    /// <summary>Abandoned invocations still running (diagnostics and tests).</summary>
    internal int AbandonedCount => _abandoned.Count;

    private void Abandon(string id, Task handler, CancellationTokenSource cap)
    {
        _abandoned[cap.Token] = (id, DateTime.UtcNow);
        Observability.HighwayClientMetrics.RecordHandlerAbandoned(TargetKind);

        try
        {
            Logger.LogError(
                "Handler abandoned for message '{MessageId}' on {Kind} '{Target}' after {Elapsed}: it did not stop " +
                "when cancelled at MaxProcessingTime. Its slot is released and its lease no longer renewed; the " +
                "message will be redelivered when the lease expires. The handler is still running.",
                id, TargetKind, TargetName, MaxProcessingTime + _abandonGrace);
        }
        catch (Exception)
        {
            // A broken log sink must not leave this invocation counted against the bound forever (C7.1).
        }

        _ = handler.ContinueWith(t =>
        {
            _ = t.Exception;   // observed: an abandoned handler's late fault is not unobserved
            _abandoned.TryRemove(cap.Token, out _);
            cap.Dispose();
            _wake.Signal();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void LogAbandonedLimitReachedOncePerMinute()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAbandonedLimitLog < TimeSpan.FromMinutes(1))
            return;
        _lastAbandonedLimitLog = now;

        var oldest = _abandoned.Values.OrderBy(v => v.Since).FirstOrDefault();
        Logger.LogError(
            "Worker loop for {Kind} '{Target}' has reached MaxAbandonedHandlers ({Count}/{Limit}); not claiming new " +
            "work until one ends. Oldest abandoned message '{MessageId}' has been running for {Elapsed}",
            TargetKind, TargetName, _abandoned.Count, _maxAbandonedHandlers, oldest.Id ?? "unknown",
            oldest.Id is null ? TimeSpan.Zero : now - oldest.Since);
    }
}
