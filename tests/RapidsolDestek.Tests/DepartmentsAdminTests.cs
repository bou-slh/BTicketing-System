using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/departments.html + department-edit.html: the B1 list over the REAL
/// department hierarchy (tree order + indent, search flattens), CRUD with the
/// materialized-path integrity guards (no self/cycle parent), the Erişim tab's
/// member rows against Staff/StaffDepartmentAccess — proving an extended-access row
/// grants the department-scoped permission through the PermissionService on the
/// next request — the department-level autoresponder/claim/reopen flags (honest
/// wiring into ThreadService/TicketService), the bulk delete guard, and the member
/// CSV export.
/// </summary>
[Collection("Postgres")]
public class DepartmentsAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // Test departments are named "A7 …" so the list's default descending Turkish
    // collation keeps the seed canon rows (Destek/Bordro/Danışmanlık) on page 1.
    private static string UniqueName(string stem) => $"A7 {stem} {Guid.NewGuid():N}"[..28];

    // ---- helpers (RolesTeamsAdminTests twins) ---------------------------------------------

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7DEPTSKEY234567AB";
        var username = $"s7dp{Guid.NewGuid():N}"[..14];
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

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (ids/memberIds/memberRoles/alertIds groups).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    private async Task<Department> CreateDeptAsync(
        string stem, int? parentId = null, Action<Department>? mutate = null)
    {
        using var s = new ServiceScopeBundle(fixture);
        var parentPath = parentId is null
            ? "/"
            : await s.Db.Departments.Where(d => d.Id == parentId).Select(d => d.Path).SingleAsync();
        var dept = new Department { Name = UniqueName(stem), ParentId = parentId };
        mutate?.Invoke(dept);
        s.Db.Departments.Add(dept);
        await s.Db.SaveChangesAsync();
        dept.Path = $"{parentPath}{dept.Id}/";
        await s.Db.SaveChangesAsync();
        return dept;
    }

    private async Task<Department?> LoadDeptAsync(int id)
    {
        using var s = new ServiceScopeBundle(fixture);
        return await s.Db.Departments.SingleOrDefaultAsync(d => d.Id == id);
    }

    // ---- departments.html (B1 list over the tree) -------------------------------------------

    [Fact]
    public async Task DepartmentsList_RendersTreeIndent_AndSearchFlattens()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/departments"));

        // Seed canon: Bordro indents under Destek (└ prefix), Destek gets a collapse
        // caret carrying its materialized path; rows carry data-tree-path.
        Assert.Contains("Destek", html);
        Assert.Contains("class=\"rd-muted\">└</span>", html);
        int destekId, bordroId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            destekId = await s.Db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync();
            bordroId = await s.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
        }
        Assert.Contains($"data-tree-toggle=\"/{destekId}/\"", html);
        Assert.Contains($"data-tree-path=\"/{destekId}/{bordroId}/\"", html);
        // Outgoing email + manager columns come from the real refs.
        Assert.Contains("bordro@rapidsol.com.tr", html);
        Assert.Contains("Merve Çetin", html);

        // Search flattens: the match renders without tree chrome, non-matches drop.
        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/departments?q=Bordro"));
        Assert.Contains("Bordro", filtered);
        Assert.DoesNotContain("Danışmanlık", filtered);
        Assert.DoesNotContain("data-tree-toggle", filtered);
        Assert.DoesNotContain("class=\"rd-muted\">└</span>", filtered);
    }

    // ---- department-edit.html (B3 form + hierarchy integrity) -------------------------------

    [Fact]
    public async Task DepartmentCreate_MaterializesPath_AndFieldsRoundTrip()
    {
        int destekId;
        string destekPath;
        int managerId, emailId, templateSetId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var destek = await s.Db.Departments.SingleAsync(d => d.Name == "Destek");
            (destekId, destekPath) = (destek.Id, destek.Path);
            managerId = await s.Db.Staff.Where(x => x.Username == "mcetin").Select(x => x.Id).SingleAsync();
            emailId = await s.Db.EmailAccounts.Select(e => e.Id).OrderBy(id => id).FirstAsync();
            templateSetId = await s.Db.EmailTemplateSets.Select(t => t.Id).OrderBy(id => id).FirstAsync();
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/department-edit");
        var name = UniqueName("Saha");

        var created = await PostFormAsync(client, "/admin/department-edit", token,
            ("parentId", destekId.ToString()), ("name", name),
            ("status", "disabled"), ("isPublic", "false"),
            ("managerId", managerId.ToString()), ("assignMode", "primary"),
            ("disableClaim", "true"), ("disableReopenAssign", "true"),
            ("emailId", emailId.ToString()), ("templateSetId", templateSetId.ToString()),
            ("arDisableNew", "true"), // arDisableMsg absent → MessageAutoResponse stays ON
            ("arEmailId", emailId.ToString()),
            ("alertGroup", "manager"), ("signature", "A7 imza"));
        Assert.Equal("/admin/departments", PathOf(created));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var dept = await s.Db.Departments.SingleAsync(d => d.Name == name);
            Assert.Equal(destekId, dept.ParentId);
            Assert.Equal($"{destekPath}{dept.Id}/", dept.Path); // osTicket pid/path semantics
            Assert.False(dept.IsActive);
            Assert.False(dept.IsArchived);
            Assert.False(dept.IsPublic);
            Assert.Equal(managerId, dept.ManagerStaffId);
            Assert.False(dept.AssignMembersOnly);
            Assert.True(dept.AssignPrimaryOnly);
            Assert.True(dept.DisableAutoClaim);
            Assert.True(dept.DisableReopenAutoAssign);
            Assert.Equal(emailId, dept.EmailAccountId);
            Assert.Equal(templateSetId, dept.TemplateSetId);
            Assert.False(dept.TicketAutoResponse);   // "disable" switch inverts the flag
            Assert.True(dept.MessageAutoResponse);   // unchecked disable switch = AR on
            Assert.Equal(emailId, dept.AutoResponseEmailAccountId);
            Assert.Equal(DepartmentAlertGroup.ManagerOnly, dept.AlertGroup);
            Assert.Equal("A7 imza", dept.Signature);
        }
    }

    [Fact]
    public async Task DepartmentSave_RefusesSelfAndDescendantParent_AndDuplicateName()
    {
        var parent = await CreateDeptAsync("Üst");
        var child = await CreateDeptAsync("Alt", parent.Id);

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, $"/admin/department-edit?id={parent.Id}");

        // Cycle: the parent cannot move under its own child.
        var cycle = await PostFormAsync(client, "/admin/department-edit", token,
            ("id", parent.Id.ToString()), ("name", parent.Name),
            ("parentId", child.Id.ToString()), ("status", "active"), ("isPublic", "true"));
        Assert.Equal("/admin/department-edit", PathOf(cycle));
        Assert.Null((await LoadDeptAsync(parent.Id))!.ParentId); // nothing written (B3)

        // Self-parent refused.
        var self = await PostFormAsync(client, "/admin/department-edit", token,
            ("id", parent.Id.ToString()), ("name", parent.Name),
            ("parentId", parent.Id.ToString()), ("status", "active"), ("isPublic", "true"));
        Assert.Equal("/admin/department-edit", PathOf(self));
        Assert.Null((await LoadDeptAsync(parent.Id))!.ParentId);

        // Duplicate name under the same parent refused (unique per Name+ParentId).
        var dup = await PostFormAsync(client, "/admin/department-edit", token,
            ("id", child.Id.ToString()), ("name", UniqueName("Kopya")),
            ("parentId", parent.Id.ToString()), ("status", "active"), ("isPublic", "true"));
        Assert.Equal("/admin/departments", PathOf(dup)); // control save lands
        var childName = (await LoadDeptAsync(child.Id))!.Name;
        var dup2 = await PostFormAsync(client, "/admin/department-edit", token,
            ("name", childName), ("parentId", parent.Id.ToString()),
            ("status", "active"), ("isPublic", "true"));
        Assert.Equal("/admin/department-edit", PathOf(dup2));

        // Reparent moves the whole subtree's materialized paths.
        var other = await CreateDeptAsync("Yeni Üst");
        var moved = await PostFormAsync(client, "/admin/department-edit", token,
            ("id", parent.Id.ToString()), ("name", parent.Name),
            ("parentId", other.Id.ToString()), ("status", "active"), ("isPublic", "true"));
        Assert.Equal("/admin/departments", PathOf(moved));
        var movedParent = await LoadDeptAsync(parent.Id);
        var movedChild = await LoadDeptAsync(child.Id);
        Assert.Equal($"{other.Path}{parent.Id}/", movedParent!.Path);
        Assert.Equal($"{other.Path}{parent.Id}/{child.Id}/", movedChild!.Path);
    }

    [Fact]
    public async Task EditorPrefills_FromSeedCanon_InclMemberRows()
    {
        int bordroId, dkayaId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            bordroId = await s.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
            dkayaId = await s.Db.Staff.Where(x => x.Username == "dkaya").Select(x => x.Id).SingleAsync();
        }

        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/department-edit?id={bordroId}"));

        Assert.Contains("Destek / Bordro", html);                       // ancestor-chain h1
        var members = Regex.Match(html, "value=\"members\"[^>]*>").Value;
        Assert.Contains("selected", members);                           // AssignMembersOnly canon
        var arMsg = Regex.Match(html, "<input[^>]*name=\"arDisableMsg\"[^>]*>").Value;
        Assert.Contains("checked", arMsg);                              // MessageAutoResponse off
        Assert.Contains("Birincil", html);                              // uakin primary badge
        // dkaya = extended access with alerts OFF (seed canon: unchecked box).
        Assert.Contains($"data-roster-staff=\"{dkayaId}\"", html);
        var alertBox = Regex.Match(html,
            $"<input type=\"checkbox\" name=\"alertIds\" value=\"{dkayaId}\"[^>]*>").Value;
        Assert.NotEqual("", alertBox);
        Assert.DoesNotContain("checked", alertBox);
    }

    // ---- Erişim rows drive the PermissionService (role × department) -------------------------

    [Fact]
    public async Task AccessRow_GrantsDeptScopedPermission_OnNextRequest()
    {
        var dept = await CreateDeptAsync("Erişim");
        int staffId, primaryDeptId, roleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var role = new Role
            {
                Name = UniqueName("Rol"),
                Permissions = [PermissionKeys.TicketReply],
            };
            var noPerm = new Role { Name = UniqueName("Boş Rol") };
            s.Db.Roles.AddRange(role, noPerm);
            primaryDeptId = await s.Db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync();
            var staff = new Staff
            {
                Username = $"a7dp{Guid.NewGuid():N}"[..12],
                FirstName = "A7",
                LastName = "Erişim",
                DepartmentId = primaryDeptId,
                Role = noPerm,
                IsVisible = false,
            };
            s.Db.Staff.Add(staff);
            await s.Db.SaveChangesAsync();
            (staffId, roleId) = (staff.Id, role.Id);
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, $"/admin/department-edit?id={dept.Id}");

        // Add the extended-access row over HTTP (the whole-page save carries it).
        var saved = await PostFormAsync(client, "/admin/department-edit", token,
            ("id", dept.Id.ToString()), ("name", dept.Name),
            ("status", "active"), ("isPublic", "true"),
            ("memberIds", staffId.ToString()), ("memberRoles", roleId.ToString()),
            ("alertIds", staffId.ToString()));
        Assert.Equal("/admin/departments", PathOf(saved));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var row = await s.Db.Set<StaffDepartmentAccess>()
                .SingleAsync(a => a.StaffId == staffId && a.DepartmentId == dept.Id);
            Assert.Equal(roleId, row.RoleId);
            Assert.True(row.AlertsEnabled);

            // The dept-scoped grant resolves on the next request; the primary
            // department's empty role still denies (role × department).
            var perms = await s.Get<IPermissionService>().ResolveAsync(staffId);
            Assert.True(perms.Can(PermissionKeys.TicketReply, dept.Id));
            Assert.False(perms.Can(PermissionKeys.TicketReply, primaryDeptId));
        }

        // Saving without the row removes the access — and revokes the grant.
        await PostFormAsync(client, "/admin/department-edit", token,
            ("id", dept.Id.ToString()), ("name", dept.Name),
            ("status", "active"), ("isPublic", "true"));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.Set<StaffDepartmentAccess>()
                .AnyAsync(a => a.StaffId == staffId && a.DepartmentId == dept.Id));
            var perms = await s.Get<IPermissionService>().ResolveAsync(staffId);
            Assert.False(perms.Can(PermissionKeys.TicketReply, dept.Id));
        }
    }

    [Fact]
    public async Task PrimaryMember_RoleAndAlertsEditable_ButRowNotRemovable()
    {
        var dept = await CreateDeptAsync("Birincil");
        int staffId, roleAId, roleBId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var roleA = new Role { Name = UniqueName("Rol A") };
            var roleB = new Role { Name = UniqueName("Rol B") };
            s.Db.Roles.AddRange(roleA, roleB);
            var staff = new Staff
            {
                Username = $"a7pm{Guid.NewGuid():N}"[..12],
                FirstName = "A7",
                LastName = "Birincil",
                DepartmentId = dept.Id,
                Role = roleA,
                IsVisible = false,
            };
            s.Db.Staff.Add(staff);
            await s.Db.SaveChangesAsync();
            (staffId, roleAId, roleBId) = (staff.Id, roleA.Id, roleB.Id);
        }

        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, $"/admin/department-edit?id={dept.Id}");
        Assert.Contains("Birincil", WebUtility.HtmlDecode(html)); // badge renders

        // The primary member's row edits their primary role + alert flag.
        await PostFormAsync(client, "/admin/department-edit", token,
            ("id", dept.Id.ToString()), ("name", dept.Name),
            ("status", "active"), ("isPublic", "true"),
            ("memberIds", staffId.ToString()), ("memberRoles", roleBId.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            Assert.Equal(roleBId, staff.RoleId);
            Assert.False(staff.PrimaryDepartmentAlerts); // alertIds absent → off
            // No redundant access row is created for the primary department.
            Assert.False(await s.Db.Set<StaffDepartmentAccess>()
                .AnyAsync(a => a.StaffId == staffId && a.DepartmentId == dept.Id));
        }

        // Dropping the primary row does NOT remove the membership (reported toast).
        await PostFormAsync(client, "/admin/department-edit", token,
            ("id", dept.Id.ToString()), ("name", dept.Name),
            ("status", "active"), ("isPublic", "true"));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            Assert.Equal(dept.Id, staff.DepartmentId); // still the primary member
            Assert.Equal(roleBId, staff.RoleId);       // untouched by the dropped row
            Assert.NotEqual(roleAId, staff.RoleId);    // the earlier role edit stuck
        }
    }

    // ---- bulk guard + department-level behavior flags ---------------------------------------

    [Fact]
    public async Task Bulk_EnableDisable_And_DeleteGuardsReferencedDepartments()
    {
        var parent = await CreateDeptAsync("Silinemez Üst");
        var child = await CreateDeptAsync("Silinir Alt", parent.Id);
        var staffed = await CreateDeptAsync("Temsilcili");
        using (var s = new ServiceScopeBundle(fixture))
        {
            s.Db.Staff.Add(new Staff
            {
                Username = $"a7bk{Guid.NewGuid():N}"[..12],
                FirstName = "A7",
                LastName = "Bulk",
                DepartmentId = staffed.Id,
                RoleId = await s.Db.Roles.Select(r => r.Id).OrderBy(id => id).FirstAsync(),
                IsVisible = false,
            });
            await s.Db.SaveChangesAsync();
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/departments");

        // Disable → Enable round-trip over the selection.
        await PostFormAsync(client, "/admin/departments/bulk", token,
            ("act", "disable"), ("ids", child.Id.ToString()));
        Assert.False((await LoadDeptAsync(child.Id))!.IsActive);
        await PostFormAsync(client, "/admin/departments/bulk", token,
            ("act", "enable"), ("ids", child.Id.ToString()));
        Assert.True((await LoadDeptAsync(child.Id))!.IsActive);

        // Delete: the parent (has a child) and the staffed dept are guarded; the
        // leaf child deletes.
        var done = await PostFormAsync(client, "/admin/departments/bulk", token,
            ("act", "delete"),
            ("ids", parent.Id.ToString()), ("ids", child.Id.ToString()), ("ids", staffed.Id.ToString()));
        Assert.Equal("/admin/departments", PathOf(done));
        Assert.NotNull(await LoadDeptAsync(parent.Id));
        Assert.Null(await LoadDeptAsync(child.Id));
        Assert.NotNull(await LoadDeptAsync(staffed.Id));

        // Now childless, the parent deletes.
        await PostFormAsync(client, "/admin/departments/bulk", token,
            ("act", "delete"), ("ids", parent.Id.ToString()));
        Assert.Null(await LoadDeptAsync(parent.Id));
    }

    [Fact]
    public async Task DisableAutoClaim_GatesClaimOnResponse_PerDepartment()
    {
        var gated = await CreateDeptAsync("Üstlenme Kapalı", mutate: d => d.DisableAutoClaim = true);
        var open = await CreateDeptAsync("Üstlenme Açık");

        async Task<int?> RespondAsync(int deptId)
        {
            using var s = new ServiceScopeBundle(fixture);
            var agent = await TestActors.StaffAsync(s.Db, "uakin"); // admin bypasses perms
            var owner = await TestActors.UserAsync(s.Db, "Burak Öztürk");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"A7 üstlenme {Guid.NewGuid():N}"[..24],
                Body = "<p>ilk mesaj</p>",
                DepartmentId = deptId,
            }, owner);
            await s.Get<IThreadService>().PostAsync(
                ticket.ThreadId, ThreadEntryType.Response, "<p>yanıt</p>", agent);
            return (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id)).StaffId;
        }

        // Global tickets.claim_on_response default is ON — the department flag gates it.
        Assert.Null(await RespondAsync(gated.Id));
        Assert.NotNull(await RespondAsync(open.Id));
    }

    [Fact]
    public async Task Reopen_ClearsAssignment_OnlyWhenDeptDisablesReopenAutoAssign()
    {
        var gated = await CreateDeptAsync("Yeniden Açma", mutate: d => d.DisableReopenAutoAssign = true);
        var normal = await CreateDeptAsync("Yeniden Normal");

        async Task<int?> CloseReopenAsync(int deptId)
        {
            using var s = new ServiceScopeBundle(fixture);
            var agent = await TestActors.StaffAsync(s.Db, "uakin"); // admin bypasses perms
            var owner = await TestActors.UserAsync(s.Db, "Burak Öztürk");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"A7 reopen {Guid.NewGuid():N}"[..24],
                Body = "<p>ilk mesaj</p>",
                DepartmentId = deptId,
            }, owner);
            var tracked = await s.Db.Tickets.SingleAsync(t => t.Id == ticket.Id);
            tracked.StaffId = agent.Id;
            await s.Db.SaveChangesAsync();

            var closed = await s.Db.TicketStatuses.SingleAsync(x => x.Key == "closed");
            var opened = await s.Db.TicketStatuses.SingleAsync(x => x.Key == "open");
            var tickets = s.Get<ITicketService>();
            await tickets.TransitionStatusAsync(ticket.Id, closed.Id, agent);
            await tickets.TransitionStatusAsync(ticket.Id, opened.Id, agent);
            return (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id)).StaffId;
        }

        // de.disableReopenAssignHelp: the reopened ticket is NOT given back to the
        // last assigned agent when the flag is on; it stays assigned otherwise.
        Assert.Null(await CloseReopenAsync(gated.Id));
        Assert.NotNull(await CloseReopenAsync(normal.Id));
    }

    // ---- Erişim tab export -------------------------------------------------------------------

    [Fact]
    public async Task MembersExport_StreamsCsv_WithPrimaryAndExtendedRows()
    {
        int bordroId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            bordroId = await s.Db.Departments.Where(d => d.Name == "Bordro").Select(d => d.Id).SingleAsync();
        }

        var client = await AdminClientAsync();
        var response = await client.GetAsync($"/admin/department-edit/export?id={bordroId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Ümit", body);       // primary member (Staff.DepartmentId)
        Assert.Contains("Deniz Kaya", body); // extended access row (seed canon)
    }

    // ---- totp helper (AdminAuthTests twin) --------------------------------------------------

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
