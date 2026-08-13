using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// Typed key/value configuration row (osTicket <c>config</c>), unique per
/// (namespace, key). Typed accessor sections over this table are built in S7 so every
/// admin settings switch is actually consumed by the engine.
/// </summary>
public class Setting : EntityBase, INotAudited
{
    public required string Namespace { get; set; }

    public required string Key { get; set; }

    public string Value { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Mirrors osTicket api_key; adapted to the JSON REST API.</summary>
public class ApiKey : TimestampedEntity
{
    public required string Key { get; set; }

    /// <summary>Requests are only accepted from this address (osTicket ipaddr).</summary>
    public required string IpAddress { get; set; }

    public bool IsActive { get; set; } = true;

    public bool CanCreateTickets { get; set; } = true;

    /// <summary>Allowed to trigger background jobs remotely (osTicket can_exec_cron).</summary>
    public bool CanTriggerJobs { get; set; }

    public string? Notes { get; set; }
}

public enum SitePageType
{
    Landing,
    Offline,
    ThankYou,
    Other,
}

/// <summary>Editable site content page (osTicket <c>content</c>): landing/offline/thank-you.</summary>
public class SitePage : TimestampedEntity
{
    public required string Name { get; set; }

    public SitePageType Type { get; set; } = SitePageType.Other;

    public required string Body { get; set; }

    public bool IsActive { get; set; }

    public string? Notes { get; set; }
}

public enum SystemLogType
{
    Debug,
    Warning,
    Error,
}

/// <summary>System log row (osTicket <c>syslog</c>) shown on admin/system-logs.</summary>
public class SystemLogEntry : EntityBase, INotAudited
{
    public SystemLogType Type { get; set; }

    public required string Title { get; set; }

    public string Log { get; set; } = "";

    /// <summary>Component that wrote the entry (osTicket logger).</summary>
    public string? Logger { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Audit trail row, written automatically for every domain mutation by the EF save
/// interceptor (roadmap B10; osTicket only audits thread events — this is broader).
/// Long key: the audit table outlives and outgrows everything else.
/// </summary>
public class AuditEvent : INotAudited
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public ActorType ActorType { get; set; } = ActorType.System;

    /// <summary>Staff.Id or User.Id depending on <see cref="ActorType"/>.</summary>
    public int? ActorId { get; set; }

    public string ActorName { get; set; } = "SYSTEM";

    /// <summary>"Created" / "Updated" / "Deleted".</summary>
    public required string Action { get; set; }

    /// <summary>Entity CLR name, e.g. "Ticket".</summary>
    public required string ObjectType { get; set; }

    public required string ObjectId { get; set; }

    /// <summary>Human-readable identifier snapshot (ticket number, user name…).</summary>
    public string? ObjectLabel { get; set; }

    /// <summary>JSON of changed properties: { prop: { old, new } }.</summary>
    public string? Data { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// File metadata (osTicket <c>file</c>). Contents live behind IFileStore
/// (<see cref="Backend"/> key + <see cref="StorageKey"/>), never in the database —
/// osTicket's file_chunk default is deliberately dropped.
/// </summary>
public class StoredFile : EntityBase, INotAudited
{
    /// <summary>File-store backend id, e.g. "fs", "s3" (osTicket bk).</summary>
    public required string Backend { get; set; }

    /// <summary>Unique key within the backend (osTicket key).</summary>
    public required string StorageKey { get; set; }

    /// <summary>Content hash for dedup/integrity (osTicket signature).</summary>
    public string? Signature { get; set; }

    public required string Name { get; set; }

    public string MimeType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Object kinds attachments can hang off (osTicket attachment.type chars).</summary>
public enum AttachmentObjectType
{
    /// <summary>'H' — thread entry.</summary>
    ThreadEntry,
    /// <summary>'F' — FAQ article.</summary>
    FaqArticle,
    /// <summary>'C' — canned response.</summary>
    CannedResponse,
    /// <summary>'T' — email template.</summary>
    EmailTemplate,
    /// <summary>'P' — site page.</summary>
    SitePage,
}

/// <summary>
/// Polymorphic link between an object and a stored file (osTicket <c>attachment</c>).
/// (ObjectType, ObjectId) is a soft reference by design — osTicket keeps it constraint-free
/// too; the owning services delete attachments with their objects.
/// </summary>
public class Attachment : EntityBase, INotAudited
{
    public AttachmentObjectType ObjectType { get; set; }

    public int ObjectId { get; set; }

    public int FileId { get; set; }
    public StoredFile? File { get; set; }

    /// <summary>Display-name override (osTicket name).</summary>
    public string? Name { get; set; }

    /// <summary>Inline image referenced from HTML body vs listed attachment.</summary>
    public bool Inline { get; set; }
}
