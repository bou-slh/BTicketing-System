using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record SystemLogRowVm(
    int Id,
    SystemLogType Type,
    string Title,
    string Log,
    string? Logger,
    string? Ip,
    DateTimeOffset CreatedAt);

public sealed record SystemLogsIndexVm(
    string? From,
    string? To,
    string Level,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int FromRow,
    int ToRow,
    IReadOnlyList<SystemLogRowVm> Rows);

/// <summary>
/// Admin system logs (mockups/admin/system-logs.html, ROADMAP §6.3): B1 filters
/// (from/to/level) + Apply over the real SystemLogEntry table (fed by
/// ISystemLogService — auth failures today, mail pipeline in S8; what gets stored
/// is system/log_level's call, which this page's banner links to). "Seçilenleri Sil"
/// really deletes the checked rows behind the mockup's dlg-purge confirm; each row
/// opens a detail dialog (B2) — INVENTED: the mockup defines no row dialog, only the
/// purge confirm (flagged). Automatic retention (log_purge_months) is LIVE (S8):
/// the daily RetentionPurgeJob deletes rows older than the configured months.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SystemLogsController(AppDbContext db, ISystemLogService syslog) : Controller
{
    // The mockup's page: 7 rows and "1–7 / 132" with a last page of 19 (= ceil(132/7)).
    public const int PageSize = 7;

    private static readonly string[] Levels = ["all", "error", "warn", "debug"];

    [HttpGet("/admin/system-logs")]
    [NavKey("system-logs")]
    public async Task<IActionResult> Index(
        string? from, string? to, string? level, string? dir, int page = 1,
        CancellationToken ct = default)
    {
        level = Levels.Contains(level) ? level! : "all";
        var desc = dir != "asc"; // mockup indicator: date sorted-desc

        var query = db.SystemLogEntries.AsQueryable();
        query = level switch
        {
            "error" => query.Where(e => e.Type == SystemLogType.Error),
            "warn" => query.Where(e => e.Type == SystemLogType.Warning),
            "debug" => query.Where(e => e.Type == SystemLogType.Debug),
            _ => query,
        };
        if (ParseDay(from) is { } fromAt)
            query = query.Where(e => e.CreatedAt >= fromAt);
        if (ParseDay(to) is { } toAt)
            query = query.Where(e => e.CreatedAt < toAt.AddDays(1)); // inclusive end day

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);

        var ordered = desc
            ? query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id);
        var rows = await ordered
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(e => new SystemLogRowVm(e.Id, e.Type, e.Title, e.Log, e.Logger, e.IpAddress, e.CreatedAt))
            .ToListAsync(ct);

        return View(new SystemLogsIndexVm(
            NormalizeDay(from), NormalizeDay(to), level, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>
    /// dlg-purge confirm → REAL delete of the checked rows. SystemLogEntry is
    /// INotAudited (osTicket parity: syslog maintenance isn't audit material);
    /// the deletion itself is offered back to the syslog as a debug event —
    /// stored only while system/log_level is "debug", honestly.
    /// </summary>
    [HttpPost("/admin/system-logs/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int[] ids, string? returnUrl, CancellationToken ct = default)
    {
        if (ids.Length == 0)
            return ToastBack("sl.errNone", error: true, returnUrl: returnUrl);

        var deleted = await db.SystemLogEntries.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
        await syslog.LogAsync(SystemLogType.Debug,
            $"Günlük kayıtları silindi ({deleted} kayıt)",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString(), logger: "syslog", ct: ct);

        TempData["SlToastCount"] = deleted;
        return ToastBack("sl.toastDeleted", returnUrl: returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Date-input day (yyyy-MM-dd) → local-midnight offset instant.</summary>
    private static DateTimeOffset? ParseDay(string? value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
            return null;
        var at = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
    }

    private static string? NormalizeDay(string? value) =>
        ParseDay(value) is null ? null : value;

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["SlToast"] = key;
        if (error)
            TempData["SlToastError"] = true;
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
    }
}
