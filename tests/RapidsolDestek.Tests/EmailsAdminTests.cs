using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Admin.Controllers;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin email cluster (emails.html + email-edit.html + email-settings.html):
/// account CRUD with the write-only secret contract (DataProtection columns +
/// bullet sentinel), channel/OAuth field persistence, the REAL MailKit
/// Test-connection endpoint (typed failures, no external network — closed local
/// port + input rejection), the email-settings round-trip with its LIVE
/// default-template-set consumer (effort emails), bulk enable/disable over channel
/// IsActive, and the reference delete guard.
/// </summary>
[Collection("Postgres")]
public class EmailsAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers (SettingsTasksKbTests pattern) ----------------------------------------

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7EMAILSKEY2345677";
        var username = $"s7em{Guid.NewGuid():N}"[..14];
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

            // The endpoints resolve the acting Staff row from the principal.
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

    // List-based (not dictionary) so the bulk ids array can repeat field names.
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    /// <summary>The full email-edit Save payload with harmless defaults.</summary>
    private static List<(string, string)> EditForm(params (string Key, string Value)[] overrides)
    {
        var form = new List<(string, string)>
        {
            ("address", ""), ("fromName", ""), ("deptId", ""), ("priorityId", ""), ("topicId", ""), ("notes", ""),
            ("inHost", ""), ("inPort", ""), ("inFolder", "INBOX"), ("inProtocol", "imap"), ("inAuth", "basic"),
            ("fetchActive", "false"), ("freq", "5"), ("maxFetch", "30"), ("afterFetch", "archive"), ("archiveFolder", ""),
            ("inUsername", ""), ("inPassword", ""), ("inClientId", ""), ("inClientSecret", ""),
            ("smtpActive", "false"), ("outHost", ""), ("outPort", ""), ("outAuth", "basic"),
            ("outUsername", ""), ("outPassword", ""), ("outClientId", ""), ("outClientSecret", ""),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            form.Add((key, value));
        }
        return form;
    }

    // Razor HTML-encodes non-ASCII (Turkish letters, the bullet sentinels) as
    // numeric entities — decode before asserting on rendered text.
    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static string ToastOf(string html) =>
        Decode(Regex.Match(html, "data-toast=\"([^\"]*)\"").Groups[1].Value);

    // ---- emails.html + email-edit.html ---------------------------------------------------

    [Fact]
    public async Task EmailsList_RendersSeededAccounts_SearchNarrows()
    {
        var admin = await AdminClientAsync();

        var html = await admin.GetStringAsync("/admin/emails");
        // Default sort = address desc (mockup sorted-desc on the E-posta column):
        // destek → bordro → bilgi, each linking its editor.
        var destek = html.IndexOf("destek@rapidsol.com.tr", StringComparison.Ordinal);
        var bordro = html.IndexOf("bordro@rapidsol.com.tr", StringComparison.Ordinal);
        var bilgi = html.IndexOf("bilgi@rapidsol.com.tr", StringComparison.Ordinal);
        Assert.True(destek >= 0 && destek < bordro && bordro < bilgi, "expected address-desc row order");
        Assert.Contains("RapidSol Destek", html);
        Assert.Contains("/admin/email-edit?id=", html);

        var filtered = await admin.GetStringAsync("/admin/emails?q=bordro");
        Assert.Contains("bordro@rapidsol.com.tr", filtered);
        Assert.DoesNotContain("bilgi@rapidsol.com.tr", filtered);
    }

    [Fact]
    public async Task AccountCrud_RoundTrips_WithWriteOnlySecrets()
    {
        var admin = await AdminClientAsync();
        var address = $"s7em{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr";

        var (token, createHtml) = await GetWithTokenAsync(admin, "/admin/email-edit");
        Assert.Contains("panel-incoming", createHtml); // tabs render on create too

        var landed = await PostFormAsync(admin, "/admin/email-edit", token, [.. EditForm(
            ("address", address), ("fromName", "S7 Posta"),
            ("inHost", "imap.example.test"), ("inPort", "993"), ("inFolder", "INBOX"),
            ("inProtocol", "imap"), ("inAuth", "basic"),
            ("fetchActive", "true"), ("freq", "7"), ("maxFetch", "40"),
            ("afterFetch", "leave"),
            ("inUsername", address), ("inPassword", "gizli-parola-1"),
            ("smtpActive", "true"), ("outHost", "smtp.example.test"), ("outPort", "587"),
            ("outAuth", "oauth2"), ("outClientId", "cid-123"), ("outClientSecret", "cs-secret-9"),
            ("noAutoResp", "true"))]);
        Assert.Contains("/admin/email-edit?id=", landed.RequestMessage!.RequestUri!.ToString());
        var id = int.Parse(Regex.Match(landed.RequestMessage.RequestUri.Query, @"id=(\d+)").Groups[1].Value);

        // Rendered page: values round-trip; secrets NEVER render back — sentinel only.
        var html = Decode(await landed.Content.ReadAsStringAsync());
        Assert.Contains(address, html);
        Assert.Contains("imap.example.test", html);
        Assert.Contains("smtp.example.test", html);
        Assert.DoesNotContain("gizli-parola-1", html);
        Assert.DoesNotContain("cs-secret-9", html);
        Assert.Contains(EmailsController.PasswordSentinel, html);
        Assert.Contains(EmailsController.ClientSecretSentinel, html);

        // DB truth: one Mailbox + one Smtp channel, secrets encrypted at rest.
        var protector = fixture.Factory.Services.GetRequiredService<IEmailSecretProtector>();
        await using (var db = fixture.CreateContext())
        {
            var account = await db.EmailAccounts.Include(x => x.Channels).SingleAsync(x => x.Id == id);
            Assert.Equal("S7 Posta", account.DisplayName);
            Assert.True(account.NoAutoResponse);

            var inbox = Assert.Single(account.Channels, c => c.Kind == EmailChannelKind.Mailbox);
            Assert.True(inbox.IsActive);
            Assert.Equal(MailProtocol.Imap, inbox.Protocol);
            Assert.Equal(MailAuthKind.Basic, inbox.AuthKind);
            Assert.Equal(993, inbox.Port);
            Assert.Equal(7, inbox.FetchFrequencyMinutes);
            Assert.Equal(40, inbox.FetchMax);
            Assert.Equal(PostFetchAction.Nothing, inbox.PostFetch);
            Assert.NotNull(inbox.PasswordProtected);
            Assert.NotEqual("gizli-parola-1", inbox.PasswordProtected);
            Assert.Equal("gizli-parola-1", protector.Unprotect(inbox.PasswordProtected));

            var smtp = Assert.Single(account.Channels, c => c.Kind == EmailChannelKind.Smtp);
            Assert.True(smtp.IsActive);
            Assert.Equal(MailAuthKind.OAuth2, smtp.AuthKind);
            Assert.Equal("cid-123", smtp.OAuthClientId);
            Assert.Equal("cs-secret-9", protector.Unprotect(smtp.OAuthClientSecretProtected));
        }

        // Save WITHOUT touching the secrets (sentinel posts back) = unchanged.
        var (token2, _) = await GetWithTokenAsync(admin, $"/admin/email-edit?id={id}");
        await PostFormAsync(admin, "/admin/email-edit", token2, [.. EditForm(
            ("id", id.ToString()), ("address", address),
            ("inHost", "imap.example.test"), ("inPort", "993"), ("fetchActive", "true"),
            ("inAuth", "basic"), ("inUsername", address),
            ("inPassword", EmailsController.PasswordSentinel),
            ("smtpActive", "true"), ("outHost", "smtp.example.test"), ("outPort", "587"),
            ("outAuth", "oauth2"), ("outClientId", "cid-123"),
            ("outClientSecret", EmailsController.ClientSecretSentinel))]);
        await using (var db = fixture.CreateContext())
        {
            var channels = await db.EmailAccounts.Where(x => x.Id == id).SelectMany(x => x.Channels).ToListAsync();
            Assert.Equal("gizli-parola-1", protector.Unprotect(
                channels.Single(c => c.Kind == EmailChannelKind.Mailbox).PasswordProtected));
            Assert.Equal("cs-secret-9", protector.Unprotect(
                channels.Single(c => c.Kind == EmailChannelKind.Smtp).OAuthClientSecretProtected));
        }

        // Save WITH a new password = replaced.
        var (token3, _) = await GetWithTokenAsync(admin, $"/admin/email-edit?id={id}");
        await PostFormAsync(admin, "/admin/email-edit", token3, [.. EditForm(
            ("id", id.ToString()), ("address", address),
            ("inHost", "imap.example.test"), ("inPort", "993"), ("fetchActive", "true"),
            ("inAuth", "basic"), ("inUsername", address), ("inPassword", "yeni-parola-2"))]);
        await using (var db = fixture.CreateContext())
        {
            var inbox = await db.EmailAccounts.Where(x => x.Id == id).SelectMany(x => x.Channels)
                .SingleAsync(c => c.Kind == EmailChannelKind.Mailbox);
            Assert.Equal("yeni-parola-2", protector.Unprotect(inbox.PasswordProtected));
        }
    }

    [Fact]
    public async Task Save_RefusesDuplicateAddress_AndIncompleteEnabledChannel()
    {
        var admin = await AdminClientAsync();

        // Duplicate of the seeded canon address.
        var (token, _) = await GetWithTokenAsync(admin, "/admin/email-edit");
        var landed = await PostFormAsync(admin, "/admin/email-edit", token,
            [.. EditForm(("address", "destek@rapidsol.com.tr"))]);
        Assert.Contains("zaten kayıtlı", ToastOf(await landed.Content.ReadAsStringAsync()));
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await db.EmailAccounts.CountAsync(a => a.Address == "destek@rapidsol.com.tr"));

        // Fetch enabled without a host: refused, nothing written (B3 server side).
        var address = $"s7in{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr";
        var (token2, _) = await GetWithTokenAsync(admin, "/admin/email-edit");
        var landed2 = await PostFormAsync(admin, "/admin/email-edit", token2,
            [.. EditForm(("address", address), ("fetchActive", "true"))]);
        Assert.Contains("sunucu adresi", ToastOf(await landed2.Content.ReadAsStringAsync()));
        await using (var db = fixture.CreateContext())
            Assert.False(await db.EmailAccounts.AnyAsync(a => a.Address == address));

        // Malformed address: refused.
        var (token3, _) = await GetWithTokenAsync(admin, "/admin/email-edit");
        var landed3 = await PostFormAsync(admin, "/admin/email-edit", token3,
            [.. EditForm(("address", "not-an-email"))]);
        Assert.Contains("Geçerli bir e-posta", ToastOf(await landed3.Content.ReadAsStringAsync()));
    }

    // ---- Test connection (REAL probe, no external network) --------------------------------

    [Fact]
    public async Task TestConnection_TypedFailure_OnClosedLocalPort_Fast()
    {
        var admin = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(admin, "/admin/email-edit");

        var watch = Stopwatch.StartNew();
        var res = await PostFormAsync(admin, "/admin/email-edit/test", token,
            ("kind", "in"), ("protocol", "imap"), ("host", "127.0.0.1"), ("port", "1"),
            ("auth", "basic"), ("username", "x"), ("password", "y"));
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("connect", json.RootElement.GetProperty("stage").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"probe took {watch.Elapsed}");

        // SMTP leg of the same contract.
        var res2 = await PostFormAsync(admin, "/admin/email-edit/test", token,
            ("kind", "out"), ("host", "127.0.0.1"), ("port", "1"), ("auth", "oauth2"));
        using var json2 = JsonDocument.Parse(await res2.Content.ReadAsStringAsync());
        Assert.False(json2.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("connect", json2.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task TestConnection_RejectsInvalidInput_BeforeAnyNetworkIO()
    {
        var admin = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(admin, "/admin/email-edit");

        foreach (var fields in new (string, string)[][]
        {
            [("kind", "in"), ("protocol", "imap"), ("host", ""), ("port", "993"), ("auth", "basic")],
            [("kind", "in"), ("protocol", "imap"), ("host", "imap.example.test"), ("port", "0"), ("auth", "basic")],
            [("kind", "out"), ("host", "smtp.example.test"), ("port", "70000"), ("auth", "basic")],
        })
        {
            var res = await PostFormAsync(admin, "/admin/email-edit/test", token, fields);
            using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("input", json.RootElement.GetProperty("stage").GetString());
        }
    }

    // ---- email-settings.html ---------------------------------------------------------------

    [Fact]
    public async Task EmailSettings_RoundTrips_AndValidatesAdminEmail()
    {
        var admin = await AdminClientAsync();
        int enSetId, bordroId, bilgiId, destekId;
        await using (var db = fixture.CreateContext())
        {
            // Pinned by canon name — template tests create further active en sets.
            enSetId = await db.EmailTemplateSets.Where(s => s.Name == "English Set (EN)")
                .Select(s => s.Id).SingleAsync();
            bordroId = await db.EmailAccounts.Where(a => a.Address == "bordro@rapidsol.com.tr").Select(a => a.Id).SingleAsync();
            bilgiId = await db.EmailAccounts.Where(a => a.Address == "bilgi@rapidsol.com.tr").Select(a => a.Id).SingleAsync();
            destekId = await db.EmailAccounts.Where(a => a.Address == "destek@rapidsol.com.tr").Select(a => a.Id).SingleAsync();
        }

        try
        {
            var (token, rawHtml) = await GetWithTokenAsync(admin, "/admin/email-settings");
            var html = Decode(rawHtml);
            Assert.Contains("section-general", html);
            Assert.Contains("section-incoming", html);
            Assert.Contains("section-outgoing", html);
            // Default-SMTP options list only accounts with an SMTP channel (seeded destek@).
            Assert.Contains("destek@rapidsol.com.tr — SMTP", html);
            Assert.DoesNotContain("bordro@rapidsol.com.tr — SMTP", html);

            var landed = await PostFormAsync(admin, "/admin/email-settings", token,
                ("default_template_set_id", enSetId.ToString()),
                ("default_email_id", bordroId.ToString()),
                ("alert_email_id", bilgiId.ToString()),
                ("admin_email", "yonetici@rapidsol.com.tr"),
                // verify_domain unchecked
                ("fetch_enabled", "true"),
                // fetch_auto_cron unchecked
                ("strip_quoted", "true"),
                ("reply_separator", "-- test ayracı --"),
                ("use_email_priority", "true"),
                // accept_unregistered unchecked
                ("auto_add_collabs", "true"),
                ("default_smtp", destekId.ToString()),
                // attachments_in_email unchecked
                ("noop", ""));
            Assert.Contains("kaydedildi", ToastOf(await landed.Content.ReadAsStringAsync()));

            using (var scope = fixture.CreateScope())
            {
                var s = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetEmailAsync();
                Assert.Equal(enSetId, s.DefaultTemplateSetId);
                Assert.Equal(bordroId, s.DefaultEmailAccountId);
                Assert.Equal(bilgiId, s.AlertEmailAccountId);
                Assert.Equal("yonetici@rapidsol.com.tr", s.AdminEmail);
                Assert.False(s.VerifyDomain);
                Assert.True(s.FetchEnabled);
                Assert.False(s.FetchAutoCron);
                Assert.True(s.StripQuoted);
                Assert.Equal("-- test ayracı --", s.ReplySeparator);
                Assert.True(s.UseEmailPriority);
                Assert.False(s.AcceptUnregistered);
                Assert.True(s.AutoAddCollabs);
                Assert.Equal(destekId.ToString(), s.DefaultSmtp);
                Assert.False(s.AttachmentsInEmail);
            }

            // Re-render reflects the persisted state (selected/checked round-trip).
            var (_, html2) = await GetWithTokenAsync(admin, "/admin/email-settings");
            Assert.Contains("-- test ayracı --", Decode(html2));

            // Invalid admin email: refused with an error toast, value untouched.
            var (token2, _) = await GetWithTokenAsync(admin, "/admin/email-settings");
            var refused = await PostFormAsync(admin, "/admin/email-settings", token2,
                ("admin_email", "not-an-email"), ("reply_separator", "x"));
            Assert.Contains("geçerli bir adres", ToastOf(await refused.Content.ReadAsStringAsync()));
            using (var scope = fixture.CreateScope())
            {
                var s = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetEmailAsync();
                Assert.Equal("yonetici@rapidsol.com.tr", s.AdminEmail);
                Assert.Equal("-- test ayracı --", s.ReplySeparator);
            }
        }
        finally
        {
            // Shared container DB: restore the namespace defaults for other tests.
            using var scope = fixture.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var (key, value) in new (string, string)[]
            {
                ("default_template_set_id", "0"), ("default_email_id", "0"), ("alert_email_id", "0"),
                ("admin_email", ""), ("verify_domain", "true"), ("fetch_enabled", "true"),
                ("fetch_auto_cron", "true"), ("strip_quoted", "true"),
                ("reply_separator", "-- lütfen bu satırın üstüne yazın --"),
                ("use_email_priority", "false"), ("accept_unregistered", "true"),
                ("auto_add_collabs", "true"), ("default_smtp", "system"), ("attachments_in_email", "true"),
            })
                await settings.SetAsync("email", key, value);
        }
    }

    /// <summary>LIVE consumer: the effort emails render from the configured template set.</summary>
    [Fact]
    public async Task EffortEmails_RenderFrom_ConfiguredTemplateSet()
    {
        var marker = $"EN Efor İsteği {Guid.NewGuid():N}";
        int enSetId;
        string originalSubject;
        await using (var db = fixture.CreateContext())
        {
            // Pinned by canon name — template tests create further active en sets.
            var template = await db.EmailTemplateSets.Where(s => s.Name == "English Set (EN)")
                .SelectMany(s => s.Templates).SingleAsync(t => t.CodeName == "effort.request");
            enSetId = template.SetId;
            originalSubject = template.Subject;
            template.Subject = marker;
            await db.SaveChangesAsync();
        }

        try
        {
            await using var flip = await SettingOverride.SetAsync(fixture, "email", "default_template_set_id", enSetId.ToString());

            using var s = new ServiceScopeBundle(fixture);
            var agent = await TestActors.StaffAsync(s.Db, "uakin");
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Efor e-posta seti testi {Guid.NewGuid():N}",
                Body = "<p>Test içeriği</p>",
            }, owner);

            fixture.Factory.Emails.Clear();
            await s.Get<IEffortProposalService>().ProposeAsync(ticket.Id, 6, null, agent);

            var request = Assert.Single(fixture.Factory.Emails.Sent);
            Assert.Equal("bourla.salehi@ulasim.com.tr", request.To);
            Assert.Contains(marker, request.Subject);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var template = await db.EmailTemplateSets.Where(s => s.Id == enSetId)
                .SelectMany(s => s.Templates).SingleAsync(t => t.CodeName == "effort.request");
            template.Subject = originalSubject;
            await db.SaveChangesAsync();
        }
    }

    // ---- bulk actions + delete guard -------------------------------------------------------

    [Fact]
    public async Task Bulk_EnableDisable_FlipChannelActive_DeleteGuardsReferencedAccounts()
    {
        var admin = await AdminClientAsync();

        // Fresh disposable accounts: one free, one referenced via the email settings.
        int freeId, referencedId, destekId;
        await using (var db = fixture.CreateContext())
        {
            var free = new EmailAccount
            {
                Address = $"s7f{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr",
                Channels =
                [
                    new EmailChannel { Kind = EmailChannelKind.Mailbox, IsActive = true, Protocol = MailProtocol.Imap, Host = "imap.example.test", Port = 993 },
                    new EmailChannel { Kind = EmailChannelKind.Smtp, IsActive = true, Protocol = MailProtocol.Smtp, Host = "smtp.example.test", Port = 587 },
                ],
            };
            var referenced = new EmailAccount { Address = $"s7r{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr" };
            db.EmailAccounts.AddRange(free, referenced);
            await db.SaveChangesAsync();
            freeId = free.Id;
            referencedId = referenced.Id;
            destekId = await db.EmailAccounts.Where(a => a.Address == "destek@rapidsol.com.tr").Select(a => a.Id).SingleAsync();
        }

        // Disable / enable flip the account's channel IsActive flags.
        var (token, _) = await GetWithTokenAsync(admin, "/admin/emails");
        await PostFormAsync(admin, "/admin/emails/bulk", token, ("act", "disable"), ("ids", freeId.ToString()));
        await using (var db = fixture.CreateContext())
            Assert.All(await db.EmailAccounts.Where(a => a.Id == freeId).SelectMany(a => a.Channels).ToListAsync(),
                c => Assert.False(c.IsActive));
        var (token2, _) = await GetWithTokenAsync(admin, "/admin/emails");
        await PostFormAsync(admin, "/admin/emails/bulk", token2, ("act", "enable"), ("ids", freeId.ToString()));
        await using (var db = fixture.CreateContext())
            Assert.All(await db.EmailAccounts.Where(a => a.Id == freeId).SelectMany(a => a.Channels).ToListAsync(),
                c => Assert.True(c.IsActive));

        // Delete guard: destek@ is referenced by departments (+ seeded canon),
        // the fresh "referenced" account by the email settings alert address —
        // both skipped; the free account deletes. Partial toast reports 1/2.
        await using var flip = await SettingOverride.SetAsync(fixture, "email", "alert_email_id", referencedId.ToString());
        var (token3, _) = await GetWithTokenAsync(admin, "/admin/emails");
        var landed = await PostFormAsync(admin, "/admin/emails/bulk", token3,
            ("act", "delete"), ("ids", destekId.ToString()), ("ids", freeId.ToString()), ("ids", referencedId.ToString()));
        var toast = ToastOf(await landed.Content.ReadAsStringAsync());
        Assert.Contains("1 işlem tamamlandı", toast);
        Assert.Contains("2 kayıt atlandı", toast);

        await using (var db = fixture.CreateContext())
        {
            Assert.True(await db.EmailAccounts.AnyAsync(a => a.Id == destekId));
            Assert.True(await db.EmailAccounts.AnyAsync(a => a.Id == referencedId));
            Assert.False(await db.EmailAccounts.AnyAsync(a => a.Id == freeId));
            // Cleanup the leftover disposable row.
            db.EmailAccounts.Remove(await db.EmailAccounts.SingleAsync(a => a.Id == referencedId));
            await db.SaveChangesAsync();
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
