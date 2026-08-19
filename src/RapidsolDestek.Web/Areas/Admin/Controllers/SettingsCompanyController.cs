using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record SettingsCompanyVm(
    string Name,                              // company/* — TODO(S8): %{company.*} template variables
    string Website,
    string Phone,
    string Address,
    int LandingPageId,                        // selects list REAL SitePage rows per type
    IReadOnlyList<OptionVm> LandingPages,
    int OfflinePageId,
    IReadOnlyList<OptionVm> OfflinePages,
    int ThanksPageId,
    IReadOnlyList<OptionVm> ThanksPages,
    string ClientLogoMode,                    // "default" | "custom" (B3 radio gates the upload zone)
    int ClientLogoFileId,
    string StaffLogoMode,
    int StaffLogoFileId,
    int BackdropFileId);

/// <summary>
/// Admin company settings (mockups/admin/settings-company.html, ROADMAP §6.3 + §4 B3/B9):
/// company identity fields persist (company/*), the three page selects list real
/// SitePage rows by type (seeded canon pages from admin/pages.html), and the logo /
/// backdrop uploads land in IFileStore with the preview served back from
/// <see cref="Logo"/> (no base64 anywhere). Live-vs-persisted-only annotations sit
/// on the VM and the ROADMAP row.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsCompanyController(
    AppDbContext db,
    ISettingsService settings,
    IFileStore files) : Controller
{
    /// <summary>Mockup canon: "PNG/SVG, en fazla 2 MB" (sc.upload).</summary>
    private const long LogoMaxBytes = 2 * 1024 * 1024;

    [HttpGet("/admin/settings-company")]
    [NavKey("settings-company")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var s = await settings.GetSectionAsync("company", ct);
        string Str(string key, string fallback) => s.GetValueOrDefault(key, fallback);
        int Int(string key) => s.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : 0;

        // Selects list ALL pages of their type — the seeded offline page ("Bakım
        // Modu") is disabled in admin/pages.html canon, hiding inactive rows would
        // leave the select empty. The mockup's second options ("Bordro dönemi
        // duyurusu" …) are sample state not present in the pages.html canon table.
        var pages = await db.SitePages.OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.Type }).ToListAsync(ct);
        List<OptionVm> OfType(SitePageType type) =>
            pages.Where(p => p.Type == type).Select(p => new OptionVm(p.Id, p.Name)).ToList();
        var landing = OfType(SitePageType.Landing);
        var offline = OfType(SitePageType.Offline);
        var thanks = OfType(SitePageType.ThankYou);

        return View(new SettingsCompanyVm(
            // Fallbacks = the mockup's canonical company sample data.
            Name: Str("name", "Rapidsol Bilişim ve Danışmanlık A.Ş."),
            Website: Str("website", "https://www.rapidsol.com.tr"),
            Phone: Str("phone", "+90 212 555 24 24"),
            Address: Str("address", "Maslak Mah. Büyükdere Cad. No:245 K:11\nSarıyer / İstanbul"),
            // Unset ⇒ the seeded canon page of each type (mockup's "(varsayılan)" selection).
            LandingPageId: Or(Int("landing_page_id"), landing),   // TODO: consumed by the portal home content block (pages port)
            LandingPages: landing,
            OfflinePageId: Or(Int("offline_page_id"), offline),   // TODO: consumed by the offline view body (serves the S5 static twin today)
            OfflinePages: offline,
            ThanksPageId: Or(Int("thanks_page_id"), thanks),      // TODO: consumed by the post-create confirmation (HelpTopic.SitePageId overrides per topic)
            ThanksPages: thanks,
            ClientLogoMode: Str("client_logo_mode", "default"),   // TODO: consumed by the portal header logo swap
            ClientLogoFileId: Int("client_logo_file_id"),
            StaffLogoMode: Str("staff_logo_mode", "default"),     // TODO: consumed by the backoffice topbar logo swap
            StaffLogoFileId: Int("staff_logo_file_id"),
            BackdropFileId: Int("backdrop_file_id")));            // TODO: consumed by the agent login backdrop
    }

    // ---- save-all (multipart: fields + up to three uploads) ---------------------------

    [HttpPost("/admin/settings-company")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        IFormFile? clientLogoFile, IFormFile? staffLogoFile, IFormFile? backdropFile,
        CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();

        // ---- validate (B3 server side; nothing persists on failure) -------------------
        var name = Str("company_name");
        var website = Str("website");
        if (name.Length == 0
            || (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var web)
                || (web.Scheme != Uri.UriSchemeHttp && web.Scheme != Uri.UriSchemeHttps))))
        {
            return SaveResult("sc.errValues", error: true);
        }

        if (!int.TryParse(Str("landing_page"), out var landingId)
            || !await db.SitePages.AnyAsync(p => p.Id == landingId && p.Type == SitePageType.Landing, ct)
            || !int.TryParse(Str("offline_page"), out var offlineId)
            || !await db.SitePages.AnyAsync(p => p.Id == offlineId && p.Type == SitePageType.Offline, ct)
            || !int.TryParse(Str("thanks_page"), out var thanksId)
            || !await db.SitePages.AnyAsync(p => p.Id == thanksId && p.Type == SitePageType.ThankYou, ct))
        {
            return SaveResult("sc.errValues", error: true);
        }

        var clientMode = Str("client_logo") == "custom" ? "custom" : "default";
        var staffMode = Str("staff_logo") == "custom" ? "custom" : "default";
        foreach (var upload in new[] { clientLogoFile, staffLogoFile, backdropFile })
        {
            if (upload is { Length: > 0 } && !IsAllowedLogo(upload))
                return SaveResult("sc.errLogoFile", error: true);
        }

        var section = await settings.GetSectionAsync("company", ct);
        int Stored(string key) => section.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : 0;

        // "Özel logo yükle" chosen without a stored or newly uploaded file (B3).
        if ((clientMode == "custom" && clientLogoFile is not { Length: > 0 } && Stored("client_logo_file_id") == 0)
            || (staffMode == "custom" && staffLogoFile is not { Length: > 0 } && Stored("staff_logo_file_id") == 0))
        {
            return SaveResult("sc.errLogo", error: true);
        }

        // ---- persist -------------------------------------------------------------------
        await settings.SetAsync("company", "name", name, ct);
        await settings.SetAsync("company", "website", website, ct);
        await settings.SetAsync("company", "phone", Str("phone"), ct);
        await settings.SetAsync("company", "address", Str("address"), ct);
        await settings.SetAsync("company", "landing_page_id", landingId.ToString(), ct);
        await settings.SetAsync("company", "offline_page_id", offlineId.ToString(), ct);
        await settings.SetAsync("company", "thanks_page_id", thanksId.ToString(), ct);

        await settings.SetAsync("company", "client_logo_mode", clientMode, ct);
        await settings.SetAsync("company", "staff_logo_mode", staffMode, ct);
        await StoreUploadAsync("client_logo_file_id", clientLogoFile, Stored("client_logo_file_id"), ct);
        await StoreUploadAsync("staff_logo_file_id", staffLogoFile, Stored("staff_logo_file_id"), ct);
        await StoreUploadAsync("backdrop_file_id", backdropFile, Stored("backdrop_file_id"), ct);

        return SaveResult("sc.toastSaved");
    }

    /// <summary>
    /// Preview (B3): serves the stored logo/backdrop file back from IFileStore —
    /// the settings page's Geçerli box and backdrop panel point here (no base64).
    /// </summary>
    [HttpGet("/admin/settings-company/logo")]
    public async Task<IActionResult> Logo(string which, CancellationToken ct = default)
    {
        var key = which switch
        {
            "client" => "client_logo_file_id",
            "staff" => "staff_logo_file_id",
            "backdrop" => "backdrop_file_id",
            _ => null,
        };
        if (key is null || !int.TryParse(await settings.GetAsync("company", key, ct), out var fileId) || fileId == 0)
            return NotFound();

        var file = await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
            return NotFound();

        var content = await files.OpenAsync(file, ct);
        return File(content, file.MimeType, file.Name);
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>Mockup canon "PNG/SVG, en fazla 2 MB": extension + content-type + size.</summary>
    private static bool IsAllowedLogo(IFormFile upload)
    {
        if (upload.Length > LogoMaxBytes)
            return false;
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        return extension is ".png" or ".svg"
            && upload.ContentType is "image/png" or "image/svg+xml" or "" or null;
    }

    /// <summary>Streams a new upload into IFileStore, points the setting at it and drops the replaced file.</summary>
    private async Task StoreUploadAsync(string key, IFormFile? upload, int previousId, CancellationToken ct)
    {
        if (upload is not { Length: > 0 })
            return;

        await using var content = upload.OpenReadStream();
        var stored = await files.SaveAsync(content, Path.GetFileName(upload.FileName),
            string.IsNullOrEmpty(upload.ContentType)
                ? (Path.GetExtension(upload.FileName).ToLowerInvariant() == ".svg" ? "image/svg+xml" : "image/png")
                : upload.ContentType, ct);
        db.StoredFiles.Add(stored);
        await db.SaveChangesAsync(ct);
        await settings.SetAsync("company", key, stored.Id.ToString(), ct);

        // The replaced file has no other reference (settings pointer only) — clean it up.
        if (previousId != 0 && await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == previousId, ct) is { } old)
        {
            await files.DeleteAsync(old, ct);
            db.StoredFiles.Remove(old);
            await db.SaveChangesAsync(ct);
        }
    }

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["ScToast"] = toastKey;
        if (error)
            TempData["ScToastError"] = true;
        return Redirect("/admin/settings-company");
    }

    private static int Or(int id, List<OptionVm> options) =>
        id != 0 && options.Any(o => o.Id == id) ? id : options.FirstOrDefault()?.Id ?? 0;
}
