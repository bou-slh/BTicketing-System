using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/banlist.html + pages.html + apikeys.html + plugins.html: four B1 lists
/// with B2 dialogs plus their engine wiring — the FilterEngine ban rejection, the
/// portal-served offline SitePage, the ApiKeyAuthenticator key+IP gate and the
/// feature-flag module map (incl. the shared agents/require_twofa binding and the
/// staff-edit LDAP gate).
/// </summary>
[Collection("Postgres")]
public class BanlistPagesApiKeysPluginsTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- banlist.html (B1 + enforcement) --------------------------------------------------

    [Fact]
    public async Task BanlistList_RendersSeedCanon_AndSearchFilters()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/banlist"));

        Assert.Contains("spam@ornek.com", html);
        Assert.Contains("toplu-reklam@kampanyamail.net", html);
        Assert.Contains("bildirim@sahte-banka.xyz", html);
        // Status pills: TR default culture (bl.stBanned / bl.stPassive).
        Assert.Contains(">Engelli<", html);
        Assert.Contains(">Pasif<", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/banlist?q=" + Uri.EscapeDataString("sahte")));
        Assert.Contains("bildirim@sahte-banka.xyz", filtered);
        Assert.DoesNotContain("spam@ornek.com", filtered);
    }

    [Fact]
    public async Task BanAdd_RejectsPortalCreate_UntilDisabled()
    {
        // Mixed-case address: the ban stores lowercase, the engine matches
        // case-insensitively (osTicket system ban-list filter parity).
        var marker = Guid.NewGuid().ToString("N")[..10];
        var address = $"S7.Ban.{marker}@Ornek.COM";
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var user = new User { Name = $"S7 Banned {marker}", Emails = [new UserEmail { Address = address }] };
            s.Db.Users.Add(user);
            await s.Db.SaveChangesAsync();
            user.DefaultEmailId = user.Emails[0].Id;
            await s.Db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/banlist");
        var created = await PostFormAsync(client, "/admin/banlist/create", token,
            ("email", address), ("active", "true"), ("notes", "S7 test"));
        Assert.Equal("/admin/banlist", PathOf(created));

        using (var s = new ServiceScopeBundle(fixture))
        {
            // Normalized to lowercase on save.
            var entry = await s.Db.BanlistEntries.SingleAsync(b => b.Address == address.ToLowerInvariant());
            Assert.True(entry.IsActive);

            // Enforcement: the create is refused BEFORE anything persists, no
            // ticket and no auto-response (silent reject).
            var actor = ActorContext.ForUser(await s.Db.Users.SingleAsync(u => u.Id == userId));
            var ex = await Assert.ThrowsAsync<TicketRejectedByFilterException>(
                () => s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
                {
                    UserId = userId,
                    Subject = $"Engellenecek {marker}",
                    Body = "<p>x</p>",
                }, actor));
            Assert.Equal(FilterEngine.BanlistName, ex.FilterName);
            Assert.False(await s.Db.Tickets.AnyAsync(t => t.UserId == userId));

            // Inactive rules are kept but not enforced (bl.dlgStatusHelp).
            var (token2, _) = await GetWithTokenAsync(client, "/admin/banlist");
            await PostFormAsync(client, "/admin/banlist/bulk", token2,
                ("act", "disable"), ("ids", entry.Id.ToString()));

            using var s2 = new ServiceScopeBundle(fixture);
            var actor2 = ActorContext.ForUser(await s2.Db.Users.SingleAsync(u => u.Id == userId));
            var ticket = await s2.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = userId,
                Subject = $"Serbest {marker}",
                Body = "<p>x</p>",
            }, actor2);
            Assert.NotNull(ticket);
        }
    }

    [Fact]
    public async Task BanEdit_And_BulkDelete_RoundTrip()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/banlist");
        await PostFormAsync(client, "/admin/banlist/create", token,
            ("email", $"s7.edit.{marker}@ornek.com"), ("active", "true"));

        int id;
        using (var s = new ServiceScopeBundle(fixture))
            id = await s.Db.BanlistEntries.Where(b => b.Address == $"s7.edit.{marker}@ornek.com")
                .Select(b => b.Id).SingleAsync();

        // Per-row dialog save: address + status + notes round-trip; a malformed
        // address is refused.
        var (token2, html) = await GetWithTokenAsync(client, "/admin/banlist");
        Assert.Contains($"dlg-ban-{id}", html); // per-row dialog rendered
        await PostFormAsync(client, "/admin/banlist/update", token2,
            ("id", id.ToString()), ("email", $"s7.edited.{marker}@ornek.com"),
            ("active", "false"), ("notes", "düzenlendi"));
        await PostFormAsync(client, "/admin/banlist/update", token2,
            ("id", id.ToString()), ("email", "gecersiz-adres"), ("active", "true"));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var entry = await s.Db.BanlistEntries.SingleAsync(b => b.Id == id);
            Assert.Equal($"s7.edited.{marker}@ornek.com", entry.Address); // invalid post refused
            Assert.False(entry.IsActive);
            Assert.Equal("düzenlendi", entry.Notes);
        }

        var (token3, _) = await GetWithTokenAsync(client, "/admin/banlist");
        await PostFormAsync(client, "/admin/banlist/bulk", token3,
            ("act", "delete"), ("ids", id.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False(await s.Db.BanlistEntries.AnyAsync(b => b.Id == id));
    }

    // ---- pages.html (B1/B2 + portal serving) ------------------------------------------------

    [Fact]
    public async Task PagesList_RendersSeedCanon_AndSearchFilters()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/pages"));

        Assert.Contains("Hoş Geldiniz", html);
        Assert.Contains("Bakım Modu", html);
        Assert.Contains("Talep Alındı", html);
        Assert.Contains("KVKK Aydınlatma", html);
        // Type cells: TR default culture (pg.typeLanding/Offline/Thanks/Other).
        Assert.Contains(">Karşılama<", html);
        Assert.Contains(">Çevrimdışı<", html);
        Assert.Contains(">Teşekkür<", html);
        Assert.Contains(">Diğer<", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/pages?q=" + Uri.EscapeDataString("KVKK")));
        Assert.Contains("KVKK Aydınlatma", filtered);
        Assert.DoesNotContain("Bakım Modu", filtered);
    }

    [Fact]
    public async Task PageCrud_RoundTrips_WithReferenceDeleteGuard()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var name = $"S7 Sayfa {marker}";
        var client = await AdminClientAsync();

        var (token, _) = await GetWithTokenAsync(client, "/admin/pages");
        await PostFormAsync(client, "/admin/pages/create", token,
            ("name", name), ("type", "thanks"), ("active", "true"),
            ("content", "Sayın %{recipient.name}, %{ticket.number} alındı."));
        // Duplicate name refused (invented rule: the selects list pages by name).
        await PostFormAsync(client, "/admin/pages/create", token,
            ("name", name), ("type", "other"), ("active", "true"), ("content", "x"));

        int id, thanksId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var page = await s.Db.SitePages.SingleAsync(p => p.Name == name);
            Assert.Equal(SitePageType.ThankYou, page.Type);
            Assert.True(page.IsActive);
            Assert.Contains("%{ticket.number}", page.Body);
            id = page.Id;
            thanksId = await s.Db.SitePages.Where(p => p.Name == "Talep Alındı")
                .Select(p => p.Id).SingleAsync();
        }

        // Per-row edit dialog save (B2 prefill asserted via the dialog id).
        var (token2, html) = await GetWithTokenAsync(client, "/admin/pages");
        Assert.Contains($"dlg-page-{id}", html);
        await PostFormAsync(client, "/admin/pages/update", token2,
            ("id", id.ToString()), ("name", name), ("type", "other"),
            ("active", "false"), ("content", "Güncellendi."));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var page = await s.Db.SitePages.SingleAsync(p => p.Id == id);
            Assert.Equal(SitePageType.Other, page.Type);
            Assert.False(page.IsActive);
            Assert.Equal("Güncellendi.", page.Body);
        }

        // Bulk delete: the seeded thank-you page is referenced by a help topic
        // (Bordro / Yol Ücreti canon) — skipped; the fresh page deletes.
        var (token3, _) = await GetWithTokenAsync(client, "/admin/pages");
        await PostFormAsync(client, "/admin/pages/bulk", token3,
            ("act", "delete"), ("ids", id.ToString()), ("ids", thanksId.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.SitePages.AnyAsync(p => p.Id == id));
            Assert.True(await s.Db.SitePages.AnyAsync(p => p.Id == thanksId));
        }
    }

    [Fact]
    public async Task OfflinePage_Body_ServedInMaintenanceMode()
    {
        await using var offline = await SettingOverride.SetAsync(fixture, "system", "offline", "true");
        var client = fixture.Factory.CreateClient();

        // Seed canon: "Bakım Modu" ships DISABLED — the S5 static copy serves.
        var fallback = await client.GetStringAsync("/");
        Assert.Contains("Destek sistemi şu anda bakımda", WebUtility.HtmlDecode(fallback));
        Assert.DoesNotContain("Bakımdayız", fallback);

        int pageId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var page = await s.Db.SitePages.SingleAsync(p => p.Name == "Bakım Modu");
            page.IsActive = true;
            await s.Db.SaveChangesAsync();
            pageId = page.Id;
        }
        try
        {
            // Activated: every portal route now renders the authored body (the
            // static of.title stays only as the <title> tag).
            var html = await client.GetStringAsync("/tickets");
            Assert.Contains("Bakımdayız", html);
            Assert.DoesNotContain("<h1>Destek sistemi şu anda bakımda</h1>", WebUtility.HtmlDecode(html));
        }
        finally
        {
            using var s = new ServiceScopeBundle(fixture);
            var page = await s.Db.SitePages.SingleAsync(p => p.Id == pageId);
            page.IsActive = false;
            await s.Db.SaveChangesAsync();
        }
    }

    // ---- apikeys.html (B1/B2 + authenticator) -----------------------------------------------

    [Fact]
    public async Task ApiKeysList_RendersSeedCanon_TruncatedKeys_AndSearch()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/apikeys"));

        // Mockup cell format: first 8 + … + last 4 of the REAL key. (The mockup's
        // own sample cells contradict its dialog's full key — "C9E1F274…8B63" vs
        // "…FB36C8D1" — so the honest truncation of the seeded canon key wins.)
        Assert.Contains("7A3F09B2…6D41", html);
        Assert.Contains("C9E1F274…C8D1", html);
        Assert.Contains("185.34.12.10", html);
        // Service pills: TR default culture (ak.svcTickets / ak.svcCron).
        Assert.Contains(">Talep Oluşturma<", html);
        Assert.Contains(">Cron<", html);
        // The create dialog carries a server-pregenerated 32-hex key.
        Assert.Matches("id=\"ak-key-dlg-key\"[^>]*readonly[^>]*value=\"[0-9A-F]{32}\"", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/apikeys?q=" + Uri.EscapeDataString("10.0.4.22")));
        Assert.Contains("C9E1F274…C8D1", filtered);
        Assert.DoesNotContain("7A3F09B2…6D41", filtered);
    }

    [Fact]
    public async Task ApiKeyCreate_GeneratesServerSide_Regenerate_ReplacesKey()
    {
        var ip = $"10.77.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/apikeys");

        // A tampered key value never persists — the server mints its own; a
        // malformed IP is refused outright (ak.ipHelp: mandatory).
        await PostFormAsync(client, "/admin/apikeys/create", token,
            ("key", "TAMPERED"), ("ip", ip), ("active", "true"),
            ("canTickets", "true"), ("notes", "S7 test"));
        await PostFormAsync(client, "/admin/apikeys/create", token,
            ("key", ApiKeyAuthenticator.GenerateKey()), ("ip", "gecersiz"), ("active", "true"));

        string firstKey;
        int id;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var row = await s.Db.ApiKeys.SingleAsync(k => k.IpAddress == ip);
            Assert.Matches("^[0-9A-F]{32}$", row.Key);
            Assert.True(row.CanCreateTickets);
            Assert.False(row.CanTriggerJobs);
            (firstKey, id) = (row.Key, row.Id);
        }

        // Per-row edit dialog exists; regenerate replaces the key with a fresh one.
        var (token2, html) = await GetWithTokenAsync(client, "/admin/apikeys");
        Assert.Contains($"dlg-key-{id}", html);
        await PostFormAsync(client, "/admin/apikeys/regenerate", token2, ("id", id.ToString()));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var row = await s.Db.ApiKeys.SingleAsync(k => k.Id == id);
            Assert.NotEqual(firstKey, row.Key);
            Assert.Matches("^[0-9A-F]{32}$", row.Key);
        }

        // Cleanup via the bulk delete (also exercises it).
        var (token3, _) = await GetWithTokenAsync(client, "/admin/apikeys");
        await PostFormAsync(client, "/admin/apikeys/bulk", token3,
            ("act", "delete"), ("ids", id.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False(await s.Db.ApiKeys.AnyAsync(k => k.Id == id));
    }

    [Fact]
    public async Task ApiKeyAuthenticator_EnforcesKey_Ip_AndPermissions()
    {
        using var s = new ServiceScopeBundle(fixture);
        var auth = s.Get<IApiKeyAuthenticator>();

        // Seed canon: 7A3F… @185.34.12.10 tickets+cron, C9E1… @10.0.4.22 cron
        // only, 4E88… disabled.
        const string full = "7A3F09B2E65C4A1893D0F7B2A94E6D41";
        const string cronOnly = "C9E1F274A08B4D63917E5A20FB36C8D1";
        const string disabled = "4E88AD10C2F94B7E8A31D6905C74F2A7";

        Assert.Equal(ApiKeyAuthStatus.Success,
            (await auth.AuthenticateAsync(full, "185.34.12.10", ApiKeyService.CreateTickets)).Status);
        Assert.Equal(ApiKeyAuthStatus.Success,
            (await auth.AuthenticateAsync(full, "185.34.12.10", ApiKeyService.TriggerJobs)).Status);
        // The IP restriction is enforced by the API gate (ROADMAP row).
        Assert.Equal(ApiKeyAuthStatus.IpMismatch,
            (await auth.AuthenticateAsync(full, "9.9.9.9", ApiKeyService.CreateTickets)).Status);
        Assert.Equal(ApiKeyAuthStatus.Forbidden,
            (await auth.AuthenticateAsync(cronOnly, "10.0.4.22", ApiKeyService.CreateTickets)).Status);
        Assert.Equal(ApiKeyAuthStatus.Inactive,
            (await auth.AuthenticateAsync(disabled, "78.186.44.9", ApiKeyService.CreateTickets)).Status);
        Assert.Equal(ApiKeyAuthStatus.UnknownKey,
            (await auth.AuthenticateAsync("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "185.34.12.10", ApiKeyService.CreateTickets)).Status);
        Assert.Equal(ApiKeyAuthStatus.UnknownKey,
            (await auth.AuthenticateAsync(null, "185.34.12.10", ApiKeyService.CreateTickets)).Status);
    }

    // ---- plugins.html (feature flags, §3 replaced) --------------------------------------------

    [Fact]
    public async Task PluginsList_RendersSeededModuleState()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/plugins"));

        // Installed table (seeded canon): LDAP + S3 active, audit disabled.
        Assert.Contains("LDAP Kimlik Doğrulama", html);
        Assert.Contains("plugins/auth-ldap", html);
        Assert.Contains("S3 Depolama", html);
        Assert.Contains("Denetim Günlüğü", html);
        // Available directory: the uninstalled modules with their install buttons.
        Assert.Contains("2FA E-posta Doğrulama", html);
        Assert.Contains("Depolama: Dosya Sistemi", html);
        Assert.Contains("Slack Bildirimleri", html);
        Assert.Contains("/admin/plugins/install", html);
        // Per-module configure entries point at real settings pages.
        Assert.Contains("href=\"/admin/settings-system\"", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/plugins?q=" + Uri.EscapeDataString("LDAP")));
        Assert.Contains("LDAP Kimlik Doğrulama", filtered);
        Assert.DoesNotContain("S3 Depolama", filtered);
    }

    [Fact]
    public async Task PluginInstall_Enable_Uninstall_RoundTrips_FeatureFlag()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/plugins");
        try
        {
            // Install: lands in the installed table DISABLED (osTicket parity).
            await PostFormAsync(client, "/admin/plugins/install", token, ("key", "slack"));
            using (var s = new ServiceScopeBundle(fixture))
            {
                Assert.False((await s.Get<ISettingsService>().GetFeaturesAsync()).SlackNotifications);
            }
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/plugins"));
            Assert.Contains("value=\"slack\" form=\"pl-bulk\"", html); // now a table row

            // Bulk enable flips the flag the typed section reads.
            var (token2, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/bulk", token2,
                ("act", "enable"), ("ids", "slack"));
            using (var s = new ServiceScopeBundle(fixture))
                Assert.True((await s.Get<ISettingsService>().GetFeaturesAsync()).SlackNotifications);
        }
        finally
        {
            // Uninstall clears the module's rows (pl.bulkHelp).
            var (token3, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/bulk", token3,
                ("act", "delete"), ("ids", "slack"));
        }
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False((await s.Get<ISettingsService>().GetFeaturesAsync()).SlackNotifications);
        var after = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/plugins"));
        Assert.DoesNotContain("value=\"slack\" form=\"pl-bulk\"", after);
    }

    [Fact]
    public async Task TwofaPlugin_BindsSharedRequireTwofaKey()
    {
        await using var restore = await SettingOverride.SetAsync(fixture, "agents", "require_twofa", "false");
        var client = await AdminClientAsync();
        try
        {
            var (token, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/install", token, ("key", "twofa_email"));

            // Enable writes the EXISTING agents/require_twofa key — the very
            // switch settings-agents renders and StaffSignInManager consumes.
            var (token2, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/bulk", token2,
                ("act", "enable"), ("ids", "twofa_email"));
            using (var s = new ServiceScopeBundle(fixture))
            {
                Assert.True((await s.Get<ISettingsService>().GetAgentsAsync()).RequireTwofa);
                Assert.True((await s.Get<ISettingsService>().GetFeaturesAsync()).TwofaEmail);
            }

            // And the binding reads back: flipping the shared key elsewhere flips
            // the plugin row's status pill source.
            using (var s = new ServiceScopeBundle(fixture))
                await s.Get<ISettingsService>().SetAsync("agents", "require_twofa", "false");
            using (var s = new ServiceScopeBundle(fixture))
                Assert.False((await s.Get<ISettingsService>().GetFeaturesAsync()).TwofaEmail);
        }
        finally
        {
            var (token3, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/bulk", token3,
                ("act", "delete"), ("ids", "twofa_email"));
        }
    }

    [Fact]
    public async Task LdapFlag_GatesStaffEditBackendOption_AndCoercesSaves()
    {
        var client = await AdminClientAsync();
        var withLdap = await client.GetStringAsync("/admin/staff-edit");
        Assert.Contains("value=\"ldap\"", withLdap);

        var (token, _) = await GetWithTokenAsync(client, "/admin/plugins");
        await PostFormAsync(client, "/admin/plugins/bulk", token,
            ("act", "disable"), ("ids", "auth_ldap"));
        try
        {
            // Option gone from the editor; a tampered ldap post coerces to local.
            var without = await client.GetStringAsync("/admin/staff-edit");
            Assert.DoesNotContain("value=\"ldap\"", without);

            var username = $"s7ld{Guid.NewGuid():N}"[..12];
            int deptId, roleId;
            using (var s = new ServiceScopeBundle(fixture))
            {
                deptId = await s.Db.Departments.Select(d => d.Id).OrderBy(x => x).FirstAsync();
                roleId = await s.Db.Roles.Select(r => r.Id).OrderBy(x => x).FirstAsync();
            }
            var (token2, _) = await GetWithTokenAsync(client, "/admin/staff-edit");
            await PostFormAsync(client, "/admin/staff-edit", token2,
                ("firstName", "S7"), ("lastName", username), ("email", $"{username}@rapidsol.com.tr"),
                ("username", username), ("backend", "ldap"),
                ("deptId", deptId.ToString()), ("roleId", roleId.ToString()),
                ("pw1", "S7parola123"), ("pw2", "S7parola123"));
            using (var s = new ServiceScopeBundle(fixture))
            {
                var staff = await s.Db.Staff.SingleAsync(x => x.Username == username);
                Assert.Equal("local", staff.AuthBackend);
            }
        }
        finally
        {
            var (token3, _) = await GetWithTokenAsync(client, "/admin/plugins");
            await PostFormAsync(client, "/admin/plugins/bulk", token3,
                ("act", "enable"), ("ids", "auth_ldap"));
        }

        // Restored: the option is offered again.
        Assert.Contains("value=\"ldap\"", await client.GetStringAsync("/admin/staff-edit"));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7BANLISTKEY23ABCD";
        var username = $"s7bp{Guid.NewGuid():N}"[..14];
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
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

            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var deptId = await db.Departments.Select(d => d.Id).OrderBy(x => x).FirstAsync();
            var roleId = await db.Roles.Select(r => r.Id).OrderBy(x => x).FirstAsync();
            db.Staff.Add(new Staff
            {
                IdentityUserId = user.Id,
                Username = username,
                FirstName = "S7",
                LastName = username,
                Email = user.Email,
                DepartmentId = deptId,
                RoleId = roleId,
                IsAdmin = true,
                IsVisible = false,
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", PathOf(landed));
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

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    // ---- totp helper (AdminAuthTests twin) --------------------------------------------

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
