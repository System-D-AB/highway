using System.Reflection;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Scanning;
using Highway.Client.Wire;

namespace Highway.Client;

/// <summary>
/// Default implementation of <see cref="IHighwayDeadLetters"/> (feature 073).
/// Provides typed operator operations for dead-letter inspection, counting, requeuing, and purging.
/// </summary>
internal sealed class HighwayDeadLetters : IHighwayDeadLetters
{
    private readonly ICatalog _catalog;
    private readonly HighwayOptions _options;
    private readonly IHighwayEngine _engine;
    private readonly IHighwayEngineInternals _engineInternals;

    public HighwayDeadLetters(
        ICatalog catalog,
        HighwayOptions options,
        IHighwayEngine engine,
        IHighwayEngineInternals engineInternals)
    {
        _catalog = catalog;
        _options = options;
        _engine = engine;
        _engineInternals = engineInternals;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeadLetter<T>>> PeekAsync<T>(int count = 20, string? group = null, CancellationToken ct = default)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");

        var connection = EnsureConnection();
        var (target, kindStr, nameStr) = ResolveTarget<T>(group);

        var rawEntries = await connection.DlqPeekAsync(kindStr, nameStr, count, ct).ConfigureAwait(false);
        var result = new List<DeadLetter<T>>(rawEntries.Count);

        foreach (var entry in rawEntries)
        {
            T? message = default;
            try
            {
                var env = HighwayJson.DecodeEnvelope(entry.Payload);
                message = (T?)HighwayJson.DeserializeBody(env, typeof(T));
            }
            catch
            {
                // Requirement 4: Bad/poison payload is returned with Message = null and RawPayload preserved
            }

            result.Add(new DeadLetter<T>(
                Message: message,
                RawPayload: entry.Payload,
                Id: entry.Id,
                DeadLetterId: entry.DeadLetterId,
                Target: target,
                Attempts: entry.Attempts,
                Requeues: entry.Requeues,
                DeadLetteredAt: entry.DeadLetteredAt,
                Reason: entry.Reason,
                LastFailure: entry.LastFailure,
                FirstFailure: entry.FirstFailure));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync<T>(string? group = null, CancellationToken ct = default)
    {
        var connection = EnsureConnection();
        var (_, kindStr, nameStr) = ResolveTarget<T>(group);
        return await connection.DlqCountAsync(kindStr, nameStr, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RequeueAsync<T>(string deadLetterId, TimeSpan? after = null, string? group = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterId);
        var connection = EnsureConnection();
        var (_, kindStr, nameStr) = ResolveTarget<T>(group);
        return await connection.DlqRequeueAsync(kindStr, nameStr, deadLetterId, after, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> PurgeAsync<T>(string deadLetterId, string? group = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterId);
        var connection = EnsureConnection();
        var (_, kindStr, nameStr) = ResolveTarget<T>(group);
        return await connection.DlqPurgeAsync(kindStr, nameStr, deadLetterId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> RequeueAllAsync<T>(int max, TimeSpan? after = null, string? group = null, CancellationToken ct = default)
    {
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(max), "Max count must be positive.");

        var connection = EnsureConnection();
        var (_, kindStr, nameStr) = ResolveTarget<T>(group);
        return await connection.DlqRequeueAllAsync(kindStr, nameStr, max, after, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PurgeAllAsync<T>(int max, string? group = null, CancellationToken ct = default)
    {
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(max), "Max count must be positive.");

        var connection = EnsureConnection();
        var (_, kindStr, nameStr) = ResolveTarget<T>(group);
        return await connection.DlqPurgeAllAsync(kindStr, nameStr, max, ct).ConfigureAwait(false);
    }

    private IHighwayConnection EnsureConnection()
    {
        if (_engine.State != EngineState.Running || _engineInternals.Connection is not { } connection)
        {
            throw new HighwayTransportException("The Highway engine is not running. Start the engine before accessing dead letters.");
        }
        return connection;
    }

    private (DeadLetterTarget Target, string TargetKindStr, string TargetNameStr) ResolveTarget<T>(string? groupOverride)
    {
        var type = typeof(T);

        var qAttr = type.GetCustomAttribute<QueueAttribute>();
        if (qAttr is not null)
        {
            return (new DeadLetterTarget(DeadLetterTargetKind.Queue, qAttr.Name), "Q", qAttr.Name);
        }

        var sAttr = type.GetCustomAttribute<ServiceAttribute>();
        if (sAttr is not null)
        {
            return (new DeadLetterTarget(DeadLetterTargetKind.Service, sAttr.Name), "SVC", sAttr.Name);
        }

        var cAttr = type.GetCustomAttribute<ChannelAttribute>();
        if (cAttr is not null)
        {
            var group = groupOverride ?? _options.EffectiveSubscriptionGroup;
            var queueName = $"{cAttr.Name}@{group}";
            return (new DeadLetterTarget(DeadLetterTargetKind.Group, cAttr.Name, group), "Q", queueName);
        }

        if (_catalog.GetQueueNameForMessageType(type) is { Length: > 0 } qName)
        {
            return (new DeadLetterTarget(DeadLetterTargetKind.Queue, qName), "Q", qName);
        }

        if (_catalog.GetServiceNameForRequestType(type) is { Length: > 0 } sName)
        {
            return (new DeadLetterTarget(DeadLetterTargetKind.Service, sName), "SVC", sName);
        }

        if (_catalog.GetChannelNameForMessageType(type) is { Length: > 0 } cName)
        {
            var group = groupOverride ?? _options.EffectiveSubscriptionGroup;
            var queueName = $"{cName}@{group}";
            return (new DeadLetterTarget(DeadLetterTargetKind.Group, cName, group), "Q", queueName);
        }

        throw new ArgumentException($"Type '{type.FullName}' is not a recognized Highway contract. Contracts must be annotated with [Queue], [Service], or [Channel].", nameof(T));
    }
}
