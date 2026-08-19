using System.Net;
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
/// Admin API keys (mockups/admin/apikeys.html, ROADMAP §6.3): the B1 engine over
/// ApiKey rows, per-row edit dialogs prefilled server-side (B2 — the mockup shares
/// ONE dlg-key between the new-button and every row; split per the SLA precedent),
/// dlg-more bulk enable/disable/delete, server-side key generation (the create
/// dialog shows a crypto-random pregenerated key, ak.keyHelp), regenerate and
/// copy-to-clipboard (both INVENTED UI — the mockup renders neither control,
/// the ROADMAP row promises them; flagged). Enforcement: the key+IP gate lives in
/// <see cref="ApiKeyAuthenticator"/> — TODO(S8): consumed by the REST dispatcher.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class ApiKeysController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["ip", "updated"];

    [HttpGet("/admin/apikeys")]
    [NavKey("apikeys")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new ApiKeysIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows,
            // Server-side generation (ak.keyHelp "otomatik oluşturulur"): the
            // create dialog displays this pregenerated key; the save re-validates
            // format/uniqueness and mints a fresh one on any mismatch.
            NewKey: ApiKeyAuthenticator.GenerateKey()));
    }

    /// <summary>Create dialog submit (B2): the posted key is the pregenerated
    /// value shown in the dialog — anything unexpected is replaced server-side.</summary>
    [HttpPost("/admin/apikeys/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? key, string? ip, bool active, bool canTickets, bool canJobs,
        string? notes, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        ip = NormalizeIp(ip);
        if (ip is null)
            return ToastBack("ak.errIp", error: true, returnUrl: returnUrl);

        // Keys are server-generated: reject tampered formats and collisions by
        // minting a fresh one instead of trusting the client value.
        key = key?.Trim().ToUpperInvariant();
        if (key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9A-F]{32}$")
            || await db.ApiKeys.AnyAsync(k => k.Key == key, ct))
        {
            key = ApiKeyAuthenticator.GenerateKey();
        }

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.ApiKeys.Add(new ApiKey
            {
                Key = key,
                IpAddress = ip,
                IsActive = active,
                CanCreateTickets = canTickets,
                CanTriggerJobs = canJobs,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            });
            await db.SaveChangesAsync(ct);
        }

        return ToastBack("ak.toastCreated", returnUrl: returnUrl);
    }

    /// <summary>Per-row edit dialog submit (B2): the key itself is immutable —
    /// use Regenerate to replace it.</summary>
    [HttpPost("/admin/apikeys/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        int id, string? ip, bool active, bool canTickets, bool canJobs,
        string? notes, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        ip = NormalizeIp(ip);
        if (ip is null)
            return ToastBack("ak.errIp", error: true, returnUrl: returnUrl);

        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct);
        if (apiKey is null)
            return NotFound();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            apiKey.IpAddress = ip;
            apiKey.IsActive = active;
            apiKey.CanCreateTickets = canTickets;
            apiKey.CanTriggerJobs = canJobs;
            apiKey.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            await db.SaveChangesAsync(ct);
        }

        return ToastBack("ak.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>Replaces the row's key with a fresh crypto-random one (ROADMAP
    /// "regenerate"; osTicket admins delete+recreate — adapted). The external
    /// system keeps working only after the new key is copied over (ak.keyHelp).</summary>
    [HttpPost("/admin/apikeys/regenerate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate(int id, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct);
        if (apiKey is null)
            return NotFound();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            apiKey.Key = ApiKeyAuthenticator.GenerateKey();
            await db.SaveChangesAsync(ct);
        }

        return ToastBack("ak.toastRegenerated", returnUrl: returnUrl);
    }

    /// <summary>dlg-more bulk actions; keys are referenced by nothing, so delete
    /// needs no guard.</summary>
    [HttpPost("/admin/apikeys/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("ak.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var keys = await db.ApiKeys.Where(k => ids.Contains(k.Id)).ToListAsync(ct);
        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var key in keys)
            {
                switch (act)
                {
                    case "enable": key.IsActive = true; break;
                    case "disable": key.IsActive = false; break;
                    case "delete": db.ApiKeys.Remove(key); break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["ApiKeysToastOk"] = keys.Count;
        TempData["ApiKeysToast"] = "ak.bulkDone";
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>ak.ipHelp: the IP restriction is mandatory — a parseable v4/v6
    /// address, stored canonically so the authenticator's exact match holds.</summary>
    private static string? NormalizeIp(string? ip) =>
        IPAddress.TryParse((ip ?? "").Trim(), out var parsed) ? parsed.ToString() : null;

    /// <summary>B1 core: key/IP search + sort (mockup indicator: updated desc).</summary>
    private IQueryable<ApiKeyRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.ApiKeys.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(k =>
                EF.Functions.ILike(k.Key, pattern) || EF.Functions.ILike(k.IpAddress, pattern));
        }

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var d = desc = dir == "asc" ? false : dir == "desc" || key == "updated";
        var ordered = (key, d) switch
        {
            ("ip", true) => query.OrderByDescending(k => k.IpAddress).ThenBy(k => k.Id),
            ("ip", false) => query.OrderBy(k => k.IpAddress).ThenBy(k => k.Id),
            (_, false) => query.OrderBy(k => k.UpdatedAt ?? k.CreatedAt).ThenBy(k => k.Id),
            // Seed rows share one CreatedAt — the id tiebreak keeps the canon row
            // order 7A3F… → B05C… under the default updated-desc sort.
            _ => query.OrderByDescending(k => k.UpdatedAt ?? k.CreatedAt).ThenBy(k => k.Id),
        };
        return ordered.Select(k => new ApiKeyRowVm(
            k.Id, k.Key, k.IpAddress, k.IsActive, k.CanCreateTickets, k.CanTriggerJobs,
            k.Notes, k.UpdatedAt ?? k.CreatedAt));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["ApiKeysToast"] = key;
        if (error)
            TempData["ApiKeysToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record ApiKeysIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<ApiKeyRowVm> Rows,
    string NewKey);

public sealed record ApiKeyRowVm(
    int Id,
    string Key,
    string IpAddress,
    bool IsActive,
    bool CanCreateTickets,
    bool CanTriggerJobs,
    string? Notes,
    DateTimeOffset Updated)
{
    /// <summary>Mockup cell format "7A3F09B2…D41C": first 8 + last 4 chars.</summary>
    public string KeyDisplay => Key.Length > 12 ? $"{Key[..8]}…{Key[^4..]}" : Key;
}
