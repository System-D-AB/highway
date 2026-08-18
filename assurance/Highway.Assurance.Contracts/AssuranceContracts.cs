namespace Highway.Assurance.Contracts;

using Highway.Abstractions;

// ── RPC ───────────────────────────────────────────────────────────────

[Service("accounts.validate")]
public sealed class ValidateAccount : IReturn<AccountResult>
{
    public string Cid { get; set; } = "";
    public int UserId { get; set; }
}

public sealed class AccountResult : Output
{
    public string Cid { get; set; } = "";
    public bool Valid { get; set; }
}

[Service("accounts.profile")]
public sealed class GetProfile : IReturn<ProfileResult>
{
    public string Cid { get; set; } = "";
    public int UserId { get; set; }
}

public sealed class ProfileResult : Output
{
    public string Cid { get; set; } = "";
    public string Name { get; set; } = "";
}

// ── Pub/Sub ───────────────────────────────────────────────────────────

[Channel("users.signedup")]
public sealed class UserSignedUp : IPublish
{
    public string Cid { get; set; } = "";
    public int UserId { get; set; }
}

[Channel("users.passwordreset")]
public sealed class PasswordResetRequested : IPublish
{
    public string Cid { get; set; } = "";
    public int UserId { get; set; }
}

[Channel("accounts.audited")]
public sealed class AccountAudited : IPublish
{
    public string Cid { get; set; } = "";
    public int UserId { get; set; }
}

[Channel("email.dispatched")]
public sealed class EmailDispatched : IPublish
{
    public string Cid { get; set; } = "";
    public string EmailCid { get; set; } = "";
}

// ── Queue ─────────────────────────────────────────────────────────────

[Queue("email.send")]
public sealed class SendEmail : ISend
{
    public string Cid { get; set; } = "";
    public string Kind { get; set; } = ""; // "signup" | "reset"
    public int UserId { get; set; }
    public string Body { get; set; } = "";
}
