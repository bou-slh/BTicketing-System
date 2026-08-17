using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;
using TaskItem = RapidsolDestek.Domain.Entities.TaskItem;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent task detail (mockups/agent/task-view.html, ROADMAP §6.2): header + meta from
/// data, the task thread (notes/entries + timeline events, ticket-view precedent), the
/// B5 note composer through IThreadService, and the header actions (Kapat / Ata /
/// Aktar / Düzenle / Sil) as B2 dialogs posting through TaskService. Visibility =
/// TaskService.VisibleAsync (the ticket QueueEngine department scope, mirrored).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class TaskViewController(
    AppDbContext db,
    ITaskService taskService,
    IThreadService threads) : Controller
{
    [HttpGet("/agent/task-view")]
    [NavKey("tasks")]
    public async Task<IActionResult> Index(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        var visible = await taskService.VisibleAsync(actor, ct);
        var now = DateTimeOffset.UtcNow;
        var task = await visible
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id, t.Number, t.Title, t.CreatedAt, t.DueDate, t.ThreadId,
                t.DepartmentId, t.StaffId, t.TeamId,
                Closed = t.ClosedAt != null,
                Overdue = t.ClosedAt == null && (t.IsOverdue || (t.DueDate != null && t.DueDate < now)),
                DeptName = t.Department!.Name,
                AssigneeName = t.Staff != null ? t.Staff.FullName : null,
                TeamName = t.Team != null ? t.Team.Name : null,
                TicketId = t.TicketId,
                TicketNumber = t.Ticket != null ? t.Ticket.Number : null,
                TicketSubject = t.Ticket != null ? t.Ticket.Subject : null,
            })
            .SingleOrDefaultAsync(ct);
        if (task is null)
            return NotFound();

        // ---- Thread: entries + timeline events interleaved (ticket-view precedent) --
        var entries = await db.ThreadEntries
            .Where(e => e.ThreadId == task.ThreadId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.Type, e.Poster, e.CreatedAt, e.Body, e.Format,
                StaffName = db.Staff.Where(s => s.Id == e.StaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var events = await db.ThreadEvents
            .Where(ev => ev.ThreadId == task.ThreadId && !ev.Annulled)
            .OrderBy(ev => ev.OccurredAt).ThenBy(ev => ev.Id)
            .Select(ev => new
            {
                Kind = ev.EventType!.Name, ev.OccurredAt, ev.Data,
                EventStaffName = db.Staff.Where(s => s.Id == ev.StaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var deptNames = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var staffNames = await db.Staff
            .ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName, ct);
        var teamNames = await db.Teams.ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var items = entries
            .Select(e => new TaskThreadItemVm(
                e.CreatedAt,
                new TaskEntryVm(e.Type, e.StaffName ?? e.Poster, e.Body, e.Format),
                Event: null))
            .Concat(events.Select(ev => new TaskThreadItemVm(
                ev.OccurredAt,
                Entry: null,
                new TaskEventVm(
                    ev.Kind,
                    AssigneeFromEventData(ev.Data, staffNames, teamNames) ?? ev.EventStaffName,
                    TargetDeptFromEventData(ev.Data, deptNames)))))
            .OrderBy(i => i.At)
            .ToList();

        // ---- Dialog data (ticket-view assign "s:/t:" + transfer precedents) ---------
        var assignees = (await db.Staff
                .Where(s => s.IsActive)
                .OrderBy(s => s.FirstName)
                .Select(s => new { s.Id, s.FullName })
                .ToListAsync(ct))
            .Select(s => new AssigneeOptionVm($"s:{s.Id}", s.FullName, task.StaffId == s.Id))
            .Concat((await db.Teams.OrderBy(t => t.Id).Select(t => new { t.Id, t.Name }).ToListAsync(ct))
                .Select(t => new AssigneeOptionVm($"t:{t.Id}", t.Name,
                    task.StaffId == null && task.TeamId == t.Id)))
            .ToList();

        var departments = await db.Departments
            .OrderBy(d => d.Id)
            .Select(d => new DeptOptionVm(d.Id, d.Name))
            .ToListAsync(ct);

        return View(new AgentTaskViewVm(
            task.Id, task.Number, task.Title,
            task.Closed ? "closed" : task.Overdue ? "overdue" : "open",
            task.CreatedAt, task.DueDate, task.DeptName,
            task.AssigneeName ?? task.TeamName,
            task.TicketId, task.TicketNumber, task.TicketSubject,
            items, assignees, departments, task.DepartmentId));
    }

    // ---- B5 note composer -------------------------------------------------------------

    /// <summary>Note composer: internal note through IThreadService (agent ticket-view Note precedent).</summary>
    [HttpPost("/agent/task-view/note")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Note(int id, string? body, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var (staff, task) = loaded.Value;
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (string.IsNullOrWhiteSpace(body))
            return RedirectBack(id, "tav.errEmpty", error: true);

        await threads.PostAsync(task.ThreadId, ThreadEntryType.Note, body, actor,
            new PostOptions { Format = "text" }, ct);
        return RedirectBack(id, "tav.toastNote");
    }

    // ---- Header actions (B2 dialogs → TaskService) ------------------------------------

    /// <summary>dlg-close: complete the task with an optional internal note.</summary>
    [HttpPost("/agent/task-view/close")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Close(int id, string? note, CancellationToken ct) =>
        RunAsync(id, "tav.toastClosed", (actor, c) => taskService.CloseAsync(id, note, actor, c), ct);

    /// <summary>dlg-assign: "s:{staffId}" or "t:{teamId}" (ticket-view encoding).</summary>
    [HttpPost("/agent/task-view/assign")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Assign(int id, string assignee, CancellationToken ct)
    {
        int? staffId = null, teamId = null;
        if (assignee.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var sid))
            staffId = sid;
        else if (assignee.StartsWith("t:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var tid))
            teamId = tid;
        else
            return Task.FromResult<IActionResult>(BadRequest());

        return RunAsync(id, "tav.toastAssigned", (actor, c) => taskService.AssignAsync(id, staffId, teamId, actor, c), ct);
    }

    /// <summary>dlg-transfer: department move.</summary>
    [HttpPost("/agent/task-view/transfer")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Transfer(int id, int departmentId, CancellationToken ct) =>
        RunAsync(id, "tav.toastTransferred", (actor, c) => taskService.TransferAsync(id, departmentId, actor, c), ct);

    /// <summary>dlg-edit: plain fields (title + due date) — the rest have their own dialogs.</summary>
    [HttpPost("/agent/task-view/edit")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(int id, string? title, DateOnly? due, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult(RedirectBack(id, "tav.errEmpty", error: true));
        return RunAsync(id, "tav.toastSaved", (actor, c) => taskService.UpdateAsync(id, title,
            due is { } d ? new DateTimeOffset(d, new TimeOnly(23, 59), DateTimeOffset.Now.Offset) : null, actor, c), ct);
    }

    /// <summary>dlg-delete: hard delete, then back to the list.</summary>
    [HttpPost("/agent/task-view/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await taskService.DeleteAsync(id, actor, ct);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "tav.errDenied", error: true);
        }
        TempData["TasksBulkOk"] = 1;
        TempData["TasksBulkSkipped"] = 0;
        return Redirect("/agent/tasks");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<IActionResult> RunAsync(
        int id, string successKey, Func<ActorContext, CancellationToken, Task> action, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await action(actor, ct);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "tav.errDenied", error: true);
        }
        return RedirectBack(id, successKey);
    }

    /// <summary>Resolves the staff actor and the task within their visibility scope.</summary>
    private async Task<(Staff Staff, TaskItem Task)?> LoadForActionAsync(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return null;
        var visible = await taskService.VisibleAsync(ActorContext.ForStaff(staff), ct);
        var task = await visible.SingleOrDefaultAsync(t => t.Id == id, ct);
        return task is null ? null : (staff, task);
    }

    private IActionResult RedirectBack(int id, string toastKey, bool error = false)
    {
        TempData["TavToast"] = toastKey;
        if (error)
            TempData["TavToastError"] = true;
        return RedirectToAction(nameof(Index), new { id });
    }

    // Event Data helpers (agent ticket-view precedent): "staffId"/"teamId" from
    // TaskService, department target under "to".
    private static string? AssigneeFromEventData(
        string? data, Dictionary<int, string> staffNames, Dictionary<int, string> teamNames)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            if (root.TryGetProperty("staffId", out var sid) && sid.ValueKind == JsonValueKind.Number
                && staffNames.TryGetValue(sid.GetInt32(), out var staffName))
                return staffName;
            if (root.TryGetProperty("teamId", out var tid) && tid.ValueKind == JsonValueKind.Number
                && teamNames.TryGetValue(tid.GetInt32(), out var teamName))
                return teamName;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TargetDeptFromEventData(string? data, Dictionary<int, string> deptNames)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            return JsonDocument.Parse(data).RootElement.TryGetProperty("to", out var to)
                && to.ValueKind == JsonValueKind.Number
                && deptNames.TryGetValue(to.GetInt32(), out var name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AgentTaskViewVm(
    int Id,
    string Number,
    string Title,
    string StatusKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? Due,
    string DeptName,
    string? AssigneeName,
    int? TicketId,
    string? TicketNumber,
    string? TicketSubject,
    IReadOnlyList<TaskThreadItemVm> Thread,
    IReadOnlyList<AssigneeOptionVm> Assignees,
    IReadOnlyList<DeptOptionVm> Departments,
    int CurrentDeptId);

/// <summary>Thread timeline item: exactly one of Entry / Event is set.</summary>
public sealed record TaskThreadItemVm(DateTimeOffset At, TaskEntryVm? Entry, TaskEventVm? Event);

public sealed record TaskEntryVm(ThreadEntryType Type, string Poster, string Body, string Format);

/// <summary>Kind: created | assigned | transferred | closed | edited …</summary>
public sealed record TaskEventVm(string Kind, string? Assignee, string? TargetDept);
