using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/login.html + admin/pwreset.html (B6): mandatory 2FA enrollment for admins
/// without a second factor, the STAFF-WIDE settings-owned lockout policy (default
/// 5 attempts / 30 min — the settings-agents mockup canon; the earlier admin-only
/// 3/30 tightening was resolved toward the mockup when that page ported), and the
/// admin reset-link flow end to end.
/// </summary>
[Collection("Postgres")]
public class AdminAuthTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers -------------------------------------------------------------------

    /// <summary>Identity-only staff account with unique username (no Staff domain row needed).</summary>
    private async Task<StaffUser> CreateStaffUserAsync(string username, bool admin, string? totpKey = null)
    {
        using var scope = fixture.CreateScope();
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
        if (admin)
            await users.AddToRoleAsync(user, "Admin");
        await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", user.FullName!));
        if (totpKey is not null)
        {
            await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", totpKey);
            await users.SetTwoFactorEnabledAsync(user, true);
        }
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
                  .ToDictionary(f => f.Item1, f => f.Item2)));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    // ---- Mandatory 2FA (admin/login.html row) --------------------------------------

    [Fact]
    public async Task AdminWithoutTwoFactor_IsForcedThroughSetup_ThenSignsInWithMfa()
    {
        var user = await CreateStaffUserAsync("s7admin1", admin: true);
        var client = fixture.Factory.CreateClient();

        // Password-only success on the admin login lands on the enrollment page, not the panel.
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var afterLogin = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", "s7admin1"), ("Password", Password));
        Assert.Equal("/admin/2fa-setup", PathOf(afterLogin));

        // The mfa-less session cannot reach the panel — AdminOnly access-denied reroutes
        // back to the setup page instead of the login form.
        var dashboardTry = await client.GetAsync("/admin/dashboard");
        Assert.Equal("/admin/2fa-setup", PathOf(dashboardTry));

        // Enroll: the page exposes the setup key + otpauth link; a valid code activates.
        var (setupToken, setupHtml) = await GetWithTokenAsync(client, "/admin/2fa-setup");
        Assert.Contains("otpauth://totp/RapidsolDestek", setupHtml);
        var key = Regex.Match(setupHtml, @"<code[^>]*>([a-zA-Z2-7]{16,})</code>").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(key), "setup key not rendered");
        var afterSetup = await PostFormAsync(client, "/admin/2fa-setup", setupToken,
            ("code", ComputeTotp(key)));
        Assert.Equal("/admin/login", PathOf(afterSetup)); // signed out to re-authenticate with mfa

        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            Assert.True((await users.FindByNameAsync("s7admin1"))!.TwoFactorEnabled);
        }

        // Second sign-in now runs the real two-step flow and grants the admin panel.
        var (token2, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", token2,
            ("User", "s7admin1"), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var afterTwofa = await PostFormAsync(client, "/admin/login/2fa", twofaToken,
            ("Code", ComputeTotp(key)));
        Assert.Equal("/admin/dashboard", PathOf(afterTwofa));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task AdminTwoFactorStep_WrongCodes_TripTheAdminLockout()
    {
        const string totpKey = "RAPIDSOLDESTEKS7TESTKEY234567ABC";
        await CreateStaffUserAsync("s7admin4", admin: true, totpKey: totpKey);
        var client = fixture.Factory.CreateClient();

        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", "s7admin4"), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));

        var html = await toTwofa.Content.ReadAsStringAsync();
        for (var i = 0; i < 5; i++) // settings-agents default threshold (sa.maxAttempts = 5)
        {
            var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var response = await PostFormAsync(client, "/admin/login/2fa", token, ("Code", "000000"));
            html = await response.Content.ReadAsStringAsync();
        }
        Assert.Contains("kilitlendi", html); // auth.lockedOut (TR default culture)

        using var scope = fixture.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
        var locked = (await users.FindByNameAsync("s7admin4"))!;
        Assert.NotNull(locked.LockoutEnd);
        Assert.True(locked.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(25));
    }

    // ---- Staff-wide settings-owned lockout policy (settings-agents row) -------------

    [Fact]
    public async Task AdminLogin_LocksAtTheSettingsThreshold_ThenUnlocksAfterExpiry()
    {
        await CreateStaffUserAsync("s7admin2", admin: true);
        var client = fixture.Factory.CreateClient();

        string lastHtml = "";
        for (var i = 0; i < 5; i++) // settings-agents default threshold (sa.maxAttempts = 5)
        {
            var (token, _) = await GetWithTokenAsync(client, "/admin/login");
            var response = await PostFormAsync(client, "/admin/login", token,
                ("User", "s7admin2"), ("Password", "yanlış-parola"));
            lastHtml = await response.Content.ReadAsStringAsync();
        }
        Assert.Contains("kilitlendi", lastHtml); // fifth failure trips the settings threshold

        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var locked = (await users.FindByNameAsync("s7admin2"))!;
            Assert.NotNull(locked.LockoutEnd);
            // settings-agents default duration: 30 minutes (sa.lockDuration).
            Assert.InRange(locked.LockoutEnd!.Value, DateTimeOffset.UtcNow.AddMinutes(25), DateTimeOffset.UtcNow.AddMinutes(35));
            Assert.Equal(0, locked.AccessFailedCount); // count reset on lock, Identity-style
        }

        // While locked, even the correct password is refused.
        var (lockedToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var whileLocked = await PostFormAsync(client, "/admin/login", lockedToken,
            ("User", "s7admin2"), ("Password", Password));
        Assert.Equal("/admin/login", PathOf(whileLocked));
        Assert.Contains("kilitlendi", await whileLocked.Content.ReadAsStringAsync());

        // Once the lock expires the account signs in again (no admin left permanently out).
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByNameAsync("s7admin2"))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        }
        var (freshToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var afterExpiry = await PostFormAsync(client, "/admin/login", freshToken,
            ("User", "s7admin2"), ("Password", Password));
        Assert.Equal("/admin/2fa-setup", PathOf(afterExpiry)); // proceeds to mandatory enrollment
    }

    [Fact]
    public async Task AgentLogin_ThreeFailures_DoNotLock_BelowTheSettingsThreshold()
    {
        await CreateStaffUserAsync("s7agent1", admin: false);
        var client = fixture.Factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var (token, _) = await GetWithTokenAsync(client, "/agent/login");
            await PostFormAsync(client, "/agent/login", token,
                ("User", "s7agent1"), ("Password", "yanlış-parola"));
        }

        using var scope = fixture.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
        var user = (await users.FindByNameAsync("s7agent1"))!;
        Assert.Null(user.LockoutEnd);          // staff-wide settings default is 5 (sa.maxAttempts)
        Assert.Equal(3, user.AccessFailedCount);
    }

    // ---- Admin reset-link flow (admin/pwreset.html row) ----------------------------

    [Fact]
    public async Task AdminPwreset_EndToEnd_RequestMailLinkNewPassword()
    {
        await CreateStaffUserAsync("s7admin3", admin: true);
        const string email = "s7admin3@rapidsol.com.tr";
        var client = fixture.Factory.CreateClient();

        // Step 1 — request: enumeration-safe notice + reset mail with the /admin link.
        var (requestToken, _) = await GetWithTokenAsync(client, "/admin/pwreset");
        var requested = await PostFormAsync(client, "/admin/pwreset", requestToken, ("email", email));
        Assert.Equal("/admin/pwreset", PathOf(requested));
        Assert.Contains("Sıfırlama bağlantısı e-posta adresinize gönderildi",
            await requested.Content.ReadAsStringAsync()); // pw.sentNotice
        var mail = fixture.Factory.Emails.Sent.Last(m => m.To == email);
        var link = Regex.Match(mail.HtmlBody, @"https?://[^\s""]+").Value;
        Assert.Contains("/admin/pwreset/new", link);

        // Step 2 — the mailed link renders the admin new-password card (rd-field
        // conventions, /admin back link — not the portal step page).
        var newPage = await client.GetStringAsync(link);
        Assert.Contains("id=\"pw-new\"", newPage);
        Assert.Contains("/admin/login", newPage);
        Assert.DoesNotContain("Adım", newPage);
        var newToken = Regex.Match(newPage, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var boundEmail = WebUtility.HtmlDecode(Regex.Match(newPage, "name=\"Email\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var boundToken = WebUtility.HtmlDecode(Regex.Match(newPage, "name=\"Token\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

        // Step 3 — set the new password, then sign in with it.
        var saved = await PostFormAsync(client, "/admin/pwreset/new", newToken,
            ("Email", boundEmail), ("Token", boundToken),
            ("Password", "yeniParola77"), ("Password2", "yeniParola77"));
        Assert.Equal("/admin/login", PathOf(saved));

        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var login = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", "s7admin3"), ("Password", "yeniParola77"));
        Assert.Equal("/admin/2fa-setup", PathOf(login)); // new password accepted → mandatory 2FA next
    }

    [Fact]
    public async Task AdminPwreset_NonAdminEmail_GetsNoMail_ButSameNotice()
    {
        const string email = "mcetin@rapidsol.com.tr"; // seeded agent, no Admin role
        var client = fixture.Factory.CreateClient();
        var mailsBefore = fixture.Factory.Emails.Sent.Count(m => m.To == email);

        var (token, _) = await GetWithTokenAsync(client, "/admin/pwreset");
        var response = await PostFormAsync(client, "/admin/pwreset", token, ("email", email));

        Assert.Contains("Sıfırlama bağlantısı e-posta adresinize gönderildi",
            await response.Content.ReadAsStringAsync()); // enumeration-safe: same notice
        Assert.Equal(mailsBefore, fixture.Factory.Emails.Sent.Count(m => m.To == email));
    }

    [Fact]
    public async Task StaffResetTokens_ExpireInThirtyMinutes_PerMockupHelpText()
    {
        // The pw.help copy promises "Bağlantı 30 dakika geçerlidir" — the staff principal's
        // password-reset provider must actually be the short-lived one.
        var options = fixture.Factory.Services.GetRequiredService<IOptions<StaffResetTokenProviderOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(30), options.TokenLifespan);

        using var scope = fixture.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
        Assert.Equal(StaffResetTokenProvider.ProviderName, users.Options.Tokens.PasswordResetTokenProvider);
    }

    // ---- RFC 6238 helper (mirrors Identity's AuthenticatorTokenProvider math) ------

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
        foreach (var c in input.TrimEnd('='))
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
