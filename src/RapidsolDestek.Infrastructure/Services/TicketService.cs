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

    /// <summary>Agent ticket-open notify choice, consumed at outbound-mail send
    /// (S8): null/"all" = default, "user" = user autoresponse only, "none" = no
    /// mail for this create. Rides the TicketCreated/TicketAssigned events.</summary>
    public string? NotifyMode { get; init; }
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

    /// <summary><paramref name="suppressAlert"/> (S8): the agent ticket-open notify
    /// choice mutes the assignment alert for the assignment made on create.</summary>
    Task AssignAsync(int ticketId, int? staffId, int? teamId, ActorContext actor,
        bool suppressAlert = false, CancellationToken ct = default);

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
    IFilterEngine filters,
    IDomainEventDispatcher dispatcher) : ITicketService
{
    public async Task<Ticket> CreateAsync(TicketCreateRequest request, ActorContext actor, CancellationToken ct = default)
    {
        var topic = request.HelpTopicId is { } topicId
            ? await db.HelpTopics.Include(t => t.Parent).SingleOrDefaultAsync(t => t.Id == topicId, ct)
                ?? throw new DomainNotFoundException("HelpTopic", topicId)
            : null;

        // Ticket filters (admin/filters, S7): every active filter runs over the
        // incoming data before creation, gated by target channel (portal=Web,
        // agent-recorded phone/other only meet target "Any"; the mail pipeline is
        // TODO(S8) and will feed Source=Email + ReplyTo). A Reject action refuses
        // the whole create with a typed exception; routing actions override the
        // topic cascade below (osTicket parity: filter vars beat topic defaults).
        var sender = await db.Users.AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails
                    .Where(e => e.Id == (request.UserEmailId ?? u.DefaultEmailId))
                    .Select(e => e.Address).FirstOrDefault(),
                Org = u.Organization != null ? u.Organization.Name : null,
            })
            .SingleOrDefaultAsync(ct);
        var outcome = await filters.RunAsync(new FilterInput
        {
            Source = request.Source,
            EmailAccountId = request.EmailAccountId,
            Name = sender?.Name,
            Email = sender?.Email,
            Subject = request.Subject,
            Body = request.Body,
            TopicName = topic?.Name,
            OrgName = sender?.Org,
        }, ct);
        if (outcome.RejectedBy is { } rejectingFilter)
            throw new TicketRejectedByFilterException(rejectingFilter);

        // A filter's "Yardım Konusu Ata" swaps the topic BEFORE the cascade so the
        // new topic's own routing applies (unless a later filter value overrode it).
        if (outcome.TopicId is { } filterTopicId && filterTopicId != topic?.Id)
        {
            topic = await db.HelpTopics.Include(t => t.Parent)
                .SingleOrDefaultAsync(t => t.Id == filterTopicId, ct) ?? topic;
        }

        // Routing cascade: filter actions beat topic overrides beat request values
        // beat settings defaults.
        var departmentId = outcome.DepartmentId
            ?? topic?.DepartmentId
            ?? request.DepartmentId
            ?? int.Parse(await settings.GetAsync("core", "default_dept_id", ct)
                ?? throw new InvalidOperationException("core.default_dept_id is not configured"));

        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketCreate, departmentId, ct);

        // tickets.max_open_per_user (S7 admin/settings-tickets): end-user creates over
        // the limit are refused; staff/system creates bypass (osTicket parity).
        // LIVE (S8): the portal OpenController catch mails the refused user the
        // "overlimit notice" (TicketMailHandler.SendOverlimitNoticeAsync).
        var behavior = await settings.GetTicketBehaviorAsync(ct);
        if (!actor.IsStaff && behavior.MaxOpenPerUser > 0)
        {
            var openCount = await db.Tickets
                .Where(t => t.UserId == request.UserId)
                .CountAsync(t => db.TicketStatuses
                    .Any(s => s.Id == t.StatusId && s.State == TicketState.Open), ct);
            if (openCount >= behavior.MaxOpenPerUser)
                throw new DomainRuleException("max-open-exceeded",
                    $"User {request.UserId} already has {openCount} open tickets (limit {behavior.MaxOpenPerUser}).");
        }

        var numbering = await settings.GetTicketNumberingAsync(ct);
        var statusKey = numbering.DefaultStatusKey ?? "open";
        var statusId = outcome.StatusId
            ?? topic?.StatusId
            ?? await db.TicketStatuses.Where(s => s.Key == statusKey).Select(s => s.Id).SingleAsync(ct);

        // Filter "Durum Ata" pointing at a non-open status = honest auto-close:
        // the ticket is born closed with ClosedAt stamped (admin/filter-edit
        // fle.aStatus; osTicket FA_SetStatus semantics).
        DateTimeOffset? closedAt = null;
        if (outcome.StatusId is { } filterStatusId)
        {
            var filterStatusState = await db.TicketStatuses
                .Where(s => s.Id == filterStatusId).Select(s => (TicketState?)s.State).SingleOrDefaultAsync(ct);
            if (filterStatusState is null)
                statusId = topic?.StatusId
                    ?? await db.TicketStatuses.Where(s => s.Key == statusKey).Select(s => s.Id).SingleAsync(ct);
            else if (filterStatusState != TicketState.Open)
                closedAt = DateTimeOffset.UtcNow;
        }

        // Priority cascade ends at core.default_priority (S7; seeded "normal").
        var priorityId = outcome.PriorityId
            ?? topic?.PriorityId
            ?? request.PriorityId
            ?? await DefaultPriorityIdAsync(ct);
        var slaId = outcome.SlaId
            ?? topic?.SlaId
            ?? request.SlaId
            ?? ParseOrNull(await settings.GetAsync("core", "default_sla_id", ct));

        var number = await DrawNumberAsync(topic, numbering, ct);

        // SLA grace consumption (admin/slas sla.grace): the plan's grace period sets
        // the estimated due instant every due/overdue consumer reads
        // (DueDate ?? EstimatedDueDate). Wall-clock hours at create — LIVE (S8):
        // SlaOverdueSweepJob recomputes schedule-aware (the clock only runs inside
        // the plan's working schedule) and flags IsOverdue.
        var estimatedDue = await ComputeEstimatedDueAsync(slaId, DateTimeOffset.UtcNow, ct);

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
            // Vacation guard (profile Tatil Modu): filter/topic auto-assignment skips
            // an agent on vacation — the ticket still routes (department/team), only
            // the staff pin is dropped so it lands unassigned in the queue.
            StaffId = await StaffAvailability.FilterAutoAssignAsync(db, outcome.StaffId ?? topic?.StaffId, ct),
            TeamId = outcome.TeamId ?? topic?.TeamId,
            EmailAccountId = request.EmailAccountId,
            Source = request.Source,
            SourceExtra = request.SourceExtra,
            IpAddress = actor.IpAddress,
            DueDate = request.DueDate,
            EstimatedDueDate = estimatedDue,
            // Filter "Otomatik Yanıtı Kapat" — persisted flag, LIVE (S8): consumed
            // at autoresponse send time (TicketMailHandler suppression layer).
            AutoResponseDisabled = outcome.DisableAutoResponse,
            ClosedAt = closedAt,
            LastUpdateAt = DateTimeOffset.UtcNow,
            Thread = new Thread { CreatedAt = DateTimeOffset.UtcNow },
        };
        db.Tickets.Add(ticket);

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Message, request.Body, actor,
            new PostOptions { Source = request.Source.ToString(), Title = request.Subject }, ct);
        await threads.AddEventAsync(ticket.ThreadId, "created", actor, null, ct);

        // Filter note actions (seed canon type "note"): the configured text lands
        // as a SYSTEM internal note on the fresh thread.
        foreach (var note in outcome.Notes)
        {
            await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Note, note, ActorContext.System,
                new PostOptions { Source = request.Source.ToString() }, ct);
        }

        var events = new List<IDomainEvent>
        {
            new TicketCreated(ticket.Id, ticket.Number, ticket.UserId, ticket.DepartmentId,
                actor.IsStaff ? actor.Id : null, request.NotifyMode),
        };
        if (ticket.StaffId is not null || ticket.TeamId is not null)
        {
            await threads.AddEventAsync(ticket.ThreadId, "assigned", ActorContext.System,
                new { staffId = ticket.StaffId, teamId = ticket.TeamId }, ct);
            // Auto-assignment during create: the ticket-open notify choice covers
            // it — anything but the default suppresses the assignment alert too.
            events.Add(new TicketAssigned(ticket.Id, ticket.StaffId, ticket.TeamId, "SYSTEM",
                SuppressAlert: request.NotifyMode is "user" or "none"));
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

        // tickets.require_topic_to_close (S7 admin/settings-tickets): topic-less
        // tickets cannot be closed so reports stay consistent (mockup help text).
        if (closing && ticket.HelpTopicId is null
            && (await settings.GetTicketBehaviorAsync(ct)).RequireTopicToClose)
        {
            throw new DomainRuleException("topic-required-to-close",
                $"Ticket {ticketId} has no help topic; tickets.require_topic_to_close is on.");
        }

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

            // Department.DisableReopenAutoAssign (admin/department-edit
            // de.disableReopenAssign, osTicket FLAG_DISABLE_REOPEN_AUTO_ASSIGN):
            // a reopened ticket is NOT automatically given back to the last
            // assigned agent — the assignment is cleared on reopen.
            if (ticket.StaffId is not null
                && await db.Departments.Where(d => d.Id == ticket.DepartmentId)
                    .Select(d => d.DisableReopenAutoAssign).FirstOrDefaultAsync(ct))
            {
                ticket.StaffId = null;
            }
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        var eventName = closing ? "closed" : reopening ? "reopened" : "edited";
        await threads.AddEventAsync(ticket.ThreadId, eventName, actor, new { status = target.Key }, ct);
        await dispatcher.DispatchAsync([new TicketStatusChanged(ticket.Id, oldStatusId, statusId, actor.Name)], ct);
    }

    public async Task AssignAsync(int ticketId, int? staffId, int? teamId, ActorContext actor,
        bool suppressAlert = false, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(ticketId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketAssign, ticket.DepartmentId, ct);

        await ApplyAssignmentAsync(ticket, staffId, teamId, actor, suppressAlert, ct);
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

        await ApplyAssignmentAsync(ticket, actor.Id, ticket.TeamId, actor, suppressAlert: false, ct);
    }

    public async Task TransferAsync(int ticketId, int departmentId, ActorContext actor, CancellationToken ct = default)
    {
        var ticket = await LoadAsync(ticketId, ct);
        if (ticket.DepartmentId == departmentId)
            return;
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new DomainNotFoundException("Department", departmentId);

        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TicketTransfer, ticket.DepartmentId, ct);

        var oldDepartmentId = ticket.DepartmentId;
        ticket.DepartmentId = departmentId;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;

        // Transient SLA (admin/slas sla.transient, osTicket SLA flag 8): a transient
        // plan is replaced by a permanent one when the ticket changes department —
        // re-resolve topic SLA → new department SLA → system default, and recompute
        // the estimated due date under the replacement plan.
        if (ticket.SlaId is { } currentSlaId
            && await db.SlaPlans.Where(s => s.Id == currentSlaId).Select(s => s.IsTransient).SingleOrDefaultAsync(ct))
        {
            var topicSlaId = ticket.HelpTopicId is { } topicId
                ? await db.HelpTopics.Where(t => t.Id == topicId).Select(t => t.SlaId).SingleOrDefaultAsync(ct)
                : null;
            var newSlaId = topicSlaId
                ?? department.SlaId
                ?? ParseOrNull(await settings.GetAsync("core", "default_sla_id", ct));
            if (newSlaId is { } replacement && replacement != currentSlaId)
            {
                ticket.SlaId = replacement;
                ticket.EstimatedDueDate = await ComputeEstimatedDueAsync(replacement, DateTimeOffset.UtcNow, ct);
            }
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(ticket.ThreadId, "transferred", actor,
            new { from = oldDepartmentId, to = departmentId }, ct);
        await dispatcher.DispatchAsync([new TicketTransferred(ticket.Id, oldDepartmentId, departmentId,
            actor.IsStaff ? actor.Id : null)], ct);
    }

    public Task<bool> IsWorkAllowedAsync(int ticketId, CancellationToken ct = default) =>
        EffortWorkGate.IsWorkAllowedAsync(db, settings, ticketId, ct);

    private async Task ApplyAssignmentAsync(Ticket ticket, int? staffId, int? teamId, ActorContext actor,
        bool suppressAlert, CancellationToken ct)
    {
        // Vacation guard (profile Tatil Modu): covers Assign, Claim and the bulk paths.
        await StaffAvailability.EnsureAssignableAsync(db, staffId, ct);

        ticket.StaffId = staffId;
        ticket.TeamId = teamId;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(ticket.ThreadId, "assigned", actor, new { staffId, teamId }, ct);
        await dispatcher.DispatchAsync([new TicketAssigned(ticket.Id, staffId, teamId, actor.Name,
            actor.IsStaff ? actor.Id : null, suppressAlert)], ct);
    }

    private async Task<Ticket> LoadAsync(int ticketId, CancellationToken ct) =>
        await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new DomainNotFoundException("Ticket", ticketId);

    private async Task<int?> DefaultPriorityIdAsync(CancellationToken ct)
    {
        var key = await settings.GetAsync("core", "default_priority", ct);
        if (string.IsNullOrEmpty(key))
            return null;
        return await db.TicketPriorities.Where(p => p.Key == key)
            .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>SLA grace period → estimated due instant (wall-clock; S8 sweep will be schedule-aware).</summary>
    private async Task<DateTimeOffset?> ComputeEstimatedDueAsync(int? slaId, DateTimeOffset from, CancellationToken ct)
    {
        if (slaId is not { } id)
            return null;
        var grace = await db.SlaPlans.Where(s => s.Id == id)
            .Select(s => (int?)s.GracePeriodHours).SingleOrDefaultAsync(ct);
        return grace is { } hours and > 0 ? from.AddHours(hours) : null;
    }

    /// <summary>
    /// Public ticket number: the topic's own numbering wins — random digits when the
    /// topic asks for them (osTicket sequence_id 0), else its own sequence — then
    /// tickets.number_mode decides: "random" draws unguessable digits, anything else
    /// advances the configured row-locked sequence.
    /// </summary>
    private async Task<string> DrawNumberAsync(HelpTopic? topic, NumberingSettings numbering, CancellationToken ct)
    {
        var format = topic?.NumberFormat ?? numbering.NumberFormat;
        if (topic is { UseRandomNumbers: true })
            return await DrawRandomAsync(format, ct);
        if (topic?.SequenceId is { } topicSeq)
            return await sequences.NextAsync(topicSeq, format, ct);

        var mode = await settings.GetAsync("tickets", "number_mode", ct);
        if (mode != "random")
            return await sequences.NextAsync(numbering.SequenceId, format, ct);
        return await DrawRandomAsync(format, ct);
    }

    /// <summary>Fill every '#' with random digits; retry on the (unlikely) collision.</summary>
    private async Task<string> DrawRandomAsync(string format, CancellationToken ct)
    {
        var digits = Math.Max(1, format.Count(c => c == '#'));
        for (var attempt = 0; ; attempt++)
        {
            var value = (long)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue)
                % Math.Pow(10, Math.Min(digits, 9)));
            var candidate = TicketNumberFormatter.Format(format, value);
            if (!await db.Tickets.AnyAsync(t => t.Number == candidate, ct))
                return candidate;
            if (attempt >= 9)
                throw new InvalidOperationException("Could not draw a unique random ticket number after 10 attempts.");
        }
    }

    private static int? ParseOrNull(string? value) =>
        int.TryParse(value, out var i) ? i : null;
}
