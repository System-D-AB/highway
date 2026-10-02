using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Claims and processes dead letters for one target feed (feature 073).
///
/// <para>Dead letters are notified to declaring nodes through the feed box.
/// A throwing handler resolves as <see cref="DeadLetterAction.Keep"/> and logs at Error,
/// ensuring a broken handler never loops or deletes evidence.</para>
/// </summary>
internal sealed class DeadLetterWorkerLoop : SingleMessageWorkerLoop
{
    private readonly DeadLetterDescriptor _descriptor;
    private readonly string? _group;
    private readonly WorkKind _wireKind;
    private readonly string _wireName;
    private readonly string _feedTarget;

    public DeadLetterWorkerLoop(
        DeadLetterDescriptor descriptor,
        IHighwayConnection connection,
        ServiceExecutor executor,
        string nodeName,
        string? group,
        LoopWake wake,
        ILogger logger,
        TimeSpan renewalInterval = default,
        TimeSpan maxProcessingTime = default)
        : base(descriptor.MessageType, connection, executor, nodeName, 1, wake, logger, renewalInterval, maxProcessingTime)
    {
        _descriptor = descriptor;
        _group = group;

        if (descriptor.TargetKind == DeadLetterTargetKind.Service)
        {
            _wireKind = WorkKind.Service;
            _wireName = descriptor.TargetName;
            _feedTarget = $"SVC:{descriptor.TargetName}";
        }
        else if (descriptor.TargetKind == DeadLetterTargetKind.Group)
        {
            _wireKind = WorkKind.Queue;
            _wireName = $"{descriptor.TargetName}@{group}";
            _feedTarget = $"Q:{descriptor.TargetName}@{group}";
        }
        else
        {
            _wireKind = WorkKind.Queue;
            _wireName = descriptor.TargetName;
            _feedTarget = $"Q:{descriptor.TargetName}";
        }
    }

    public string FeedTarget => _feedTarget;

    protected override FailureTarget Target => new(FailureFamily.Queue, _feedTarget, NodeName);

    protected override string FailureDisposition => "dead letter will be kept on the target";

    protected override Task<ClaimedWork?> ClaimAsync(CancellationToken stopToken)
        => Connection.ClaimDeadAsync(_wireKind, _wireName, NodeName, stopToken);

    protected override async Task ProcessAsync(ClaimedWork work, CancellationToken ct)
    {
        object deadLetter;
        try
        {
            deadLetter = DeadLetterWire.DecodeDeadLetter(
                _descriptor.MessageType,
                work.Payload,
                work.Id,
                _descriptor.TargetKind,
                _descriptor.TargetName,
                _group);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Failed to decode dead letter payload for '{DeadLetterId}' on {FeedTarget}; resolving KEEP",
                work.Id, _feedTarget);

            await Connection.ResolveDeadLetterAsync(work.Token, DeadLetterAction.Keep, ct).ConfigureAwait(false);
            return;
        }

        DeadLetterAction action;
        try
        {
            action = await Executor.ExecuteDeadLetterAsync(_descriptor, deadLetter, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Dead-letter handler for '{MessageType}' threw on dead letter '{DeadLetterId}'; resolving as KEEP to preserve evidence",
                _descriptor.MessageType.FullName, work.Id);
            action = DeadLetterAction.Keep;
        }

        var result = await Connection.ResolveDeadLetterAsync(work.Token, action, ct).ConfigureAwait(false);
        if (result.Status == "refused")
        {
            Logger.LogWarning(
                "Dead-letter action '{Action}' for '{DeadLetterId}' on {FeedTarget} was refused: {Detail}",
                action.Type, work.Id, _feedTarget, result.Detail);
        }
    }
}
