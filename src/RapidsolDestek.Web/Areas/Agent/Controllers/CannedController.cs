using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent canned-responses manager (mockups/agent/canned.html, ROADMAP §6.2): the B1
/// engine (sort/search/pagination) over all canned responses, dlg-canned creating
/// through CannedResponseService, per-row edit dialogs prefilled server-side (B2),
/// and toolbar bulk Devre Dışı Bırak / Sil over the checkbox selection (users-page
/// bulk precedent). Variable expansion stays in CannedResponseService — this page
/// only manages the templates.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class CannedController(AppDbContext db, ICannedResponseService cannedService) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["title", "dept", "updated"];

    [HttpGet("/agent/canned")]
    [NavKey("canned")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new CannedIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            rows, await DepartmentOptionsAsync(ct)));
    }

    /// <summary>dlg-canned submit: creates the canned response and returns to the list.</summary>
    [HttpPost("/agent/canned/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? title, int? deptId, string? body, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await cannedService.CreateAsync(new CannedUpsertRequest
            {
                Title = title ?? "",
                DepartmentId = deptId,
                Response = body ?? "",
            }, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return ToastBack(ex.Code == "title-in-use" ? "cn.errTitleInUse" : "cn.errInvalid", error: true);
        }
        catch (DomainException)
        {
            return ToastBack("cn.errDenied", error: true);
        }

        return ToastBack("cn.toastCreated");
    }

    /// <summary>Per-row edit dialog submit (B2): saves title/department/body/enabled.</summary>
    [HttpPost("/agent/canned/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        int id, string? title, int? deptId, string? body, bool enabled, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await cannedService.UpdateAsync(id, new CannedUpsertRequest
            {
                Title = title ?? "",
                DepartmentId = deptId,
                Response = body ?? "",
                IsEnabled = enabled,
            }, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return ToastBack(ex.Code == "title-in-use" ? "cn.errTitleInUse" : "cn.errInvalid", error: true, returnUrl);
        }
        catch (DomainException)
        {
            return ToastBack("cn.errDenied", error: true, returnUrl);
        }

        return ToastBack("cn.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>
    /// Toolbar bulk actions over the selection: Devre Dışı Bırak (SetEnabled false) and
    /// Sil (hard delete behind the confirm dialog) — users-page bulk loop precedent.
    /// </summary>
    [HttpPost("/agent/canned/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (ids.Length == 0)
        {
            return ToastBack("cn.bulkNone", error: true, returnUrl);
        }

        int ok = 0, skipped = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (act)
                {
                    case "disable":
                        await cannedService.SetEnabledAsync(id, false, actor, ct);
                        ok++;
                        break;
                    case "delete":
                        await cannedService.DeleteAsync(id, actor, ct);
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

        // Two plain ints (users-page precedent): TempData's serializer has no array support.
        TempData["CannedToastOk"] = ok;
        TempData["CannedToastSkipped"] = skipped;
        TempData["CannedToast"] = skipped > 0 ? "cn.bulkPartial" : "cn.bulkDone";
        if (skipped > 0)
            TempData["CannedToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>B1 core: search + department name + sort, shared by Index paging and count.</summary>
    private IQueryable<CannedRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.CannedResponses.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Title, pattern)
                || EF.Functions.ILike(c.Response, pattern));
        }

        // Anonymous intermediate so the computed columns stay translatable under OrderBy
        // (constructor-projected records cannot be sorted server-side — orgs precedent).
        var projected = query.Select(c => new
        {
            c.Id, c.Title, c.IsEnabled, c.DepartmentId, c.Response,
            DeptName = c.Department != null ? c.Department.Name : null,
            Updated = c.UpdatedAt ?? c.CreatedAt,
        });

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        // Text columns read naturally ascending; the date column defaults new-first
        // (mockup's sorted-desc on Updated).
        var d = desc = dir == "desc" || (dir != "asc" && key is "updated");
        projected = (key, d) switch
        {
            ("title", true) => projected.OrderByDescending(r => r.Title).ThenBy(r => r.Id),
            ("title", false) => projected.OrderBy(r => r.Title).ThenBy(r => r.Id),
            ("dept", true) => projected.OrderByDescending(r => r.DeptName ?? "").ThenBy(r => r.Id),
            ("dept", false) => projected.OrderBy(r => r.DeptName ?? "").ThenBy(r => r.Id),
            (_, true) => projected.OrderByDescending(r => r.Updated).ThenByDescending(r => r.Id),
            _ => projected.OrderBy(r => r.Updated).ThenBy(r => r.Id),
        };
        return projected.Select(r => new CannedRowVm(
            r.Id, r.Title, r.IsEnabled, r.DepartmentId, r.DeptName, r.Response, r.Updated));
    }

    /// <summary>Dialog department select: routable departments, name order (mockup Destek/Bordro/Danışmanlık).</summary>
    private async Task<List<DeptOptionVm>> DepartmentOptionsAsync(CancellationToken ct) =>
        await db.Departments
            .Where(d => !d.IsArchived)
            .OrderBy(d => d.Name)
            .Select(d => new DeptOptionVm(d.Id, d.Name))
            .ToListAsync(ct);

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["CannedToast"] = key;
        if (error)
            TempData["CannedToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record CannedIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<CannedRowVm> Rows,
    IReadOnlyList<DeptOptionVm> Departments);

public sealed record CannedRowVm(
    int Id,
    string Title,
    bool IsEnabled,
    int? DepartmentId,
    string? DeptName,
    string Response,
    DateTimeOffset Updated);

// DeptOptionVm is shared with TicketViewController (same namespace).
