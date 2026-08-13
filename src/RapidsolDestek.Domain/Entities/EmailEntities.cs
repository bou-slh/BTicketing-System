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
