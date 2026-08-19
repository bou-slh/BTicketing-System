using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin SLA plans list (mockups/admin/slas.html, ROADMAP §6.3): the B1 engine over
/// SlaPlan rows, per-row edit dialogs prefilled server-side (B2 — the mockup shares
/// ONE hardcoded dlg-sla between the new-button and every row; split into a create
/// dialog + per-row dialogs, teams precedent), and dlg-more bulk enable/disable/
/// delete. The dialog's grace/transient switches are consumed by the engine:
/// TicketService stamps EstimatedDueDate from the plan's grace period at create and
/// swaps a transient plan for a permanent one on transfer; the overdue-alerts switch
/// persists (inverted DisableOverdueAlerts) for the S8 SLA sweep's alert fan-out.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SlasController(AppDbContext db, ISettingsService settings) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "grace", "updated"];

    [HttpGet("/admin/slas")]
    [NavKey("slas")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        // Dialog schedule options: working-hour calendars only (the seeded canon
        // options Hafta içi / 7/24 / Cumartesi — holiday lists are no SLA clock).
        var schedules = await db.Schedules
            .Where(s => s.Kind == ScheduleKind.BusinessHours)
            .OrderBy(s => s.Id)
            .Select(s => new OptionVm(s.Id, s.Name))
            .ToListAsync(ct);

        return View(new SlasIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows, schedules));
    }

    /// <summary>Create dialog submit (B2).</summary>
    [HttpPost("/admin/slas/create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(
        string? name, int? grace, bool active, int? scheduleId,
        bool transient, bool alerts, string? notes, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(null, name, grace, active, scheduleId, transient, alerts, notes, returnUrl, ct);

    /// <summary>Per-row edit dialog submit (B2): prefilled server-side, saves the row.</summary>
    [HttpPost("/admin/slas/update")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Update(
        int id, string? name, int? grace, bool active, int? scheduleId,
        bool transient, bool alerts, string? notes, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(id, name, grace, active, scheduleId, transient, alerts, notes, returnUrl, ct);

    /// <summary>
    /// dlg-more bulk actions. Delete guard: plans referenced by tickets, departments,
    /// help topics or the core default-SLA pointer are skipped and reported in the
    /// partial toast (teams precedent, flagged).
    /// </summary>
    [HttpPost("/admin/slas/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("sla.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var plans = await db.SlaPlans.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - plans.Count;
        var defaultSlaId = int.TryParse(await settings.GetAsync("core", "default_sla_id", ct), out var d) ? d : (int?)null;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var plan in plans)
            {
                switch (act)
                {
                    case "enable":
                        plan.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        plan.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        var inUse = plan.Id == defaultSlaId
                            || await db.Tickets.AnyAsync(t => t.SlaId == plan.Id, ct)
                            || await db.Departments.AnyAsync(dep => dep.SlaId == plan.Id, ct)
                            || await db.HelpTopics.AnyAsync(t => t.SlaId == plan.Id, ct);
                        if (inUse)
                        {
                            skipped++;
                        }
                        else
                        {
                            db.SlaPlans.Remove(plan);
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["SlasToastOk"] = ok;
        TempData["SlasToastSkipped"] = skipped;
        TempData["SlasToast"] = skipped > 0 ? "sla.bulkPartial" : "sla.bulkDone";
        if (skipped > 0)
            TempData["SlasToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<IActionResult> UpsertAsync(
        int? id, string? name, int? grace, bool active, int? scheduleId,
        bool transient, bool alerts, string? notes, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return ToastBack("sla.errName", error: true, returnUrl: returnUrl);
        if (await db.SlaPlans.AnyAsync(s => s.Name == name && s.Id != id, ct))
            return ToastBack("sla.errNameInUse", error: true, returnUrl: returnUrl);
        // Mockup input: type=number min=1 — the grace period is mandatory hours.
        if (grace is not (>= 1 and <= 8760))
            return ToastBack("sla.errGrace", error: true, returnUrl: returnUrl);
        if (scheduleId is { } sch && !await db.Schedules.AnyAsync(
                s => s.Id == sch && s.Kind == ScheduleKind.BusinessHours, ct))
            scheduleId = null;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            SlaPlan plan;
            if (id is null)
            {
                plan = new SlaPlan { Name = name };
                db.SlaPlans.Add(plan);
            }
            else
            {
                var found = await db.SlaPlans.SingleOrDefaultAsync(s => s.Id == id, ct);
                if (found is null)
                    return NotFound();
                plan = found;
            }

            plan.Name = name;
            plan.GracePeriodHours = grace.Value;
            plan.IsActive = active;
            plan.ScheduleId = scheduleId;
            plan.IsTransient = transient;
            // The mockup switch is "overdue alerts ON" — inverted flag.
            plan.DisableOverdueAlerts = !alerts;
            plan.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            await db.SaveChangesAsync(ct);
        }

        return ToastBack(id is null ? "sla.toastCreated" : "sla.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>B1 core: search + schedule name + sort (mockup indicator: updated desc).</summary>
    private IQueryable<SlaRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.SlaPlans.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.Name, pattern));
        }

        var projected = query.Select(s => new
        {
            s.Id, s.Name, s.IsActive, s.GracePeriodHours, s.ScheduleId,
            Schedule = s.Schedule!.Name,
            s.IsTransient, s.DisableOverdueAlerts, s.Notes,
            Updated = s.UpdatedAt ?? s.CreatedAt,
        });

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var d = desc = dir == "asc" ? false : dir == "desc" || key == "updated";
        projected = (key, d) switch
        {
            ("name", true) => projected.OrderByDescending(s => s.Name).ThenBy(s => s.Id),
            ("name", false) => projected.OrderBy(s => s.Name).ThenBy(s => s.Id),
            ("grace", true) => projected.OrderByDescending(s => s.GracePeriodHours).ThenBy(s => s.Id),
            ("grace", false) => projected.OrderBy(s => s.GracePeriodHours).ThenBy(s => s.Id),
            (_, false) => projected.OrderBy(s => s.Updated).ThenBy(s => s.Id),
            // Seed rows share one CreatedAt — the id tiebreak keeps the canon row
            // order Standart → Dahili Talepler under the default updated-desc sort.
            _ => projected.OrderByDescending(s => s.Updated).ThenBy(s => s.Id),
        };
        return projected.Select(s => new SlaRowVm(
            s.Id, s.Name, s.IsActive, s.GracePeriodHours, s.ScheduleId, s.Schedule,
            s.IsTransient, s.DisableOverdueAlerts, s.Notes, s.Updated));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["SlasToast"] = key;
        if (error)
            TempData["SlasToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record SlasIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<SlaRowVm> Rows,
    IReadOnlyList<OptionVm> Schedules);

public sealed record SlaRowVm(
    int Id,
    string Name,
    bool IsActive,
    int GraceHours,
    int? ScheduleId,
    string? ScheduleName,
    bool IsTransient,
    bool DisableOverdueAlerts,
    string? Notes,
    DateTimeOffset Updated);
