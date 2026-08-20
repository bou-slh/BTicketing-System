namespace RapidsolDestek.Domain.Events;

/// <summary>
/// In-process domain events raised by the S4 services after their transaction commits.
/// S8 (email pipeline) and B7 (SignalR live board) subscribe via handlers; events carry
/// ids and small snapshots, never tracked entities.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEventBase : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// <paramref name="ActorStaffId"/> (S8): non-null = agent-opened (ticket.notice path,
/// skip-actor rule); null = end-user/system create. <paramref name="NotifyMode"/> is
/// the agent ticket-open notify choice consumed at send time: "all"/null = default,
/// "user" = user autoresponse only (staff alerts suppressed), "none" = no mail at all
/// for this create.
/// </summary>
public sealed record TicketCreated(
    int TicketId, string Number, int UserId, int DepartmentId,
    int? ActorStaffId = null, string? NotifyMode = null) : DomainEventBase;

public sealed record TicketStatusChanged(int TicketId, int OldStatusId, int NewStatusId, string ActorName) : DomainEventBase;

/// <summary>Raised for both explicit assignment and self-claim. S8:
/// <paramref name="ActorStaffId"/> feeds the skip-actor alert rule (a self-claim
/// alerts nobody); <paramref name="SuppressAlert"/> = the agent ticket-open notify
/// choice silenced alerts for the create this assignment belongs to.</summary>
public sealed record TicketAssigned(
    int TicketId, int? StaffId, int? TeamId, string ActorName,
    int? ActorStaffId = null, bool SuppressAlert = false) : DomainEventBase;

public sealed record TicketTransferred(
    int TicketId, int OldDepartmentId, int NewDepartmentId, int? ActorStaffId = null) : DomainEventBase;

public sealed record ThreadEntryAdded(int ThreadId, int EntryId, Entities.ThreadEntryType Type, int? TicketId) : DomainEventBase;

public sealed record EffortProposed(int TicketId, int ProposalId, int RevisionNo, decimal Hours) : DomainEventBase;

public sealed record EffortRevised(int TicketId, int ProposalId, int RevisionNo, decimal Hours) : DomainEventBase;

public sealed record EffortWithdrawn(int TicketId, int ProposalId, int RevisionNo) : DomainEventBase;

public sealed record EffortApproved(int TicketId, int ProposalId, int RevisionNo, bool AutoApproved) : DomainEventBase;

public sealed record EffortRejected(int TicketId, int ProposalId, int RevisionNo, string? Note) : DomainEventBase;

/// <summary>Declared for the S8 SLA sweep contract; the S8 slice-2 mail handler is
/// subscribed and tested — the sweep that raises it is the follow-up slice.</summary>
public sealed record TicketOverdue(int TicketId) : DomainEventBase;

// ---- Task events (S8 alert fan-out; raised by TaskService) --------------------------

public sealed record TaskCreated(int TaskId, string Number, int DepartmentId, int? ActorStaffId = null) : DomainEventBase;

public sealed record TaskAssigned(int TaskId, int? StaffId, int? TeamId, int? ActorStaffId = null) : DomainEventBase;

public sealed record TaskTransferred(int TaskId, int OldDepartmentId, int NewDepartmentId, int? ActorStaffId = null) : DomainEventBase;

/// <summary>Declared for the S8 sweep contract (TicketOverdue twin); the mail
/// handler is subscribed — the sweep that raises it is the follow-up slice.</summary>
public sealed record TaskOverdue(int TaskId) : DomainEventBase;
