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
/// <para><b><see cref="ChannelConsumerLoop"/> is deliberately not a subclass.</b> It is
/// batch-shaped — <c>HW.RECEIVE</c> returns many messages and it has no gate and no in-flight
/// list. Forcing it in would mean either losing batching or filling this class with
/// <c>if (batch)</c> branches, which is the wrong shape for one of three callers.</para>
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

    protected SingleMessageWorkerLoop(
        Type contractType,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        int concurrency,
        LoopWake wake,
        ILogger logger)
    {
        Connection = connection;
        Executor = executor;
        NodeName = nodeName;
        Logger = logger;
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

    /// <summary>The queue or service this loop serves, for logging and diagnostics.</summary>
    protected abstract string TargetName { get; }

    /// <summary>"service" or "queue" — the noun used in this loop's log messages.</summary>
    protected abstract string TargetKind { get; }

    /// <summary>
    /// Takes the next message, or <see langword="null"/> when the source is drained. Called
    /// only while a concurrency slot is held.
    /// </summary>
    protected abstract Task<(string Id, byte[] Payload)?> ClaimAsync(CancellationToken stopToken);

    /// <summary>Handles one claimed message. Exceptions are caught and logged by the base.</summary>
    protected abstract Task ProcessAsync(string id, byte[] payload, CancellationToken workToken);

    /// <summary>
    /// Logged when <see cref="ProcessAsync"/> throws. The two loops describe the consequence
    /// differently — an RPC caller may be waiting on a reply, a queue message is simply
    /// redelivered — and that wording is worth keeping accurate.
    /// </summary>
    protected abstract void LogProcessingFailure(Exception ex, string id);

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

    private async Task ProcessAndReleaseAsync(string id, byte[] payload, CancellationToken workToken)
    {
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
            LogProcessingFailure(ex, id);
        }
        finally
        {
            _gate.Release();
        }
    }
}
