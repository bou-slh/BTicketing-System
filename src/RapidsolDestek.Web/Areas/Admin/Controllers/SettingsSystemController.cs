using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record LanguageRowVm(string Code, string Name);

public sealed record SettingsSystemVm(
    bool Online,                            // LIVE: inverse of system/offline (MaintenanceModeMiddleware)
    string HelpdeskUrl,
    string HelpdeskTitle,                   // LIVE: layout <title> (core/helpdesk_title)
    int DefaultDeptId,                      // LIVE: TicketService.CreateAsync cascade (core/default_dept_id)
    IReadOnlyList<OptionVm> Departments,
    bool ForceHttps,
    int CollisionMinutes,
    int PageSize,
    string LogLevel,
    int LogPurgeMonths,
    bool ShowAvatars,
    bool RichText,
    string IframeAllowlist,
    string EmbedAllowlist,
    string AclIps,
    string AclScope,
    string Locale,
    string Timezone,
    string TimeFormatMode,
    string TimeFormat,
    string DateFormat,
    string DateTimeFormat,
    string DayFormat,
    int DefaultScheduleId,
    IReadOnlyList<OptionVm> Schedules,
    string PrimaryLanguage,                 // LIVE: request-culture fallback (Program.cs provider)
    IReadOnlyList<LanguageRowVm> SecondaryLanguages,
    IReadOnlyList<LanguageRowVm> AddableLanguages,
    AttachmentSettings Attachments);        // MaxSizeMb LIVE at every upload ingress

/// <summary>
/// Admin system settings (mockups/admin/settings-system.html, ROADMAP §6.3 + §4 B3/B4/B9):
/// the online switch reads/writes the SAME system/offline Setting the S5
/// MaintenanceModeMiddleware serves the portal offline page from; the attachment
/// max-size is enforced at every real upload path (AttachmentSettings); the language
/// section is real B4 add/remove rows persisted as system/secondary_languages.
/// Live-vs-persisted-only per control is annotated on the save maps below and on
/// the ROADMAP row.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsSystemController(AppDbContext db, ISettingsService settings) : Controller
{
    // Language catalog (autonyms — not translated, mockup option text): tr/en are the
    // real cultures (resx exist); de/fr/ar persist as secondary languages with
    // TODO: i18n resources — only TR/EN resx ship, extra languages have no UI
    // translations yet (flagged for canon on the ROADMAP row). Public: the S7
    // system-info language-pack table renders names from the same catalog.
    public static readonly LanguageRowVm[] LanguageCatalog =
    [
        new("tr", "Türkçe"),
        new("en", "English (United States)"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("ar", "العربية"),
    ];

    private static readonly string[] LogLevels = ["none", "error", "warn", "debug"];
    private static readonly int[] LogPurgeChoices = [0, 1, 3, 6, 12];
    private static readonly int[] PageSizes = [10, 25, 50, 100];
    private static readonly int[] MaxSizeChoices = [1, 8, 16, 64];
    // Locale / timezone catalogs = the mockup's option lists (display strings are data).
    private static readonly string[] Locales = ["tr-TR", "en-US", "en-GB"];
    private static readonly string[] Timezones = ["Europe/Istanbul", "UTC", "Europe/Berlin"];

    public static string LocaleLabel(string code) => code switch
    {
        "en-US" => "English (United States)",
        "en-GB" => "English (United Kingdom)",
        _ => "Türkçe (Türkiye)",
    };

    public static string TimezoneLabel(string id) => id switch
    {
        "UTC" => "UTC (+00:00)",
        "Europe/Berlin" => "Europe/Berlin (+02:00)",
        _ => "Europe/Istanbul (+03:00)",
    };

    [HttpGet("/admin/settings-system")]
    [NavKey("settings-system")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var sys = await settings.GetSectionAsync("system", ct);
        string Str(string key, string fallback) => sys.GetValueOrDefault(key, fallback);
        bool Bool(string key, bool fallback) =>
            sys.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
        int Int(string key, int fallback) =>
            sys.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

        var departments = await db.Departments.OrderBy(d => d.Id)
            .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct);
        // Business-hours schedules only (a holiday list cannot be the default work
        // calendar); the mockup's 2 options are a sample subset of the 3 real rows.
        // Inactive schedules stay pickable only while currently pointed at (S7
        // schedules bulk enable/disable).
        var storedScheduleId = Int("default_schedule_id", 0);
        var schedules = await db.Schedules
            .Where(s => s.Kind != ScheduleKind.Holidays && (s.IsActive || s.Id == storedScheduleId))
            .OrderBy(s => s.Id).Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct);

        // Unset ⇒ the calendar SLA timers already run against: the default SLA's schedule.
        var defaultScheduleId = storedScheduleId;
        if (defaultScheduleId == 0)
        {
            var defaultSlaId = ParseOrZero(await settings.GetAsync("core", "default_sla_id", ct));
            defaultScheduleId = await db.SlaPlans.Where(s => s.Id == defaultSlaId)
                .Select(s => s.ScheduleId ?? 0).FirstOrDefaultAsync(ct);
            if (defaultScheduleId == 0)
                defaultScheduleId = schedules.FirstOrDefault()?.Id ?? 0;
        }

        var primary = Str("primary_language", "tr") is "en" ? "en" : "tr";
        var secondary = ParseLanguages(Str("secondary_languages", "en"), primary);

        return View(new SettingsSystemVm(
            Online: !(bool.TryParse(Str(MaintenanceModeMiddleware.SettingKey, "false"), out var off) && off),
            HelpdeskUrl: await settings.GetAsync("core", "helpdesk_url", ct) ?? "https://destek.rapidsol.com.tr/",
            HelpdeskTitle: await settings.GetAsync("core", "helpdesk_title", ct) ?? "RapidsolDestek",
            DefaultDeptId: ParseOrZero(await settings.GetAsync("core", "default_dept_id", ct)),
            Departments: departments,
            ForceHttps: Bool("force_https", true),          // TODO: consumed by the HTTPS redirect policy (env-gated UseHttpsRedirection today)
            CollisionMinutes: Int("collision_minutes", 3),  // TODO: consumed by the composer lock TTL (IThreadService lock API exists, wiring pending with tickets/lock_mode)
            PageSize: Int("page_size", 25),                 // TODO: consumed by list pagination (TicketListEngine.PageSize is a const today)
            LogLevel: Str("log_level", "warn"),             // LIVE: SystemLogService stores only levels this allows (S7 system-logs port)
            LogPurgeMonths: Int("log_purge_months", 3),     // LIVE (S8): RetentionPurgeJob (syslog + sent outbox rows; 0 = never)
            ShowAvatars: Bool("show_avatars", true),        // TODO: consumed by the thread renderers
            RichText: Bool("rich_text", true),              // TODO: consumed by the composer toolbar gate (rd.js enhanceTextareas)
            IframeAllowlist: Str("iframe_allowlist", ""),   // TODO(S9): consumed by CSP frame-ancestors
            EmbedAllowlist: Str("embed_allowlist", ""),     // TODO(S8): consumed by the HTML sanitizer's embed policy
            AclIps: Str("acl_ips", ""),                     // TODO(S9): consumed by an IP allowlist middleware
            AclScope: Str("acl_scope", "admin"),            // TODO(S9): scope of the ACL middleware
            Locale: Str("locale", "tr-TR"),                 // TODO: consumed by number/date formatting helpers
            Timezone: Str("timezone", "Europe/Istanbul"),   // TODO: consumed by date display conversion
            TimeFormatMode: Str("time_format_mode", "locale"), // TODO: with the four patterns below (display formatting helpers)
            TimeFormat: Str("format_time", "HH:mm"),
            DateFormat: Str("format_date", "d MMMM y"),
            DateTimeFormat: Str("format_datetime", "d MMM y HH:mm"),
            DayFormat: Str("format_day", "EEEE, d MMMM"),
            DefaultScheduleId: defaultScheduleId,           // TODO: consumed as the SLA fallback calendar (SLA sweeps use each plan's schedule today)
            Schedules: schedules,
            PrimaryLanguage: primary,
            SecondaryLanguages: secondary,
            AddableLanguages: LanguageCatalog
                .Where(l => l.Code != primary && secondary.All(s => s.Code != l.Code))
                .ToList(),
            Attachments: await settings.GetAttachmentsAsync(ct)));
    }

    // ---- save-all (main form) ----------------------------------------------------------

    [HttpPost("/admin/settings-system")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();
        bool Chk(string name) => form[name].Contains("true");

        // ---- validate (B3 server side) ------------------------------------------------
        var url = Str("helpdesk_url");
        var title = Str("helpdesk_title");
        if (title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return SaveResult("ss.errValues", error: true);
        }

        if (!int.TryParse(Str("default_dept"), out var deptId)
            || !await db.Departments.AnyAsync(d => d.Id == deptId, ct))
            return SaveResult("ss.errValues", error: true);

        if (!int.TryParse(Str("collision"), out var collision) || collision < 1
            || !int.TryParse(Str("page_size"), out var pageSize) || !PageSizes.Contains(pageSize)
            || !LogLevels.Contains(Str("log_level"))
            || !int.TryParse(Str("log_purge"), out var logPurge) || !LogPurgeChoices.Contains(logPurge)
            || Str("acl_scope") is not ("admin" or "staff" or "all")
            || !Locales.Contains(Str("locale"))
            || !Timezones.Contains(Str("timezone"))
            || Str("time_format_mode") is not ("locale" or "24h" or "advanced"))
        {
            return SaveResult("ss.errValues", error: true);
        }

        if (!int.TryParse(Str("default_schedule"), out var scheduleId)
            || !await db.Schedules.AnyAsync(s => s.Id == scheduleId && s.Kind != ScheduleKind.Holidays, ct))
            return SaveResult("ss.errValues", error: true);

        var primary = Str("primary_lang") == "en" ? "en" : "tr";

        if (Str("storage") is not ("fs" or "db" or "s3")
            || !int.TryParse(Str("max_size"), out var maxSize) || !MaxSizeChoices.Contains(maxSize))
            return SaveResult("ss.errValues", error: true);

        // ---- persist --------------------------------------------------------------------
        // General. Online is LIVE: same key MaintenanceModeMiddleware reads (inverted).
        await settings.SetAsync(MaintenanceModeMiddleware.SettingNamespace,
            MaintenanceModeMiddleware.SettingKey, (!Chk("online")).ToString(), ct);
        await settings.SetAsync("core", "helpdesk_url", url, ct);       // LIVE (S8): %{ticket.link} base (EmailTemplateRenderer)
        await settings.SetAsync("core", "helpdesk_title", title, ct);   // LIVE: layout <title>; S8: From display-name fallback (OutboundMailJob)
        await settings.SetAsync("core", "default_dept_id", deptId.ToString(), ct); // LIVE: TicketService.CreateAsync cascade

        // Advanced general — persisted-only, consumers annotated on the VM build above.
        await settings.SetAsync("system", "force_https", Chk("force_https").ToString(), ct);
        await settings.SetAsync("system", "collision_minutes", collision.ToString(), ct);
        await settings.SetAsync("system", "page_size", pageSize.ToString(), ct);
        await settings.SetAsync("system", "log_level", Str("log_level"), ct);
        await settings.SetAsync("system", "log_purge_months", logPurge.ToString(), ct);
        await settings.SetAsync("system", "show_avatars", Chk("avatars").ToString(), ct);
        await settings.SetAsync("system", "rich_text", Chk("richtext").ToString(), ct);
        await settings.SetAsync("system", "iframe_allowlist", Str("iframe_list"), ct);
        await settings.SetAsync("system", "embed_allowlist", Str("embed_list"), ct);
        await settings.SetAsync("system", "acl_ips", Str("acl_ips"), ct);
        await settings.SetAsync("system", "acl_scope", Str("acl_scope"), ct);

        // Date & time — persisted-only (display formatting helpers pending).
        await settings.SetAsync("system", "locale", Str("locale"), ct);
        await settings.SetAsync("system", "timezone", Str("timezone"), ct);
        await settings.SetAsync("system", "time_format_mode", Str("time_format_mode"), ct);
        await settings.SetAsync("system", "format_time", Str("format_time"), ct);
        await settings.SetAsync("system", "format_date", Str("format_date"), ct);
        await settings.SetAsync("system", "format_datetime", Str("format_datetime"), ct);
        await settings.SetAsync("system", "format_day", Str("format_day"), ct);
        await settings.SetAsync("system", "default_schedule_id", scheduleId.ToString(), ct);

        // Languages. Primary is LIVE (request-culture fallback provider, Program.cs).
        // A primary that also sits in the secondary list is dropped from the list.
        await settings.SetAsync("system", "primary_language", primary, ct);
        var secondary = ParseLanguages(
            await settings.GetAsync("system", "secondary_languages", ct) ?? "en", primary);
        await settings.SetAsync("system", "secondary_languages",
            string.Join(",", secondary.Select(l => l.Code)), ct);

        // Attachments. max_size_mb is LIVE at every upload ingress; storage +
        // auth_required persisted-only (annotated at AttachmentSettings).
        await settings.SetAsync("attachments", "storage", Str("storage"), ct);
        await settings.SetAsync("attachments", "max_size_mb", maxSize.ToString(), ct);
        await settings.SetAsync("attachments", "auth_required", Chk("auth_required").ToString(), ct);

        return SaveResult("ss.toastSaved");
    }

    // ---- secondary-language rows (B4): add/remove round-trip like dlg-seq --------------

    [HttpPost("/admin/settings-system/languages")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Languages(string? add, string? remove, string? code, CancellationToken ct = default)
    {
        var primary = await settings.GetAsync("system", "primary_language", ct) is "en" ? "en" : "tr";
        var secondary = ParseLanguages(
            await settings.GetAsync("system", "secondary_languages", ct) ?? "en", primary)
            .Select(l => l.Code).ToList();

        if (!string.IsNullOrEmpty(remove))
        {
            if (!secondary.Remove(remove))
                return SaveResult("ss.errLang", error: true);
        }
        else if (add is not null)
        {
            var chosen = (code ?? "").Trim();
            if (LanguageCatalog.All(l => l.Code != chosen) || chosen == primary || secondary.Contains(chosen))
                return SaveResult("ss.errLang", error: true);
            // TODO: i18n resources — only TR/EN resx exist; extra languages persist
            // in the preference list without UI translations yet (canon flag).
            secondary.Add(chosen);
        }
        else
        {
            return SaveResult("ss.errLang", error: true);
        }

        await settings.SetAsync("system", "secondary_languages", string.Join(",", secondary), ct);
        return SaveResult("ss.toastLangs");
    }

    // ---- helpers ------------------------------------------------------------------------

    private static List<LanguageRowVm> ParseLanguages(string csv, string primary) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c != primary)
            .Select(c => LanguageCatalog.FirstOrDefault(l => l.Code == c))
            .Where(l => l is not null)
            .Cast<LanguageRowVm>()
            .Distinct()
            .ToList();

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["SsToast"] = toastKey;
        if (error)
            TempData["SsToastError"] = true;
        return Redirect("/admin/settings-system");
    }

    private static int ParseOrZero(string? value) => int.TryParse(value, out var i) ? i : 0;
}
