using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record InfoRowVm(string LabelKey, string Value, string? Pill = null, string? PillKey = null);

public sealed record ComponentVm(string Name, string Version, string HelpKey);

public sealed record LanguagePackVm(string Name, string Code, string Version, bool Installed);

public sealed record SystemInfoVm(
    string AppVersion,          // informational version; SDK SourceLink appends +sha when built in git
    string? GitSha,             // short commit, when the informational version carries one
    IReadOnlyList<InfoRowVm> ServerRows,
    TimeSpan Uptime,            // rendered via si.uptimeFmt (localized d/h/m units)
    long MemoryMiB,
    IReadOnlyList<ComponentVm> Components,
    IReadOnlyList<InfoRowVm> DbRows,
    IReadOnlyList<LanguagePackVm> Languages);

/// <summary>
/// Admin system information (mockups/admin/system-info.html, ROADMAP §6.3): the
/// mockup's osTicket/PHP-era rows translated HONESTLY to this stack per the row's
/// "PHP→.NET" note — .NET runtime + ASP.NET Core versions, PostgreSQL server via
/// SELECT version(), EF/Npgsql/MailKit… package versions instead of PHP extensions,
/// app assembly version (+ git sha when embedded). Every value on the page is read
/// from the live process/database at request time; nothing is sample data.
/// "Update check": no update channel exists — the mockup's "Güncel" pill would lie,
/// so the version renders with an honest "no update channel configured" pill
/// instead. TODO(S9): update feed (GitHub releases probe) — flagged on the ROADMAP row.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public partial class SystemInfoController(AppDbContext db, ISettingsService settings) : Controller
{
    [HttpGet("/admin/system-info")]
    [NavKey("system-info")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        // ---- app + runtime (mockup rows si.version / si.webServer / si.phpVersion →
        //      .NET runtime; si.os/si.uptime/si.memory are INVENTED honest additions) ----
        var (appVersion, gitSha) = SplitInformational(typeof(Program).Assembly);
        var process = Process.GetCurrentProcess();
        var uptime = DateTimeOffset.Now - process.StartTime;

        // Mockup's "MySQL Sürümü" server row → the real PostgreSQL server
        // (si.mysqlVersion key kept for DOM/key parity, VALUE translated in resx).
        var pgVersion = await ScalarAsync("SELECT version() AS \"Value\"", ct);
        var shortPg = ShortPgVersion().Match(pgVersion) is { Success: true } m ? m.Value : pgVersion;

        var serverRows = new List<InfoRowVm>
        {
            // Kestrel is the in-process server; version = the ASP.NET Core framework.
            new("si.webServer",
                $"Kestrel (ASP.NET Core {VersionOf(typeof(WebApplication).Assembly)})", Pill: "ok"),
            new("si.mysqlVersion", shortPg, Pill: "ok"),
            new("si.phpVersion", RuntimeInformation.FrameworkDescription, Pill: "ok"),
            new("si.os", RuntimeInformation.OSDescription),
        };

        // ---- database (si.secDb) ---------------------------------------------------------
        var dbName = await ScalarAsync("SELECT current_database() AS \"Value\"", ct);
        var encoding = await ScalarAsync("SELECT current_setting('server_encoding') AS \"Value\"", ct);
        var dbSize = await ScalarAsync(
            "SELECT pg_size_pretty(pg_database_size(current_database())) AS \"Value\"", ct);
        var dbTz = await ScalarAsync("SELECT current_setting('TimeZone') AS \"Value\"", ct);
        var dbTzOffset = await ScalarAsync("SELECT to_char(now(), 'TZH:TZM') AS \"Value\"", ct);
        // "Ek Dosya Alanı": bytes actually held for attachments = the StoredFile ledger
        // (contents live behind IFileStore, never in the database — see StoredFile).
        var attachBytes = await db.StoredFiles.SumAsync(f => (long?)f.Size, ct) ?? 0;

        var dbRows = new List<InfoRowVm>
        {
            new("si.dbSchema", $"{dbName} ({encoding})"),
            new("si.dbSize", dbSize),
            new("si.dbAttach", $"{attachBytes / (1024.0 * 1024.0):0.#} MiB"),
            new("si.dbTz", $"{dbTz} ({dbTzOffset})"),
        };

        // ---- components (mockup's PHP-extension grid → the real package set; same
        //      tile DOM, honest count — flagged on the ROADMAP row) ----------------------
        var components = new List<ComponentVm>
        {
            new("ASP.NET Core", VersionOf(typeof(WebApplication).Assembly), "si.compAspnet"),
            new("EF Core", VersionOf(typeof(DbContext).Assembly), "si.compEf"),
            new("Npgsql", VersionOf(typeof(Npgsql.NpgsqlConnection).Assembly), "si.compNpgsql"),
            new("Npgsql.EntityFrameworkCore.PostgreSQL",
                VersionOf(typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder).Assembly),
                "si.compNpgsqlEf"),
            new("ASP.NET Core Identity",
                VersionOf(typeof(Microsoft.AspNetCore.Identity.UserManager<>).Assembly), "si.compIdentity"),
            new("SignalR", VersionOf(typeof(Microsoft.AspNetCore.SignalR.Hub).Assembly), "si.compSignalr"),
            new("MailKit", VersionOf(typeof(MailKit.Net.Smtp.SmtpClient).Assembly), "si.compMailkit"),
            new("MimeKit", VersionOf(typeof(MimeKit.MimeMessage).Assembly), "si.compMimekit"),
            new("HtmlSanitizer", VersionOf(typeof(Ganss.Xss.HtmlSanitizer).Assembly), "si.compSanitizer"),
        };

        // ---- language packs (si.secLang): tr/en resx ship in the app assembly (version
        //      = app version); configured secondary languages beyond them persist without
        //      UI translations — rendered honestly as missing (settings-system flag) -----
        var primary = await settings.GetAsync("system", "primary_language", ct) is "en" ? "en" : "tr";
        var secondaryCsv = await settings.GetAsync("system", "secondary_languages", ct) ?? "en";
        var configured = new List<string> { primary };
        configured.AddRange(secondaryCsv.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var languages = new List<LanguagePackVm>();
        foreach (var code in new[] { "tr", "en" }.Union(configured))
        {
            var name = SettingsSystemController.LanguageCatalog
                .FirstOrDefault(l => l.Code == code)?.Name ?? code;
            var installed = code is "tr" or "en";
            languages.Add(new LanguagePackVm(name, code, installed ? appVersion : "—", installed));
        }

        return View(new SystemInfoVm(
            appVersion, gitSha, serverRows,
            uptime, process.WorkingSet64 / (1024 * 1024),
            components, dbRows, languages));
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<string> ScalarAsync(string sql, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<string>(sql).FirstAsync(ct);

    /// <summary>Package version without the SourceLink "+sha" build-metadata tail.</summary>
    private static string VersionOf(Assembly assembly)
    {
        var (version, _) = SplitInformational(assembly);
        return version;
    }

    private static (string Version, string? Sha) SplitInformational(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3) ?? "?";
        var plus = info.IndexOf('+');
        if (plus < 0)
            return (info, null);
        var sha = info[(plus + 1)..];
        return (info[..plus], sha.Length > 9 ? sha[..9] : sha);
    }

    [GeneratedRegex(@"^PostgreSQL \S+")]
    private static partial Regex ShortPgVersion();
}
