using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// S8 recurring SLA/overdue sweep (osTicket cron TicketMonitor → Ticket::checkOverdue,
/// modeled not copied). Two passes over OPEN tickets:
///
/// 1. Recompute <see cref="Ticket.EstimatedDueDate"/> for tickets under an SLA —
///    osTicket getSLADueDate semantics: base = reopen date ?? create date, plus the
///    plan's grace period. When the plan carries a schedule the clock only runs while
///    the schedule is open (<see cref="ScheduleEvaluator.AddWorkingHours"/>; entries
///    minus holidays, schedule-local wall clock); a schedule that never opens within
///    the horizon falls back to wall-clock, exactly like osTicket addGracePeriod. No
///    schedule = plain wall-clock — the create-time stamp (TicketService) already says
///    that, so the recompute converges. A recompute that moves the SLA due date back
///    into the future clears a stale overdue flag (osTicket updateEstDueDate parity)
///    unless a past hard due date still holds the ticket overdue.
///
/// 2. Mark overdue (checkOverdue parity): open + not yet flagged + (hard due date
///    past, or no hard due date and SLA due past — the hard date always wins). Each
///    hit sets the flag, logs the "overdue" thread event and raises
///    <see cref="TicketOverdue"/> exactly once — the flag itself is the once-guard,
///    so later sweeps re-alert nothing. The flag clears on close
///    (TicketService.TransitionStatusAsync), never on reply (osTicket parity:
///    replies touch isanswered, not isoverdue). Batch capped at 100 per run like
///    osTicket's cron.
/// </summary>
public sealed class SlaOverdueSweepJob(
    AppDbContext db,
    ISettingsService settings,
    IThreadService threads,
    IDomainEventDispatcher dispatcher,
    TimeProvider clock,
    ILogger<SlaOverdueSweepJob> logger)
{
    /// <summary>Every 5 minutes — osTicket's recommended 5-minute cron cadence.</summary>
    public const string Cron = "*/5 * * * *";

    /// <summary>osTicket checkOverdue batch limit.</summary>
    private const int MarkBatchSize = 100;

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // ---- pass 1: schedule-aware EstimatedDueDate recompute ---------------------
        var slaTickets = await db.Tickets
            .Where(t => t.SlaId != null
                && db.TicketStatuses.Any(s => s.Id == t.StatusId && s.State == TicketState.Open))
            .ToListAsync(ct);

        var slaIds = slaTickets.Select(t => t.SlaId!.Value).Distinct().ToList();
        var plans = await db.SlaPlans
            .Where(s => slaIds.Contains(s.Id))
            .Include(s => s.Schedule)!.ThenInclude(s => s!.Entries)
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, ct);
        // Schedule timezone fallback chain ends at the admin system timezone.
        var systemTz = await settings.GetAsync("system", "timezone", ct);

        var recomputed = 0;
        foreach (var ticket in slaTickets)
        {
            if (!plans.TryGetValue(ticket.SlaId!.Value, out var plan))
                continue; // plan row vanished — soft ref, leave the stamp alone

            var estimated = ComputeDue(plan, ticket.ReopenedAt ?? ticket.CreatedAt, systemTz);
            if (estimated == ticket.EstimatedDueDate)
                continue;

            ticket.EstimatedDueDate = estimated;
            recomputed++;
            // updateEstDueDate parity: a due date pushed back into the future clears
            // the stale flag — unless a past hard due date keeps the ticket overdue.
            if (ticket.IsOverdue
                && (ticket.DueDate is null || ticket.DueDate > now)
                && estimated is { } due && due > now)
            {
                ticket.IsOverdue = false;
            }
        }
        if (recomputed > 0)
            await db.SaveChangesAsync(ct);

        // ---- pass 2: mark overdue + alert once (checkOverdue parity) ---------------
        var overdue = await db.Tickets
            .Where(t => !t.IsOverdue
                && db.TicketStatuses.Any(s => s.Id == t.StatusId && s.State == TicketState.Open)
                && ((t.DueDate != null && t.DueDate < now)
                    || (t.DueDate == null && t.EstimatedDueDate != null && t.EstimatedDueDate < now)))
            .OrderBy(t => t.Id)
            .Take(MarkBatchSize)
            .ToListAsync(ct);

        foreach (var ticket in overdue)
            ticket.IsOverdue = true;
        if (overdue.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var ticket in overdue)
                await threads.AddEventAsync(ticket.ThreadId, "overdue", ActorContext.System, null, ct);
            // Exactly once per ticket: the flag guards re-dispatch on later sweeps.
            await dispatcher.DispatchAsync(
                [.. overdue.Select(t => (IDomainEvent)new TicketOverdue(t.Id))], ct);
        }

        if (recomputed > 0 || overdue.Count > 0)
        {
            logger.LogInformation(
                "SLA sweep: {Recomputed} due dates recomputed, {Marked} tickets marked overdue",
                recomputed, overdue.Count);
        }
    }

    /// <summary>Grace period → due instant: schedule-aware when the plan carries a
    /// schedule (wall-clock fallback when it never opens), wall-clock otherwise.</summary>
    private static DateTimeOffset? ComputeDue(SlaPlan plan, DateTimeOffset from, string? systemTz)
    {
        if (plan.GracePeriodHours <= 0)
            return null;
        if (plan.Schedule is { } schedule)
        {
            var tz = ScheduleEvaluator.ResolveTimeZone(schedule.Timezone, systemTz);
            if (ScheduleEvaluator.AddWorkingHours(schedule, from, plan.GracePeriodHours, tz) is { } due)
                return due.ToUniversalTime(); // Npgsql timestamptz wants offset 0
        }
        return from.AddHours(plan.GracePeriodHours);
    }
}
