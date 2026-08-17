using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Input for <see cref="ITaskService.CreateAsync"/> (agent/tasks.html dlg-newtask fields).</summary>
public sealed record TaskCreateRequest
{
    public required string Title { get; init; }
    public required int DepartmentId { get; init; }
    public int? StaffId { get; init; }
    public int? TicketId { get; init; }
    public DateTimeOffset? DueDate { get; init; }

    /// <summary>Optional first thread entry (mockup ta.fDescHelp promise).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Task mutations for agent/tasks.html + task-view.html (ROADMAP §6.2), shaped after
/// <see cref="TicketService"/>: permission checks per department, AuditEvent via the
/// interceptor (actor audit scope), thread events through <see cref="IThreadService"/>.
/// </summary>
public interface ITaskService
{
    /// <summary>Base visibility scope (QueueEngine ticket parity): staff see their departments' tasks plus their own assignments; admins all.</summary>
    Task<IQueryable<TaskItem>> VisibleAsync(ActorContext actor, CancellationToken ct = default);

    /// <summary>Creates a task with its thread, drawing the number from the task sequence; the description becomes the first entry.</summary>
    Task<TaskItem> CreateAsync(TaskCreateRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>Kapat: stamps ClosedAt (+ optional internal note). No-op when already closed.</summary>
    Task CloseAsync(int taskId, string? note, ActorContext actor, CancellationToken ct = default);

    Task AssignAsync(int taskId, int? staffId, int? teamId, ActorContext actor, CancellationToken ct = default);

    Task TransferAsync(int taskId, int departmentId, ActorContext actor, CancellationToken ct = default);

    /// <summary>Düzenle: plain fields only (title + due date; the rest have their own actions).</summary>
    Task UpdateAsync(int taskId, string title, DateTimeOffset? dueDate, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Sil: hard delete of the task and its thread (osTicket task delete parity — the
    /// entity has no soft-delete flag). Entries/events cascade at the DB level.
    /// </summary>
    Task DeleteAsync(int taskId, ActorContext actor, CancellationToken ct = default);
}

public sealed class TaskService(
    AppDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    ISequenceNumberService sequences,
    IThreadService threads) : ITaskService
{
    public async Task<IQueryable<TaskItem>> VisibleAsync(ActorContext actor, CancellationToken ct = default)
    {
        IQueryable<TaskItem> query = db.TaskItems;
        if (!actor.IsStaff)
            return query.Where(_ => false);

        var set = await permissions.ResolveAsync(actor.Id!.Value, ct);
        if (!set.IsAdmin)
        {
            var depts = set.DepartmentIds.ToArray();
            query = query.Where(t => depts.Contains(t.DepartmentId) || t.StaffId == actor.Id);
        }
        if (set.AssignedOnly)
            query = query.Where(t => t.StaffId == actor.Id);
        return query;
    }

    public async Task<TaskItem> CreateAsync(TaskCreateRequest request, ActorContext actor, CancellationToken ct = default)
    {
        _ = await db.Departments.SingleOrDefaultAsync(d => d.Id == request.DepartmentId, ct)
            ?? throw new DomainNotFoundException("Department", request.DepartmentId);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskCreate, request.DepartmentId, ct);

        var numbering = await settings.GetTaskNumberingAsync(ct);
        var number = await sequences.NextAsync(numbering.SequenceId, numbering.NumberFormat, ct);

        var task = new TaskItem
        {
            Number = number,
            Title = request.Title.Trim(),
            TicketId = request.TicketId,
            DepartmentId = request.DepartmentId,
            StaffId = request.StaffId,
            DueDate = request.DueDate,
            Thread = new Thread { CreatedAt = DateTimeOffset.UtcNow },
        };
        db.TaskItems.Add(task);

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            // First entry of the task thread (mockup ta.fDescHelp). Response = the
            // plain (non-note) entry variant task-view renders without the note badge.
            await threads.PostAsync(task.ThreadId, ThreadEntryType.Response, request.Description.Trim(), actor,
                new PostOptions { Format = "text", Title = task.Title }, ct);
        }
        await threads.AddEventAsync(task.ThreadId, "created", actor, null, ct);
        if (task.StaffId is not null)
            await threads.AddEventAsync(task.ThreadId, "assigned", actor, new { staffId = task.StaffId, teamId = (int?)null }, ct);

        return task;
    }

    public async Task CloseAsync(int taskId, string? note, ActorContext actor, CancellationToken ct = default)
    {
        var task = await LoadAsync(taskId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskClose, task.DepartmentId, ct);
        if (task.ClosedAt is not null)
            return;

        task.ClosedAt = DateTimeOffset.UtcNow;
        task.IsOverdue = false;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(note))
        {
            await threads.PostAsync(task.ThreadId, ThreadEntryType.Note, note.Trim(), actor,
                new PostOptions { Format = "text" }, ct);
        }
        await threads.AddEventAsync(task.ThreadId, "closed", actor, null, ct);
    }

    public async Task AssignAsync(int taskId, int? staffId, int? teamId, ActorContext actor, CancellationToken ct = default)
    {
        var task = await LoadAsync(taskId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskAssign, task.DepartmentId, ct);

        task.StaffId = staffId;
        task.TeamId = teamId;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(task.ThreadId, "assigned", actor, new { staffId, teamId }, ct);
    }

    public async Task TransferAsync(int taskId, int departmentId, ActorContext actor, CancellationToken ct = default)
    {
        var task = await LoadAsync(taskId, ct);
        if (task.DepartmentId == departmentId)
            return;
        _ = await db.Departments.SingleOrDefaultAsync(d => d.Id == departmentId, ct)
            ?? throw new DomainNotFoundException("Department", departmentId);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskTransfer, task.DepartmentId, ct);

        var oldDepartmentId = task.DepartmentId;
        task.DepartmentId = departmentId;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(task.ThreadId, "transferred", actor,
            new { from = oldDepartmentId, to = departmentId }, ct);
    }

    public async Task UpdateAsync(int taskId, string title, DateTimeOffset? dueDate, ActorContext actor, CancellationToken ct = default)
    {
        var task = await LoadAsync(taskId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskEdit, task.DepartmentId, ct);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Task title must not be empty.", nameof(title));

        task.Title = title.Trim();
        task.DueDate = dueDate;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await threads.AddEventAsync(task.ThreadId, "edited", actor, null, ct);
    }

    public async Task DeleteAsync(int taskId, ActorContext actor, CancellationToken ct = default)
    {
        var task = await LoadAsync(taskId, ct);
        if (actor.IsStaff)
            await permissions.EnsureAsync(actor, PermissionKeys.TaskDelete, task.DepartmentId, ct);

        var thread = await db.Threads.SingleAsync(t => t.Id == task.ThreadId, ct);

        // Task first (its ThreadId FK restricts the thread), then the thread —
        // entries/events cascade at the DB level. Task entries carry no attachments
        // (the task-view mockup defines no attach control), so no orphan cleanup.
        db.TaskItems.Remove(task);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        db.Threads.Remove(thread);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    private async Task<TaskItem> LoadAsync(int taskId, CancellationToken ct) =>
        await db.TaskItems.SingleOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new DomainNotFoundException("TaskItem", taskId);
}
