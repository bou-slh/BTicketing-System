using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/settings-tasks.html + settings-kb.html: settings round-trips through the
/// admin controllers, the LIVE task numbering consumer (TaskService draws random /
/// sequential numbers per the admin flip), the dlg-seq HTTP CRUD, and the KB master
/// switch gating the portal KB end-to-end (routes 404, nav + home card disappear).
/// </summary>
[Collection("Postgres")]
public class SettingsTasksKbTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA (SettingsTicketsTests pattern).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7TASKSKBKEY234567";
        var username = $"s7tkb{Guid.NewGuid():N}"[..14];
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

            // The dlg-seq endpoint resolves the acting Staff row from the principal
            // (ResolveStaffAsync) — give the fresh identity a matching domain row.
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

    /// <summary>Seeded portal customer over HTTP (portal /login form — no 2FA).</summary>
    private async Task<HttpClient> PortalClientAsync()
    {
        var client = fixture.Factory.CreateClient();
        var (token, _) = await GetWithTokenAsync(client, "/login");
        var landed = await PostFormAsync(client, "/login", token,
            ("User", "bourla.salehi@ulasim.com.tr"), ("Password", Password));
        Assert.Equal("/tickets", landed.RequestMessage!.RequestUri!.AbsolutePath);
        return client;
    }

    /// <summary>Seeded agent over HTTP (no 2FA — AgentProfileTests pattern).</summary>
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

    // List-based (not dictionary) so the dlg-seq arrays can repeat field names.
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    /// <summary>The full settings-tasks main-form payload with seed-default values.</summary>
    private static List<(string, string)> TasksForm(params (string Key, string Value)[] overrides)
    {
        var form = new List<(string, string)>
        {
            ("number_format", "T-####"), ("number_mode", "sequential"),
            ("default_priority", "normal"),
            ("tal_new", "true"), ("tal_new_admin", "true"), ("tal_new_dept_manager", "true"),
            ("tal_activity", "true"), ("tal_activity_last_respondent", "true"), ("tal_activity_assigned", "true"),
            ("tal_assignment", "true"), ("tal_assignment_assigned", "true"), ("tal_assignment_team_lead", "true"),
            ("tal_transfer", "true"), ("tal_transfer_assigned", "true"), ("tal_transfer_dept_manager", "true"),
            ("tal_overdue", "true"), ("tal_overdue_assigned", "true"), ("tal_overdue_dept_manager", "true"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            if (value != "")
                form.Add((key, value));
        }
        return form;
    }

    /// <summary>The settings-kb payload; all three switches default to the mockup/seed state.</summary>
    private static List<(string, string)> KbForm(params (string Key, string Value)[] overrides)
    {
        var form = new List<(string, string)>
        {
            ("enable_kb", "true"), ("enable_canned", "true"),
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

    /// <summary>Creates a task the TaskServiceTests way (dkaya in Destek).</summary>
    private static async Task<TaskItem> CreateTaskAsync(ServiceScopeBundle s)
    {
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        var destek = await s.Db.Departments.SingleAsync(d => d.Name == "Destek");
        return await s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
        {
            Title = $"Ayar testi görevi {Guid.NewGuid():N}",
            DepartmentId = destek.Id,
        }, dkaya);
    }

    // ---- settings-tasks --------------------------------------------------------------

    [Fact]
    public async Task TasksPage_SaveRoundTrips_AndRerendersPersistedState()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-tasks");
        Assert.Contains("Görev Ayarları", html); // TR default culture
        Assert.Contains("tal_new", html);

        try
        {
            var form = TasksForm(
                ("number_format", "G####"), ("number_mode", "random"),
                ("default_priority", "high"), ("tal_overdue", ""));
            var saved = await PostFormAsync(client, "/admin/settings-tasks", token, form.ToArray());
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal("/admin/settings-tasks", saved.RequestMessage!.RequestUri!.AbsolutePath);

            // Typed accessors see the writes…
            using (var s = new ServiceScopeBundle(fixture))
            {
                var settings = s.Get<ISettingsService>();
                Assert.Equal("G####", (await settings.GetTaskNumberingAsync()).NumberFormat);
                var tasks = await settings.GetTasksAsync();
                Assert.Equal("random", tasks.NumberMode);
                Assert.Equal("high", tasks.DefaultPriorityKey);
            }
            Assert.Equal("False", await ReadSettingAsync("alerts", "task_overdue"));
            Assert.Equal("True", await ReadSettingAsync("alerts", "task_assignment"));

            // …and the re-rendered page carries the persisted state back into the form.
            var (_, html2) = await GetWithTokenAsync(client, "/admin/settings-tasks");
            Assert.Contains("value=\"G####\"", html2);
            Assert.Matches(new Regex("value=\"random\"\\s+selected"), html2);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");
            await PostFormAsync(client, "/admin/settings-tasks", token2, TasksForm().ToArray());
        }
    }

    [Fact]
    public async Task TasksPage_InvalidNumberFormat_SavesNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");

        var before = (await ReadSettingAsync("tasks", "number_format")) ?? "T-####";
        var form = TasksForm(("number_format", "HATALI"), ("default_priority", "high"));
        await PostFormAsync(client, "/admin/settings-tasks", token, form.ToArray());

        Assert.Equal(before, (await ReadSettingAsync("tasks", "number_format")) ?? "T-####");
        Assert.NotEqual("high", await ReadSettingAsync("tasks", "default_priority"));
    }

    [Fact]
    public async Task TasksNumbering_AdminFlip_DrivesTaskServiceEndToEnd()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");

        try
        {
            // Admin flips tasks to random G#### through the real form POST →
            // TaskService draws unguessable digits and leaves the counter alone.
            await PostFormAsync(client, "/admin/settings-tasks", token,
                TasksForm(("number_format", "G####"), ("number_mode", "random")).ToArray());

            int seqId;
            using (var s = new ServiceScopeBundle(fixture))
            {
                seqId = int.Parse((await s.Get<ISettingsService>().GetAsync("tasks", "sequence_id"))!);
                var nextBefore = (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next;
                var task = await CreateTaskAsync(s);
                Assert.Matches(new Regex("^G\\d{4}$"), task.Number);
                Assert.Equal(nextBefore, (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next);
            }

            // Back to the seeded sequential mode through the page → the counter advances.
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");
            await PostFormAsync(client, "/admin/settings-tasks", token2, TasksForm().ToArray());

            using (var s = new ServiceScopeBundle(fixture))
            {
                var nextBefore = (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next;
                var task = await CreateTaskAsync(s);
                Assert.Matches(new Regex("^T-\\d{4,}$"), task.Number);
                Assert.Equal(nextBefore + 1, (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next);
            }
        }
        finally
        {
            var (token3, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");
            await PostFormAsync(client, "/admin/settings-tasks", token3, TasksForm().ToArray());
        }
    }

    [Fact]
    public async Task TasksSequenceDialog_HttpCrud_AddsAndRemovesARow()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");

        List<(string, string)> RowFields(IEnumerable<(int Id, string Name, long Next)> rows) =>
            rows.SelectMany(r => new[]
            {
                ("seqId", r.Id.ToString()), ("seqName", r.Name), ("seqNext", r.Next.ToString()),
            }).ToList();

        async Task<List<(int Id, string Name, long Next)>> CurrentAsync()
        {
            using var s = new ServiceScopeBundle(fixture);
            return (await s.Db.Sequences.AsNoTracking().OrderBy(x => x.Id).ToListAsync())
                .Select(x => (x.Id, x.Name, x.Next)).ToList();
        }

        var name = $"Görev Test {Guid.NewGuid():N}"[..20];
        var withNew = (await CurrentAsync()).Append((0, name, 500L));
        var saved = await PostFormAsync(client, "/admin/settings-tasks/sequences", token, RowFields(withNew).ToArray());
        Assert.Equal("/admin/settings-tasks", saved.RequestMessage!.RequestUri!.AbsolutePath);

        using (var s = new ServiceScopeBundle(fixture))
        {
            var created = await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Name == name);
            Assert.Equal(500, created.Next);
        }

        // Second round-trip without the row deletes it (the ✕ + save semantics).
        var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-tasks");
        var withoutNew = (await CurrentAsync()).Where(r => r.Name != name);
        await PostFormAsync(client, "/admin/settings-tasks/sequences", token2, RowFields(withoutNew).ToArray());

        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.Sequences.AnyAsync(x => x.Name == name));
        }
    }

    // ---- settings-kb -----------------------------------------------------------------

    [Fact]
    public async Task KbPage_SaveRoundTrips()
    {
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-kb");
        Assert.Contains("Bilgi Bankası Ayarları", html); // TR default culture
        Assert.Contains("enable_kb", html);

        try
        {
            await PostFormAsync(client, "/admin/settings-kb", token,
                KbForm(("require_login", "true"), ("enable_canned", "")).ToArray());

            using var s = new ServiceScopeBundle(fixture);
            var kb = await s.Get<ISettingsService>().GetKbAsync();
            Assert.True(kb.EnableKb);
            Assert.True(kb.RequireLogin);
            Assert.False(kb.EnableCanned);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-kb");
            await PostFormAsync(client, "/admin/settings-kb", token2, KbForm().ToArray());
        }
    }

    [Fact]
    public async Task KbMasterSwitch_HidesPortalKbAndNav_EndToEnd()
    {
        var admin = await AdminClientAsync();
        var portal = await PortalClientAsync();

        int articleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            articleId = await s.Db.FaqArticles
                .Where(a => a.IsPublished && a.Category!.IsPublic)
                .Select(a => a.Id).FirstAsync();
        }

        // Enabled (default): routes serve, the nav + home card link to /kb.
        Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync("/kb")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync($"/kb-article?id={articleId}")).StatusCode);
        Assert.Contains("href=\"/kb\"", await portal.GetStringAsync("/"));

        try
        {
            // Admin flips the master switch OFF through the real form POST.
            var (token, _) = await GetWithTokenAsync(admin, "/admin/settings-kb");
            await PostFormAsync(admin, "/admin/settings-kb", token, KbForm(("enable_kb", "")).ToArray());

            // Portal KB routes hide honestly (404) and every KB surface disappears.
            Assert.Equal(HttpStatusCode.NotFound, (await portal.GetAsync("/kb")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await portal.GetAsync($"/kb-article?id={articleId}")).StatusCode);
            Assert.DoesNotContain("href=\"/kb\"", await portal.GetStringAsync("/"));

            // Flip back ON through the page → the KB comes back.
            var (token2, _) = await GetWithTokenAsync(admin, "/admin/settings-kb");
            await PostFormAsync(admin, "/admin/settings-kb", token2, KbForm().ToArray());
            Assert.Equal(HttpStatusCode.OK, (await portal.GetAsync("/kb")).StatusCode);
            Assert.Contains("href=\"/kb\"", await portal.GetStringAsync("/"));
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture, "kb", "enable_kb", "true");
        }
    }

    [Fact]
    public async Task CannedSwitch_HidesComposerMenu_AndRefusesInserts()
    {
        // A ticket for uakin's composer (SettingsTicketsTests helper shape).
        int ticketId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            ticketId = (await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Hazır yanıt testi {Guid.NewGuid():N}",
                Body = "<p>Test içeriği</p>",
            }, owner)).Id;
        }

        // dkaya: plain agent (no 2FA) whose department Destek is the default-dept
        // cascade target of the topic-less ticket above.
        var agent = await AgentClientAsync("dkaya");

        // Default (on): the composer offers the canned menu.
        Assert.Contains("data-canned-insert", await agent.GetStringAsync($"/agent/ticket-view?id={ticketId}"));

        await using (await SettingOverride.SetAsync(fixture, "kb", "enable_canned", "false"))
        {
            // Off: the menu disappears and canned-id inserts refuse.
            Assert.DoesNotContain("data-canned-insert", await agent.GetStringAsync($"/agent/ticket-view?id={ticketId}"));
            int cannedId;
            using (var s = new ServiceScopeBundle(fixture))
            {
                cannedId = await s.Db.CannedResponses.Where(c => c.IsEnabled).Select(c => c.Id).FirstAsync();
            }
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.GetAsync($"/agent/ticket-view/canned?id={ticketId}&what={cannedId}")).StatusCode);
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
