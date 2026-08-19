using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin ban list (mockups/admin/banlist.html, ROADMAP §6.3): the B1 engine over
/// BanlistEntry rows with per-row edit dialogs prefilled server-side (B2 — the
/// mockup shares ONE dlg-ban between the add-button and every row; split per the
/// SLA precedent) and the toolbar's inline enable/disable/delete bulk buttons
/// (the mockup has no dlg-more here). Enforcement is LIVE: FilterEngine.RunAsync
/// rejects any ticket create whose sender address matches an active ban before
/// any filter executes (osTicket system ban-list filter parity — silent refusal,
/// no ticket, no auto-response).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class BanlistController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["email", "status", "created", "updated"];

    [HttpGet("/admin/banlist")]
    [NavKey("banlist")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new BanlistIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>Add-ban dialog submit (B2).</summary>
    [HttpPost("/admin/banlist/create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(
        string? email, bool active, string? notes, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(null, email, active, notes, returnUrl, ct);

    /// <summary>Per-row edit dialog submit (B2): prefilled server-side.</summary>
    [HttpPost("/admin/banlist/update")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Update(
        int id, string? email, bool active, string? notes, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(id, email, active, notes, returnUrl, ct);

    /// <summary>Toolbar bulk enable/disable/delete over the selection (B1). The
    /// mockup places the three buttons inline (no dlg-more) and no delete-confirm
    /// dialog exists on this page — DOM parity kept.</summary>
    [HttpPost("/admin/banlist/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("bl.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var entries = await db.BanlistEntries.Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var entry in entries)
            {
                switch (act)
                {
                    case "enable": entry.IsActive = true; break;
                    case "disable": entry.IsActive = false; break;
                    case "delete": db.BanlistEntries.Remove(entry); break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["BanlistToastOk"] = entries.Count;
        TempData["BanlistToast"] = "bl.bulkDone";
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<IActionResult> UpsertAsync(
        int? id, string? email, bool active, string? notes, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        // Addresses are normalized to lowercase: the unique index and the
        // FilterEngine ban check are both case-insensitive matches.
        email = (email ?? "").Trim().ToLowerInvariant();
        if (email.Length is 0 or > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            return ToastBack("bl.errEmail", error: true, returnUrl: returnUrl);
        if (await db.BanlistEntries.AnyAsync(b => b.Address == email && b.Id != id, ct))
            return ToastBack("bl.errEmailInUse", error: true, returnUrl: returnUrl);

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            BanlistEntry entry;
            if (id is null)
            {
                entry = new BanlistEntry { Address = email };
                db.BanlistEntries.Add(entry);
            }
            else
            {
                var found = await db.BanlistEntries.SingleOrDefaultAsync(b => b.Id == id, ct);
                if (found is null)
                    return NotFound();
                entry = found;
            }

            entry.Address = email;
            entry.IsActive = active;
            entry.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            await db.SaveChangesAsync(ct);
        }

        return ToastBack(id is null ? "bl.toastCreated" : "bl.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>B1 core: address search + sort (mockup indicator: created desc).</summary>
    private IQueryable<BanRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.BanlistEntries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(b => EF.Functions.ILike(b.Address, pattern));
        }

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "created";
        var d = desc = dir == "asc" ? false : dir == "desc" || key == "created";
        var ordered = (key, d) switch
        {
            ("email", true) => query.OrderByDescending(b => b.Address).ThenBy(b => b.Id),
            ("email", false) => query.OrderBy(b => b.Address).ThenBy(b => b.Id),
            ("status", true) => query.OrderByDescending(b => b.IsActive).ThenBy(b => b.Id),
            ("status", false) => query.OrderBy(b => b.IsActive).ThenBy(b => b.Id),
            ("updated", true) => query.OrderByDescending(b => b.UpdatedAt ?? b.CreatedAt).ThenBy(b => b.Id),
            ("updated", false) => query.OrderBy(b => b.UpdatedAt ?? b.CreatedAt).ThenBy(b => b.Id),
            (_, false) => query.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id),
            // Seed rows share one CreatedAt — the id tiebreak keeps the canon row
            // order stable under the default created-desc sort.
            _ => query.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id),
        };
        return ordered.Select(b => new BanRowVm(
            b.Id, b.Address, b.IsActive, b.Notes, b.CreatedAt, b.UpdatedAt ?? b.CreatedAt));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["BanlistToast"] = key;
        if (error)
            TempData["BanlistToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record BanlistIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<BanRowVm> Rows);

public sealed record BanRowVm(
    int Id,
    string Address,
    bool IsActive,
    string? Notes,
    DateTimeOffset Created,
    DateTimeOffset Updated);
