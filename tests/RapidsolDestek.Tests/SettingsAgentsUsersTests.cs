using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/settings-agents.html + settings-users.html: settings round-trips through
/// the admin controllers, the LIVE consumers — staff lockout thresholds read from the
/// page's settings, the pwreset-flow gate, the forced email-code 2FA policy, the
/// portal registration-mode gate — and the B2 template dialogs (prefill + save back).
/// </summary>
[Collection("Postgres")]
public class SettingsAgentsUsersTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA, plus a
    /// matching Staff domain row (the template endpoints resolve the acting staff).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7AGUSKEY234567ABC";
        var username = $"s7agu{Guid.NewGuid():N}"[..14];
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
            var deptId = await db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync();
            var roleId = await db.Roles.Select(r => r.Id).OrderBy(id => id).FirstAsync();
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

    /// <summary>Fresh agent (no 2FA) with a matching Staff domain row so the agent
    /// panel renders after sign-in.</summary>
    private async Task<StaffUser> CreateAgentUserAsync(string username)
    {
        using var scope = fixture.CreateScope();
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
        await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", user.FullName!));

        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var deptId = await db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync();
        var roleId = await db.Roles.Select(r => r.Id).OrderBy(id => id).FirstAsync();
        db.Staff.Add(new Staff
        {
            IdentityUserId = user.Id,
            Username = username,
            FirstName = "S7",
            LastName = username,
            Email = user.Email,
            DepartmentId = deptId,
            RoleId = roleId,
            IsVisible = false,
        });
        await db.SaveChangesAsync();
        return user;
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
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    /// <summary>The full settings-agents main-form payload with settings-default values.</summary>
    private static List<(string, string)> AgentsForm(params (string Key, string Value)[] overrides)
    {
        var form = new List<(string, string)>
        {
            ("name_format", "full"), ("avatar_source", "initials"),
            ("block_collab", "true"), ("password_policy", "basic"),
            ("allow_pwreset", "true"), ("reset_window_minutes", "30"),
            ("max_login_attempts", "5"), ("lockout_minutes", "30"),
            ("session_timeout_minutes", "120"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            if (value != "")
                form.Add((key, value));
        }
        return form;
    }

    /// <summary>The full settings-users main-form payload with settings-default values.</summary>
    private static List<(string, string)> UsersForm(params (string Key, string Value)[] overrides)
    {
        var form = new List<(string, string)>
        {
            ("name_format", "full"), ("avatar_source", "initials"),
            ("registration_required", "true"), ("registration_mode", "public"),
            ("password_policy", "basic"),
            ("max_login_attempts", "5"), ("lockout_minutes", "30"),
            ("session_timeout_minutes", "0"), ("auth_tokens", "true"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            if (value != "")
                form.Add((key, value));
        }
        return form;
    }

    private async Task<string?> ReadSettingAsync(string ns, string key)
    {
        using var s = new ServiceScopeBundle(fixture);
        return await s.Get<ISettingsService>().GetAsync(ns, key);
    }

    // ---- settings-agents ---------------------------------------------------------------

    [Fact]
    public async Task AgentsPage_SaveRoundTrips_AndRerendersPersistedState()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-agents");
        Assert.Contains("Temsilci Ayarları", html); // TR default culture
        Assert.Contains("max_login_attempts", html);

        try
        {
            var form = AgentsForm(
                ("name_format", "lastfirst"), ("identity_masking", "true"),
                ("avatar_source", "gravatar"), ("block_collab", ""),
                ("password_policy", "strong"), ("reset_window_minutes", "45"),
                ("max_login_attempts", "10"), ("lockout_minutes", "60"),
                ("session_timeout_minutes", "90"), ("ip_binding", "true"));
            var saved = await PostFormAsync(client, "/admin/settings-agents", token, form.ToArray());
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal("/admin/settings-agents", PathOf(saved));

            using (var s = new ServiceScopeBundle(fixture))
            {
                var agents = await s.Get<ISettingsService>().GetAgentsAsync();
                Assert.Equal("lastfirst", agents.NameFormat);
                Assert.True(agents.IdentityMasking);
                Assert.Equal("gravatar", agents.AvatarSource);
                Assert.False(agents.BlockCollab);
                Assert.Equal("strong", agents.PasswordPolicy);
                Assert.True(agents.AllowPwreset);
                Assert.Equal(45, agents.ResetWindowMinutes);
                Assert.False(agents.RequireTwofa);
                Assert.Equal(10, agents.MaxLoginAttempts);
                Assert.Equal(60, agents.LockoutMinutes);
                Assert.Equal(90, agents.SessionTimeoutMinutes);
                Assert.True(agents.IpBinding);
            }

            // …and the re-rendered page carries the persisted state back into the form.
            var (_, html2) = await GetWithTokenAsync(client, "/admin/settings-agents");
            Assert.Matches(new Regex("value=\"lastfirst\"\\s+selected"), html2);
            Assert.Matches(new Regex("value=\"10\"\\s+selected"), html2);
            Assert.Contains("value=\"45\"", html2);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-agents");
            await PostFormAsync(client, "/admin/settings-agents", token2, AgentsForm().ToArray());
        }
    }

    [Fact]
    public async Task AgentsPage_InvalidValues_SaveNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-agents");

        // 7 is not a select option — the whole save must refuse.
        await PostFormAsync(client, "/admin/settings-agents", token,
            AgentsForm(("max_login_attempts", "7"), ("name_format", "lastfirst")).ToArray());

        Assert.NotEqual("lastfirst", await ReadSettingAsync("agents", "name_format"));
        Assert.NotEqual("7", await ReadSettingAsync("agents", "max_login_attempts"));
    }

    [Fact]
    public async Task StaffLockout_ReadsThePageSettings_FlippedThresholdTakesEffect()
    {
        var admin = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(admin, "/admin/settings-agents");

        try
        {
            // Admin tightens the staff-wide policy to 3 attempts / 15 min through the page.
            await PostFormAsync(admin, "/admin/settings-agents", token,
                AgentsForm(("max_login_attempts", "3"), ("lockout_minutes", "15")).ToArray());

            var username = $"s7lock{Guid.NewGuid():N}"[..14];
            await CreateAgentUserAsync(username);
            var client = fixture.Factory.CreateClient();
            string lastHtml = "";
            for (var i = 0; i < 3; i++)
            {
                var (t, _) = await GetWithTokenAsync(client, "/agent/login");
                var response = await PostFormAsync(client, "/agent/login", t,
                    ("User", username), ("Password", "yanlış-parola"));
                lastHtml = await response.Content.ReadAsStringAsync();
            }
            Assert.Contains("kilitlendi", lastHtml); // third failure trips the flipped threshold

            using var scope = fixture.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var locked = (await users.FindByNameAsync(username))!;
            Assert.NotNull(locked.LockoutEnd);
            // The flipped duration (15 min) applies — not the 30-minute default.
            Assert.InRange(locked.LockoutEnd!.Value, DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(20));
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(admin, "/admin/settings-agents");
            await PostFormAsync(admin, "/admin/settings-agents", token2, AgentsForm().ToArray());
        }
    }

    [Fact]
    public async Task PwresetSwitch_GatesTheStaffResetFlow()
    {
        var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/agent/pwreset")).StatusCode);

        await using (await SettingOverride.SetAsync(fixture, "agents", "allow_pwreset", "False"))
        {
            Assert.Equal("/agent/login", PathOf(await client.GetAsync("/agent/pwreset")));
            Assert.Equal("/admin/login", PathOf(await client.GetAsync("/admin/pwreset")));
        }

        Assert.Equal("/agent/pwreset", PathOf(await client.GetAsync("/agent/pwreset")));
    }

    [Fact]
    public async Task RequireTwofaSwitch_ForcesEmailCodeStep_ForUnenrolledStaff()
    {
        var username = $"s72fa{Guid.NewGuid():N}"[..14];
        var user = await CreateAgentUserAsync(username);

        await using (await SettingOverride.SetAsync(fixture, "agents", "require_twofa", "True"))
        {
            // Password-only success no longer signs in — the email-code step interposes.
            var client = fixture.Factory.CreateClient();
            var (token, _) = await GetWithTokenAsync(client, "/agent/login");
            var toTwofa = await PostFormAsync(client, "/agent/login", token,
                ("User", username), ("Password", Password));
            Assert.Equal("/agent/login/2fa", PathOf(toTwofa));

            // The Email token provider is TOTP-based: regenerating in a scope yields the
            // pending code, and verifying it completes the sign-in.
            string code;
            using (var scope = fixture.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
                code = await users.GenerateTwoFactorTokenAsync(
                    (await users.FindByNameAsync(username))!, TokenOptions.DefaultEmailProvider);
            }
            var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
                "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var landed = await PostFormAsync(client, "/agent/login/2fa", twofaToken, ("Code", code));
            Assert.Equal("/agent/dashboard", PathOf(landed));
        }

        // Policy off again: the same agent signs in with password only.
        var fresh = fixture.Factory.CreateClient();
        var (token2, _) = await GetWithTokenAsync(fresh, "/agent/login");
        var direct = await PostFormAsync(fresh, "/agent/login", token2,
            ("User", username), ("Password", Password));
        Assert.Equal("/agent/dashboard", PathOf(direct));
    }

    // ---- settings-users ----------------------------------------------------------------

    [Fact]
    public async Task UsersPage_SaveRoundTrips()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-users");
        Assert.Contains("Kullanıcı Ayarları", html); // TR default culture
        Assert.Contains("registration_mode", html);

        try
        {
            await PostFormAsync(client, "/admin/settings-users", token, UsersForm(
                ("name_format", "short"), ("avatar_source", "gravatar"),
                ("registration_required", ""), ("password_policy", "strong"),
                ("max_login_attempts", "3"), ("lockout_minutes", "15"),
                ("session_timeout_minutes", "60"), ("auth_tokens", ""),
                ("email_verify", "true")).ToArray());

            using var s = new ServiceScopeBundle(fixture);
            var users = await s.Get<ISettingsService>().GetUsersAsync();
            Assert.Equal("short", users.NameFormat);
            Assert.Equal("gravatar", users.AvatarSource);
            Assert.False(users.RegistrationRequired);
            Assert.Equal("public", users.RegistrationMode);
            Assert.Equal("strong", users.PasswordPolicy);
            Assert.Equal(3, users.MaxLoginAttempts);
            Assert.Equal(15, users.LockoutMinutes);
            Assert.Equal(60, users.SessionTimeoutMinutes);
            Assert.False(users.AuthTokens);
            Assert.True(users.EmailVerify);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-users");
            await PostFormAsync(client, "/admin/settings-users", token2, UsersForm().ToArray());
        }
    }

    [Fact]
    public async Task RegistrationMode_FlipGatesPortalRegister_EndToEnd()
    {
        var admin = await AdminClientAsync();
        var portal = fixture.Factory.CreateClient();

        // Default (public): the route serves and the login page offers the link.
        Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync("/register")).StatusCode);
        Assert.Contains("href=\"/register\"", await portal.GetStringAsync("/login"));

        try
        {
            // Admin closes self-registration through the real form POST.
            var (token, _) = await GetWithTokenAsync(admin, "/admin/settings-users");
            await PostFormAsync(admin, "/admin/settings-users", token,
                UsersForm(("registration_mode", "closed")).ToArray());

            // The route hides honestly (404, GET and POST) and the link disappears.
            Assert.Equal(HttpStatusCode.NotFound, (await portal.GetAsync("/register")).StatusCode);
            // Antiforgery tokens are per-site: borrow one from the login page so the
            // POST reaches the action (the register page itself is gone).
            var (portalToken, loginHtml) = await GetWithTokenAsync(portal, "/login");
            Assert.Equal(HttpStatusCode.NotFound,
                (await PostFormAsync(portal, "/register", portalToken,
                    ("Email", "kapali@ulasim.com.tr"), ("Name", "Kapalı Test"),
                    ("Password", Password), ("Password2", Password), ("Kvkk", "true"))).StatusCode);
            Assert.DoesNotContain("href=\"/register\"", loginHtml);

            // Invite-only refuses self-registration too (no invite mechanism yet).
            var (token2, _) = await GetWithTokenAsync(admin, "/admin/settings-users");
            await PostFormAsync(admin, "/admin/settings-users", token2,
                UsersForm(("registration_mode", "invite")).ToArray());
            Assert.Equal(HttpStatusCode.NotFound, (await portal.GetAsync("/register")).StatusCode);

            // Back to public through the page → registration reopens.
            var (token3, _) = await GetWithTokenAsync(admin, "/admin/settings-users");
            await PostFormAsync(admin, "/admin/settings-users", token3, UsersForm().ToArray());
            Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync("/register")).StatusCode);
            Assert.Contains("href=\"/register\"", await portal.GetStringAsync("/login"));
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture, "users", "registration_mode", "public");
        }
    }

    // ---- B2 template dialogs -------------------------------------------------------------

    [Fact]
    public async Task TemplateDialog_SaveRoundTrips_SubjectAndBothBodies()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-users");

        // Prefill: the dialog renders the catalog default before any save.
        Assert.Contains("name=\"code\" value=\"user.access.link\"", html);

        // ASCII-only sample values: Razor's HtmlEncoder writes non-Latin attribute
        // characters as numeric entities, which would defeat the literal asserts below.
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var saved = await PostFormAsync(client, "/admin/settings-users/template", token,
            ("code", "user.access.link"),
            ("name", $"Misafir Erisim {stamp}"),
            ("body_tr", $"Merhaba %{{user.name}}, erisim: %{{link}} ({stamp})"),
            ("body_en", $"Hello %{{user.name}}, your link: %{{link}} ({stamp})"));
        Assert.Equal("/admin/settings-users", PathOf(saved));

        // Both language rows exist in the active sets with the shared subject.
        using (var s = new ServiceScopeBundle(fixture))
        {
            var rows = await s.Db.Set<EmailTemplate>()
                .Where(t => t.CodeName == "user.access.link" && t.Set!.IsActive)
                .Select(t => new { t.Set!.Language, t.Subject, t.Body })
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal($"Misafir Erisim {stamp}", r.Subject));
            Assert.Contains(rows, r => r.Language == "tr" && r.Body.Contains($"({stamp})"));
            Assert.Contains(rows, r => r.Language == "en" && r.Body.Contains($"({stamp})"));
        }

        // The re-rendered dialog prefills from the saved rows (B2 round-trip).
        var (_, html2) = await GetWithTokenAsync(client, "/admin/settings-users");
        Assert.Contains($"Misafir Erisim {stamp}", html2);
        Assert.Contains($"({stamp})", html2);

        // Unknown codes refuse without writing anything.
        var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-users");
        await PostFormAsync(client, "/admin/settings-users/template", token2,
            ("code", "user.invented"), ("name", "X"), ("body_tr", "x"), ("body_en", "x"));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.Set<EmailTemplate>().AnyAsync(t => t.CodeName == "user.invented"));
        }
    }

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
