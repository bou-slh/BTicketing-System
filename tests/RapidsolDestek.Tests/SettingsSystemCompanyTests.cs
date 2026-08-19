using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/settings-system.html + settings-company.html: settings round-trips through
/// both admin pages, the maintenance-mode flip through THIS page (portal offline, admin
/// unaffected), B4 language add/remove rows, the attachment size cap at a real upload
/// path (agent reply), logo upload + preview round-trip, and SitePage seed + select
/// binding.
/// </summary>
[Collection("Postgres")]
public class SettingsSystemCompanyTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers (SettingsTicketsTests twins) ------------------------------------------

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7SETTINGSKEY23456";
        var username = $"s7sys{Guid.NewGuid():N}"[..14];
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

    /// <summary>Seeded agent over HTTP (no 2FA — AgentProfileTests full-stack pattern).</summary>
    private async Task<HttpClient> AgentClientAsync(string username)
    {
        var client = fixture.Factory.CreateClient();
        var (token, _) = await GetWithTokenAsync(client, "/agent/login");
        var landed = await PostFormAsync(client, "/agent/login", token,
            ("User", username), ("Password", Password));
        Assert.True(landed.IsSuccessStatusCode);
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .ToDictionary(f => f.Item1, f => f.Item2)));

    private async Task<string?> ReadSettingAsync(string ns, string key)
    {
        using var s = new ServiceScopeBundle(fixture);
        return await s.Get<ISettingsService>().GetAsync(ns, key);
    }

    /// <summary>The full settings-system main-form payload with seed-default values.</summary>
    private async Task<List<(string, string)>> SystemFormAsync(params (string Key, string Value)[] overrides)
    {
        using var s = new ServiceScopeBundle(fixture);
        var deptId = await s.Db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync();
        var scheduleId = await s.Db.Schedules
            .Where(x => x.Name == "Hafta içi 09:00–18:00").Select(x => x.Id).SingleAsync();

        var form = new List<(string, string)>
        {
            ("online", "true"),
            ("helpdesk_url", "https://destek.rapidsol.com.tr/"),
            ("helpdesk_title", "RapidsolDestek"),
            ("default_dept", deptId.ToString()),
            ("force_https", "true"), ("collision", "3"), ("page_size", "25"),
            ("log_level", "warn"), ("log_purge", "3"),
            ("avatars", "true"), ("richtext", "true"),
            ("iframe_list", ""), ("embed_list", ""), ("acl_ips", ""), ("acl_scope", "admin"),
            ("locale", "tr-TR"), ("timezone", "Europe/Istanbul"),
            ("time_format_mode", "locale"),
            ("format_time", "HH:mm"), ("format_date", "d MMMM y"),
            ("format_datetime", "d MMM y HH:mm"), ("format_day", "EEEE, d MMMM"),
            ("default_schedule", scheduleId.ToString()),
            ("primary_lang", "tr"),
            ("storage", "fs"), ("max_size", "16"), ("auth_required", "true"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            if (value != "")
                form.Add((key, value));
        }
        return form;
    }

    // ---- settings-system round-trip -----------------------------------------------------

    [Fact]
    public async Task SystemPage_SaveRoundTrips_AndRerendersPersistedState()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-system");
        Assert.Contains("Sistem Ayarları", html);       // TR default culture renders
        Assert.Contains("Destek", html);                // real department options

        try
        {
            var form = await SystemFormAsync(
                ("collision", "5"), ("page_size", "50"), ("log_level", "debug"),
                ("helpdesk_title", "RapidsolDestek QA"), ("max_size", "8"), ("avatars", ""));
            var saved = await PostFormAsync(client, "/admin/settings-system", token, form.ToArray());
            Assert.Equal("/admin/settings-system", saved.RequestMessage!.RequestUri!.AbsolutePath);

            Assert.Equal("5", await ReadSettingAsync("system", "collision_minutes"));
            Assert.Equal("50", await ReadSettingAsync("system", "page_size"));
            Assert.Equal("debug", await ReadSettingAsync("system", "log_level"));
            Assert.Equal("False", await ReadSettingAsync("system", "show_avatars"));
            Assert.Equal("RapidsolDestek QA", await ReadSettingAsync("core", "helpdesk_title"));
            using (var s = new ServiceScopeBundle(fixture))
            {
                Assert.Equal(8, (await s.Get<ISettingsService>().GetAttachmentsAsync()).MaxSizeMb);
            }

            // Re-render carries the persisted state, and the layout consumes the
            // helpdesk name in the browser-tab title (live consumer).
            var (_, rerendered) = await GetWithTokenAsync(client, "/admin/settings-system");
            Assert.Contains("value=\"5\"", rerendered);
            Assert.Matches(new Regex("value=\"debug\"\\s+selected"), rerendered);
            Assert.Contains("<title>RapidsolDestek QA —", rerendered);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-system");
            await PostFormAsync(client, "/admin/settings-system", token2, (await SystemFormAsync()).ToArray());
        }
    }

    [Fact]
    public async Task SystemPage_InvalidValues_SaveNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-system");

        var before = await ReadSettingAsync("system", "page_size");
        var form = await SystemFormAsync(("page_size", "37"), ("collision", "9"));
        await PostFormAsync(client, "/admin/settings-system", token, form.ToArray());

        Assert.Equal(before, await ReadSettingAsync("system", "page_size"));
        Assert.NotEqual("9", await ReadSettingAsync("system", "collision_minutes"));
    }

    // ---- maintenance mode drives portal/offline (the §6.3 row's core promise) ----------

    [Fact]
    public async Task MaintenanceFlip_ThroughThePage_TogglesPortalOffline_AdminUnaffected()
    {
        var admin = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(admin, "/admin/settings-system");

        try
        {
            // Flip the online switch OFF through the real form POST.
            var off = await SystemFormAsync(("online", ""));
            await PostFormAsync(admin, "/admin/settings-system", token, off.ToArray());
            Assert.Equal("True", await ReadSettingAsync(
                RapidsolDestek.Web.MaintenanceModeMiddleware.SettingNamespace,
                RapidsolDestek.Web.MaintenanceModeMiddleware.SettingKey));

            // Portal serves the offline page (S5 middleware) — anonymous client.
            var portal = fixture.Factory.CreateClient();
            var offline = await portal.GetStringAsync("/");
            Assert.Contains("🛠", offline);
            Assert.Contains("mailto:destek@rapidsol.com.tr", offline);

            // Staff spaces keep working — the same admin page still renders.
            var stillUp = await admin.GetStringAsync("/admin/settings-system");
            Assert.Contains("Sistem Ayarları", stillUp);

            // Flip back ON through the page → the portal comes back (login redirect,
            // not the offline card).
            var (token2, _) = await GetWithTokenAsync(admin, "/admin/settings-system");
            await PostFormAsync(admin, "/admin/settings-system", token2, (await SystemFormAsync()).ToArray());
            var back = await portal.GetStringAsync("/");
            Assert.DoesNotContain("🛠", back);
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture,
                RapidsolDestek.Web.MaintenanceModeMiddleware.SettingNamespace,
                RapidsolDestek.Web.MaintenanceModeMiddleware.SettingKey, "false");
        }
    }

    // ---- B4 language rows ----------------------------------------------------------------

    [Fact]
    public async Task LanguageRows_AddAndRemove_RoundTrip()
    {
        var client = await AdminClientAsync();

        try
        {
            // Add Deutsch through the row form → persisted + rendered as a row.
            var (token, _) = await GetWithTokenAsync(client, "/admin/settings-system");
            await PostFormAsync(client, "/admin/settings-system/languages", token,
                ("add", "true"), ("code", "de"));
            Assert.Equal("en,de", await ReadSettingAsync("system", "secondary_languages"));

            var (token2, html) = await GetWithTokenAsync(client, "/admin/settings-system");
            Assert.Contains("Deutsch", html);
            Assert.Contains(">de</span>", html);

            // The primary language cannot be added; the list is unchanged.
            await PostFormAsync(client, "/admin/settings-system/languages", token2, ("add", "true"), ("code", "tr"));
            Assert.Equal("en,de", await ReadSettingAsync("system", "secondary_languages"));

            // ✕ removes the row server-side.
            var (token3, _) = await GetWithTokenAsync(client, "/admin/settings-system");
            await PostFormAsync(client, "/admin/settings-system/languages", token3, ("remove", "de"));
            Assert.Equal("en", await ReadSettingAsync("system", "secondary_languages"));
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture, "system", "secondary_languages", "en");
        }
    }

    // ---- attachment size cap at a real upload path (agent reply over HTTP) ---------------

    [Fact]
    public async Task AttachmentLimit_EnforcedAtAgentReply()
    {
        // A Destek-department ticket dkaya (Destek Temsilci) can act on.
        int ticketId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Ek sınırı testi {Guid.NewGuid():N}",
                Body = "<p>içerik</p>",
            }, owner);
            ticketId = ticket.Id;
        }

        var agent = await AgentClientAsync("dkaya");
        var (token, _) = await GetWithTokenAsync(agent, $"/agent/ticket-view?id={ticketId}");

        await using (await SettingOverride.SetAsync(fixture, "attachments", "max_size_mb", "1"))
        {
            // 1.5 MB payload over the 1 MB cap → the whole reply is refused with the
            // error toast; nothing posts, nothing is stored.
            var big = new byte[1536 * 1024];
            var refused = await PostReplyAsync(agent, token, ticketId, "sınır üstü yanıt", "buyuk.bin", big);
            Assert.Contains("Ek dosya, izin verilen", await refused.Content.ReadAsStringAsync());
            using (var s = new ServiceScopeBundle(fixture))
            {
                var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
                Assert.False(await s.Db.ThreadEntries.AnyAsync(e =>
                    e.ThreadId == threadId && e.Type == ThreadEntryType.Response));
                Assert.False(await s.Db.StoredFiles.AnyAsync(f => f.Name == "buyuk.bin"));
            }

            // A file under the cap posts normally with its attachment row.
            var small = new byte[16 * 1024];
            await PostReplyAsync(agent, token, ticketId, "sınır altı yanıt", "kucuk.bin", small);
            using (var s = new ServiceScopeBundle(fixture))
            {
                var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
                var entryId = await s.Db.ThreadEntries
                    .Where(e => e.ThreadId == threadId && e.Type == ThreadEntryType.Response)
                    .Select(e => e.Id).SingleAsync();
                Assert.True(await s.Db.Attachments.AnyAsync(a =>
                    a.ObjectType == AttachmentObjectType.ThreadEntry && a.ObjectId == entryId));
            }
        }
    }

    private static async Task<HttpResponseMessage> PostReplyAsync(
        HttpClient client, string token, int ticketId, string body, string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(ticketId.ToString()), "id" },
            { new StringContent(body), "body" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "attachments", fileName);
        var response = await client.PostAsync("/agent/ticket-view/reply", form);
        Assert.True(response.IsSuccessStatusCode);
        return response;
    }

    // ---- settings-company: SitePage selects + round-trip + logo upload/preview -----------

    /// <summary>The full settings-company main-form payload with seed-default values.</summary>
    private async Task<List<KeyValuePair<string, string>>> CompanyFormAsync(params (string Key, string Value)[] overrides)
    {
        using var s = new ServiceScopeBundle(fixture);
        var landing = await s.Db.SitePages.Where(p => p.Type == SitePageType.Landing).Select(p => p.Id).FirstAsync();
        var offline = await s.Db.SitePages.Where(p => p.Type == SitePageType.Offline).Select(p => p.Id).FirstAsync();
        var thanks = await s.Db.SitePages.Where(p => p.Type == SitePageType.ThankYou).Select(p => p.Id).FirstAsync();

        var form = new List<KeyValuePair<string, string>>
        {
            new("company_name", "Rapidsol Bilişim ve Danışmanlık A.Ş."),
            new("website", "https://www.rapidsol.com.tr"),
            new("phone", "+90 212 555 24 24"),
            new("address", "Maslak Mah. Büyükdere Cad. No:245 K:11\nSarıyer / İstanbul"),
            new("landing_page", landing.ToString()),
            new("offline_page", offline.ToString()),
            new("thanks_page", thanks.ToString()),
            new("client_logo", "default"),
            new("staff_logo", "default"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Key == key);
            if (value != "")
                form.Add(new(key, value));
        }
        return form;
    }

    [Fact]
    public async Task CompanyPage_ListsSeededSitePages_AndSaveRoundTrips()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-company");

        // SitePage seed + select binding: the canon pages.html rows are the options.
        // (Plain-string Razor output HTML-encodes non-ASCII — decode before matching.)
        var decoded = WebUtility.HtmlDecode(html);
        Assert.Contains("Şirket Ayarları", decoded);
        Assert.Contains("Hoş Geldiniz", decoded);
        Assert.Contains("Bakım Modu", decoded);
        Assert.Contains("Talep Alındı", decoded);

        try
        {
            var form = await CompanyFormAsync(
                ("company_name", "Rapidsol QA A.Ş."), ("phone", "+90 212 555 99 99"));
            using var payload = new MultipartFormDataContent();
            payload.Add(new StringContent(token), "__RequestVerificationToken");
            foreach (var field in form)
                payload.Add(new StringContent(field.Value), field.Key);
            var saved = await client.PostAsync("/admin/settings-company", payload);
            Assert.Equal("/admin/settings-company", saved.RequestMessage!.RequestUri!.AbsolutePath);

            Assert.Equal("Rapidsol QA A.Ş.", await ReadSettingAsync("company", "name"));
            Assert.Equal("+90 212 555 99 99", await ReadSettingAsync("company", "phone"));
            Assert.False(string.IsNullOrEmpty(await ReadSettingAsync("company", "landing_page_id")));

            var (_, rerendered) = await GetWithTokenAsync(client, "/admin/settings-company");
            Assert.Contains("Rapidsol QA A.Ş.", WebUtility.HtmlDecode(rerendered));
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-company");
            using var restore = new MultipartFormDataContent();
            restore.Add(new StringContent(token2), "__RequestVerificationToken");
            foreach (var field in await CompanyFormAsync())
                restore.Add(new StringContent(field.Value), field.Key);
            await client.PostAsync("/admin/settings-company", restore);
        }
    }

    [Fact]
    public async Task LogoUpload_StoresPersistsAndPreviews()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-company");

        // A small synthetic PNG (magic bytes + payload — content is stored verbatim).
        var png = new byte[256];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        Random.Shared.NextBytes(png.AsSpan(8));

        try
        {
            using var payload = new MultipartFormDataContent();
            payload.Add(new StringContent(token), "__RequestVerificationToken");
            foreach (var field in await CompanyFormAsync(("client_logo", "custom")))
                payload.Add(new StringContent(field.Value), field.Key);
            var upload = new ByteArrayContent(png);
            upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            payload.Add(upload, "clientLogoFile", "logo-qa.png");
            var saved = await client.PostAsync("/admin/settings-company", payload);
            Assert.True(saved.IsSuccessStatusCode);

            // Persisted pointer + mode…
            Assert.Equal("custom", await ReadSettingAsync("company", "client_logo_mode"));
            var fileId = int.Parse((await ReadSettingAsync("company", "client_logo_file_id"))!);
            Assert.True(fileId > 0);

            // …the preview action streams the stored bytes back (B3, no base64)…
            var preview = await client.GetAsync("/admin/settings-company/logo?which=client");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("image/png", preview.Content.Headers.ContentType!.MediaType);
            Assert.Equal(png, await preview.Content.ReadAsByteArrayAsync());

            // …and the page's Geçerli box points at it.
            var (_, html) = await GetWithTokenAsync(client, "/admin/settings-company");
            Assert.Contains("/admin/settings-company/logo?which=client", html);

            // Over the mockup's 2 MB canon → refused, pointer unchanged.
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-company");
            using var tooBig = new MultipartFormDataContent();
            tooBig.Add(new StringContent(token2), "__RequestVerificationToken");
            foreach (var field in await CompanyFormAsync(("client_logo", "custom")))
                tooBig.Add(new StringContent(field.Value), field.Key);
            var bigFile = new ByteArrayContent(new byte[3 * 1024 * 1024]);
            bigFile.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            tooBig.Add(bigFile, "clientLogoFile", "dev.png");
            await client.PostAsync("/admin/settings-company", tooBig);
            Assert.Equal(fileId.ToString(), await ReadSettingAsync("company", "client_logo_file_id"));
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture, "company", "client_logo_mode", "default");
        }
    }

    // ---- totp helper (AdminAuthTests twin) ------------------------------------------------

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
