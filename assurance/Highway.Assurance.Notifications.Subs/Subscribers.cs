namespace Highway.Assurance.Notifications.Subs;

using Highway.Abstractions;
using Highway.Assurance.Contracts;
using Highway.Assurance.Ledger;
using Microsoft.Extensions.Logging;

public sealed class UserSignedUpSubscriber(
    IHighwayClient client,
    LedgerWriter ledger,
    ILogger<UserSignedUpSubscriber> logger)
    : ISubscribe<UserSignedUp>
{
    private static long _dispatchSeq;

    public async Task SubscribeAsync(UserSignedUp message, CancellationToken ct = default)
    {
        // Record receipt in ledger
        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "received",
            Type = "UserSignedUp",
            Cid = message.Cid,
            Group = "notifications-subs"
        }, ct);

        // Publish EmailDispatched back to close the cycle
        var seq = Interlocked.Increment(ref _dispatchSeq);
        var emailDispatchedCid = $"notif-{seq:000000}";

        await client.PublishAsync(new EmailDispatched
        {
            Cid = emailDispatchedCid,
            EmailCid = message.Cid
        }, ct);

        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "published",
            Type = "EmailDispatched",
            Cid = emailDispatchedCid
        }, ct);

        logger.LogInformation("[Notifications.Subs] Processed UserSignedUp {Cid} -> Published EmailDispatched {DispatchCid}",
            message.Cid, emailDispatchedCid);
    }
}

public sealed class AccountAuditedSubscriber(
    LedgerWriter ledger,
    ILogger<AccountAuditedSubscriber> logger)
    : ISubscribe<AccountAudited>
{
    public async Task SubscribeAsync(AccountAudited message, CancellationToken ct = default)
    {
        // OD10: Tuned for non-blocking sequential processing
        await Task.Yield();

        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "received",
            Type = "AccountAudited",
            Cid = message.Cid,
            Group = "notifications-subs"
        }, ct);

        logger.LogInformation("[Notifications.Subs] Processed AccountAudited {Cid}", message.Cid);
    }
}
