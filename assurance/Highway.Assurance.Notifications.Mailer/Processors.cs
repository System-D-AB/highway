namespace Highway.Assurance.Notifications.Mailer;

using Highway.Abstractions;
using Highway.Assurance.Contracts;
using Highway.Assurance.Ledger;
using Microsoft.Extensions.Logging;

public sealed class SendEmailProcessor(
    LedgerWriter ledger,
    ClaimTracker claimTracker,
    ILogger<SendEmailProcessor> logger)
    : IProcess<SendEmail>
{
    public async Task ProcessAsync(SendEmail message, CancellationToken ct = default)
    {
        var attempt = claimTracker.RecordClaim(message.Cid);

        // Record claimed / in-flight state immediately upon receiving
        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "claimed",
            Type = "SendEmail",
            Cid = message.Cid,
            Attempt = attempt
        }, ct);

        // OD10: Artificial 500ms delay for realistic transactional email delivery work
        await Task.Delay(500, ct);

        // Record processed state upon successful completion
        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "processed",
            Type = "SendEmail",
            Cid = message.Cid,
            Attempt = attempt
        }, ct);

        logger.LogInformation("[Notifications.Mailer] Processed SendEmail {Cid} (Kind={Kind}, Attempt={Attempt})",
            message.Cid, message.Kind, attempt);
    }
}
