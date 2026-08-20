using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// A helpdesk mail address (osTicket <c>email</c>): the identity tickets are sent from
/// and/or fetched for, with default routing for inbound mail. Transport settings live in
/// <see cref="Channels"/> (osTicket email_account rows, one mailbox + one smtp).
/// </summary>
public class EmailAccount : TimestampedEntity
{
    public required string Address { get; set; }

    /// <summary>Display name used in From headers (osTicket name).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Department new mail on this address routes to (osTicket dept_id).</summary>
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? PriorityId { get; set; }

    public int? HelpTopicId { get; set; }

    /// <summary>Suppress auto-responses for tickets created via this address (osTicket noautoresp).</summary>
    public bool NoAutoResponse { get; set; }

    public string? Notes { get; set; }

    public List<EmailChannel> Channels { get; set; } = [];
}

public enum EmailChannelKind
{
    Mailbox,
    Smtp,
}

public enum MailProtocol
{
    Imap,
    Pop,
    Smtp,
    Other,
}

public enum MailEncryption
{
    None,
    Auto,
    Ssl,
}

public enum PostFetchAction
{
    Nothing,
    Archive,
    Delete,
}

public enum MailAuthKind
{
    Basic,
    OAuth2,
}

/// <summary>
/// The identity provider an OAuth2 mailbox channel authenticates against (S8 slice 6).
/// Only the two providers helpdesks actually meet are modeled as presets — each one
/// pins its authorize/token endpoints and the IMAP/POP3/SMTP scopes; a hand-rolled
/// authority string is deliberately NOT offered (an unknown provider's XOAUTH2
/// dialect cannot be verified, and a half-working preset is worse than none).
/// osTicket has no equivalent enum — its plugin stores a bare "auth_bk" string.
/// </summary>
public enum MailOAuthProvider
{
    /// <summary>Microsoft 365 / Outlook (Entra ID app registration).</summary>
    Microsoft,

    /// <summary>Google Workspace / Gmail (Google Cloud OAuth client).</summary>
    Google,
}

/// <summary>
/// Transport configuration for one direction of an <see cref="EmailAccount"/>
/// (osTicket <c>email_account</c>): at most one Mailbox (fetch) and one Smtp (send)
/// channel per address. Secrets are not stored here — <see cref="CredentialRef"/> points
/// into protected configuration/secret storage.
/// </summary>
public class EmailChannel : TimestampedEntity
{
    public int EmailAccountId { get; set; }
    public EmailAccount? EmailAccount { get; set; }

    public EmailChannelKind Kind { get; set; }

    public bool IsActive { get; set; }

    public MailProtocol Protocol { get; set; } = MailProtocol.Other;
    public MailAuthKind AuthKind { get; set; } = MailAuthKind.Basic;

    public string Host { get; set; } = "";
    public int Port { get; set; }
    public MailEncryption Encryption { get; set; } = MailEncryption.Auto;

    public string? Username { get; set; }

    /// <summary>Reference key into secret storage (password / OAuth token cache).</summary>
    public string? CredentialRef { get; set; }

    /// <summary>
    /// Basic-auth password, DataProtection-encrypted at rest (S7 admin/email-edit;
    /// purpose string in the Web protector). Write-only in the UI: rendered as a
    /// bullet sentinel, never in plaintext.
    /// </summary>
    public string? PasswordProtected { get; set; }

    /// <summary>OAuth2 app registration client id (S7 admin/email-edit dlg-auth;
    /// the token flow went live in S8 slice 6).</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>OAuth2 client secret, DataProtection-encrypted at rest (write-only
    /// in the UI, same sentinel contract as <see cref="PasswordProtected"/>).</summary>
    public string? OAuthClientSecretProtected { get; set; }

    /// <summary>Which provider preset supplies the endpoints + default scopes
    /// (S8 slice 6). Only meaningful while <see cref="AuthKind"/> is OAuth2.</summary>
    public MailOAuthProvider OAuthProvider { get; set; } = MailOAuthProvider.Microsoft;

    /// <summary>Entra ID tenant (id, domain, or "common"/"organizations") for the
    /// Microsoft preset's authority; ignored by Google. Null = "common".</summary>
    public string? OAuthTenant { get; set; }

    /// <summary>Space-separated scope override; null = the provider preset's
    /// defaults (<c>MailOAuthProviders</c>). Stored so an admin can widen/narrow
    /// consent without a code change.</summary>
    public string? OAuthScopes { get; set; }

    /// <summary>
    /// Long-lived refresh token from the admin consent flow, DataProtection-encrypted
    /// at rest. Never rendered — the UI shows only whether consent exists
    /// (<see cref="OAuthConsentAt"/>) and the account it was granted for.
    /// </summary>
    public string? OAuthRefreshTokenProtected { get; set; }

    /// <summary>Cached access token, DataProtection-encrypted at rest; refreshed by
    /// the token service when <see cref="OAuthAccessTokenExpiresAt"/> is inside the
    /// safety margin. Never rendered.</summary>
    public string? OAuthAccessTokenProtected { get; set; }

    /// <summary>Absolute expiry of the cached access token (provider expires_in
    /// applied at issue time); null = nothing cached.</summary>
    public DateTimeOffset? OAuthAccessTokenExpiresAt { get; set; }

    /// <summary>When the authorization-code consent last completed; null = never
    /// consented (the channel cannot authenticate).</summary>
    public DateTimeOffset? OAuthConsentAt { get; set; }

    /// <summary>Mailbox the consent was granted for, as reported by the provider's
    /// id_token/userinfo — evidence in the UI that the right account was used.</summary>
    public string? OAuthConsentAccount { get; set; }

    /// <summary>Mailbox folder to fetch from (osTicket folder).</summary>
    public string? Folder { get; set; }

    /// <summary>Minutes between fetches (osTicket fetchfreq).</summary>
    public int FetchFrequencyMinutes { get; set; } = 5;

    /// <summary>Max emails per fetch (osTicket fetchmax).</summary>
    public int FetchMax { get; set; } = 30;

    public PostFetchAction PostFetch { get; set; } = PostFetchAction.Nothing;

    public string? ArchiveFolder { get; set; }

    /// <summary>Allow sending as addresses other than the account's (osTicket allow_spoofing).</summary>
    public bool AllowSpoofing { get; set; }

    public int ErrorCount { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
}

/// <summary>Send state of one <see cref="EmailOutbound"/> row (S8 outbox).</summary>
public enum EmailOutboundStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
}

/// <summary>
/// One queued outbound email (S8 persistent outbox). Rows are written by
/// <c>IMailQueue.EnqueueAsync</c> and driven to Sent/Failed by the Hangfire send job
/// (Hangfire AutomaticRetry reschedules failures; <see cref="Attempts"/> mirrors the
/// executions). osTicket has no outbox table — its sends are fire-and-forget inside
/// the request; the durable queue is a deliberate deviation (ROADMAP §2 mail rows).
/// Marked INotAudited: high-churn technical state, not a domain mutation.
/// </summary>
public class EmailOutbound : TimestampedEntity, INotAudited
{
    public required string ToAddress { get; set; }

    /// <summary>Comma-separated CC list; null = none (no consumer fills it yet).</summary>
    public string? CcAddresses { get; set; }

    public required string Subject { get; set; }

    public required string HtmlBody { get; set; }

    /// <summary>Explicit from-account (e.g. the ticket department's address); null =
    /// the email settings default (default_smtp / default_email_id) decides at send.</summary>
    public int? FromEmailAccountId { get; set; }

    public int? TicketId { get; set; }

    public int? ThreadEntryId { get; set; }

    /// <summary>
    /// False for mail whose body is human-authored prose to a customer (the agent
    /// reply/response path); true for everything the system writes by itself
    /// (autoresponses, alerts, notices, account mail). Decides the RFC 3834
    /// <c>Auto-Submitted: auto-generated</c> header at send time — osTicket stamps it
    /// only on its 'notice'/'autoreply' sends (class.mailer.php), never on postReply,
    /// and marking a human reply auto-generated tells the recipient's system not to
    /// answer it. Slice 4 stamped every message; this column is the fix.
    /// </summary>
    public bool IsAutomated { get; set; } = true;

    /// <summary>
    /// Attach the files of <see cref="ThreadEntryId"/> to the outgoing message
    /// (osTicket <c>email/attachments_in_email</c> → postReply's
    /// <c>$response->getAttachments()</c>). Only the reply composer sets it; the row
    /// keeps the reference instead of the bytes, so a purged file simply drops out.
    /// </summary>
    public bool IncludeAttachments { get; set; }

    public EmailOutboundStatus Status { get; set; } = EmailOutboundStatus.Pending;

    /// <summary>
    /// RFC 5322 Message-Id stamped at send time for ticket mail (S8 inbound slice):
    /// carries the signed reply token (<c>RD1-…@domain</c>) so inbound
    /// In-Reply-To/References headers thread back to the ticket, and doubles as the
    /// bounce-correlation key (a DSN's original-message id marks this row Failed).
    /// Null until sent, and for non-ticket mail (Identity resets etc.).
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>Send executions so far (success or failure).</summary>
    public int Attempts { get; set; }

    /// <summary>Last transport failure ("stage: detail"), or the dev-fallback note.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Outcome of one processed inbound mail (S8 inbound pipeline).</summary>
public enum EmailInboundStatus
{
    /// <summary>A new ticket was created from the mail.</summary>
    TicketCreated,
    /// <summary>The mail threaded onto an existing ticket.</summary>
    ThreadAppended,
    /// <summary>Dropped before any domain effect (auto-submitted, loop, bounce,
    /// self-mail, unregistered sender…) — <see cref="EmailInbound.Reason"/> says why.</summary>
    Skipped,
    /// <summary>Refused by the banlist or a filter Reject action.</summary>
    Rejected,
}

/// <summary>
/// Per-message bookkeeping of the S8 fetch pipeline: one row per inbound mail the
/// processor handled, keyed by (channel, UID) — the idempotency guard that keeps a
/// re-listed message (post-fetch "Nothing", POP3 without deletion) from being
/// processed twice; <see cref="MessageId"/> is the cross-channel backstop (UIDVALIDITY
/// resets, POP3 servers without stable UIDL). osTicket has no such table — it relies
/// on marking messages seen/deleted at the server; the persisted log is a deliberate
/// deviation that also serves as the observable drop/reject trail (ROADMAP §2 mail
/// rows). Ticket/channel references are soft (EmailOutbound precedent): rows outlive
/// channel and ticket deletions as a fetch log. Marked INotAudited: high-churn
/// technical state.
/// </summary>
public class EmailInbound : TimestampedEntity, INotAudited
{
    public int EmailChannelId { get; set; }

    public int EmailAccountId { get; set; }

    /// <summary>Server-side identity within the channel (IMAP UID / POP3 UIDL, else
    /// a fallback index).</summary>
    public required string Uid { get; set; }

    /// <summary>Normalized Message-Id header (no angle brackets); null when absent.</summary>
    public string? MessageId { get; set; }

    public string? FromAddress { get; set; }

    public string? Subject { get; set; }

    public EmailInboundStatus Status { get; set; }

    /// <summary>Skip/reject reason key (+ detail), e.g. "auto-submitted", "loop",
    /// "bounce", "banlist", "filter:Spam Engeli".</summary>
    public string? Reason { get; set; }

    public int? TicketId { get; set; }

    public int? ThreadEntryId { get; set; }
}

/// <summary>What a <see cref="MailToken"/> authorizes (S8 slice 5 token flows).</summary>
public enum MailTokenPurpose
{
    /// <summary>Registration email verification (osTicket <c>email_verify</c>).</summary>
    EmailVerify,

    /// <summary>Guest → portal account invitation (osTicket registration mode
    /// "invite"; agent users page "Kaydet").</summary>
    Invite,
}

/// <summary>
/// Redemption ledger for the ONE-SHOT signed links the mail pipeline sends
/// (verification + invitation). The token itself is a DataProtection payload — it
/// already carries scope and expiry and is unforgeable — so this row exists purely to
/// make a token single-use and revocable: <see cref="ConsumedAt"/> is stamped on the
/// first successful redemption and every later presentation is refused. Reusable
/// links (the ticket-access auto-login token) stay stateless and have NO row here.
/// osTicket has no equivalent table (its auth tokens are replayable HMACs of ticket +
/// user); single-use is a deliberate hardening, flagged. Marked INotAudited: token
/// churn is technical state, and the issuing action is already audited.
/// </summary>
public class MailToken : TimestampedEntity, INotAudited
{
    /// <summary>Token identity embedded in the protected payload (the "jti").</summary>
    public Guid Jti { get; set; }

    public MailTokenPurpose Purpose { get; set; }

    /// <summary>Domain user the token is scoped to.</summary>
    public int UserId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>First (and only) successful redemption; null while unused.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>Template set (osTicket <c>email_template_group</c>), e.g. "Varsayılan" (tr).</summary>
public class EmailTemplateSet : TimestampedEntity
{
    public required string Name { get; set; }

    /// <summary>Culture of the set's content, e.g. "tr", "en" (osTicket lang).</summary>
    public required string Language { get; set; }

    public bool IsActive { get; set; }

    public string? Notes { get; set; }

    public List<EmailTemplate> Templates { get; set; } = [];
}

/// <summary>
/// One template within a set (osTicket <c>email_template</c>), addressed by a stable
/// code name (e.g. "ticket.autoresp", "effort.request", "effort.response"); body is a
/// Razor-rendered HTML template with %{variable} pills.
/// </summary>
public class EmailTemplate : TimestampedEntity
{
    public int SetId { get; set; }
    public EmailTemplateSet? Set { get; set; }

    public required string CodeName { get; set; }

    public required string Subject { get; set; }

    public required string Body { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// Banned sender address (admin/banlist page). osTicket models the banlist as a special
/// system filter with email rules; a dedicated table is simpler and serves the same job.
/// </summary>
public class BanlistEntry : TimestampedEntity
{
    public required string Address { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }
}
