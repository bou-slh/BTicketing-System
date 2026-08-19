using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/roles.html + role-edit.html + teams.html: the B1 lists over real Role/Team
/// rows, the 42-box permission matrix reading/writing the REAL PermissionKeys the
/// PermissionService enforces (a matrix save flips service behavior on the next
/// request — no cache), unknown-key filtering (masters are UI-only), the role/team
/// delete guards, and the team dialogs prefilled server-side incl. the member roster
/// round-trip (B2/B4).
/// </summary>
[Collection("Postgres")]
public class RolesTeamsAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers (SettingsAgentsUsersTests twins) ---------------------------------------

    /// <summary>Fresh identity admin signed in over HTTP with mandatory 2FA, plus a
    /// matching Staff domain row (the endpoints resolve the acting staff for audit).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7ROLESKEY23456ABC";
        var username = $"s7rt{Guid.NewGuid():N}"[..14];
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

    /// <summary>Duplicate keys allowed (perms/ids/memberIds checkbox groups).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    /// <summary>Own Role + Staff pair with unique natural keys (seed rows are read-only).</summary>
    private async Task<(int RoleId, int StaffId)> CreateRoleWithStaffAsync(params string[] permissions)
    {
        using var s = new ServiceScopeBundle(fixture);
        var suffix = $"{Guid.NewGuid():N}"[..8];
        var role = new Role { Name = $"S7 Rol {suffix}", Permissions = [.. permissions] };
        s.Db.Roles.Add(role);
        var deptId = await s.Db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync();
        var staff = new Staff
        {
            Username = $"s7r{suffix}",
            FirstName = "S7",
            LastName = $"Rol {suffix}",
            DepartmentId = deptId,
            Role = role,
            IsVisible = false,
        };
        s.Db.Staff.Add(staff);
        await s.Db.SaveChangesAsync();
        return (role.Id, staff.Id);
    }

    private async Task<Role?> LoadRoleAsync(int id)
    {
        using var s = new ServiceScopeBundle(fixture);
        return await s.Db.Roles.SingleOrDefaultAsync(r => r.Id == id);
    }

    // ---- roles.html (B1) ------------------------------------------------------------------

    [Fact]
    public async Task RolesList_RendersSeedRoles_AndSearchFilters()
    {
        var client = await AdminClientAsync();
        // Razor entity-encodes non-ASCII (K&#x131;demli) — decode before matching.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/roles"));
        Assert.Contains("Kıdemli Temsilci", html);
        Assert.Contains("Salt Okunur", html);   // disabled role listed too

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/roles?q=" + Uri.EscapeDataString("Kıdemli")));
        Assert.Contains("Kıdemli Temsilci", filtered);
        Assert.DoesNotContain("Salt Okunur", filtered);
    }

    // ---- role-edit.html: the matrix is the editor of what PermissionService enforces ------

    [Fact]
    public async Task RoleMatrixSave_FlipsPermissionServiceBehavior_OnNextRequest()
    {
        var (roleId, staffId) = await CreateRoleWithStaffAsync(
            PermissionKeys.CannedManage, PermissionKeys.TicketReply);

        // Before: the staff member's role grants canned.manage — service allows.
        var title = $"S7 canned {Guid.NewGuid():N}"[..24];
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            await s.Get<ICannedResponseService>().CreateAsync(
                new CannedUpsertRequest { Title = title, Response = "S7 gövde" },
                ActorContext.ForStaff(staff));
        }

        // The editor prefills from the real permission keys.
        var client = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, $"/admin/role-edit?id={roleId}");
        var cannedBox = Regex.Match(html, "<input[^>]*value=\"canned\\.manage\"[^>]*>").Value;
        Assert.Contains("checked", cannedBox);
        var deleteBox = Regex.Match(html, "<input[^>]*value=\"ticket\\.delete\"[^>]*>").Value;
        Assert.DoesNotContain("checked", deleteBox);

        // Save the matrix WITHOUT canned.manage (keep ticket.reply).
        var role = await LoadRoleAsync(roleId);
        var saved = await PostFormAsync(client, "/admin/role-edit", token,
            ("id", roleId.ToString()), ("name", role!.Name),
            ("notes", ""), ("perms", PermissionKeys.TicketReply));
        Assert.Equal("/admin/roles", PathOf(saved));

        // After: a fresh request scope resolves the new keys — the same call is refused.
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<ICannedResponseService>().CreateAsync(
                    new CannedUpsertRequest { Title = title + "-2", Response = "S7 gövde" },
                    ActorContext.ForStaff(staff)));
            var perms = (await s.Get<IPermissionService>().ResolveAsync(staffId));
            Assert.True(perms.CanAnywhere(PermissionKeys.TicketReply));
            Assert.False(perms.CanAnywhere(PermissionKeys.CannedManage));
        }
    }

    [Fact]
    public async Task RoleSave_KeepsOnlyCanonicalMatrixKeys()
    {
        var (roleId, _) = await CreateRoleWithStaffAsync(PermissionKeys.TicketCreate);

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, $"/admin/role-edit?id={roleId}");
        var role = await LoadRoleAsync(roleId);
        await PostFormAsync(client, "/admin/role-edit", token,
            ("id", roleId.ToString()), ("name", role!.Name), ("notes", "S7 not"),
            ("perms", PermissionKeys.TicketCreate),
            ("perms", PermissionKeys.UserCreate),   // new canon key persists
            ("perms", "hack.everything"),           // unknown key dropped
            ("perms", PermissionKeys.TicketCreate)); // duplicate collapses

        var updated = await LoadRoleAsync(roleId);
        Assert.Equal(
            new[] { PermissionKeys.TicketCreate, PermissionKeys.UserCreate }.Order(),
            updated!.Permissions.Order());
        Assert.Equal("S7 not", updated.Notes);
    }

    [Fact]
    public async Task RoleCreate_ValidationRefusals_WriteNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/role-edit");

        // Empty name → PRG back to the editor, nothing created.
        using (var s = new ServiceScopeBundle(fixture))
        {
            var before = await s.Db.Roles.CountAsync();
            var refused = await PostFormAsync(client, "/admin/role-edit", token,
                ("name", "  "), ("perms", PermissionKeys.TicketCreate));
            Assert.Equal("/admin/role-edit", PathOf(refused));
            Assert.Equal(before, await s.Db.Roles.CountAsync());
        }

        // Duplicate name (seeded canon role) refused too.
        var dup = await PostFormAsync(client, "/admin/role-edit", token, ("name", "Kıdemli Temsilci"));
        Assert.Equal("/admin/role-edit", PathOf(dup));

        // Valid create lands on the list with the role persisted.
        var name = $"S7 Yeni Rol {Guid.NewGuid():N}"[..24];
        var created = await PostFormAsync(client, "/admin/role-edit", token,
            ("name", name), ("perms", PermissionKeys.StatsView));
        Assert.Equal("/admin/roles", PathOf(created));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var role = await s.Db.Roles.SingleAsync(r => r.Name == name);
            Assert.Equal(new[] { PermissionKeys.StatsView }, role.Permissions);
            Assert.True(role.IsEnabled);
        }
    }

    [Fact]
    public async Task RoleBulk_DeleteGuardsRolesInUse_DeletesUnused()
    {
        var (usedRoleId, _) = await CreateRoleWithStaffAsync(PermissionKeys.TicketCreate);
        int unusedRoleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var unused = new Role { Name = $"S7 Boş Rol {Guid.NewGuid():N}"[..24] };
            s.Db.Roles.Add(unused);
            await s.Db.SaveChangesAsync();
            unusedRoleId = unused.Id;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/roles");
        var done = await PostFormAsync(client, "/admin/roles/bulk", token,
            ("act", "delete"), ("ids", usedRoleId.ToString()), ("ids", unusedRoleId.ToString()));
        Assert.Equal("/admin/roles", PathOf(done));

        Assert.NotNull(await LoadRoleAsync(usedRoleId));   // guarded: staff holds it
        Assert.Null(await LoadRoleAsync(unusedRoleId));    // deleted

        // Disable round-trips through the same endpoint (list bulk owns status).
        await PostFormAsync(client, "/admin/roles/bulk", token,
            ("act", "disable"), ("ids", usedRoleId.ToString()));
        Assert.False((await LoadRoleAsync(usedRoleId))!.IsEnabled);
        await PostFormAsync(client, "/admin/roles/bulk", token,
            ("act", "enable"), ("ids", usedRoleId.ToString()));
        Assert.True((await LoadRoleAsync(usedRoleId))!.IsEnabled);
    }

    // ---- teams.html (B1/B2/B4) --------------------------------------------------------------

    [Fact]
    public async Task TeamsList_PerRowDialogPrefilled_FromSeedCanon()
    {
        int teamId, leadId, noAlertMemberId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var bordro = await s.Db.Teams.Include(t => t.Members)
                .SingleAsync(t => t.Name == "Bordro Ekibi");
            teamId = bordro.Id;
            leadId = bordro.LeadStaffId!.Value;
            noAlertMemberId = bordro.Members.Single(m => !m.AlertsEnabled).StaffId; // dkaya
        }

        var client = await AdminClientAsync();
        // Razor entity-encodes non-ASCII — decode before matching text content.
        var html = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/teams?q=" + Uri.EscapeDataString("Bordro")));

        // Row opens ITS dialog; the dialog carries the persisted state (B2).
        Assert.Contains($"data-dialog-open=\"dlg-team-{teamId}\"", html);
        Assert.Contains("value=\"Bordro Ekibi\"", html);
        Assert.Contains("Bordro dönem kapanışlarında öncelikli müdahale ekibi.", html);
        Assert.Matches(new Regex($"<option value=\"{leadId}\" selected"), html);

        // Member roster rows render from real TeamMember rows, alert state included:
        // the seeded no-alert member's checkbox is unchecked.
        Assert.Contains($"data-roster-staff=\"{noAlertMemberId}\"", html);
        var alertBox = Regex.Match(html,
            $"<input type=\"checkbox\" name=\"alertIds\" value=\"{noAlertMemberId}\"[^>]*>").Value;
        Assert.NotEqual("", alertBox);
        Assert.DoesNotContain("checked", alertBox);

        // Already-member staff render as hidden roster-select options (restored on ✕).
        Assert.Matches(new Regex($"<option value=\"{noAlertMemberId}\" hidden"), html);
    }

    [Fact]
    public async Task TeamCreate_ThenUpdate_RoundTripsRosterAddRemove()
    {
        int staffA, staffB;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var ids = await s.Db.Staff.Where(x => x.Username == "dkaya" || x.Username == "kyilmaz")
                .OrderBy(x => x.Username).Select(x => x.Id).ToListAsync();
            (staffA, staffB) = (ids[0], ids[1]); // dkaya, kyilmaz
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/teams");
        var name = $"S7 Takım {Guid.NewGuid():N}"[..22];

        // Create with two members; only A gets alerts.
        var created = await PostFormAsync(client, "/admin/teams/create", token,
            ("name", name), ("active", "true"), ("leadId", staffA.ToString()),
            ("notes", "S7 takım notu"),
            ("memberIds", staffA.ToString()), ("memberIds", staffB.ToString()),
            ("alertIds", staffA.ToString()));
        Assert.Equal("/admin/teams", PathOf(created));

        int teamId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var team = await s.Db.Teams.Include(t => t.Members).SingleAsync(t => t.Name == name);
            teamId = team.Id;
            Assert.True(team.IsActive);
            Assert.Equal(staffA, team.LeadStaffId);
            Assert.Equal("S7 takım notu", team.Notes);
            Assert.Equal(2, team.Members.Count);
            Assert.True(team.Members.Single(m => m.StaffId == staffA).AlertsEnabled);
            Assert.False(team.Members.Single(m => m.StaffId == staffB).AlertsEnabled);
        }

        // Update: remove A, keep B (alerts now on), disable, clear lead, no-alerts on.
        var updated = await PostFormAsync(client, "/admin/teams/update", token,
            ("id", teamId.ToString()), ("name", name), ("active", "false"), ("leadId", ""),
            ("noAlerts", "true"), ("notes", ""),
            ("memberIds", staffB.ToString()), ("alertIds", staffB.ToString()));
        Assert.Equal("/admin/teams", PathOf(updated));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var team = await s.Db.Teams.Include(t => t.Members).SingleAsync(t => t.Id == teamId);
            Assert.False(team.IsActive);
            Assert.Null(team.LeadStaffId);
            Assert.True(team.NoAlerts);
            Assert.Null(team.Notes);
            var member = Assert.Single(team.Members);
            Assert.Equal(staffB, member.StaffId);
            Assert.True(member.AlertsEnabled);
        }
    }

    [Fact]
    public async Task TeamBulk_DeleteGuardsReferencedTeams_DeletesUnused()
    {
        int referencedId, unusedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var referenced = new Team { Name = $"S7 Takım R {Guid.NewGuid():N}"[..22] };
            var unused = new Team { Name = $"S7 Takım U {Guid.NewGuid():N}"[..22] };
            s.Db.Teams.AddRange(referenced, unused);
            await s.Db.SaveChangesAsync();
            // Help-topic routing references the team (the lightest real reference).
            s.Db.HelpTopics.Add(new HelpTopic
            {
                Name = $"S7 Konu {Guid.NewGuid():N}"[..20],
                IsPublic = false,
                TeamId = referenced.Id,
            });
            await s.Db.SaveChangesAsync();
            (referencedId, unusedId) = (referenced.Id, unused.Id);
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/teams");
        var done = await PostFormAsync(client, "/admin/teams/bulk", token,
            ("act", "delete"), ("ids", referencedId.ToString()), ("ids", unusedId.ToString()));
        Assert.Equal("/admin/teams", PathOf(done));

        using var s2 = new ServiceScopeBundle(fixture);
        Assert.NotNull(await s2.Db.Teams.SingleOrDefaultAsync(t => t.Id == referencedId));
        Assert.Null(await s2.Db.Teams.SingleOrDefaultAsync(t => t.Id == unusedId));
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
