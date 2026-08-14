using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent dashboard "Panom" (mockups/agent/dashboard.html): live stat tiles and
/// my-tickets / my-tasks tables for the signed-in staff member. Scope is personal
/// per the mockup subtitle — team statistics live in the Admin panel (S7).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class DashboardController(AppDbContext db) : Controller
{
    [HttpGet("/agent")]
    [HttpGet("/agent/dashboard")]
    [NavKey("dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        // Local-day window (portal Tickets MetaDate precedent: server-local time).
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Date, now.Offset);
        var todayEnd = todayStart.AddDays(1);

        // ---- Stat tiles (my-scope = assigned to me, ROADMAP §6.2 dashboard row) ----
        var myOpen = db.Tickets.Where(t =>
            t.StaffId == staff.Id && t.Status!.State == TicketState.Open);

        var myOpenCount = await myOpen.CountAsync(ct);
        var updatedToday = await myOpen.CountAsync(
            t => (t.LastUpdateAt ?? t.CreatedAt) >= todayStart, ct);
        var dueToday = await myOpen.CountAsync(t =>
            (t.DueDate ?? t.EstimatedDueDate) >= todayStart
            && (t.DueDate ?? t.EstimatedDueDate) < todayEnd, ct);
        // Active (= highest-revision) proposal pending — QueueEngine "effort=pending" parity.
        var effortPending = await myOpen.CountAsync(t =>
            t.EffortProposals.OrderByDescending(p => p.RevisionNo).Take(1)
                .Any(p => p.State == EffortState.Pending), ct);

        var myTasks = db.TaskItems.Where(t => t.StaffId == staff.Id && t.ClosedAt == null);
        var myTaskCount = await myTasks.CountAsync(ct);
        var overdueTasks = await myTasks.CountAsync(
            t => t.IsOverdue || (t.DueDate != null && t.DueDate < now), ct);

        // ---- Taleplerim table: 5 most recently updated of my tickets ----------------
        // Mockup shows a solved row too, so closed-state tickets stay in; archived/
        // deleted never surface (portal Tickets parity).
        var ticketRows = await db.Tickets
            .Where(t => t.StaffId == staff.Id
                && (t.Status!.State == TicketState.Open || t.Status.State == TicketState.Closed))
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)
            .Take(5)
            .Select(t => new DashboardTicketVm(
                t.Id,
                t.Number,
                t.Subject,
                t.UserId,
                t.User!.Name,
                t.Priority != null ? t.Priority.Key : "normal",
                // Derived pseudo-statuses (§2 canon: hero R716555 open + pending
                // effort renders as effortWait; overdue is a flag, not a status row).
                t.EffortProposals.OrderByDescending(p => p.RevisionNo).Take(1)
                        .Any(p => p.State == EffortState.Pending)
                    ? "effortWait"
                    : t.IsOverdue && t.Status!.State == TicketState.Open
                        ? "overdue"
                        : t.Status!.Key,
                t.DueDate ?? t.EstimatedDueDate))
            .ToListAsync(ct);

        // ---- Görevlerim table: 3 newest of my open tasks ----------------------------
        var taskRows = await myTasks
            .OrderByDescending(t => t.Number)
            .Take(3)
            .Select(t => new DashboardTaskVm(
                t.Id,
                t.Number,
                t.Title,
                t.IsOverdue || (t.DueDate != null && t.DueDate < now),
                t.DueDate))
            .ToListAsync(ct);

        return View(new DashboardVm(
            myOpenCount, updatedToday, dueToday, effortPending, myTaskCount, overdueTasks,
            ticketRows, taskRows));
    }
}

public sealed record DashboardVm(
    int MyOpenCount,
    int UpdatedTodayCount,
    int DueTodayCount,
    int EffortPendingCount,
    int MyTaskCount,
    int OverdueTaskCount,
    IReadOnlyList<DashboardTicketVm> Tickets,
    IReadOnlyList<DashboardTaskVm> Tasks);

public sealed record DashboardTicketVm(
    int Id,
    string Number,
    string Subject,
    int UserId,
    string UserName,
    string PriorityKey,
    string StatusKey,
    DateTimeOffset? Due);

public sealed record DashboardTaskVm(
    int Id,
    string Number,
    string Title,
    bool IsOverdue,
    DateTimeOffset? Due);
