using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// One conversation, shared by tickets and tasks — osTicket's polymorphic
/// <c>thread(object_id, object_type)</c> inverted into owner-side FKs
/// (<see cref="Ticket.ThreadId"/> / <see cref="TaskItem.ThreadId"/>) so the
/// relational model stays declarative while the shared-thread mechanism is kept.
/// </summary>
public class Thread : EntityBase
{
    public DateTimeOffset? LastResponseAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<ThreadEntry> Entries { get; set; } = [];
    public List<ThreadEvent> Events { get; set; } = [];
    public List<ThreadCollaborator> Collaborators { get; set; } = [];
}

/// <summary>osTicket thread_entry.type chars M / R / N.</summary>
public enum ThreadEntryType
{
    /// <summary>'M' — end-user message.</summary>
    Message,
    /// <summary>'R' — agent response (public).</summary>
    Response,
    /// <summary>'N' — internal note.</summary>
    Note,
}

public class ThreadEntry : TimestampedEntity
{
    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }

    /// <summary>Parent entry when replying to a specific entry (osTicket pid).</summary>
    public int? ParentId { get; set; }

    public ThreadEntryType Type { get; set; }

    /// <summary>Author when posted by staff.</summary>
    public int? StaffId { get; set; }

    /// <summary>Author when posted by an end-user.</summary>
    public int? UserId { get; set; }

    /// <summary>Display name snapshot of the author (osTicket poster).</summary>
    public required string Poster { get; set; }

    /// <summary>Staff member who last edited the body, if edited.</summary>
    public int? EditedByStaffId { get; set; }

    /// <summary>Origin channel: Web, Email, API… (osTicket source).</summary>
    public string? Source { get; set; }

    public string? Title { get; set; }

    public required string Body { get; set; }

    /// <summary>"html" or "text" (osTicket format).</summary>
    public string Format { get; set; } = "html";

    /// <summary>
    /// osTicket-style bitmask (edited / original-message-preserved / system-generated…);
    /// interpreted by the thread service in S4.
    /// </summary>
    public int Flags { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>JSON snapshot of to/cc recipients for outbound entries.</summary>
    public string? Recipients { get; set; }
}

/// <summary>
/// Lookup of auditable thread happenings (osTicket <c>event</c> table: created, closed,
/// reopened, assigned, transferred, overdue, …) extended with the effort-loop events.
/// </summary>
public class ThreadEventType : EntityBase
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>Timeline row rendered between messages (osTicket thread_event).</summary>
public class ThreadEvent : EntityBase, INotAudited
{
    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }

    public int EventTypeId { get; set; }
    public ThreadEventType? EventType { get; set; }

    /// <summary>Related object ids captured at event time (osTicket keeps all four).</summary>
    public int? StaffId { get; set; }
    public int? TeamId { get; set; }
    public int? DepartmentId { get; set; }
    public int? HelpTopicId { get; set; }

    /// <summary>Encoded differences, JSON (osTicket data).</summary>
    public string? Data { get; set; }

    /// <summary>Display name of the actor, "SYSTEM" when automated.</summary>
    public string Username { get; set; } = "SYSTEM";

    public ActorType ActorType { get; set; } = ActorType.System;
    public int? ActorId { get; set; }

    /// <summary>Soft-deleted events stay for stats but leave the timeline (osTicket annulled).</summary>
    public bool Annulled { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public enum ActorType
{
    System,
    Staff,
    User,
}

/// <summary>osTicket thread_collaborator.role chars M / N / R.</summary>
public enum CollaboratorRole
{
    /// <summary>'M' — client CC, receives messages.</summary>
    Cc,
    /// <summary>'N' — third party, note-level visibility.</summary>
    ThirdParty,
    /// <summary>'R' — external authority, receives responses.</summary>
    ExternalAuthority,
}

public class ThreadCollaborator : TimestampedEntity
{
    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public CollaboratorRole Role { get; set; } = CollaboratorRole.Cc;

    /// <summary>osTicket flags bit 1 — collaborator receives notifications.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Concurrent-edit lock on a ticket/task composer (osTicket <c>lock</c>). Short-lived,
/// heartbeat-extended; the owning object points here via LockId.
/// </summary>
public class EditLock : EntityBase, INotAudited
{
    public int StaffId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Random code the client must echo to retain the lock (osTicket code).</summary>
    public string? Code { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Autosaved composer draft (osTicket draft), namespaced per composer instance.</summary>
public class Draft : TimestampedEntity, INotAudited
{
    public int StaffId { get; set; }

    /// <summary>Composer identity, e.g. "ticket.reply.716555".</summary>
    public required string Namespace { get; set; }

    public string Body { get; set; } = "";

    /// <summary>JSON extras (attachment ids, cursor state).</summary>
    public string? Extra { get; set; }
}
