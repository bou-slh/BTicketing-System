using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;

namespace RapidsolDestek.Infrastructure.Services;

public interface IEffortProposalService
{
    /// <summary>Staff proposes hours; may auto-approve under the settings threshold.</summary>
    Task<EffortProposal> ProposeAsync(int ticketId, decimal hours, string? note, ActorContext actor, CancellationToken ct = default);

    /// <summary>Supersedes the pending proposal with a new pending revision.</summary>
    Task<EffortProposal> ReviseAsync(int ticketId, decimal hours, string? note, ActorContext actor, CancellationToken ct = default);

    Task WithdrawAsync(int ticketId, ActorContext actor, CancellationToken ct = default);

    /// <summary>Ticket owner approves the pending proposal (portal "Onayla", one click).</summary>
    Task ApproveAsync(int ticketId, ActorContext actor, CancellationToken ct = default);

    /// <summary>Ticket owner rejects (portal "Reddet"); note mandatory per settings gate.</summary>
    Task RejectAsync(int ticketId, string? note, ActorContext actor, CancellationToken ct = default);

    /// <summary>Latest revision — its state is the loop state (none when no proposals).</summary>
    Task<EffortProposal?> GetActiveAsync(int ticketId, CancellationToken ct = default);
}

/// <summary>
/// Efor Onayı (B8) — the flagship flow, product-original (no osTicket counterpart).
/// State legality lives in the pure <see cref="EffortStateMachine"/>; this service adds
/// actor rights, settings gates, thread events, and domain events. Mutations run in a
/// transaction with the ticket row locked so racing decisions can't both win.
/// </summary>
public sealed class EffortProposalService(
    AppDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    IThreadService threads,
    IDomainEventDispatcher dispatcher) : IEffortProposalService
{
    public Task<EffortProposal> ProposeAsync(int ticketId, decimal hours, string? note, ActorContext actor, CancellationToken ct = default) =>
        ProposeCoreAsync(ticketId, hours, note, actor, EffortAction.Propose, ct);

    public Task<EffortProposal> ReviseAsync(int ticketId, decimal hours, string? note, ActorContext actor, CancellationToken ct = default) =>
        ProposeCoreAsync(ticketId, hours, note, actor, EffortAction.Revise, ct);

    private async Task<EffortProposal> ProposeCoreAsync(int ticketId, decimal hours, string? note,
        ActorContext actor, EffortAction action, CancellationToken ct)
    {
        if (hours <= 0)
            throw new ArgumentOutOfRangeException(nameof(hours), "Effort hours must be positive.");

        return await InTicketTransactionAsync(ticketId, async ticket =>
        {
            await permissions.EnsureAsync(actor, PermissionKeys.EffortPropose, ticket.DepartmentId, ct);

            var effort = await settings.GetEffortAsync(ct);
            var revisions = await db.EffortProposals.CountAsync(p => p.TicketId == ticketId, ct);
            var active = await GetActiveAsync(ticketId, ct);

            Validate(action, active?.State, effort, await TicketOpenAsync(ticket, ct), revisions, noteProvided: true);

            if (action == EffortAction.Revise && active is not null)
                active.State = EffortState.Superseded;

            var autoApproved = effort.AutoApproveThresholdHours > 0 && hours <= effort.AutoApproveThresholdHours;
            var proposal = new EffortProposal
            {
                TicketId = ticketId,
                RevisionNo = revisions + 1,
                Hours = hours,
                Note = note,
                ProposedByStaffId = actor.Id!.Value,
                State = autoApproved ? EffortState.Approved : EffortState.Pending,
                DecidedAt = autoApproved ? DateTimeOffset.UtcNow : null,
            };
            db.EffortProposals.Add(proposal);

            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);

            var eventName = action == EffortAction.Revise ? "effort-revised" : "effort-proposed";
            await threads.AddEventAsync(ticket.ThreadId, eventName, actor,
                new { hours, revision = proposal.RevisionNo, note }, ct);

            var events = new List<IDomainEvent>
            {
                action == EffortAction.Revise
                    ? new EffortRevised(ticketId, proposal.Id, proposal.RevisionNo, hours)
                    : new EffortProposed(ticketId, proposal.Id, proposal.RevisionNo, hours),
            };

            if (autoApproved)
            {
                // Under the auto-approve threshold: decided by the system, no request email.
                await threads.AddEventAsync(ticket.ThreadId, "effort-approved", ActorContext.System,
                    new { hours, revision = proposal.RevisionNo, auto = true }, ct);
                events.Add(new EffortApproved(ticketId, proposal.Id, proposal.RevisionNo, AutoApproved: true));
            }

            await dispatcher.DispatchAsync(events, ct);
            return proposal;
        }, ct);
    }

    public async Task WithdrawAsync(int ticketId, ActorContext actor, CancellationToken ct = default)
    {
        await InTicketTransactionAsync(ticketId, async ticket =>
        {
            await permissions.EnsureAsync(actor, PermissionKeys.EffortPropose, ticket.DepartmentId, ct);

            var effort = await settings.GetEffortAsync(ct);
            var active = await GetActiveAsync(ticketId, ct);
            Validate(EffortAction.Withdraw, active?.State, effort, await TicketOpenAsync(ticket, ct),
                existingRevisions: 0, noteProvided: true);

            active!.State = EffortState.Withdrawn;
            active.DecidedAt = DateTimeOffset.UtcNow;

            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);

            await threads.AddEventAsync(ticket.ThreadId, "effort-withdrawn", actor,
                new { hours = active.Hours, revision = active.RevisionNo }, ct);
            await dispatcher.DispatchAsync([new EffortWithdrawn(ticketId, active.Id, active.RevisionNo)], ct);
            return active;
        }, ct);
    }

    public async Task ApproveAsync(int ticketId, ActorContext actor, CancellationToken ct = default)
    {
        await DecideAsync(ticketId, actor, approve: true, note: null, ct);
    }

    public async Task RejectAsync(int ticketId, string? note, ActorContext actor, CancellationToken ct = default)
    {
        await DecideAsync(ticketId, actor, approve: false, note, ct);
    }

    private async Task DecideAsync(int ticketId, ActorContext actor, bool approve, string? note, CancellationToken ct)
    {
        await InTicketTransactionAsync(ticketId, async ticket =>
        {
            // Only the ticket's owner decides (B8; collaborators are v2).
            if (!actor.IsUser || actor.Id != ticket.UserId)
                throw new PermissionDeniedException("effort.decide", ticket.DepartmentId);

            var effort = await settings.GetEffortAsync(ct);
            var active = await GetActiveAsync(ticketId, ct);
            Validate(approve ? EffortAction.Approve : EffortAction.Reject, active?.State, effort,
                await TicketOpenAsync(ticket, ct), existingRevisions: 0,
                noteProvided: !string.IsNullOrWhiteSpace(note));

            active!.State = approve ? EffortState.Approved : EffortState.Rejected;
            active.DecidedByUserId = actor.Id;
            active.DecidedAt = DateTimeOffset.UtcNow;
            active.DecisionNote = note;
            ticket.LastUpdateAt = DateTimeOffset.UtcNow;

            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);

            await threads.AddEventAsync(ticket.ThreadId,
                approve ? "effort-approved" : "effort-rejected", actor,
                new { hours = active.Hours, revision = active.RevisionNo, note }, ct);

            // A ticket parked in "wait" returns to "open" once the customer decides.
            var statusKey = await db.TicketStatuses.Where(s => s.Id == ticket.StatusId)
                .Select(s => s.Key).SingleAsync(ct);
            if (approve && statusKey == "wait")
            {
                var openId = await db.TicketStatuses.Where(s => s.Key == "open").Select(s => s.Id).SingleAsync(ct);
                ticket.StatusId = openId;
                await db.SaveChangesAsync(ct);
            }

            await dispatcher.DispatchAsync([
                approve
                    ? new EffortApproved(ticketId, active.Id, active.RevisionNo, AutoApproved: false)
                    : new EffortRejected(ticketId, active.Id, active.RevisionNo, note),
            ], ct);
            return active;
        }, ct);
    }

    public Task<EffortProposal?> GetActiveAsync(int ticketId, CancellationToken ct = default) =>
        db.EffortProposals
            .Where(p => p.TicketId == ticketId)
            .OrderByDescending(p => p.RevisionNo)
            .FirstOrDefaultAsync(ct);

    private static void Validate(EffortAction action, EffortState? activeState, EffortSettings effort,
        bool ticketOpen, int existingRevisions, bool noteProvided)
    {
        var error = EffortStateMachine.Validate(action, activeState,
            effort.Enabled, ticketOpen, existingRevisions, effort.RevisionLimit,
            noteProvided, effort.MandatoryRejectNote);
        if (error != EffortError.None)
            throw new EffortException(error);
    }

    private async Task<bool> TicketOpenAsync(Ticket ticket, CancellationToken ct) =>
        await db.TicketStatuses.Where(s => s.Id == ticket.StatusId)
            .Select(s => s.State == TicketState.Open)
            .SingleAsync(ct);

    /// <summary>Runs the mutation with the ticket row locked (FOR UPDATE) in one transaction.</summary>
    private async Task<EffortProposal> InTicketTransactionAsync(int ticketId,
        Func<Ticket, Task<EffortProposal>> action, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(ct);
        try
        {
            var ticket = await db.Tickets
                .FromSql($"SELECT * FROM tickets WHERE id = {ticketId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
                ?? throw new DomainNotFoundException("Ticket", ticketId);

            var result = await action(ticket);

            if (ownsTransaction)
                await tx.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (ownsTransaction)
                await tx.DisposeAsync();
        }
    }
}
