using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Input for <see cref="ITicketService.CreateAsync"/> (osTicket Ticket::create).</summary>
public sealed record TicketCreateRequest
{
    public required int UserId { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public int? HelpTopicId { get; init; }
    public int? DepartmentId { get; init; }
    public int? PriorityId { get; init; }
    public int? SlaId { get; init; }
    public int? UserEmailId { get; init; }
    public int? EmailAccountId { get; init; }
    public TicketSource Source { get; init; } = TicketSource.Web;
    public string? SourceExtra { get; init; }
    public DateTimeOffset? DueDate { get; init; }
}

public interface ITicketService
{
    /// <summary>
    /// Creates and routes a ticket: help-topic cascade (dept/priority/SLA/status/
    /// assignee/numbering, osTicket parity) over settings defaults, draws the public
    /// number from the row-locked sequence, opens the thread with the first message.
    /// </summary>
    Task<Ticket> CreateAsync(TicketCreateRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>Status change with permission + B8 work-gate checks; stamps Closed/Reopened.</summary>
    Task TransitionStatusAsync(int ticketId, int statusId, ActorContext actor, CancellationToken ct = default);

    Task AssignAsync(int ticketId, int? staffId, int? teamId, ActorContext actor, CancellationToken ct = default);

    /// <summary>Self-assign ("Üstlen"); allowed for any visible unassigned ticket.</summary>
    Task ClaimAsync(int ticketId, ActorContext actor, CancellationToken ct = default);

    Task TransferAsync(int ticketId, int departmentId, ActorContext actor, CancellationToken ct = default);

    /// <summary>False while effort.block_work_until_approved holds a pending proposal over the ticket.</summary>
    Task<bool> IsWorkAllowedAsync(int ticketId, CancellationToken ct = default);
}

public sealed class TicketService(
    AppDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    ISequenceNumberService sequences,
    IThreadService threads,
    IDomainEventDispatcher dispatcher) : ITicketService
{
    public async Task<Ticket> CreateAsync(TicketCreateRequest request, ActorContext actor, CancellationToken ct = default)
    {
        var topic = request.HelpTopicId is { } topicId
            ? await db.HelpTopics.Include(t => t.Parent).SingleOrDefaultAsync(t => t.Id == topicId, ct)
                ?? throw new DomainNotFoundException("HelpTopic", topicId)
            : null;

        // Routing cascade: topic overrides beat request values beat settings defaults.
        var departmentId = topic?.DepartmentId
            ?? request.DepartmentId
            ?? int.Parse(await settings.GetAsync("core", "default_dept_id", ct)
                ?? throw new InvalidOperationException("core.default_dept_id is not configured"));

        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketCreate, departmentId, ct);

        var numbering = await settings.GetTicketNumberingAsync(ct);
        var statusKey = numbering.DefaultStatusKey ?? "open";
        var statusId = topic?.StatusId
            ?? await db.TicketStatuses.Where(s => s.Key == statusKey).Select(s => s.Id).SingleAsync(ct);

        var priorityId = topic?.PriorityId ?? request.PriorityId;
        var slaId = topic?.SlaId
            ?? request.SlaId
            ?? ParseOrNull(await settings.GetAsync("core", "default_sla_id", ct));

        var number = await sequences.NextAsync(
            topic?.SequenceId ?? numbering.SequenceId,
            topic?.NumberFormat ?? numbering.NumberFormat, ct);

        var ticket = new Ticket
        {
            Number = number,
            Subject = request.Subject,
            UserId = request.UserId,
            UserEmailId = request.UserEmailId,
            StatusId = statusId,
            DepartmentId = departmentId,
            PriorityId = priorityId,
            SlaId = slaId,
            HelpTopicId = topic?.Id,
            // Vacation guard (profile Tatil Modu): topic auto-assignment skips an agent
            // on vacation — the ticket still routes (department/team), only the staff
            // pin is dropped so it lands unassigned in the queue.
            StaffId = await StaffAvailability.FilterAutoAssignAsync(db, topic?.StaffId, ct),
            TeamId = topic?.TeamId,
            EmailAccountId = request.EmailAccountId,
            Source = request.Source,
            SourceExtra = request.SourceExtra,
            IpAddress = actor.IpAddress,
            DueDate = request.DueDate,
            LastUpdateAt = DateTimeOffset.UtcNow,
            Thread = new Thread { CreatedAt = DateTimeOffset.UtcNow },
        };
        db.Tickets.Add(ticket);

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Message, request.Body, actor,
            new PostOptions { Source = request.Source.ToString(), Title = request.Subject }, ct);
        await threads.AddEventAsync(ticket.ThreadId, "created", actor, null, ct);

        var events = new List<IDomainEvent>
        {
            new TicketCreated(ticket.Id, ticket.Number, ticket.UserId, ticket.DepartmentId),
        };
        if (ticket.StaffId is not null || ticket.TeamId is not null)
        {
            await threads.AddEventAsync(ticket.ThreadId, "assigned", ActorContext.System,
                new { staffId = ticket.StaffId, teamId = ticket.TeamId }, ct);
            events.Add(new TicketAssigned(ticket.Id, ticket.StaffId, ticket.TeamId, "SYSTEM"));
        }

        await dispatcher.DispatchAsync(events, ct);
        return ticket;
    }

    public async Task TransitionStatusAsync(int ticketId, int statusId, ActorContext actor, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(ticketId, ct);
        var target = await db.TicketStatuses.SingleOrDefaultAsync(s => s.Id == statusId, ct)
            ?? throw new DomainNotFoundException("TicketStatus", statusId);
        if (ticket.StatusId == statusId)
            return;

        var current = await db.TicketStatuses.SingleAsync(s => s.Id == ticket.StatusId, ct);
        var closing = current.State == TicketState.Open && target.State != TicketState.Open;
        var reopening = current.State != TicketState.Open && target.State == TicketState.Open;

        if (actor.IsStaff)
        {
            await permissions.EnsureAsync(actor,
                closing || reopening ? PermissionKeys.TicketClose : PermissionKeys.TicketEdit,
                ticket.DepartmentId, ct);
        }

        // B8 gate: can't resolve/close while an effort proposal awaits the customer.
        if (closing && !await IsWorkAllowedAsync(ticketId, ct))
            throw new WorkBlockedByEffortException(ticketId);

        if (reopening && !current.AllowReopen && actor.IsStaff && !(await permissions.ResolveAsync(actor.Id!.Value, ct)).IsAdmin)
            throw new PermissionDeniedException(PermissionKeys.TicketClose, ticket.DepartmentId);

        var oldStatusId = ticket.StatusId;
        ticket.StatusId = statusId;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;
        if (closing)
        {
            ticket.ClosedAt = DateTimeOffset.UtcNow;
            ticket.IsOverdue = false;
        }
        if (reopening)
        {
            ticket.ClosedAt = null;
            ticket.ReopenedAt = DateTimeOffset.UtcNow;
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        var eventName = closing ? "closed" : reopening ? "reopened" : "edited";
        await threads.AddEventAsync(ticket.ThreadId, eventName, actor, new { status = target.Key }, ct);
        await dispatcher.DispatchAsync([new TicketStatusChanged(ticket.Id, oldStatusId, statusId, actor.Name)], ct);
    }

    public async Task AssignAsync(int ticketId, int? staffId, int? teamId, ActorContext actor, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(ticketId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketAssign, ticket.DepartmentId, ct);

        await ApplyAssignmentAsync(ticket, staffId, teamId, actor, ct);
    }

    public async Task ClaimAsync(int ticketId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsStaff)
            throw new PermissionDeniedException(PermissionKeys.TicketAssign);

        var ticket = await LoadAsync(ticketId, ct);
        // osTicket claim parity: self-assign needs no assign permission, only an
        // unassigned ticket in a visible department.
        if (ticket.StaffId is not null)
            throw new PermissionDeniedException(PermissionKeys.TicketAssign, ticket.DepartmentId);
        var set = await permissions.ResolveAsync(actor.Id!.Value, ct);
        if (!set.IsAdmin && !set.DepartmentIds.Contains(ticket.DepartmentId))
            throw new PermissionDeniedException(PermissionKeys.TicketAssign, ticket.DepartmentId);

        await ApplyAssignmentAsync(ticket, actor.Id, ticket.TeamId, actor, ct);
    }

    public async Task TransferAsync(int ticketId, int departmentId, ActorContext actor, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(ticketId, ct);
        if (ticket.DepartmentId == departmentId)
            return;
        _ = await db.Departments.SingleOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new DomainNotFoundException("Department", departmentId);

        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketTransfer, ticket.DepartmentId, ct);

        var oldDepartmentId = ticket.DepartmentId;
        ticket.DepartmentId = departmentId;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(ticket.ThreadId, "transferred", actor,
            new { from = oldDepartmentId, to = departmentId }, ct);
        await dispatcher.DispatchAsync([new TicketTransferred(ticket.Id, oldDepartmentId, departmentId)], ct);
    }

    public Task<bool> IsWorkAllowedAsync(int ticketId, CancellationToken ct = default) =>
        EffortWorkGate.IsWorkAllowedAsync(db, settings, ticketId, ct);

    private async Task ApplyAssignmentAsync(Ticket ticket, int? staffId, int? teamId, ActorContext actor, CancellationToken ct)
    {
        // Vacation guard (profile Tatil Modu): covers Assign, Claim and the bulk paths.
        await StaffAvailability.EnsureAssignableAsync(db, staffId, ct);

        ticket.StaffId = staffId;
        ticket.TeamId = teamId;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(ticket.ThreadId, "assigned", actor, new { staffId, teamId }, ct);
        await dispatcher.DispatchAsync([new TicketAssigned(ticket.Id, staffId, teamId, actor.Name)], ct);
    }

    private async Task<Ticket> LoadAsync(int ticketId, CancellationToken ct) =>
        await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new DomainNotFoundException("Ticket", ticketId);

    private static int? ParseOrNull(string? value) =>
        int.TryParse(value, out var i) ? i : null;
}
