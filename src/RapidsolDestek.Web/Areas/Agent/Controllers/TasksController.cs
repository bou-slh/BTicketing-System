using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;
using TaskItem = RapidsolDestek.Domain.Entities.TaskItem;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent task list (mockups/agent/tasks.html, ROADMAP §6.2): the S0-fixed tab bar
/// (Açık / Görevlerim / Gecikmiş / Tamamlanan) filtering from data with live counts,
/// the B1 engine (sort/search/pagination/selection) over the staff's visibility
/// scope, the dlg-newtask dialog creating a real TaskItem through TaskService, and
/// toolbar bulk actions (Ata / Aktar / Sil) over the row selection.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class TasksController(AppDbContext db, ITaskService taskService) : Controller
{
    public const int PageSize = 8;

    /// <summary>Mockup tab order; "mine" carries the active class in the mockup.</summary>
    public static readonly string[] Tabs = ["open", "mine", "overdue", "done"];

    private static readonly string[] SortKeys = ["no", "date", "title", "dept", "assignee"];

    [HttpGet("/agent/tasks")]
    [NavKey("tasks")]
    public async Task<IActionResult> Index(
        string? tab, string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        var activeTab = Tabs.Contains(tab) ? tab! : "mine";
        var visible = await taskService.VisibleAsync(actor, ct);
        var now = DateTimeOffset.UtcNow;

        // ---- Live tab counts (one COUNT per tab, tickets queue-tree precedent) ------
        var counts = new Dictionary<string, int>();
        foreach (var key in Tabs)
            counts[key] = await ApplyTab(visible, key, staff.Id, now).CountAsync(ct);

        // ---- B1 list: tab filter ∩ quick search, sort, pagination -------------------
        var query = ApplyTab(visible, activeTab, staff.Id, now);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.Number, pattern) || EF.Functions.ILike(t.Title, pattern));
        }

        var sortKey = SortKeys.Contains(sort) ? sort! : "date";
        // Text columns read naturally ascending; date/number default to newest-first.
        var desc = dir == "desc" || (dir != "asc" && sortKey is "date" or "no");
        query = (sortKey, desc) switch
        {
            ("no", true) => query.OrderByDescending(t => t.Number),
            ("no", false) => query.OrderBy(t => t.Number),
            ("title", true) => query.OrderByDescending(t => t.Title),
            ("title", false) => query.OrderBy(t => t.Title),
            ("dept", true) => query.OrderByDescending(t => t.Department!.Name),
            ("dept", false) => query.OrderBy(t => t.Department!.Name),
            ("assignee", true) => query.OrderByDescending(t => t.Staff != null ? t.Staff.FullName : ""),
            ("assignee", false) => query.OrderBy(t => t.Staff != null ? t.Staff.FullName : ""),
            (_, false) => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            _ => query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
        };

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(t => new TaskRowVm(
                t.Id, t.Number, t.CreatedAt, t.Title,
                t.TicketId, t.Ticket != null ? t.Ticket.Number : null,
                t.Department!.Name,
                t.Staff != null ? t.Staff.FullName : null))
            .ToListAsync(ct);

        // ---- dlg-newtask selects (ticket-view assignee/department precedent) --------
        var departments = await db.Departments
            .OrderBy(d => d.Id)
            .Select(d => new DeptOptionVm(d.Id, d.Name))
            .ToListAsync(ct);
        // Vacation guard (profile Tatil Modu): a new task can't target a vacationing agent.
        var assignees = await db.Staff
            .Where(s => s.IsActive && !s.OnVacation)
            .OrderBy(s => s.FirstName)
            .Select(s => new TaskAssigneeVm(s.Id, s.FullName))
            .ToListAsync(ct);

        return View(new TasksIndexVm(
            activeTab, counts, q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            rows, departments, assignees));
    }

    /// <summary>dlg-newtask submit: creates the TaskItem + thread and lands on its task-view.</summary>
    [HttpPost("/agent/tasks/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? title, int departmentId, int? staffId, DateOnly? due, string? desc, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (string.IsNullOrWhiteSpace(title))
            return RedirectToAction(nameof(Index));

        TaskItem task;
        try
        {
            task = await taskService.CreateAsync(new TaskCreateRequest
            {
                Title = title,
                DepartmentId = departmentId,
                StaffId = staffId,
                // Mockup due input is date-only; end of the local day (seed parity 18:00 is sample styling).
                DueDate = due is { } d ? new DateTimeOffset(d, new TimeOnly(23, 59), DateTimeOffset.Now.Offset) : null,
                Description = desc,
            }, actor, ct);
        }
        catch (DomainException)
        {
            TempData["TasksBulkNone"] = true;
            return RedirectToAction(nameof(Index));
        }

        TempData["TavToast"] = "tav.toastCreated";
        return Redirect($"/agent/task-view?id={task.Id}");
    }

    /// <summary>
    /// Toolbar bulk actions over the selection: Ata (assign to me, tickets-toolbar
    /// precedent), Aktar (department dialog), Sil (confirm dialog). Rows the actor
    /// may not touch are skipped and reported, never silently dropped.
    /// </summary>
    [HttpPost("/agent/tasks/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, int? departmentId, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        if (ids.Length == 0 || (act == "transfer" && departmentId is null))
        {
            TempData["TasksBulkNone"] = true;
            return LocalRedirectOrIndex(returnUrl);
        }

        int ok = 0, skipped = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (act)
                {
                    case "assign":
                        await taskService.AssignAsync(id, staff.Id, null, actor, ct);
                        ok++;
                        break;
                    case "transfer":
                        await taskService.TransferAsync(id, departmentId!.Value, actor, ct);
                        ok++;
                        break;
                    case "delete":
                        await taskService.DeleteAsync(id, actor, ct);
                        ok++;
                        break;
                    default:
                        return LocalRedirectOrIndex(returnUrl);
                }
            }
            catch (DomainException)
            {
                // Permission denied / unknown id — count and report.
                skipped++;
            }
        }

        TempData["TasksBulkOk"] = ok;
        TempData["TasksBulkSkipped"] = skipped;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        returnUrl is not null && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Index));

    private static IQueryable<TaskItem> ApplyTab(IQueryable<TaskItem> query, string tab, int staffId, DateTimeOffset now) => tab switch
    {
        "mine" => query.Where(t => t.ClosedAt == null && t.StaffId == staffId),
        "overdue" => query.Where(t => t.ClosedAt == null
            && (t.IsOverdue || (t.DueDate != null && t.DueDate < now))),
        "done" => query.Where(t => t.ClosedAt != null),
        _ => query.Where(t => t.ClosedAt == null),
    };
}

public sealed record TasksIndexVm(
    string Tab,
    IReadOnlyDictionary<string, int> Counts,
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<TaskRowVm> Rows,
    IReadOnlyList<DeptOptionVm> Departments,
    IReadOnlyList<TaskAssigneeVm> Assignees);

public sealed record TaskRowVm(
    int Id,
    string Number,
    DateTimeOffset Created,
    string Title,
    int? TicketId,
    string? TicketNumber,
    string DeptName,
    string? AssigneeName);

public sealed record TaskAssigneeVm(int Id, string FullName);
