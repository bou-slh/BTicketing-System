using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// S8 recurring task overdue sweep. Tasks carry only a hard due date (osTicket task
/// has no SLA leg), so this is checkOverdue's simple half: open tasks past their due
/// date get the flag, an "overdue" thread event and one <see cref="TaskOverdue"/>
/// dispatch — the flag is the once-guard, later sweeps re-alert nothing. The flag
/// clears on close (TaskService.CloseAsync), matching the ticket rule.
/// </summary>
public sealed class TaskOverdueSweepJob(
    AppDbContext db,
    IThreadService threads,
    IDomainEventDispatcher dispatcher,
    TimeProvider clock,
    ILogger<TaskOverdueSweepJob> logger)
{
    /// <summary>Every 5 minutes — same cadence as the ticket sweep.</summary>
    public const string Cron = "*/5 * * * *";

    /// <summary>Batch cap per run (osTicket checkOverdue precedent).</summary>
    private const int MarkBatchSize = 100;

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var overdue = await db.TaskItems
            .Where(t => t.ClosedAt == null && !t.IsOverdue
                && t.DueDate != null && t.DueDate < now)
            .OrderBy(t => t.Id)
            .Take(MarkBatchSize)
            .ToListAsync(ct);
        if (overdue.Count == 0)
            return;

        foreach (var task in overdue)
            task.IsOverdue = true;
        await db.SaveChangesAsync(ct);

        foreach (var task in overdue)
            await threads.AddEventAsync(task.ThreadId, "overdue", ActorContext.System, null, ct);
        await dispatcher.DispatchAsync(
            [.. overdue.Select(t => (IDomainEvent)new TaskOverdue(t.Id))], ct);

        logger.LogInformation("Task sweep: {Marked} tasks marked overdue", overdue.Count);
    }
}
