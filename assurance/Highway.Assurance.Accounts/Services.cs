namespace Highway.Assurance.Accounts;

using Highway.Abstractions;
using Highway.Assurance.Contracts;
using Highway.Assurance.Ledger;
using Microsoft.Extensions.Logging;

public sealed class ValidateAccountService(
    ILogger<ValidateAccountService> logger)
    : AsyncService<ValidateAccount, AccountResult>
{
    public override Task<AccountResult> ExecuteAsync(ValidateAccount request, CancellationToken ct = default)
    {
        logger.LogInformation("[Accounts] ValidateAccount {Cid} for UserId {UserId}", request.Cid, request.UserId);
        return Task.FromResult(new AccountResult
        {
            Cid = request.Cid,
            Valid = true,
            StatusCode = 200
        });
    }
}

public sealed class GetProfileService(
    ILogger<GetProfileService> logger)
    : AsyncService<GetProfile, ProfileResult>
{
    public override Task<ProfileResult> ExecuteAsync(GetProfile request, CancellationToken ct = default)
    {
        // R1.6: UserId 9999 represents the known-absent user id that returns 404 as data
        if (request.UserId == 9999)
        {
            logger.LogInformation("[Accounts] GetProfile {Cid} for absent UserId 9999 -> returning 404 as data", request.Cid);
            return Task.FromResult(new ProfileResult
            {
                Cid = request.Cid,
                StatusCode = 404,
                Error = new ErrorDetail
                {
                    Code = "USER_NOT_FOUND",
                    Message = $"User id '{request.UserId}' was not found in accounts registry."
                }
            });
        }

        logger.LogInformation("[Accounts] GetProfile {Cid} for UserId {UserId} -> returning 200", request.Cid, request.UserId);
        return Task.FromResult(new ProfileResult
        {
            Cid = request.Cid,
            Name = $"User #{request.UserId}",
            StatusCode = 200
        });
    }
}

public sealed class PasswordResetSubscriber(
    IHighwayClient client,
    LedgerWriter ledger,
    ILogger<PasswordResetSubscriber> logger)
    : ISubscribe<PasswordResetRequested>
{
    private static long _emailSeq;
    private static long _auditSeq;

    public async Task SubscribeAsync(PasswordResetRequested message, CancellationToken ct = default)
    {
        // Record receipt
        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "received",
            Type = "PasswordResetRequested",
            Cid = message.Cid,
            Group = "accounts"
        }, ct);

        // Accounts is the SECOND PRODUCER into the email.send queue (Kind="reset")
        var eSeq = Interlocked.Increment(ref _emailSeq);
        var resetEmailCid = $"acc-email-{eSeq:000000}";

        var msgId = await client.SendAsync(new SendEmail
        {
            Cid = resetEmailCid,
            Kind = "reset",
            UserId = message.UserId,
            Body = "Your password reset code is 123456"
        }, ct);

        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "sent",
            Type = "SendEmail",
            Cid = resetEmailCid,
            MsgId = msgId
        }, ct);

        // Publish AccountAudited
        var aSeq = Interlocked.Increment(ref _auditSeq);
        var auditCid = $"acc-audit-{aSeq:000000}";

        await client.PublishAsync(new AccountAudited
        {
            Cid = auditCid,
            UserId = message.UserId
        }, ct);

        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "published",
            Type = "AccountAudited",
            Cid = auditCid
        }, ct);

        logger.LogInformation("[Accounts] Processed PasswordResetRequested {Cid} -> Sent reset email {EmailCid} & Published AccountAudited {AuditCid}",
            message.Cid, resetEmailCid, auditCid);
    }
}
