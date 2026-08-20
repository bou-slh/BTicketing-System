using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// osTicket keeps ticket state in a <c>ticket_status</c> table (rows over four fixed
/// states), not an enum — statuses are admin-editable data. We mirror that. The mockup
/// status keys (<c>new, open, wait, test, solved, closed</c>) live in <see cref="Key"/>;
/// the display-only pseudo-statuses <c>overdue</c> / <c>effortWait</c> /
/// <c>effortApproved</c> / <c>effortRejected</c> are derived from
/// <see cref="Ticket.IsOverdue"/> and the active <see cref="EffortProposal"/> — they are
/// deliberately not rows here (parity: osTicket models overdue as a ticket flag too).
/// </summary>
public class TicketStatus : TimestampedEntity
{
    /// <summary>Stable i18n key from the mockup global status enum (e.g. "wait").</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public TicketState State { get; set; }

    /// <summary>osTicket mode bit 1 — status selectable in the UI.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>osTicket mode bit 2 — status only usable by the system, not agents.</summary>
    public bool IsInternal { get; set; }

    public int Sort { get; set; }

    /// <summary>osTicket properties.allowreopen.</summary>
    public bool AllowReopen { get; set; }

    /// <summary>Status a ticket transitions to when reopened (osTicket properties.reopenstatus).</summary>
    public int? ReopenStatusId { get; set; }

    public string? Description { get; set; }
}

/// <summary>osTicket ticket_status.state — the fixed lifecycle behind admin-defined statuses.</summary>
public enum TicketState
{
    Open,
    Closed,
    Archived,
    Deleted,
}

/// <summary>osTicket ticket_priority row for row.</summary>
public class TicketPriority : EntityBase
{
    /// <summary>Stable key (low/normal/high/emergency) mirroring osTicket's lookup names.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    /// <summary>Badge color, osTicket priority_color.</summary>
    public string? Color { get; set; }

    /// <summary>Lower value = more urgent (osTicket priority_urgency).</summary>
    public int Urgency { get; set; }

    public bool IsPublic { get; set; } = true;
}

public enum TicketSource
{
    Web,
    Email,
    Phone,
    Api,
    Other,
}

public class Ticket : TimestampedEntity
{
    /// <summary>Formatted public number from the sequence, e.g. "R716555".</summary>
    public required string Number { get; set; }

    /// <summary>osTicket keeps the subject in a dynamic form field; we promote it to a column.</summary>
    public required string Subject { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Which of the user's addresses opened the ticket (osTicket user_email_id).</summary>
    public int? UserEmailId { get; set; }

    public int StatusId { get; set; }
    public TicketStatus? Status { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? PriorityId { get; set; }
    public TicketPriority? Priority { get; set; }

    public int? SlaId { get; set; }
    public SlaPlan? Sla { get; set; }

    public int? HelpTopicId { get; set; }
    public HelpTopic? HelpTopic { get; set; }

    /// <summary>Assigned agent (osTicket staff_id; 0 in osTicket, null here).</summary>
    public int? StaffId { get; set; }
    public Staff? Staff { get; set; }

    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Mail account the ticket arrived on, for email-sourced tickets.</summary>
    public int? EmailAccountId { get; set; }

    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }

    /// <summary>Parent for merged/linked tickets (osTicket ticket_pid).</summary>
    public int? ParentId { get; set; }

    /// <summary>Order among merged children (osTicket sort).</summary>
    public int Sort { get; set; }

    public int? LockId { get; set; }

    public TicketSource Source { get; set; } = TicketSource.Other;
    public string? SourceExtra { get; set; }
    public string? IpAddress { get; set; }

    public bool IsOverdue { get; set; }
    public bool IsAnswered { get; set; }

    /// <summary>
    /// A matching ticket filter's "Otomatik Yanıtı Kapat" action suppressed the
    /// new-ticket auto-response (admin/filter-edit fle.aNoAutoresp, osTicket
    /// FA_DisableAutoResponse). Persisted at create; LIVE (S8): TicketMailHandler
    /// suppresses every autoresponse on a flagged ticket.
    /// </summary>
    public bool AutoResponseDisabled { get; set; }

    /// <summary>Agent-set hard due date (osTicket duedate).</summary>
    public DateTimeOffset? DueDate { get; set; }

    /// <summary>SLA-computed due date (osTicket est_duedate), recomputed by the SLA sweep.</summary>
    public DateTimeOffset? EstimatedDueDate { get; set; }

    public DateTimeOffset? ReopenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? LastUpdateAt { get; set; }

    public List<EffortProposal> EffortProposals { get; set; } = [];
}

/// <summary>
/// Efor Onayı — product-original flagship flow, no osTicket counterpart. One row per
/// proposal revision on a ticket; the highest <see cref="RevisionNo"/> is the active one.
/// State machine (enforced by EffortProposalService in S4):
/// none → pending → approved | rejected → (revise → pending again as a new revision).
/// </summary>
public class EffortProposal : TimestampedEntity
{
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>1-based, increments each revision; unique per ticket.</summary>
    public int RevisionNo { get; set; }

    public decimal Hours { get; set; }

    /// <summary>Agent's justification shown on the portal effort card.</summary>
    public string? Note { get; set; }

    public EffortState State { get; set; } = EffortState.Pending;

    public int ProposedByStaffId { get; set; }

    /// <summary>Portal user who approved/rejected.</summary>
    public int? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Mandatory on rejection when the settings gate requires it.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>Last pending-decision reminder mail instant (S8 effort reminder job;
    /// effort/reminder_days cadence). Null = never reminded for this revision.</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }
}

public enum EffortState
{
    Pending,
    Approved,
    Rejected,
    /// <summary>Agent withdrew the proposal before a decision.</summary>
    Withdrawn,
    /// <summary>Replaced by a newer revision.</summary>
    Superseded,
}
