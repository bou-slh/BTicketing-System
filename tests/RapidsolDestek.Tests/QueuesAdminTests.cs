using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Admin.Controllers;
using RapidsolDestek.Web.Areas.Agent.Controllers;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/queues.html — the queue builder over the SHARED SavedQueue tree:
/// pure two-way mapping (builder rows ↔ flat criteria JSON incl. the seeded
/// descriptive keys and verbatim passthrough; sort rows ↔ QueueSortOption columns
/// consumed by TicketListEngine), the whole-page save round-trip (criteria/columns/
/// sort/conditions/export/quick filter), column drag order persistence, the live
/// preview executing UNSAVED state through the real QueueEngine, the agent CSV
/// export honoring the queue's export column set, and the delete guards
/// (IsSystem canon rows, queues with children).
/// </summary>
[Collection("Postgres")]
public class QueuesAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- pure mapping (no db) ------------------------------------------------------------

    [Fact]
    public void CriteriaMapping_RoundTrips_MockupFieldsAndVerbatimKeys()
    {
        // Builder rows → JSON: canonical "is" keys, op-suffixed descriptive keys,
        // seed-canon sla_remaining_lt / org_tag spelling, typed dept id.
        var json = QueuesController.BuildCriteriaJson(
            ["status", "dept", "slaRemaining", "orgTag", "assignee"],
            ["is", "is", "lt", "is", "is"],
            ["open", "3", "2h", "VIP", "me"]);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal("open", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("dept").GetInt32());
            Assert.Equal("2h", doc.RootElement.GetProperty("sla_remaining_lt").GetString());
            Assert.Equal("VIP", doc.RootElement.GetProperty("org_tag").GetString());
            Assert.Equal("me", doc.RootElement.GetProperty("assignee").GetString());
        }

        // The seeded SLA VIP criteria round-trips loss-free through rows and back.
        const string seeded = """{"state":"open","sla_remaining_lt":"2h","org_tag":"VIP"}""";
        var rows = QueuesController.ParseCriteriaRows(seeded);
        Assert.Collection(rows,
            r => { Assert.Equal("state", r.Field); Assert.Equal("is", r.Op); Assert.Equal("open", r.Value); },
            r => { Assert.Equal("slaRemaining", r.Field); Assert.Equal("lt", r.Op); Assert.Equal("2h", r.Value); },
            r => { Assert.Equal("orgTag", r.Field); Assert.Equal("is", r.Op); Assert.Equal("VIP", r.Value); });
        var rebuilt = QueuesController.BuildCriteriaJson(
            [.. rows.Select(r => r.Field)], [.. rows.Select(r => r.Op)], [.. rows.Select(r => r.Value)]);
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse(seeded).RootElement),
            JsonSerializer.Serialize(JsonDocument.Parse(rebuilt).RootElement));

        // Verbatim booleans keep their type ({"isanswered":false} — seeded tree).
        var boolRows = QueuesController.ParseCriteriaRows("""{"state":"open","isanswered":false}""");
        Assert.Equal("false", boolRows.Single(r => r.Field == "isanswered").Value);
        var boolJson = QueuesController.BuildCriteriaJson(
            [.. boolRows.Select(r => r.Field)], [.. boolRows.Select(r => r.Op)], [.. boolRows.Select(r => r.Value)]);
        using var boolDoc = JsonDocument.Parse(boolJson);
        Assert.Equal(JsonValueKind.False, boolDoc.RootElement.GetProperty("isanswered").ValueKind);
    }

    [Fact]
    public void SortMapping_RoundTrips_AndTicketListEngineConsumesIt()
    {
        // "Azalan" on updated = "-last_update_at"; priority flips onto the urgency
        // scale (Azalan = most urgent first = bare "priority__urgency").
        var json = QueuesController.BuildSortColumnsJson(
            ["updated", "priority"], ["desc", "desc"]);
        Assert.Equal("""["-last_update_at","priority__urgency"]""", json);

        var rows = QueuesController.ParseSortRows(json);
        Assert.Collection(rows,
            r => { Assert.Equal("updated", r.Field); Assert.True(r.Desc); },
            r => { Assert.Equal("priority", r.Field); Assert.True(r.Desc); });

        // The agent list's resolver consumes the builder's output (first mappable
        // entry wins) — and still understands the seeded literals.
        Assert.Equal(("updated", true), TicketListEngine.SortFromOptionColumns(json));
        Assert.Equal(("due", false), TicketListEngine.SortFromOptionColumns("""["estimated_due_date"]"""));
        Assert.Equal(("priority", true), TicketListEngine.SortFromOptionColumns("""["priority__urgency"]"""));
        Assert.Equal(("due", true),
            TicketListEngine.SortFromOptionColumns("""["-estimated_due_date","subject"]"""));
        Assert.Null(TicketListEngine.SortFromOptionColumns("""["status__key"]""")); // unmapped page key
        Assert.Null(TicketListEngine.SortFromOptionColumns("not-json"));
    }

    // ---- whole-page save round-trip ---------------------------------------------------------

    [Fact]
    public async Task Save_CreatesSharedQueue_AllTabsRoundTrip_AndReorderPersists()
    {
        var marker = $"S7 Kuyruk {Guid.NewGuid():N}"[..24];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/queues?id=0");

        int openId, bordroDept, colNo, colSubject, colSla;
        using (var s = new ServiceScopeBundle(fixture))
        {
            openId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == "Açık" && x.StaffId == null)).Id;
            bordroDept = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
            colNo = (await s.Db.QueueColumns.FirstAsync(c => c.PrimaryPath == "number")).Id;
            colSubject = (await s.Db.QueueColumns.FirstAsync(c => c.PrimaryPath == "subject")).Id;
            colSla = (await s.Db.QueueColumns.FirstAsync(c => c.PrimaryPath == "estimated_due_date")).Id;
        }

        var landed = await PostFormAsync(client, "/admin/queues", token,
        [
            ("name", marker), ("parentId", openId.ToString()), ("quickFilter", "priority"),
            ("cfF", "dept"), ("cfO", "is"), ("cfV", bordroDept.ToString()),
            ("cfF", "orgTag"), ("cfO", "contains"), ("cfV", "VIP"),
            ("colIds", colNo.ToString()), ("colHeads", "Talep No"), ("colWidths", "100"),
            ("colIds", colSubject.ToString()), ("colHeads", "Konu Başlığı"), ("colWidths", "auto"),
            ("colIds", colSla.ToString()), ("colHeads", "SLA Kalan"), ("colWidths", "110"),
            ("sortFields", "slaRemaining"), ("sortDirs", "asc"),
            ("sortFields", "priority"), ("sortDirs", "desc"),
            ("condFields", "slaRemaining"), ("condOps", "lt"), ("condVals", "2 saat"), ("condActs", "highlight-red"),
            ("exportFields", "number"), ("exportFields", "subject"), ("exportFields", "estimated_due_date"),
        ]);
        Assert.Matches(@"^/admin/queues$", PathOf(landed));

        int queueId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var q = await s.Db.SavedQueues
                .Include(x => x.Columns).ThenInclude(c => c.Column)
                .Include(x => x.Sorts).ThenInclude(x => x.SortOption)
                .Include(x => x.ExportFields)
                .SingleAsync(x => x.Title == marker);
            queueId = q.Id;

            Assert.Null(q.StaffId);              // shared, appears in every agent's tree
            Assert.False(q.IsSystem);            // builder rows are Custom
            Assert.Equal(openId, q.ParentId);
            Assert.EndsWith($"/{q.Id}/", q.Path);
            Assert.StartsWith($"/{openId}/", q.Path);
            Assert.Equal("priority", q.QuickFilter);
            Assert.False(q.InheritColumns);

            using var crit = JsonDocument.Parse(q.Criteria!);
            Assert.Equal(bordroDept, crit.RootElement.GetProperty("dept").GetInt32());
            Assert.Equal("VIP", crit.RootElement.GetProperty("org_tag_contains").GetString());

            Assert.Collection(q.Columns.OrderBy(c => c.Sort),
                c => { Assert.Equal(colNo, c.ColumnId); Assert.Equal(100, c.Width); },
                c => { Assert.Equal(colSubject, c.ColumnId); Assert.Equal("Konu Başlığı", c.Heading); Assert.Equal(0, c.Width); },
                c => { Assert.Equal(colSla, c.ColumnId); Assert.Equal(110, c.Width); });

            var sort = Assert.Single(q.Sorts);
            Assert.True(sort.IsDefault);
            Assert.Equal("""["estimated_due_date","priority__urgency"]""", sort.SortOption!.Columns);

            Assert.Collection(q.ExportFields.OrderBy(f => f.Sort),
                f => { Assert.Equal("number", f.FieldPath); Assert.Equal("Talep No", f.Heading); },
                f => Assert.Equal("subject", f.FieldPath),
                f => Assert.Equal("estimated_due_date", f.FieldPath));

            using var conds = JsonDocument.Parse(q.Conditions!);
            var cond = conds.RootElement.EnumerateArray().Single();
            Assert.Equal("slaRemaining", cond.GetProperty("field").GetString());
            Assert.Equal("highlight-red", cond.GetProperty("action").GetString());
        }

        // ---- Edit round-trip: the builder page prefills, drag order persists. ----
        // (Razor's default encoder writes non-ASCII as numeric entities — decode.)
        var (editToken, editHtmlRaw) = await GetWithTokenAsync(client, $"/admin/queues?id={queueId}");
        var editHtml = System.Net.WebUtility.HtmlDecode(editHtmlRaw);
        Assert.Contains("Konu Başlığı", editHtml); // column heading override prefilled
        Assert.Contains("value=\"VIP\"", editHtml); // orgTag criteria row prefilled (hidden cfV)

        var resave = await PostFormAsync(client, "/admin/queues", editToken,
        [
            ("id", queueId.ToString()),
            ("name", marker), ("parentId", openId.ToString()),
            ("cfF", "dept"), ("cfO", "is"), ("cfV", bordroDept.ToString()),
            // Drag result: SLA first, subject dropped.
            ("colIds", colSla.ToString()), ("colHeads", "SLA Kalan"), ("colWidths", "110"),
            ("colIds", colNo.ToString()), ("colHeads", "Talep No"), ("colWidths", "90"),
            ("inheritSort", "on"),
            ("exportFields", "number"),
        ]);
        Assert.Matches(@"^/admin/queues$", PathOf(resave));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var q = await s.Db.SavedQueues
                .Include(x => x.Columns).Include(x => x.Sorts).Include(x => x.ExportFields)
                .SingleAsync(x => x.Id == queueId);
            Assert.Collection(q.Columns.OrderBy(c => c.Sort),
                c => { Assert.Equal(colSla, c.ColumnId); Assert.Equal(1, c.Sort); },
                c => { Assert.Equal(colNo, c.ColumnId); Assert.Equal(90, c.Width); });
            Assert.Empty(q.Sorts);               // inherit switch = no own rows
            Assert.Single(q.ExportFields);
            using var crit = JsonDocument.Parse(q.Criteria!);
            Assert.False(crit.RootElement.TryGetProperty("org_tag_contains", out _)); // removed row gone
        }
    }

    [Fact]
    public async Task Save_Validation_RefusesBadNameParentAndDupColumns()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/queues?id=0");

        int before;
        int openId, colNo;
        using (var s = new ServiceScopeBundle(fixture))
        {
            before = await s.Db.SavedQueues.CountAsync();
            openId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == "Açık" && x.StaffId == null)).Id;
            colNo = (await s.Db.QueueColumns.FirstAsync(c => c.PrimaryPath == "number")).Id;
        }

        // Empty name.
        await PostFormAsync(client, "/admin/queues", token, [("name", " "), ("parentId", "0")]);
        // Duplicate column rows collide with the composite PK.
        await PostFormAsync(client, "/admin/queues", token,
        [
            ("name", $"dup {Guid.NewGuid():N}"[..20]), ("parentId", "0"),
            ("colIds", colNo.ToString()), ("colHeads", "A"), ("colWidths", "100"),
            ("colIds", colNo.ToString()), ("colHeads", "B"), ("colWidths", "100"),
        ]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.Equal(before, await s.Db.SavedQueues.CountAsync()); // nothing written (B3)
        }

        // Cycle guard: "Açık" cannot move under its own child "Yanıtlanmamış".
        var childId = 0;
        using (var s = new ServiceScopeBundle(fixture))
        {
            childId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == "Yanıtlanmamış")).Id;
        }
        await PostFormAsync(client, "/admin/queues", token,
            [("id", openId.ToString()), ("name", "Açık"), ("parentId", childId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            var open = await s.Db.SavedQueues.SingleAsync(x => x.Id == openId);
            Assert.Null(open.ParentId); // unchanged
        }

        // Personal queues are not editable through the admin builder.
        int personalId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            personalId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == "SLA Riskli VIP")).Id;
        }
        var personal = await client.GetAsync($"/admin/queues?id={personalId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, personal.StatusCode);
    }

    // ---- live preview -------------------------------------------------------------------------

    [Fact]
    public async Task Preview_ExecutesUnsavedState_ThroughTheRealEngine()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/queues?id=0");

        int bordroDept;
        using (var s = new ServiceScopeBundle(fixture))
        {
            bordroDept = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
        }

        // Unsaved criteria: dept = Bordro → the hero R716555 lists.
        var hit = await PostFormAsync(client, "/admin/queues/preview", token,
            [("cfF", "dept"), ("cfO", "is"), ("cfV", bordroDept.ToString())]);
        hit.EnsureSuccessStatusCode();
        var html = System.Net.WebUtility.HtmlDecode(await hit.Content.ReadAsStringAsync());
        Assert.Contains("R716555", html);
        Assert.Contains("Yol ücreti hatası hakkında", html);

        // Impossible criteria → the localized empty state, still HTTP 200.
        var miss = await PostFormAsync(client, "/admin/queues/preview", token,
            [("cfF", "dept"), ("cfO", "is"), ("cfV", "999999")]);
        miss.EnsureSuccessStatusCode();
        Assert.Contains("Kriterlere uyan talep yok",
            System.Net.WebUtility.HtmlDecode(await miss.Content.ReadAsStringAsync()));

        // Posted column rows drive the preview table headings.
        int colNo;
        using (var s = new ServiceScopeBundle(fixture))
        {
            colNo = (await s.Db.QueueColumns.FirstAsync(c => c.PrimaryPath == "number")).Id;
        }
        var custom = await PostFormAsync(client, "/admin/queues/preview", token,
        [
            ("cfF", "dept"), ("cfO", "is"), ("cfV", bordroDept.ToString()),
            ("colIds", colNo.ToString()), ("colHeads", "Özel Başlık X"),
        ]);
        Assert.Contains("Özel Başlık X",
            System.Net.WebUtility.HtmlDecode(await custom.Content.ReadAsStringAsync()));
    }

    // ---- agent CSV export honors the queue's export set -----------------------------------------

    [Fact]
    public async Task AgentExport_HonorsTheQueueExportColumnSet()
    {
        var marker = $"S7 Export {Guid.NewGuid():N}"[..22];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/queues?id=0");

        int bordroDept;
        using (var s = new ServiceScopeBundle(fixture))
        {
            bordroDept = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
        }
        await PostFormAsync(client, "/admin/queues", token,
        [
            ("name", marker), ("parentId", "0"),
            ("cfF", "dept"), ("cfO", "is"), ("cfV", bordroDept.ToString()),
            ("exportFields", "number"), ("exportFields", "dept__name"), ("exportFields", "created_at"),
        ]);

        int queueId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            queueId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == marker)).Id;
        }

        var csv = await client.GetStringAsync($"/agent/tickets/export?queue={queueId}");
        var lines = csv.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Header = the configured set's canonical headings, in catalog order.
        Assert.Equal("Talep No,Departman,Oluşturulma", lines[0].TrimEnd('\r'));
        Assert.Contains(lines, l => l.StartsWith("R716555,Bordro,", StringComparison.Ordinal));
    }

    // ---- delete guards -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_GuardsSystemAndParents_DeletesCustomRows()
    {
        var client = await AdminClientAsync();

        int openId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            openId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == "Açık" && x.StaffId == null)).Id;
        }

        // System root refused (IsSystem guard fires before the children guard).
        var (token, _) = await GetWithTokenAsync(client, $"/admin/queues?id={openId}");
        await PostFormAsync(client, "/admin/queues/delete", token, [("id", openId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.SavedQueues.AnyAsync(x => x.Id == openId));
        }

        // Custom parent with a child: refused until the child is gone, then deletable.
        var name = $"S7 Sil {Guid.NewGuid():N}"[..20];
        await PostFormAsync(client, "/admin/queues", token, [("name", name), ("parentId", "0")]);
        int parentId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            parentId = (await s.Db.SavedQueues.SingleAsync(x => x.Title == name)).Id;
        }
        await PostFormAsync(client, "/admin/queues", token,
            [("name", $"{name} alt"), ("parentId", parentId.ToString())]);
        int childId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var child = await s.Db.SavedQueues.SingleAsync(x => x.Title == $"{name} alt");
            childId = child.Id;
            Assert.StartsWith($"/{parentId}/", child.Path); // materialized path under the parent
        }

        await PostFormAsync(client, "/admin/queues/delete", token, [("id", parentId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.SavedQueues.AnyAsync(x => x.Id == parentId)); // children guard
        }

        await PostFormAsync(client, "/admin/queues/delete", token, [("id", childId.ToString())]);
        await PostFormAsync(client, "/admin/queues/delete", token, [("id", parentId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.SavedQueues.AnyAsync(x => x.Id == parentId || x.Id == childId));
        }
    }

    // ---- helpers (FiltersAdminTests twins) ---------------------------------------------------

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7QUEUESKEY2345ABC";
        var username = $"s7qb{Guid.NewGuid():N}"[..14];
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
            [("User", username), ("Password", Password)]);
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, [("Code", ComputeTotp(totpKey))]);
        Assert.Equal("/admin/dashboard", PathOf(landed));
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (row arrays).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

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
