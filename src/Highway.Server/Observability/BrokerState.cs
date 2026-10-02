using Highway.Server.Internal;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Highway.Server.Observability;

/// <summary>One queue's live state, as an operator would want to read it.</summary>
internal sealed record QueueStateDto(
    string Name,
    long Depth,
    long Bytes,
    long MaxBytes,
    long InFlight,
    long DeadLettered,
    long Delayed,
    bool IsSubscriberGroup,
    bool Draining = false);   // 070: a group queue whose last member left

/// <summary>
/// The result of a state read. **Never an exception**: a view that cannot load must say so while
/// the rest of the page keeps working (020 R1.3), which is C7.1 applied to the dashboard itself.
/// </summary>
internal sealed record StateResult<T>(T? Value, string? Unavailable)
{
    public static StateResult<T> Ok(T value) => new(value, null);
    public static StateResult<T> Fail(string why) => new(default, why);
}

/// <summary>
/// Read-only access to broker state, for components hosted inside the server process.
///
/// <para><b>Read-only by construction, not by convention</b> (020 R1.4). The interface exposes
/// no mutation, so "the dashboard cannot change anything" is a compile-time fact rather than a
/// code-review promise.</para>
/// </summary>
internal interface IBrokerState
{
    Task<StateResult<IReadOnlyList<QueueStateDto>>> QueuesAsync(CancellationToken ct = default);

    /// <summary>Registered nodes and what each declared (022 T2).</summary>
    Task<StateResult<IReadOnlyList<NodeDto>>> NodesAsync(CancellationToken ct = default);

    /// <summary>Recurring-job schedules, from HW.JOB LIST (feature 028).</summary>
    Task<StateResult<IReadOnlyList<JobScheduleDto>>> JobsAsync(CancellationToken ct = default);

    /// <summary>
    /// The catalogue: every entity, classified, as a union of what nodes declared and what the
    /// recorder observed. <paramref name="observedNames"/> comes from the in-process recorder,
    /// which is why an entity nobody declared can still appear.
    /// </summary>
    Task<StateResult<IReadOnlyList<CatalogueEntryDto>>> CatalogueAsync(
        IReadOnlyCollection<string> observedNames, CancellationToken ct = default);

    /// <summary>Replication role/epoch/slots (042 T4). Unavailable on an ephemeral broker.</summary>
    Task<StateResult<IReadOnlyDictionary<string, string>>> ReplicationAsync(CancellationToken ct = default);

    /// <summary>Dead letters for a queue, service, or subscriber group (feature 073).</summary>
    Task<StateResult<IReadOnlyList<DeadLetterStateDto>>> DeadLettersAsync(
        string kind, string name, int count = 20, CancellationToken ct = default);
}

/// <summary>Dead-letter entry state for dashboard inspection (feature 073).</summary>
internal sealed record DeadLetterStateDto(
    string DeadId,
    string? MessageId,
    string? RequestId,
    DateTimeOffset DeadLetteredAt,
    int Attempts,
    int Requeues,
    string Reason,
    string? FailureType,
    string? FailureFirstType,
    string? FailureDetail,
    byte[] Payload);
