using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin templates + email-diagnostic (templates.html + template-edit.html +
/// email-diagnostic.html): set CRUD with the create dialog's real submit and the
/// reference delete guard, the 21-template per-set editor round-trip (B2), the
/// honest per-code variable pills (B5), the per-template preview through the real
/// substitution path, and the diagnostic's REAL pending → success/failure states
/// (B10 — the transport is captured by the email fake; channel-config failures are
/// typed before any transport, and the real MailKit sender is probed directly).
/// </summary>
[Collection("Postgres")]
public class EmailTemplatesDiagnosticTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- templates.html -------------------------------------------------------------------

    [Fact]
    public async Task TemplatesList_RendersSeededSets_SearchNarrows()
    {
        var admin = await AdminClientAsync();

        // Search narrows to the two seeded canon sets (sibling tests add their own).
        var html = Decode(await admin.GetStringAsync("/admin/templates?q=%28"));
        // Default sort = name desc (mockup sorted-desc on the Şablon Seti column):
        // Varsayılan before English Set.
        var tr = html.IndexOf("Varsayılan (TR)", StringComparison.Ordinal);
        var en = html.IndexOf("English Set (EN)", StringComparison.Ordinal);
        Assert.True(tr >= 0 && tr < en, "expected name-desc row order");
        Assert.Contains("/admin/template-edit?id=", html);
        Assert.Contains("Türkçe", html); // language column autonym

        // Row links only — the create dialog's clone select still lists every set.
        var filtered = Decode(await admin.GetStringAsync("/admin/templates?q=English"));
        Assert.Contains(">English Set (EN)</a>", filtered);
        Assert.DoesNotContain(">Varsayılan (TR)</a>", filtered);
    }

    [Fact]
    public async Task SetCreate_FromStock_Creates21Templates_AndDialogSubmitLands()
    {
        var admin = await AdminClientAsync();
        var name = $"Kurumsal {Guid.NewGuid():N}"[..18];

        var (token, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var landed = await PostFormAsync(admin, "/admin/templates/create", token,
            ("name", name), ("cloneId", ""), ("lang", "tr"));
        // The create dialog's submit really saves and the PRG lands on the new
        // set's editor (the S0-audited close-swallows-save defect stays fixed).
        Assert.Equal("/admin/template-edit", landed.RequestMessage!.RequestUri!.AbsolutePath);
        var id = int.Parse(Regex.Match(landed.RequestMessage.RequestUri.Query, @"id=(\d+)").Groups[1].Value);

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var set = await db.EmailTemplateSets.Include(s => s.Templates).SingleAsync(s => s.Id == id);
        Assert.Equal(name, set.Name);
        Assert.True(set.IsActive);
        Assert.Equal(EmailTemplateCatalog.All.Count, set.Templates.Count);
        foreach (var d in EmailTemplateCatalog.All)
        {
            var row = set.Templates.SingleOrDefault(t => t.CodeName == d.Code);
            Assert.NotNull(row);
            Assert.Equal(EmailTemplateCatalog.StockBody(d), row.Body);
        }

        // Duplicate name → refused with an error toast back on the list.
        var (token2, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var refused = await PostFormAsync(admin, "/admin/templates/create", token2,
            ("name", name), ("cloneId", ""), ("lang", "tr"));
        Assert.Equal("/admin/templates", refused.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("zaten var", ToastOf(await refused.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task SetCreate_Clone_CopiesSourceContent()
    {
        var admin = await AdminClientAsync();
        int sourceId;
        string sourceBody;
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var source = await db.EmailTemplateSets.Include(s => s.Templates)
                .SingleAsync(s => s.Name == "Varsayılan (TR)");
            sourceId = source.Id;
            sourceBody = source.Templates.Single(t => t.CodeName == "ticket.autoresp").Body;
        }

        var name = $"Klon {Guid.NewGuid():N}"[..14];
        var (token, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var landed = await PostFormAsync(admin, "/admin/templates/create", token,
            ("name", name), ("cloneId", sourceId.ToString()), ("lang", "en"));
        var id = int.Parse(Regex.Match(landed.RequestMessage!.RequestUri!.Query, @"id=(\d+)").Groups[1].Value);

        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var clone = await db.EmailTemplateSets.Include(s => s.Templates).SingleAsync(s => s.Id == id);
            Assert.Equal("en", clone.Language);
            Assert.Equal(sourceBody, clone.Templates.Single(t => t.CodeName == "ticket.autoresp").Body);
            Assert.True(clone.Templates.Count >= EmailTemplateCatalog.All.Count);
        }
    }

    [Fact]
    public async Task TemplateSave_RoundTrips_PerSet_OtherSetUntouched()
    {
        var admin = await AdminClientAsync();
        // A private pair so the seeded canon sets stay untouched for other tests.
        var trId = await CreateSetAsync(admin, "tr");
        var enId = await CreateSetAsync(admin, "en");

        var subject = $"Talebiniz alındı [#%{{ticket.number}}] {Guid.NewGuid():N}"[..40];
        const string body = "<p>Sayın %{ticket.user.name}, talebiniz bize ulaştı: %{ticket.number}</p>";
        var (token, _) = await GetWithTokenAsync(admin, $"/admin/template-edit?id={trId}");
        var landed = await PostFormAsync(admin, "/admin/template-edit/save-template", token,
            ("id", trId.ToString()), ("code", "ticket.autoresp"), ("subject", subject), ("body", body));
        Assert.Equal($"id={trId}", landed.RequestMessage!.RequestUri!.Query.TrimStart('?'));

        // The editor page prefills the row's OWN dialog with the saved content (B2).
        var html = Decode(await landed.Content.ReadAsStringAsync());
        Assert.Contains("dlg-editor-ticket-autoresp", html);
        Assert.Contains(subject, html);

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        var saved = await db.Set<EmailTemplate>().SingleAsync(t => t.SetId == trId && t.CodeName == "ticket.autoresp");
        Assert.Equal(subject, saved.Subject);
        Assert.Equal(body, saved.Body);
        // One code of ONE set — the sibling set keeps its stock content.
        var sibling = await db.Set<EmailTemplate>().SingleAsync(t => t.SetId == enId && t.CodeName == "ticket.autoresp");
        Assert.NotEqual(subject, sibling.Subject);

        // Unknown codes are refused (canon catalog only).
        var (token2, _) = await GetWithTokenAsync(admin, $"/admin/template-edit?id={trId}");
        var refused = await PostFormAsync(admin, "/admin/template-edit/save-template", token2,
            ("id", trId.ToString()), ("code", "not.a.template"), ("subject", "x"), ("body", "y"));
        Assert.Contains("kaydedilemedi", ToastOf(await refused.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Bulk_DisableWorks_DeleteGuardsReferencedSets()
    {
        var admin = await AdminClientAsync();
        var freeId = await CreateSetAsync(admin, "tr");
        int referencedId;
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            // The seeded canon: Bordro's template-set pointer references Varsayılan (TR).
            referencedId = (await db.EmailTemplateSets.SingleAsync(s => s.Name == "Varsayılan (TR)")).Id;
            Assert.True(await db.Departments.AnyAsync(d => d.TemplateSetId == referencedId));
        }

        var (token, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var disabled = await PostFormAsync(admin, "/admin/templates/bulk", token,
            ("act", "disable"), ("ids", freeId.ToString()));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            Assert.False((await db.EmailTemplateSets.SingleAsync(s => s.Id == freeId)).IsActive);
        }

        // Delete: the referenced set is skipped (partial toast), the free one goes
        // (its 21 templates cascade).
        var (token2, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var deleted = await PostFormAsync(admin, "/admin/templates/bulk", token2,
            ("act", "delete"), ("ids", freeId.ToString()), ("ids", referencedId.ToString()));
        Assert.Contains("atlandı", ToastOf(await deleted.Content.ReadAsStringAsync()));
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            Assert.False(await db.EmailTemplateSets.AnyAsync(s => s.Id == freeId));
            Assert.False(await db.Set<EmailTemplate>().AnyAsync(t => t.SetId == freeId));
            Assert.True(await db.EmailTemplateSets.AnyAsync(s => s.Id == referencedId));
        }
    }

    // ---- template-edit.html ----------------------------------------------------------------

    [Fact]
    public async Task VariablePills_HonestPerCode_EffortVarsOnlyOnEffortTemplates()
    {
        var admin = await AdminClientAsync();
        var id = await CreateSetAsync(admin, "tr");
        var html = Decode(await admin.GetStringAsync($"/admin/template-edit?id={id}"));

        string Dialog(string domId)
        {
            var start = html.IndexOf($"id=\"{domId}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{domId} missing");
            var end = html.IndexOf("</dialog>", start, StringComparison.Ordinal);
            return html[start..end];
        }

        // Every one of the 21 canon rows opens its OWN dialog (B2).
        foreach (var d in EmailTemplateCatalog.All)
            Assert.Contains($"dlg-editor-{d.Code.Replace('.', '-')}", html);

        var effort = Dialog("dlg-editor-effort-request");
        Assert.Contains("data-var-insert=\"effort.hours\"", effort);
        Assert.Contains("data-var-insert=\"ticket.number\"", effort);

        // Non-effort templates never advertise effort variables (B5 honesty).
        var autoresp = Dialog("dlg-editor-ticket-autoresp");
        Assert.DoesNotContain("effort.hours", autoresp);
        Assert.Contains("data-var-insert=\"ticket.user.name\"", autoresp);
    }

    [Fact]
    public async Task Preview_SubstitutesHeroTicketData_UnknownVarsEmpty()
    {
        var admin = await AdminClientAsync();
        var seededSetId = await FirstSetIdAsync();
        var (token, _) = await GetWithTokenAsync(admin, $"/admin/template-edit?id={seededSetId}");

        var res = await PostFormAsync(admin, "/admin/template-edit/preview", token,
            ("subject", "Talebiniz alındı [#%{ticket.number}]"),
            ("body", "<p>Sayın %{ticket.user.name}, %{unknown.thing} departman: %{ticket.dept.name}</p>"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        // The real substitution path over the hero ticket (§2 R716555 / Bourla Salehi).
        Assert.Equal("Talebiniz alındı [#R716555]", json.RootElement.GetProperty("subject").GetString());
        var body = json.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("Bourla Salehi", body);
        Assert.Contains("Bordro", body);
        Assert.DoesNotContain("%{", body); // unknown variables expand to empty, never leak
    }

    // ---- email-diagnostic.html --------------------------------------------------------------

    [Fact]
    public async Task DiagnosticSend_RealSendCaptured_PendingResolvesToSuccess()
    {
        var admin = await AdminClientAsync();
        var to = $"diag{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr";
        int destekId;
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            destekId = (await db.EmailAccounts.SingleAsync(a => a.Address == "destek@rapidsol.com.tr")).Id;
        }

        var (token, formHtml) = await GetWithTokenAsync(admin, "/admin/email-diagnostic");
        Assert.Contains("destek@rapidsol.com.tr", formHtml); // FROM = seeded accounts
        // No always-on success banner: without a job the banner is rendered hidden (S0 defect).
        Assert.Contains("data-job-success hidden", formHtml);

        var landed = await PostFormAsync(admin, "/admin/email-diagnostic/send", token,
            ("from", destekId.ToString()), ("to", to),
            ("subject", "S7 tanılama"), ("message", "Gerçek gönderim testi."));
        var job = Regex.Match(landed.RequestMessage!.RequestUri!.Query, @"job=([0-9a-f-]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(job), "expected PRG to ?job=");

        var state = await PollAsync(admin, job);
        Assert.Equal("success", state.State);

        // REAL send: the transport (captured by the email fake) carried the form's content.
        var mail = fixture.Factory.Emails.Sent.Single(m => m.To == to);
        Assert.Equal("S7 tanılama", mail.Subject);
        Assert.Contains("Gerçek gönderim testi.", mail.HtmlBody);

        // The refreshed page resolves the same terminal state server-side (JS-free).
        var refreshed = Decode(await admin.GetStringAsync($"/admin/email-diagnostic?job={job}"));
        Assert.Contains("id=\"ed-success\" data-job-success>", refreshed.Replace("  ", " "));
    }

    [Fact]
    public async Task DiagnosticSend_UnroutableChannel_TypedFailureState()
    {
        var admin = await AdminClientAsync();
        var to = $"diag{Guid.NewGuid():N}"[..12] + "@rapidsol.com.tr";
        int bordroId;
        using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            // bordro@ has no SMTP channel at all — an unroutable outgoing config.
            bordroId = (await db.EmailAccounts.SingleAsync(a => a.Address == "bordro@rapidsol.com.tr")).Id;
        }

        var (token, _) = await GetWithTokenAsync(admin, "/admin/email-diagnostic");
        var landed = await PostFormAsync(admin, "/admin/email-diagnostic/send", token,
            ("from", bordroId.ToString()), ("to", to), ("subject", "x"), ("message", "y"));
        var job = Regex.Match(landed.RequestMessage!.RequestUri!.Query, @"job=([0-9a-f-]+)").Groups[1].Value;

        var state = await PollAsync(admin, job);
        Assert.Equal("failure", state.State);
        Assert.Equal("channel", state.Stage); // typed BEFORE any transport I/O
        Assert.DoesNotContain(fixture.Factory.Emails.Sent, m => m.To == to);

        // Server-rendered failure banner carries the typed message.
        var refreshed = Decode(await admin.GetStringAsync($"/admin/email-diagnostic?job={job}"));
        Assert.Contains("etkin bir SMTP kanalı yok", refreshed);

        // Bad recipient input never starts a job — immediate error toast instead.
        var (token2, _) = await GetWithTokenAsync(admin, "/admin/email-diagnostic");
        var rejected = await PostFormAsync(admin, "/admin/email-diagnostic/send", token2,
            ("from", bordroId.ToString()), ("to", "not-an-email"), ("subject", "x"), ("message", "y"));
        Assert.DoesNotContain("job=", rejected.RequestMessage!.RequestUri!.Query);
        Assert.Contains("alıcı adresi", ToastOf(await rejected.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task MailDiagnosticSender_RealTransport_TypedFailures_NoExternalNetwork()
    {
        var sender = new MailDiagnosticSender();

        // Rejected before any I/O.
        var input = await sender.SendAsync(new MailSendRequest(
            "", 0, MailAuthKind.Basic, null, null,
            "destek@rapidsol.com.tr", null, "x@rapidsol.com.tr", "s", "b"));
        Assert.False(input.Success);
        Assert.Equal("input", input.Stage);

        // Closed local port → typed connect failure (EmailsAdminTests probe precedent).
        var connect = await sender.SendAsync(new MailSendRequest(
            "127.0.0.1", 1, MailAuthKind.Basic, null, null,
            "destek@rapidsol.com.tr", null, "x@rapidsol.com.tr", "s", "b"));
        Assert.False(connect.Success);
        Assert.Equal("connect", connect.Stage);
    }

    // ---- helpers (EmailsAdminTests pattern) -------------------------------------------------

    private sealed record JobState(string State, string? Stage);

    private static async Task<JobState> PollAsync(HttpClient client, string job)
    {
        for (var i = 0; i < 100; i++)
        {
            var raw = await client.GetStringAsync($"/admin/email-diagnostic/status?id={job}");
            using var json = JsonDocument.Parse(raw);
            var state = json.RootElement.GetProperty("state").GetString()!;
            if (state != "pending")
            {
                return new JobState(state,
                    json.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null);
            }
            await Task.Delay(100);
        }
        Assert.Fail("diagnostic job never left pending");
        return null!;
    }

    private async Task<int> CreateSetAsync(HttpClient admin, string lang)
    {
        var (token, _) = await GetWithTokenAsync(admin, "/admin/templates");
        var landed = await PostFormAsync(admin, "/admin/templates/create", token,
            ("name", $"Set {Guid.NewGuid():N}"[..16]), ("cloneId", ""), ("lang", lang));
        return int.Parse(Regex.Match(landed.RequestMessage!.RequestUri!.Query, @"id=(\d+)").Groups[1].Value);
    }

    private async Task<int> FirstSetIdAsync()
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
        return (await db.EmailTemplateSets.OrderBy(s => s.Id).FirstAsync()).Id;
    }

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7TEMPLATESKEY2345";
        var username = $"s7tp{Guid.NewGuid():N}"[..14];
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

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static string ToastOf(string html) =>
        Decode(Regex.Match(html, "data-toast=\"([^\"]*)\"").Groups[1].Value);

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
