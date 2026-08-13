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

public sealed record TicketCreated(int TicketId, string Number, int UserId, int DepartmentId) : DomainEventBase;

public sealed record TicketStatusChanged(int TicketId, int OldStatusId, int NewStatusId, string ActorName) : DomainEventBase;

/// <summary>Raised for both explicit assignment and self-claim.</summary>
public sealed record TicketAssigned(int TicketId, int? StaffId, int? TeamId, string ActorName) : DomainEventBase;

public sealed record TicketTransferred(int TicketId, int OldDepartmentId, int NewDepartmentId) : DomainEventBase;

public sealed record ThreadEntryAdded(int ThreadId, int EntryId, Entities.ThreadEntryType Type, int? TicketId) : DomainEventBase;

public sealed record EffortProposed(int TicketId, int ProposalId, int RevisionNo, decimal Hours) : DomainEventBase;

public sealed record EffortRevised(int TicketId, int ProposalId, int RevisionNo, decimal Hours) : DomainEventBase;

public sealed record EffortWithdrawn(int TicketId, int ProposalId, int RevisionNo) : DomainEventBase;

public sealed record EffortApproved(int TicketId, int ProposalId, int RevisionNo, bool AutoApproved) : DomainEventBase;

public sealed record EffortRejected(int TicketId, int ProposalId, int RevisionNo, string? Note) : DomainEventBase;

/// <summary>Declared for the S8 SLA sweep contract; S4 never raises it.</summary>
public sealed record TicketOverdue(int TicketId) : DomainEventBase;
