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
/// Admin site pages (mockups/admin/pages.html, ROADMAP §6.3): the B1 engine over
/// SitePage rows, per-row edit dialogs prefilled server-side (B2 — the mockup
/// shares ONE dlg-page between the new-button and every row; split per the SLA
/// precedent) and dlg-more bulk enable/disable/delete with a reference guard
/// (pages picked by help topics or the company settings selects are skipped).
/// "Pages served on portal": the OFFLINE page body is LIVE — the portal /offline
/// view renders the configured active Offline-type page while maintenance mode
/// holds (Portal AccountController.Offline). Landing/thank-you consumers do not
/// exist yet (settings-company precedent) — TODO flags stay on their selects.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class PagesController(AppDbContext db, ISettingsService settings) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "updated"];

    /// <summary>Posted type values ↔ SitePageType (mockup pg.type select order).</summary>
    public static readonly IReadOnlyDictionary<string, SitePageType> TypeKeys =
        new Dictionary<string, SitePageType>
        {
            ["landing"] = SitePageType.Landing,
            ["offline"] = SitePageType.Offline,
            ["thanks"] = SitePageType.ThankYou,
            ["other"] = SitePageType.Other,
        };

    [HttpGet("/admin/pages")]
    [NavKey("pages")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new PagesIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>Create dialog submit (B2).</summary>
    [HttpPost("/admin/pages/create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(
        string? name, string? type, bool active, string? content, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(null, name, type, active, content, returnUrl, ct);

    /// <summary>Per-row edit dialog submit (B2): prefilled server-side.</summary>
    [HttpPost("/admin/pages/update")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Update(
        int id, string? name, string? type, bool active, string? content, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(id, name, type, active, content, returnUrl, ct);

    /// <summary>
    /// dlg-more bulk actions. Delete guard: pages referenced by a help topic's
    /// thank-you pointer or by the company settings landing/offline/thank-you
    /// selects are skipped and reported in the partial toast (SLA precedent).
    /// </summary>
    [HttpPost("/admin/pages/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("pg.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var pages = await db.SitePages.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - pages.Count;

        var company = await settings.GetSectionAsync("company", ct);
        var referencedByCompany = new[] { "landing_page_id", "offline_page_id", "thanks_page_id" }
            .Select(key => int.TryParse(company.GetValueOrDefault(key), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var page in pages)
            {
                switch (act)
                {
                    case "enable":
                        page.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        page.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        var inUse = referencedByCompany.Contains(page.Id)
                            || await db.HelpTopics.AnyAsync(t => t.SitePageId == page.Id, ct);
                        if (inUse)
                        {
                            skipped++;
                        }
                        else
                        {
                            db.SitePages.Remove(page);
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["PagesToastOk"] = ok;
        TempData["PagesToastSkipped"] = skipped;
        TempData["PagesToast"] = skipped > 0 ? "pg.bulkPartial" : "pg.bulkDone";
        if (skipped > 0)
            TempData["PagesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<IActionResult> UpsertAsync(
        int? id, string? name, string? type, bool active, string? content, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return ToastBack("pg.errName", error: true, returnUrl: returnUrl);
        // Duplicate names are refused: the settings-company / helptopic selects
        // list pages by name (invented rule — no unique index in osTicket, flagged).
        if (await db.SitePages.AnyAsync(p => p.Name == name && p.Id != id, ct))
            return ToastBack("pg.errNameInUse", error: true, returnUrl: returnUrl);
        if (type is null || !TypeKeys.TryGetValue(type, out var pageType))
            return ToastBack("pg.errType", error: true, returnUrl: returnUrl);
        if (string.IsNullOrWhiteSpace(content))
            return ToastBack("pg.errContent", error: true, returnUrl: returnUrl);

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            SitePage page;
            if (id is null)
            {
                page = new SitePage { Name = name, Body = "" };
                db.SitePages.Add(page);
            }
            else
            {
                var found = await db.SitePages.SingleOrDefaultAsync(p => p.Id == id, ct);
                if (found is null)
                    return NotFound();
                page = found;
            }

            page.Name = name;
            page.Type = pageType;
            page.IsActive = active;
            // Stored as authored; portal render sites sanitize (offline view).
            page.Body = content.Trim();
            await db.SaveChangesAsync(ct);
        }

        return ToastBack(id is null ? "pg.toastCreated" : "pg.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>B1 core: name search + sort (mockup indicator: updated desc).</summary>
    private IQueryable<PageRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.SitePages.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var d = desc = dir == "asc" ? false : dir == "desc" || key == "updated";
        var ordered = (key, d) switch
        {
            ("name", true) => query.OrderByDescending(p => p.Name).ThenBy(p => p.Id),
            ("name", false) => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            (_, false) => query.OrderBy(p => p.UpdatedAt ?? p.CreatedAt).ThenBy(p => p.Id),
            // Seed rows share one CreatedAt — the id tiebreak keeps the canon row
            // order Hoş Geldiniz → KVKK under the default updated-desc sort.
            _ => query.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt).ThenBy(p => p.Id),
        };
        return ordered.Select(p => new PageRowVm(
            p.Id, p.Name, p.Type, p.IsActive, p.Body, p.UpdatedAt ?? p.CreatedAt));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["PagesToast"] = key;
        if (error)
            TempData["PagesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record PagesIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<PageRowVm> Rows);

public sealed record PageRowVm(
    int Id,
    string Name,
    SitePageType Type,
    bool IsActive,
    string Body,
    DateTimeOffset Updated);
