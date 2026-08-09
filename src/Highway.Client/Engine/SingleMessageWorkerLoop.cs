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
    private readonly TimeSpan _maxProcessingTime;

    protected SingleMessageWorkerLoop(
        Type contractType,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default)
    {
        _renewalInterval = renewalInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : renewalInterval;
        _maxProcessingTime = maxProcessingTime;
        Connection = connection;
        Executor = executor;
        NodeName = nodeName;
        Logger = logger;
        Reporter = new FailureReporter(connection, logger);
        _concurrency = Math.Max(1, concurrency);
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
    protected abstract Task<(string Id, byte[] Payload)?> ClaimAsync(CancellationToken stopToken);

    /// <summary>Handles one claimed message. Exceptions are caught and logged by the base.</summary>
    protected abstract Task ProcessAsync(string id, byte[] payload, CancellationToken workToken);

    /// <summary>
    /// Two-token contract: <paramref name="stopToken"/> stops claiming new work;
    /// <paramref name="workToken"/> governs in-flight processing and is only cancelled after
    /// the drain timeout — so graceful shutdown drains rather than aborts.
    /// </summary>
    public async Task RunAsync(TimeSpan selfHealTimeout, CancellationToken stopToken, CancellationToken workToken)
    {
        Logger.LogInformation("Worker loop started for {Kind} '{Target}' (concurrency {Concurrency})",
            TargetKind, TargetName, _concurrency);

        while (!stopToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(selfHealTimeout, stopToken).ConfigureAwait(false);
            if (stopToken.IsCancellationRequested) break;

            try
            {
                await DrainAsync(stopToken, workToken).ConfigureAwait(false);
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

    private async Task DrainAsync(CancellationToken stopToken, CancellationToken workToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            // Slot first, then claim. See the class remarks: claiming starts the lease, so a
            // claim taken while the gate is full is a lease running on work that cannot start.
            await _gate.WaitAsync(stopToken).ConfigureAwait(false);

            (string Id, byte[] Payload)? item;
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

            var (id, payload) = item.Value;

            // Process on the thread pool so a synchronous-heavy handler cannot stall the
            // drain. The slot is already held and is released by ProcessAndReleaseAsync.
            var task = Task.Run(() => ProcessAndReleaseAsync(id, payload, workToken), CancellationToken.None);
            lock (_inflight)
            {
                _inflight.Add(task);
                if (_inflight.Count > 64)
                    _inflight.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Renews this message's lease while its handler runs, and stops at
    /// <c>MaxProcessingTime</c> (019).
    ///
    /// <para><b>Why it is bounded.</b> Unbounded renewal deletes lease recovery: a handler
    /// stuck in a deadlock would hold its message forever — never redelivered, never
    /// dead-lettered, never visible. Past the cap the message returns to exactly the behaviour
    /// it had before this feature.</para>
    ///
    /// <para><b>A failed renewal is swallowed</b> (C7.1). A mechanism that exists to protect
    /// delivery must never be able to break it; the ordinary sweep still recovers the message.
    /// Individual renewals are not recorded — at this interval they would flood the recorder
    /// with the least interesting thing it could hold.</para>
    /// </summary>
    private async Task RenewWhileRunningAsync(string id, CancellationToken stop)
    {
        var started = DateTime.UtcNow;

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_renewalInterval, stop).ConfigureAwait(false);

                if (DateTime.UtcNow - started >= _maxProcessingTime)
                {
                    // Loud, and once: a handler that routinely exhausts its cap is either
                    // mis-sized or hung, and both are worth knowing BEFORE the dead letter.
                    Observability.HighwayActivity.MarkProcessingCapExceeded(
                        System.Diagnostics.Activity.Current, id, DateTime.UtcNow - started);

                    Logger.LogWarning(
                        "Message '{MessageId}' on {Kind} '{Target}' has run for {Elapsed} and reached " +
                        "MaxProcessingTime ({Cap}); its lease is no longer being renewed. It will be " +
                        "redelivered when the lease expires and eventually dead-lettered. Either the " +
                        "handler is hung, or this work should be chunked rather than run in one message.",
                        id, TargetKind, TargetName, DateTime.UtcNow - started, _maxProcessingTime);
                    return;
                }

                await Connection.TouchAsync(Target.WireKind, Target.Name, NodeName, id, stop)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The handler finished, or the engine is stopping. Neither is a problem.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Could not renew the lease for '{MessageId}' on {Kind} '{Target}'; the message is " +
                "unaffected and the ordinary sweep still recovers it, but a slow handler may now " +
                "be duplicated",
                id, TargetKind, TargetName);
        }
    }

    private async Task ProcessAndReleaseAsync(string id, byte[] payload, CancellationToken workToken)
    {
        // Renewal is on by default (019 R2.2): a handler outliving the lease is a correctness
        // failure with silent duplicate execution, and a developer should not have to opt in to
        // not having it. Zero disables it, restoring pre-019 behaviour exactly.
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        var renewal = _maxProcessingTime > TimeSpan.Zero
            ? RenewWhileRunningAsync(id, renewalStop.Token)
            : Task.CompletedTask;

        try
        {
            await ProcessAsync(id, payload, workToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping mid-message; server lease recovery handles redelivery.
        }
        catch (Exception ex)
        {
            // Reported with CancellationToken.None: a failure that happens during shutdown is
            // exactly the one worth recording, so the report must not be cancelled with the work.
            await Reporter.ReportAsync(Target, id, ex, FailureDisposition, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            // Stops the moment the handler completes, throws or is cancelled (R2.3). A
            // completed message is never renewed.
            await renewalStop.CancelAsync().ConfigureAwait(false);
            try { await renewal.ConfigureAwait(false); } catch { /* already logged */ }

            _gate.Release();
        }
    }
}
