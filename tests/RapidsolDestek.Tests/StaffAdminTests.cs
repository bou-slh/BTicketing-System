using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/staff.html + staff-edit.html: the B1 list over real Staff rows, create =
/// Identity+domain pair (initial password rides the create post), edit syncs
/// username/email/admin-role to Identity, the 24-box per-staff permission override
/// consumed by PermissionService (null = inherit role), extended-access + team rows
/// round-trip (B4), the admin password dialog (B2, reset-token path) and the
/// self/last-admin/lock guards incl. the locked-staff login gate.
/// </summary>
[Collection("Postgres")]
public class StaffAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers (RolesTeamsAdminTests twins) --------------------------------------------

    private async Task<(HttpClient Client, int StaffId)> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7STAFFKEY23456ABC";
        var username = $"s7sf{Guid.NewGuid():N}"[..14];
        int staffId;
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
            var staff = new Staff
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
            };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            staffId = staff.Id;
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
        return (client, staffId);
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (perms/ids/accessAlerts groups).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    /// <summary>Minimal valid staff-edit Save post; extras append (duplicates allowed).</summary>
    private static (string, string)[] SaveFields(
        int? id, string username, string email, int deptId, int roleId,
        params (string, string)[] extra)
    {
        var fields = new List<(string, string)>
        {
            ("firstName", "S7"),
            ("lastName", username),
            ("email", email),
            ("username", username),
            ("backend", "local"),
            ("deptId", deptId.ToString()),
            ("roleId", roleId.ToString()),
        };
        if (id is not null)
            fields.Insert(0, ("id", id.Value.ToString()));
        fields.AddRange(extra);
        return [.. fields];
    }

    private async Task<(int DeptId, int RoleId)> SeedIdsAsync()
    {
        using var s = new ServiceScopeBundle(fixture);
        return (
            await s.Db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync(),
            await s.Db.Roles.Select(r => r.Id).OrderBy(id => id).FirstAsync());
    }

    /// <summary>Creates a full Identity+domain pair through the real create endpoint.</summary>
    private async Task<(HttpClient Client, int StaffId, string Username)> CreatePairAsync(
        params (string, string)[] extra)
    {
        var (client, _) = await AdminClientAsync();
        var (deptId, roleId) = await SeedIdsAsync();
        var username = $"s7np{Guid.NewGuid():N}"[..14];
        var (token, _) = await GetWithTokenAsync(client, "/admin/staff-edit");
        var created = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(null, username, $"{username}@rapidsol.com.tr", deptId, roleId,
                [("pw1", "S7parola123"), ("pw2", "S7parola123"), .. extra]));
        Assert.Equal("/admin/staff", PathOf(created));

        using var s = new ServiceScopeBundle(fixture);
        var staff = await s.Db.Staff.SingleAsync(x => x.Username == username);
        return (client, staff.Id, username);
    }

    // ---- staff.html (B1) ------------------------------------------------------------------

    [Fact]
    public async Task StaffList_RendersSeedRoster_SearchAndDeptFilterWork()
    {
        var (client, _) = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/staff?q=kaya"));
        Assert.Contains("Deniz Kaya", html);
        Assert.Contains("dkaya", html);
        Assert.DoesNotContain("Merve Çetin", html);

        // Department filter: mcetin is Danışmanlık's only primary member (seed canon).
        int danismanlikId;
        using (var s = new ServiceScopeBundle(fixture))
            danismanlikId = await s.Db.Departments.Where(d => d.Name == "Danışmanlık")
                .Select(d => d.Id).SingleAsync();
        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync($"/admin/staff?dept={danismanlikId}"));
        Assert.Contains("Merve Çetin", filtered);
        Assert.DoesNotContain("Deniz Kaya", filtered);
    }

    // ---- staff-edit.html: create = Identity + domain pair ---------------------------------

    [Fact]
    public async Task StaffCreate_CreatesIdentityDomainPair_PasswordAndFlagsApply()
    {
        // Post the matrix EQUAL to the role's grants → Permissions stores NULL (inherit).
        var extras = new List<(string, string)> { ("pwRequireChange", "true") };
        var (_, firstRoleId) = await SeedIdsAsync();
        using (var s = new ServiceScopeBundle(fixture))
        {
            var rolePerms = await s.Db.Roles.Where(r => r.Id == firstRoleId)
                .Select(r => r.Permissions).SingleAsync();
            extras.AddRange(rolePerms.Where(PermissionKeys.StaffOverridable.Contains)
                .Select(p => ("perms", p)));
        }
        var (_, staffId, username) = await CreatePairAsync([.. extras]);

        using var s2 = new ServiceScopeBundle(fixture);
        var staff = await s2.Db.Staff.SingleAsync(x => x.Id == staffId);
        Assert.True(staff.IsActive);
        Assert.False(staff.IsAdmin);
        Assert.True(staff.RequirePasswordChange);
        Assert.NotNull(staff.PasswordChangedAt);
        Assert.Equal("local", staff.AuthBackend);
        Assert.Null(staff.Permissions); // matrix == role grants → inherit

        var users = s2.Get<UserManager<StaffUser>>();
        var user = await users.FindByNameAsync(username);
        Assert.NotNull(user);
        Assert.Equal(staff.IdentityUserId, user!.Id);
        Assert.True(await users.CheckPasswordAsync(user, "S7parola123"));
        Assert.True(await users.IsInRoleAsync(user, "Agent"));
        Assert.False(await users.IsInRoleAsync(user, "Admin"));
    }

    [Fact]
    public async Task StaffCreate_ValidationRefusals_WriteNothing()
    {
        var (client, _) = await AdminClientAsync();
        var (deptId, roleId) = await SeedIdsAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/staff-edit");

        using var s = new ServiceScopeBundle(fixture);
        var before = await s.Db.Staff.CountAsync();

        // Missing initial password (local backend) → PRG back to the editor.
        var noPw = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(null, $"s7x{Guid.NewGuid():N}"[..12], "s7x@rapidsol.com.tr", deptId, roleId));
        Assert.Equal("/admin/staff-edit", PathOf(noPw));

        // Duplicate username (seeded canon agent) refused too.
        var dup = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(null, "uakin", "s7dup@rapidsol.com.tr", deptId, roleId,
                ("pw1", "S7parola123"), ("pw2", "S7parola123")));
        Assert.Equal("/admin/staff-edit", PathOf(dup));

        Assert.Equal(before, await s.Db.Staff.CountAsync());
    }

    [Fact]
    public async Task StaffEdit_SyncsUsernameEmailAdminRole_ToIdentity()
    {
        var (client, staffId, username) = await CreatePairAsync();
        var (deptId, roleId) = await SeedIdsAsync();

        var renamed = $"s7rn{Guid.NewGuid():N}"[..14];
        var (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        var saved = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(staffId, renamed, $"{renamed}@rapidsol.com.tr", deptId, roleId,
                ("isAdmin", "true")));
        Assert.Equal("/admin/staff", PathOf(saved));

        using var s = new ServiceScopeBundle(fixture);
        var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
        Assert.Equal(renamed, staff.Username);
        Assert.True(staff.IsAdmin);

        var users = s.Get<UserManager<StaffUser>>();
        Assert.Null(await users.FindByNameAsync(username));
        var user = await users.FindByNameAsync(renamed);
        Assert.NotNull(user);
        Assert.Equal($"{renamed}@rapidsol.com.tr", user!.Email);
        Assert.True(await users.IsInRoleAsync(user, "Admin"));
    }

    // ---- İzinler: the per-staff override consumed by PermissionService ---------------------

    [Fact]
    public async Task StaffPermissionOverride_FlipsPermissionService_NullMeansInherit()
    {
        // Own role + staff pair (seed rows are read-only).
        int roleId, staffId, deptId;
        var suffix = $"{Guid.NewGuid():N}"[..8];
        using (var s = new ServiceScopeBundle(fixture))
        {
            var role = new Role
            {
                Name = $"S7 Sf Rol {suffix}",
                Permissions = [PermissionKeys.CannedManage, PermissionKeys.TicketReply],
            };
            s.Db.Roles.Add(role);
            deptId = await s.Db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync();
            var staff = new Staff
            {
                Username = $"s7sp{suffix}",
                FirstName = "S7",
                LastName = $"Sp {suffix}",
                Email = $"s7sp{suffix}@rapidsol.com.tr",
                DepartmentId = deptId,
                Role = role,
                IsVisible = false,
            };
            s.Db.Staff.Add(staff);
            await s.Db.SaveChangesAsync();
            roleId = role.Id;
            staffId = staff.Id;
        }

        // Override: drop canned.manage, keep ticket.reply, add user.create.
        var (client, _) = await AdminClientAsync();
        var (token, html) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        // Prefill = the role's grants while no override exists.
        var cannedBox = Regex.Match(html, "<input[^>]*value=\"canned\\.manage\"[^>]*>").Value;
        Assert.Contains("checked", cannedBox);

        var saved = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(staffId, $"s7sp{suffix}", $"s7sp{suffix}@rapidsol.com.tr", deptId, roleId,
                ("perms", PermissionKeys.TicketReply),
                ("perms", PermissionKeys.UserCreate),
                ("perms", "hack.everything"))); // unknown key dropped
        Assert.Equal("/admin/staff", PathOf(saved));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            Assert.Equal(
                new[] { PermissionKeys.TicketReply, PermissionKeys.UserCreate }.Order(),
                staff.Permissions!.Order());

            var perms = await s.Get<IPermissionService>().ResolveAsync(staffId);
            Assert.True(perms.CanAnywhere(PermissionKeys.TicketReply));
            Assert.True(perms.CanAnywhere(PermissionKeys.UserCreate));  // beyond the role
            Assert.False(perms.CanAnywhere(PermissionKeys.CannedManage)); // revoked from the role
        }

        // Saving the matrix EQUAL to the role's grants stores NULL — inherit again.
        (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(staffId, $"s7sp{suffix}", $"s7sp{suffix}@rapidsol.com.tr", deptId, roleId,
                ("perms", PermissionKeys.CannedManage),
                ("perms", PermissionKeys.TicketReply)));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            Assert.Null(staff.Permissions);
            var perms = await s.Get<IPermissionService>().ResolveAsync(staffId);
            Assert.True(perms.CanAnywhere(PermissionKeys.CannedManage));
            Assert.False(perms.CanAnywhere(PermissionKeys.UserCreate));
        }
    }

    // ---- Erişim + Takımlar rows (B4) --------------------------------------------------------

    [Fact]
    public async Task StaffAccessAndTeamRows_RoundTrip()
    {
        var (client, staffId, username) = await CreatePairAsync();
        var (deptId, roleId) = await SeedIdsAsync();

        int otherDeptId, teamId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            otherDeptId = await s.Db.Departments.Where(d => d.Id != deptId)
                .Select(d => d.Id).OrderBy(id => id).FirstAsync();
            teamId = await s.Db.Teams.Where(t => t.Name == "Entegrasyon")
                .Select(t => t.Id).SingleAsync();
        }

        // Add one extended-access row (alerts ON via the row/on marker pairing)
        // and one team membership (alerts ON).
        var (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        var saved = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(staffId, username, $"{username}@rapidsol.com.tr", deptId, roleId,
                ("accessDepts", otherDeptId.ToString()),
                ("accessRoles", roleId.ToString()),
                ("accessAlerts", "row"), ("accessAlerts", "on"),
                ("teamIds", teamId.ToString()),
                ("teamAlertIds", teamId.ToString())));
        Assert.Equal("/admin/staff", PathOf(saved));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var access = await s.Db.Set<StaffDepartmentAccess>()
                .SingleAsync(a => a.StaffId == staffId);
            Assert.Equal(otherDeptId, access.DepartmentId);
            Assert.Equal(roleId, access.RoleId);
            Assert.True(access.AlertsEnabled);

            var member = await s.Db.Set<TeamMember>().SingleAsync(m => m.StaffId == staffId);
            Assert.Equal(teamId, member.TeamId);
            Assert.True(member.AlertsEnabled);
        }

        // Re-save: alerts off on the access row, team row removed.
        (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(staffId, username, $"{username}@rapidsol.com.tr", deptId, roleId,
                ("accessDepts", otherDeptId.ToString()),
                ("accessRoles", roleId.ToString()),
                ("accessAlerts", "row")));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var access = await s.Db.Set<StaffDepartmentAccess>()
                .SingleAsync(a => a.StaffId == staffId);
            Assert.False(access.AlertsEnabled);
            Assert.False(await s.Db.Set<TeamMember>().AnyAsync(m => m.StaffId == staffId));
        }
    }

    // ---- dlg-password (B2) -------------------------------------------------------------------

    [Fact]
    public async Task SetPassword_ResetsIdentityPassword_MismatchRefused()
    {
        var (client, staffId, username) = await CreatePairAsync();

        // Mismatch → refused, old password still valid.
        var (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        await PostFormAsync(client, "/admin/staff-edit/password", token,
            ("id", staffId.ToString()), ("pw1", "Yeni1parola"), ("pw2", "Baska1parola"));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var users = s.Get<UserManager<StaffUser>>();
            var user = await users.FindByNameAsync(username);
            Assert.True(await users.CheckPasswordAsync(user!, "S7parola123"));
        }

        // Valid set → the new password signs in, the change-at-next-login flag persists.
        (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        var done = await PostFormAsync(client, "/admin/staff-edit/password", token,
            ("id", staffId.ToString()), ("pw1", "Yeni1parola"), ("pw2", "Yeni1parola"),
            ("pwRequireChange", "true"));
        Assert.Equal("/admin/staff-edit", PathOf(done));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var users = s.Get<UserManager<StaffUser>>();
            var user = await users.FindByNameAsync(username);
            Assert.True(await users.CheckPasswordAsync(user!, "Yeni1parola"));
            Assert.False(await users.CheckPasswordAsync(user!, "S7parola123"));
            var staff = await s.Db.Staff.SingleAsync(x => x.Id == staffId);
            Assert.True(staff.RequirePasswordChange);
        }
    }

    [Fact]
    public async Task Reset2fa_DropsEnrollment()
    {
        var (client, staffId, username) = await CreatePairAsync();
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = await users.FindByNameAsync(username);
            await users.SetAuthenticationTokenAsync(user!, "[AspNetUserStore]", "AuthenticatorKey", "S7KEY23456ABCDEF");
            await users.SetTwoFactorEnabledAsync(user!, true);
        }

        var (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={staffId}");
        var done = await PostFormAsync(client, "/admin/staff-edit/reset-2fa", token,
            ("id", staffId.ToString()));
        Assert.Equal("/admin/staff-edit", PathOf(done));

        using var s = new ServiceScopeBundle(fixture);
        var users2 = s.Get<UserManager<StaffUser>>();
        var reset = await users2.FindByNameAsync(username);
        Assert.False(reset!.TwoFactorEnabled);
        Assert.Equal(TwoFactorMethod.None, (await s.Db.Staff.SingleAsync(x => x.Id == staffId)).TwoFactorMethod);
    }

    // ---- guards -------------------------------------------------------------------------------

    [Fact]
    public async Task Guards_SelfCannotBeLockedOrDeleted()
    {
        var (client, selfId) = await AdminClientAsync();
        var (deptId, roleId) = await SeedIdsAsync();

        string username;
        using (var s = new ServiceScopeBundle(fixture))
            username = (await s.Db.Staff.SingleAsync(x => x.Id == selfId)).Username;

        // Editor save with "Hesap kilitli" on the own account → refused (PRG back).
        var (token, _) = await GetWithTokenAsync(client, $"/admin/staff-edit?id={selfId}");
        var refused = await PostFormAsync(client, "/admin/staff-edit", token,
            SaveFields(selfId, username, $"{username}@rapidsol.com.tr", deptId, roleId,
                ("locked", "true"), ("isAdmin", "true")));
        Assert.Equal("/admin/staff-edit", PathOf(refused));

        // Bulk lock + delete of the own row are guard-skipped.
        var (bulkToken, _) = await GetWithTokenAsync(client, "/admin/staff");
        await PostFormAsync(client, "/admin/staff/bulk", bulkToken,
            ("act", "lock"), ("ids", selfId.ToString()));
        await PostFormAsync(client, "/admin/staff/bulk", bulkToken,
            ("act", "delete"), ("ids", selfId.ToString()));

        using var s2 = new ServiceScopeBundle(fixture);
        var self = await s2.Db.Staff.SingleAsync(x => x.Id == selfId);
        Assert.True(self.IsActive);
    }

    [Fact]
    public async Task BulkLock_BlocksLogin_EnableRestoresIt()
    {
        var (client, staffId, username) = await CreatePairAsync();

        var (token, _) = await GetWithTokenAsync(client, "/admin/staff");
        await PostFormAsync(client, "/admin/staff/bulk", token,
            ("act", "lock"), ("ids", staffId.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False((await s.Db.Staff.SingleAsync(x => x.Id == staffId)).IsActive);

        // Locked staff cannot sign in (se.lockedHelp canon).
        var agent = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(agent, "/agent/login");
        var stays = await PostFormAsync(agent, "/agent/login", loginToken,
            ("User", username), ("Password", "S7parola123"));
        Assert.Equal("/agent/login", PathOf(stays));

        // Enable → the same credentials sign in again.
        var (token2, _) = await GetWithTokenAsync(client, "/admin/staff");
        await PostFormAsync(client, "/admin/staff/bulk", token2,
            ("act", "enable"), ("ids", staffId.ToString()));
        var agent2 = fixture.Factory.CreateClient();
        var (loginToken2, _) = await GetWithTokenAsync(agent2, "/agent/login");
        var lands = await PostFormAsync(agent2, "/agent/login", loginToken2,
            ("User", username), ("Password", "S7parola123"));
        Assert.NotEqual("/agent/login", PathOf(lands));

        using (var s = new ServiceScopeBundle(fixture))
            Assert.True((await s.Db.Staff.SingleAsync(x => x.Id == staffId)).IsActive);
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
