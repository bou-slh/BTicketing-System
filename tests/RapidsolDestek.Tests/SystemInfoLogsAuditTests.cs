using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Admin.Controllers;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/system-info.html + system-logs.html + audit-logs.html: the honest
/// runtime/db info page, the syslog write path (system/log_level LIVE) + filters +
/// purge + the invented row-detail dialog, and the audit trail's B1 filters, CSV
/// export and B10 drill-down link map.
/// </summary>
[Collection("Postgres")]
public class SystemInfoLogsAuditTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- system-info.html ------------------------------------------------------------------

    [Fact]
    public async Task SystemInfo_RendersRealRuntimeAndDbValues()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/system-info"));

        // Real values read from the live process/database — not sample data.
        Assert.Contains("PostgreSQL", html);                      // SELECT version()
        Assert.Contains(".NET", html);                            // RuntimeInformation.FrameworkDescription
        Assert.Contains("Kestrel", html);
        Assert.Contains("EF Core", html);                         // component grid
        Assert.Contains("Npgsql", html);
        // Language packs: shipped resx cultures render active.
        Assert.Contains("Türkçe", html);
        Assert.Contains("English (United States)", html);
        // Honest update pill: no update channel exists.
        Assert.Contains("Güncelleme kanalı yapılandırılmadı", html);
    }

    // ---- system-logs.html: write path (system/log_level LIVE) -------------------------------

    [Fact]
    public async Task SystemLogWriter_HonorsLogLevelSetting()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];

        // Default is "warn": errors + warnings stored, debug dropped.
        using (var s = new ServiceScopeBundle(fixture))
        {
            var log = s.Get<ISystemLogService>();
            await log.LogAsync(SystemLogType.Warning, $"warn-a-{marker}");
            await log.LogAsync(SystemLogType.Debug, $"debug-a-{marker}");
        }
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"warn-a-{marker}"));
            Assert.False(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"debug-a-{marker}"));
        }

        // "none": nothing stored, not even errors.
        await using (await SettingOverride.SetAsync(fixture, "system", "log_level", "none"))
        using (var s = new ServiceScopeBundle(fixture))
        {
            await s.Get<ISystemLogService>().LogAsync(SystemLogType.Error, $"err-b-{marker}");
        }

        // "error": errors only.
        await using (await SettingOverride.SetAsync(fixture, "system", "log_level", "error"))
        using (var s = new ServiceScopeBundle(fixture))
        {
            var log = s.Get<ISystemLogService>();
            await log.LogAsync(SystemLogType.Error, $"err-c-{marker}");
            await log.LogAsync(SystemLogType.Warning, $"warn-c-{marker}");
        }

        // "debug": everything stored.
        await using (await SettingOverride.SetAsync(fixture, "system", "log_level", "debug"))
        using (var s = new ServiceScopeBundle(fixture))
        {
            await s.Get<ISystemLogService>().LogAsync(SystemLogType.Debug, $"debug-d-{marker}");
        }

        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"err-b-{marker}"));
            Assert.True(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"err-c-{marker}"));
            Assert.False(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"warn-c-{marker}"));
            Assert.True(await s.Db.SystemLogEntries.AnyAsync(e => e.Title == $"debug-d-{marker}"));
        }
    }

    [Fact]
    public async Task FailedLogin_WritesSyslogWarning()
    {
        // Unknown-user attempt at the staff login → the canon syslog warning
        // ("bilinmeyen kullanıcı") is written for real by the auth hook.
        var unknown = $"ghost{Guid.NewGuid():N}"[..14];
        var client = fixture.Factory.CreateClient();
        var (token, _) = await GetWithTokenAsync(client, "/agent/login");
        await PostFormAsync(client, "/agent/login", token, ("User", unknown), ("Password", "yanlis-parola1"));

        using var s = new ServiceScopeBundle(fixture);
        var row = await s.Db.SystemLogEntries.SingleAsync(e => e.Title.Contains(unknown));
        Assert.Equal(SystemLogType.Warning, row.Type);
        Assert.Equal("auth", row.Logger);
    }

    // ---- system-logs.html: filters + purge + detail dialog ----------------------------------

    [Fact]
    public async Task SystemLogsPage_FiltersApply_AndDetailDialogPrefilled()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        int errorId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            // Distinct 2025 dates so from/to isolates them from the 2026 seed canon.
            s.Db.SystemLogEntries.AddRange(
                new SystemLogEntry
                {
                    Type = SystemLogType.Error, Title = $"filter-err-{marker}",
                    Log = $"detail-body-{marker}", Logger = "test", IpAddress = "10.9.9.9",
                    CreatedAt = new DateTimeOffset(2025, 1, 5, 12, 0, 0, TimeSpan.FromHours(3)),
                },
                new SystemLogEntry
                {
                    Type = SystemLogType.Debug, Title = $"filter-dbg-{marker}",
                    CreatedAt = new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.FromHours(3)),
                });
            await s.Db.SaveChangesAsync();
            errorId = await s.Db.SystemLogEntries
                .Where(e => e.Title == $"filter-err-{marker}").Select(e => e.Id).SingleAsync();
        }

        var client = await AdminClientAsync();

        // Level + date filters (B1): only the matching row renders.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(
            "/admin/system-logs?level=error&from=2025-01-01&to=2025-01-07"));
        Assert.Contains($"filter-err-{marker}", html);
        Assert.DoesNotContain($"filter-dbg-{marker}", html);
        // Level pill (sl.error TR).
        Assert.Contains(">HATA<", html);
        // Invented B2 row-detail dialog, prefilled server-side with the Log body.
        Assert.Contains($"dlg-log-{errorId}", html);
        Assert.Contains($"detail-body-{marker}", html);

        // Date window that excludes both markers.
        var none = WebUtility.HtmlDecode(await client.GetStringAsync(
            "/admin/system-logs?from=2025-02-01&to=2025-02-05"));
        Assert.DoesNotContain($"filter-err-{marker}", none);
        Assert.DoesNotContain($"filter-dbg-{marker}", none);
    }

    [Fact]
    public async Task SystemLogsPurge_DeletesSelectedRowsOnly()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        int doomedId, keptId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var doomed = new SystemLogEntry
            {
                Type = SystemLogType.Warning, Title = $"purge-doomed-{marker}",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var kept = new SystemLogEntry
            {
                Type = SystemLogType.Warning, Title = $"purge-kept-{marker}",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            s.Db.SystemLogEntries.AddRange(doomed, kept);
            await s.Db.SaveChangesAsync();
            doomedId = doomed.Id;
            keptId = kept.Id;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/system-logs");
        var back = await PostFormAsync(client, "/admin/system-logs/delete", token,
            ("ids", doomedId.ToString()));
        Assert.Equal("/admin/system-logs", back.RequestMessage!.RequestUri!.AbsolutePath);

        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.SystemLogEntries.AnyAsync(e => e.Id == doomedId));
            Assert.True(await s.Db.SystemLogEntries.AnyAsync(e => e.Id == keptId));
        }

        // Empty selection refuses (sl.errNone) and deletes nothing.
        var (token2, _) = await GetWithTokenAsync(client, "/admin/system-logs");
        await PostFormAsync(client, "/admin/system-logs/delete", token2);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.SystemLogEntries.AnyAsync(e => e.Id == keptId));
        }
    }

    // ---- audit-logs.html --------------------------------------------------------------------

    [Fact]
    public async Task AuditLogsPage_TypeFilter_Drilldown_AndCsvExport()
    {
        // A REAL audited mutation attributed to a staff actor: the S3 interceptor
        // writes the row; the page must surface actor, event text and drill-down link.
        var marker = $"Denetim İzi {Guid.NewGuid().ToString("N")[..8]}";
        int userId;
        string actorName;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Username == "uakin");
            actorName = staff.FullName;
            using (ActorContext.ForStaff(staff, "10.7.7.7").BeginAuditScope())
            {
                var user = new User { Name = marker };
                s.Db.Users.Add(user);
                await s.Db.SaveChangesAsync();
                userId = user.Id;
            }
        }

        var client = await AdminClientAsync();

        // type=user filter surfaces the row with actor + created event + B10 link.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/audit-logs?type=user"));
        Assert.Contains(marker, html);
        Assert.Contains(actorName, html);
        Assert.Contains($"/agent/user-view?id={userId}", html);
        Assert.Contains("oluşturuldu", html); // al.evCreated (TR default culture)
        Assert.Contains("10.7.7.7", html);

        // type=ticket: seed-era ticket audit rows drill down to the agent ticket view.
        var tickets = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/audit-logs?type=ticket"));
        Assert.Contains("/agent/ticket-view?id=", tickets);
        Assert.DoesNotContain($"/agent/user-view?id={userId}", tickets);

        // Export: the FILTERED trail as CSV with a UTF-8 BOM.
        var export = await client.GetAsync("/admin/audit-logs/export?type=user");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var csv = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains(marker, csv);
        Assert.Contains(actorName, csv);
    }

    [Fact]
    public void AuditDrilldownLinkMap_CoversMainTypes()
    {
        // B10 drill-down map: editors take ?id=; dialog-based admin lists link to the
        // hosting list page; pageless types render plain (null).
        Assert.Equal("/agent/ticket-view?id=5", AuditLogsController.LinkFor("Ticket", "5"));
        Assert.Equal("/agent/task-view?id=3", AuditLogsController.LinkFor("TaskItem", "3"));
        Assert.Equal("/agent/user-view?id=7", AuditLogsController.LinkFor("User", "7"));
        Assert.Equal("/agent/org-view?id=2", AuditLogsController.LinkFor("Organization", "2"));
        Assert.Equal("/admin/staff-edit?id=4", AuditLogsController.LinkFor("Staff", "4"));
        Assert.Equal("/admin/department-edit?id=1", AuditLogsController.LinkFor("Department", "1"));
        Assert.Equal("/admin/helptopic-edit?id=6", AuditLogsController.LinkFor("HelpTopic", "6"));
        Assert.Equal("/admin/role-edit?id=2", AuditLogsController.LinkFor("Role", "2"));
        Assert.Equal("/admin/queues?id=9", AuditLogsController.LinkFor("SavedQueue", "9"));
        Assert.Equal("/admin/form-edit?id=1", AuditLogsController.LinkFor("FormDefinition", "1"));
        Assert.Equal("/admin/list-edit?id=1", AuditLogsController.LinkFor("ListDefinition", "1"));
        Assert.Equal("/admin/filter-edit?id=8", AuditLogsController.LinkFor("Filter", "8"));
        Assert.Equal("/admin/schedule-edit?id=1", AuditLogsController.LinkFor("Schedule", "1"));
        Assert.Equal("/admin/email-edit?id=1", AuditLogsController.LinkFor("EmailAccount", "1"));
        Assert.Equal("/agent/kb-faq?id=1", AuditLogsController.LinkFor("FaqArticle", "1"));
        // Dialog-based lists: partial drill-down to the hosting page.
        Assert.Equal("/admin/teams", AuditLogsController.LinkFor("Team", "1"));
        Assert.Equal("/admin/slas", AuditLogsController.LinkFor("SlaPlan", "1"));
        Assert.Equal("/admin/pages", AuditLogsController.LinkFor("SitePage", "1"));
        Assert.Equal("/admin/banlist", AuditLogsController.LinkFor("BanlistEntry", "1"));
        Assert.Equal("/admin/apikeys", AuditLogsController.LinkFor("ApiKey", "1"));
        // No page → plain row.
        Assert.Null(AuditLogsController.LinkFor("ThreadEntry", "1"));
        Assert.Null(AuditLogsController.LinkFor("Setting", "1"));
        // Composite/non-integer ids never build a broken editor link.
        Assert.Null(AuditLogsController.LinkFor("Ticket", "5/7"));
    }

    // ---- helpers (SettingsSystemCompanyTests twins) ------------------------------------------

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7SYSTEMLOGKEY2345";
        var username = $"s7log{Guid.NewGuid():N}"[..14];
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = new StaffUser
            {
                UserName = username,
                Email = $"{username}@rapidsol.com.tr",
                EmailConfirmed = true,
                FullName = $"S7 {username}",
            };
            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, "Agent");
            await users.AddToRoleAsync(user, "Admin");
            await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", user.FullName!));
            await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", totpKey);
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        var client = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", toTwofa.RequestMessage!.RequestUri!.AbsolutePath);
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", landed.RequestMessage!.RequestUri!.AbsolutePath);
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (ids arrays).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    // ---- totp helper (AdminAuthTests twin) ---------------------------------------------------

    private static string ComputeTotp(string base32Key)
    {
        var keyBytes = FromBase32(base32Key.Replace(" ", "").ToUpperInvariant());
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var timestepBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestepBytes);
        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(timestepBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
