using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// One built-in feature module managed by admin/plugins (§3 "replaced": no PHP
/// plugin runtime — the catalog is code, the install/enable state is the
/// "features" Setting section). Name/description/version mirror the mockup rows
/// (sample-data treatment: the mockup keeps them outside PAGE_I18N — hardcoded,
/// flagged for canon sign-off). ConfigureUrl points at the module's settings
/// page where one exists; null renders a dead link (flagged).
/// </summary>
public sealed record FeatureModule(
    string Key,
    string Name,
    string Slug,
    string Version,
    string Description,
    string? ConfigureUrl,
    bool BindsRequireTwofa = false);

/// <summary>
/// Admin plugins page (mockups/admin/plugins.html, ROADMAP §6.3, §3 "replaced"):
/// osTicket's plugin marketplace is REPLACED by feature flags over built-in
/// modules. Installed table = catalog rows with features/&lt;key&gt;.installed set
/// (B1: search/sort/pagination/bulk); "Kurulabilir Eklentiler" = the remaining
/// catalog, installed with one click. Bulk enable/disable flips
/// features/&lt;key&gt;.enabled; delete uninstalls (clears the module's rows —
/// pl.bulkHelp "deleting also removes the plugin's configuration").
/// LIVE consumers: auth_ldap gates the staff-edit LDAP backend option
/// (StaffController); the 2FA-email module binds to the EXISTING
/// agents/require_twofa key (StaffSignInManager email-code step) — enable/disable
/// here IS the settings-agents switch, no duplicate flag (flagged).
/// Persisted-only: storage_s3/storage_fs (only the "fs" IFileStore backend is
/// registered; configure → settings-system attachments), audit (the audit
/// interceptor is a compliance floor and runs unconditionally — the mockup's
/// disabled row is sample state, flagged), slack (TODO(S8): notification fan-out).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class PluginsController(AppDbContext db, ISettingsService settings) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "installed"];

    /// <summary>The built-in module catalog, mockup row order (installed table
    /// first, then the available directory).</summary>
    public static readonly IReadOnlyList<FeatureModule> Catalog =
    [
        new("auth_ldap", "LDAP Kimlik Doğrulama", "plugins/auth-ldap", "0.6.2",
            "Temsilci girişleri için LDAP / Active Directory arka ucunu açar.",
            "/admin/staff"),
        new("storage_s3", "S3 Depolama", "plugins/storage-s3", "1.0.1",
            "Ekleri S3 uyumlu bir nesne deposunda saklar.",
            "/admin/settings-system"),
        new("audit", "Denetim Günlüğü", "plugins/audit", "1.1.0",
            "Yönetim panelindeki denetim izi görünümlerini açar.",
            "/admin/audit-logs"),
        new("twofa_email", "2FA E-posta Doğrulama", "plugins/twofa-email", "1.2.0",
            "Temsilci girişlerinde e-posta ile ikinci doğrulama adımı ekler.",
            "/admin/settings-agents", BindsRequireTwofa: true),
        new("storage_fs", "Depolama: Dosya Sistemi", "plugins/storage-fs", "1.0.3",
            "Ekleri veritabanı yerine sunucu diskinde saklar.",
            "/admin/settings-system"),
        new("slack", "Slack Bildirimleri", "plugins/slack", "0.9.4",
            "Yeni talep ve yanıt olaylarını seçilen Slack kanalına iletir.",
            null),
    ];

    [HttpGet("/admin/plugins")]
    [NavKey("plugins")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var rows = await LoadStateAsync(ct);

        // B1 search filters BOTH lists (name/slug, the toolbar's single box).
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            rows = rows.Where(r =>
                r.Module.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Module.Slug.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var installed = rows.Where(r => r.InstalledOn is not null).ToList();
        var available = rows.Where(r => r.InstalledOn is null).Select(r => r.Module).ToList();

        var key = SortKeys.Contains(sort) ? sort! : "installed";
        var desc = dir == "asc" ? false : dir == "desc" || key == "installed";
        installed = (key, desc) switch
        {
            ("name", true) => [.. installed.OrderByDescending(r => r.Module.Name, StringComparer.Create(new CultureInfo("tr-TR"), true))],
            ("name", false) => [.. installed.OrderBy(r => r.Module.Name, StringComparer.Create(new CultureInfo("tr-TR"), true))],
            (_, false) => [.. installed.OrderBy(r => r.InstalledOn)],
            // Mockup note: the canon table's row order (LDAP → S3 → Audit) does not
            // match its own sorted-desc "Kurulma" indicator — the real sort wins.
            _ => [.. installed.OrderByDescending(r => r.InstalledOn)],
        };

        var total = installed.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        installed = [.. installed.Skip((page - 1) * PageSize).Take(PageSize)];

        return View(new PluginsIndexVm(
            q, key, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), installed, available));
    }

    /// <summary>"Kur" on an available row: stamps the install date. osTicket
    /// parity: a fresh install starts DISABLED — enabling is the separate step
    /// (bulk enable / the shared agents/require_twofa switch for 2FA-email).</summary>
    [HttpPost("/admin/plugins/install")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Install(string key, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var module = Catalog.FirstOrDefault(m => m.Key == key);
        if (module is null)
            return ToastBack("pl.errUnknown", error: true, returnUrl: returnUrl);

        await settings.SetAsync("features", $"{module.Key}.installed",
            DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        if (!module.BindsRequireTwofa)
            await settings.SetAsync("features", $"{module.Key}.enabled", "false", ct);

        return ToastBack("pl.toastInstalled", returnUrl: returnUrl);
    }

    /// <summary>dlg-more bulk enable/disable/delete over the selected modules.
    /// Delete = uninstall: the module's feature rows are cleared (pl.bulkHelp);
    /// the 2FA-email module writes the SHARED agents/require_twofa key instead of
    /// a features/* twin — disabling/uninstalling it turns the settings-agents
    /// switch off too (flagged).</summary>
    [HttpPost("/admin/plugins/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, string[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("pl.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        // Only installed modules respond to bulk actions (the available list has
        // no checkboxes — tampered ids are skipped).
        var state = await LoadStateAsync(ct);
        var count = 0;
        foreach (var row in state.Where(r => r.InstalledOn is not null && ids.Contains(r.Module.Key)))
        {
            var module = row.Module;
            switch (act)
            {
                case "enable":
                case "disable":
                    var on = act == "enable";
                    if (module.BindsRequireTwofa)
                        await settings.SetAsync("agents", "require_twofa", on.ToString(), ct);
                    else
                        await settings.SetAsync("features", $"{module.Key}.enabled", on.ToString(), ct);
                    break;
                case "delete":
                    await settings.SetAsync("features", $"{module.Key}.installed", "", ct);
                    if (module.BindsRequireTwofa)
                        await settings.SetAsync("agents", "require_twofa", "false", ct);
                    else
                        await settings.SetAsync("features", $"{module.Key}.enabled", "false", ct);
                    break;
            }
            count++;
        }

        TempData["PluginsToastOk"] = count;
        TempData["PluginsToast"] = "pl.bulkDone";
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<List<PluginRowVm>> LoadStateAsync(CancellationToken ct)
    {
        var section = await settings.GetSectionAsync("features", ct);
        var agents = await settings.GetAgentsAsync(ct);

        // Missing rows fall back to the seeded plugins.html canon (fresh-db parity
        // with GetFeaturesAsync defaults): first three installed, audit disabled.
        var defaults = new Dictionary<string, (string InstalledOn, bool Enabled)>
        {
            ["auth_ldap"] = ("2026-05-12", true),
            ["storage_s3"] = ("2026-01-03", true),
            ["audit"] = ("2026-02-22", false),
        };

        return Catalog.Select(m =>
        {
            var hasRow = section.TryGetValue($"{m.Key}.installed", out var stamp);
            if (!hasRow)
                stamp = defaults.TryGetValue(m.Key, out var d) ? d.InstalledOn : "";
            DateOnly? installedOn = DateOnly.TryParse(stamp, CultureInfo.InvariantCulture, out var day) ? day : null;

            bool enabled;
            if (m.BindsRequireTwofa)
                enabled = agents.RequireTwofa; // shared key — settings-agents twin
            else if (section.TryGetValue($"{m.Key}.enabled", out var e))
                enabled = bool.TryParse(e, out var b) && b;
            else
                enabled = defaults.TryGetValue(m.Key, out var d) && d.Enabled;

            return new PluginRowVm(m, installedOn, enabled);
        }).ToList();
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["PluginsToast"] = key;
        if (error)
            TempData["PluginsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record PluginRowVm(FeatureModule Module, DateOnly? InstalledOn, bool Enabled);

public sealed record PluginsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<PluginRowVm> Installed,
    IReadOnlyList<FeatureModule> Available);
