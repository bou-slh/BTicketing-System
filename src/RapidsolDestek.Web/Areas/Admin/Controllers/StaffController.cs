using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Areas.Agent.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin agents list + editor (mockups/admin/staff.html + staff-edit.html, ROADMAP
/// §6.3). List: the B1 engine over real Staff rows (status pill = locked/vacation/
/// active), department + team filter selects with the mockup's Uygula button, toolbar
/// bulk enable/lock/delete (self and last-active-admin are guard-skipped; staff
/// referenced by tickets/tasks/threads/team-lead/department-manager block deletion —
/// teams precedent) and CSV export. Editor: create + edit over the Identity+domain
/// pair (username/email/full-name sync, IsAdmin ↔ the Identity "Admin" role); the
/// Erişim tab manages the primary department/role plus StaffDepartmentAccess rows
/// (B4 rule rows); the İzinler tab is the 24-box per-staff override matrix
/// (PermissionKeys.StaffOverridable — null = inherit role) with tri-state masters
/// (B3); the Takımlar tab manages TeamMember rows from the staff side (B4 roster);
/// the password dialog resets the Identity password (B2, admin path — no old
/// password) and the auth-backend select gates it (local only; LDAP persists with
/// TODO(S9 SSO) — sign-in still verifies the local password).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class StaffController(AppDbContext db, UserManager<StaffUser> users) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "username", "status", "dept", "role", "login"];

    private static readonly string[] Backends = ["local", "ldap"];

    /// <summary>
    /// The staff-edit İzinler matrix, card by card in mockup order (24 child boxes +
    /// 5 masters). Box label keys are the mockup's se.p* i18n keys; permission keys
    /// are the canonical subset PermissionService overrides per staff
    /// (PermissionKeys.StaffOverridable — keep the two in sync).
    /// </summary>
    public static readonly PermGroup[] Matrix =
    [
        new("tickets", "se.grpTickets", "se.grpTicketsHelp",
        [
            new(PermissionKeys.TicketCreate, "se.pCreate"),
            new(PermissionKeys.TicketEdit, "se.pEdit"),
            new(PermissionKeys.TicketAssign, "se.pAssign"),
            new(PermissionKeys.TicketTransfer, "se.pTransfer"),
            new(PermissionKeys.TicketReply, "se.pReply"),
            new(PermissionKeys.EffortPropose, "se.pEffortPropose"),
            new(PermissionKeys.TicketClose, "se.pClose"),
            new(PermissionKeys.TicketDelete, "se.pDelete"),
        ]),
        new("tasks", "se.grpTasks", "se.grpTasksHelp",
        [
            new(PermissionKeys.TaskCreate, "se.pCreate"),
            new(PermissionKeys.TaskEdit, "se.pEdit"),
            new(PermissionKeys.TaskAssign, "se.pAssign"),
            new(PermissionKeys.TaskTransfer, "se.pTransfer"),
            new(PermissionKeys.TaskReply, "se.pReply"),
            new(PermissionKeys.TaskClose, "se.pClose"),
            new(PermissionKeys.TaskDelete, "se.pDelete"),
        ]),
        new("users", "se.grpUsers", "se.grpUsersHelp",
        [
            new(PermissionKeys.UserCreate, "se.pCreate"),
            new(PermissionKeys.UserEdit, "se.pEdit"),
            new(PermissionKeys.UserDelete, "se.pDelete"),
            new(PermissionKeys.UserManage, "se.pManageAccount"),
        ]),
        new("orgs", "se.grpOrgs", "se.grpOrgsHelp",
        [
            new(PermissionKeys.OrgCreate, "se.pCreate"),
            new(PermissionKeys.OrgEdit, "se.pEdit"),
            new(PermissionKeys.OrgDelete, "se.pDelete"),
        ]),
        new("kb", "se.grpKb", "se.grpKbHelp",
        [
            new(PermissionKeys.FaqManage, "se.pFaq"),
            new(PermissionKeys.CannedManage, "se.pCanned"),
        ]),
    ];

    /// <summary>Every persistable matrix key — unknown posted values are dropped.</summary>
    public static readonly HashSet<string> AllMatrixKeys =
        [.. Matrix.SelectMany(g => g.Boxes).Select(b => b.Key)];

    // ---- staff.html (B1 list) --------------------------------------------------------------

    [HttpGet("/admin/staff")]
    [NavKey("staff")]
    public async Task<IActionResult> Index(
        string? q, int? dept, int? team, string? sort, string? dir, int page = 1,
        CancellationToken ct = default)
    {
        var query = BuildQuery(q, dept, team, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new StaffIndexVm(
            q, dept, team, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows,
            await db.Departments.OrderBy(d => d.Path)
                .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct),
            await db.Teams.OrderBy(t => t.Name)
                .Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct)));
    }

    /// <summary>
    /// Toolbar bulk actions over the selection (Etkinleştir / Kilitle / Sil). Guards:
    /// the acting admin and the LAST remaining active admin are never locked or
    /// deleted (skipped + partial toast — invented guard, flagged); staff referenced
    /// by tickets, tasks, thread entries, team leadership or department management
    /// are skipped on delete (guard-skip per the teams precedent; TeamMember and
    /// StaffDepartmentAccess rows cascade). Deleting a staff row also deletes its
    /// Identity user, so the pair never dangles.
    /// </summary>
    [HttpPost("/admin/staff/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var actor = await db.ResolveStaffAsync(User, ct);
        if (actor is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("sf.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "lock" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var targets = await db.Staff.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - targets.Count;

        // Live count of active admins; each lock/delete of one decrements. Refusing
        // the step that would reach zero keeps the panel reachable.
        var activeAdmins = await db.Staff.CountAsync(s => s.IsAdmin && s.IsActive, ct);

        using (ActorContext.ForStaff(actor, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var s in targets)
            {
                switch (act)
                {
                    case "enable":
                        if (!s.IsActive)
                            activeAdmins += s.IsAdmin ? 1 : 0;
                        s.IsActive = true;
                        ok++;
                        break;
                    case "lock":
                        if (s.Id == actor.Id || (s.IsAdmin && s.IsActive && activeAdmins <= 1))
                        {
                            skipped++;
                        }
                        else
                        {
                            if (s.IsAdmin && s.IsActive)
                                activeAdmins--;
                            s.IsActive = false;
                            ok++;
                        }
                        break;
                    case "delete":
                        if (s.Id == actor.Id || (s.IsAdmin && s.IsActive && activeAdmins <= 1)
                            || await IsReferencedAsync(s.Id, ct))
                        {
                            skipped++;
                        }
                        else
                        {
                            if (s.IsAdmin && s.IsActive)
                                activeAdmins--;
                            if (s.IdentityUserId is { } uid && await users.FindByIdAsync(uid.ToString()) is { } user)
                                await users.DeleteAsync(user);
                            db.Staff.Remove(s); // access + team rows cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["StaffToastOk"] = ok;
        TempData["StaffToastSkipped"] = skipped;
        TempData["StaffToast"] = skipped > 0 ? "sf.bulkPartial" : "sf.bulkDone";
        if (skipped > 0)
            TempData["StaffToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Toolbar export: the FILTERED list as CSV (UTF-8 BOM, orgs precedent).</summary>
    [HttpGet("/admin/staff/export")]
    public async Task<IActionResult> Export(
        string? q, int? dept, int? team, string? sort, string? dir,
        [FromServices] IStringLocalizerFactory localizerFactory,
        [FromServices] IStringLocalizer<SharedResources> sl,
        CancellationToken ct = default)
    {
        var rows = await BuildQuery(q, dept, team, sort, dir, out _, out _).ToListAsync(ct);

        var pageL = localizerFactory.Create("Areas.Admin.Views.Staff.Index",
            typeof(Program).Assembly.GetName().Name!);
        string[] headers =
        [
            pageL["sf.colName"], pageL["sf.colUsername"], sl["common.status"],
            pageL["sf.colDept"], pageL["sf.colRole"], pageL["sf.colLastLogin"],
        ];

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=temsilciler.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", headers.Select(TicketListEngine.Csv)));
        foreach (var r in rows)
        {
            string[] cells =
            [
                r.Name, r.Username,
                pageL[!r.Active ? "sf.stLocked" : r.Vacation ? "sf.stVacation" : "sf.stActive"],
                r.DeptName, r.RoleName,
                r.LastLogin?.ToLocalTime().ToString(pageL["sf.dateFmt"]) ?? "",
            ];
            await writer.WriteLineAsync(string.Join(",", cells.Select(TicketListEngine.Csv)));
        }
        return new EmptyResult();
    }

    // ---- staff-edit.html --------------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Temsilci" create form
    /// (the mockup's new-button links straight to staff-edit.html).</summary>
    [HttpGet("/admin/staff-edit")]
    [NavKey("staff")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        Staff? staff = null;
        if (id is not null)
        {
            staff = await db.Staff.Include(s => s.DepartmentAccess)
                .SingleOrDefaultAsync(s => s.Id == id, ct);
            if (staff is null)
                return NotFound();
        }

        return View(await BuildEditVmAsync(staff, ct));
    }

    /// <summary>
    /// Whole-page save (B3): identity/auth/status fields, primary access, the
    /// extended-access rows, the permission override matrix and team memberships in
    /// one post. accessDepts/accessRoles are parallel arrays in DOM order;
    /// accessAlerts pairs by row markers ("row" per row, "on" when checked) because
    /// unchecked checkboxes post nothing. teamIds/teamAlertIds follow the roster
    /// contract (values = team ids). The matrix stores NULL when it equals the
    /// primary role's grants — the staff keeps inheriting future role edits; any
    /// difference persists as the per-staff override. On create, the password dialog
    /// rides along in the same post (pw1/pw2/pwRequireChange via form=""
    /// association) and a local-backend staff requires one.
    /// </summary>
    [HttpPost("/admin/staff-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? firstName, string? lastName, string? email,
        string? phone, string? phoneExt, string? mobile,
        string? username, string? backend,
        bool locked, bool isAdmin, bool assignedOnly, bool vacation, string? notes,
        int deptId, int roleId, bool usePrimaryOnAssigned,
        int[] accessDepts, int[] accessRoles, string[] accessAlerts,
        string[] perms, int[] teamIds, int[] teamAlertIds,
        string? pw1, string? pw2, bool pwRequireChange,
        CancellationToken ct)
    {
        var actor = await db.ResolveStaffAsync(User, ct);
        if (actor is null)
            return Forbid();

        firstName = (firstName ?? "").Trim();
        lastName = (lastName ?? "").Trim();
        username = (username ?? "").Trim();
        email = (email ?? "").Trim();
        backend = Backends.Contains(backend) ? backend! : "local";

        if (firstName.Length is 0 or > 64 || lastName.Length is 0 or > 64)
            return EditToastBack(id, "se.errName");
        if (username.Length is 0 or > 64)
            return EditToastBack(id, "se.errUsername");
        if (await db.Staff.AnyAsync(s => s.Username == username && s.Id != id, ct))
            return EditToastBack(id, "se.errUsernameInUse");
        if (email.Length is 0 or > 254 || !email.Contains('@'))
            return EditToastBack(id, "se.errEmail");
        if (await db.Staff.AnyAsync(s => s.Email == email && s.Id != id, ct))
            return EditToastBack(id, "se.errEmailInUse");
        if (!await db.Departments.AnyAsync(d => d.Id == deptId, ct)
            || !await db.Roles.AnyAsync(r => r.Id == roleId, ct))
            return EditToastBack(id, "se.errAccess");

        // Create needs an initial password for the local backend; the dialog fields
        // ride the create post. Validation mirrors rd.js data-pw-strength/-match.
        if (id is null && backend == "local")
        {
            if (string.IsNullOrEmpty(pw1))
                return EditToastBack(id, "se.errPasswordRequired");
            if (pw1 != pw2)
                return EditToastBack(id, "se.errPasswordMismatch");
            if (!PasswordLooksStrong(pw1))
                return EditToastBack(id, "se.errPasswordWeak");
        }

        // Guards: you cannot lock yourself or drop your own admin flag; the last
        // active admin can be neither locked nor demoted (invented, flagged).
        if (id == actor.Id && (locked || !isAdmin))
            return EditToastBack(id, "se.errSelf");
        if (id is not null)
        {
            var target = await db.Staff.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
            if (target is null)
                return NotFound();
            if (target.IsAdmin && target.IsActive && (locked || !isAdmin)
                && !await db.Staff.AnyAsync(s => s.Id != id && s.IsAdmin && s.IsActive, ct))
                return EditToastBack(id, "se.errLastAdmin");
        }

        // Extended access rows: pair depts with roles by DOM order; the alert marker
        // sequence rebuilds the per-row checkbox state. Unknown depts/roles and the
        // primary department are dropped; duplicate depts collapse (last wins).
        var rowAlerts = ParseRowAlerts(accessAlerts);
        var access = new Dictionary<int, (int RoleId, bool Alerts)>();
        for (var i = 0; i < accessDepts.Length && i < accessRoles.Length; i++)
            access[accessDepts[i]] = (accessRoles[i], i < rowAlerts.Count && rowAlerts[i]);
        access.Remove(deptId);
        var validDepts = (await db.Departments.Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var validRoles = (await db.Roles.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        foreach (var dept in access.Keys.Where(k =>
                     !validDepts.Contains(k) || !validRoles.Contains(access[k].RoleId)).ToList())
            access.Remove(dept);

        // Permission override (İzinler): equal to the primary role's staff-matrix
        // grants → NULL (keep inheriting); different → the explicit per-staff set.
        var granted = perms.Where(AllMatrixKeys.Contains).Distinct().ToList();
        var rolePerms = await db.Roles.Where(r => r.Id == roleId)
            .Select(r => r.Permissions).SingleAsync(ct);
        var roleBase = rolePerms.Where(AllMatrixKeys.Contains).ToHashSet();
        List<string>? overrideSet = roleBase.SetEquals(granted) ? null : granted;

        var validTeams = (await db.Teams.Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var wantedTeams = teamIds.Where(validTeams.Contains).Distinct().ToArray();
        var teamAlerts = teamAlertIds.ToHashSet();

        using (ActorContext.ForStaff(actor, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Staff staff;
            if (id is null)
            {
                staff = new Staff { Username = username, FirstName = firstName, LastName = lastName };
                db.Staff.Add(staff);
            }
            else
            {
                var found = await db.Staff.Include(s => s.DepartmentAccess)
                    .SingleOrDefaultAsync(s => s.Id == id, ct);
                if (found is null)
                    return NotFound();
                staff = found;
            }

            staff.Username = username;
            staff.FirstName = firstName;
            staff.LastName = lastName;
            staff.Email = email;
            staff.Phone = NullIfEmpty(phone);
            staff.PhoneExt = NullIfEmpty(phoneExt);
            staff.Mobile = NullIfEmpty(mobile);
            staff.AuthBackend = backend;
            staff.IsActive = !locked;
            staff.IsAdmin = isAdmin;
            staff.AssignedOnly = assignedOnly;
            staff.OnVacation = vacation;
            staff.Notes = NullIfEmpty(notes);
            staff.DepartmentId = deptId;
            staff.RoleId = roleId;
            staff.UsePrimaryRoleOnAssigned = usePrimaryOnAssigned;
            staff.Permissions = overrideSet;

            // ---- Identity pair sync (profile-port precedent) ------------------------
            var fullName = staff.FullName;
            if (id is null)
            {
                var user = new StaffUser
                {
                    UserName = username,
                    Email = email,
                    EmailConfirmed = true, // dev parity with the seed (profile precedent)
                    FullName = fullName,
                };
                var created = backend == "local"
                    ? await users.CreateAsync(user, pw1!)
                    : await users.CreateAsync(user); // TODO(S9 SSO): external backend, no local password
                if (!created.Succeeded)
                {
                    return EditToastBack(id, created.Errors.Any(e => e.Code.Contains("UserName"))
                        ? "se.errUsernameInUse"
                        : created.Errors.Any(e => e.Code.Contains("Email")) ? "se.errEmailInUse"
                        : "se.errPasswordWeak");
                }
                await users.AddToRoleAsync(user, "Agent");
                if (isAdmin)
                    await users.AddToRoleAsync(user, "Admin");
                await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", fullName));
                staff.IdentityUserId = user.Id;
                staff.PasswordChangedAt = backend == "local" ? DateTimeOffset.UtcNow : null;
                staff.RequirePasswordChange = backend == "local" && pwRequireChange;
            }
            else if (staff.IdentityUserId is { } uid
                && await users.FindByIdAsync(uid.ToString()) is { } user)
            {
                if (!string.Equals(user.UserName, username, StringComparison.Ordinal))
                {
                    var renamed = await users.SetUserNameAsync(user, username);
                    if (!renamed.Succeeded)
                        return EditToastBack(id, "se.errUsernameInUse");
                }
                if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                    || user.FullName != fullName)
                {
                    user.Email = email;
                    user.EmailConfirmed = true;
                    user.FullName = fullName;
                    var updated = await users.UpdateAsync(user);
                    if (!updated.Succeeded)
                        return EditToastBack(id, "se.errEmailInUse");
                }
                // Topbar chrome reads the FullName claim (profile precedent).
                var claims = await users.GetClaimsAsync(user);
                var nameClaim = claims.FirstOrDefault(c => c.Type == "FullName");
                if (nameClaim is null)
                    await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", fullName));
                else if (nameClaim.Value != fullName)
                    await users.ReplaceClaimAsync(user, nameClaim, new System.Security.Claims.Claim("FullName", fullName));

                // IsAdmin is the source of truth the Identity "Admin" role syncs from.
                var inAdminRole = await users.IsInRoleAsync(user, "Admin");
                if (isAdmin && !inAdminRole)
                    await users.AddToRoleAsync(user, "Admin");
                else if (!isAdmin && inAdminRole)
                    await users.RemoveFromRoleAsync(user, "Admin");
            }

            await db.SaveChangesAsync(ct); // staff.Id materializes for create

            // ---- Extended-access reconciliation (B4) ---------------------------------
            var accessRows = await db.Set<StaffDepartmentAccess>()
                .Where(a => a.StaffId == staff.Id).ToListAsync(ct);
            db.RemoveRange(accessRows.Where(a => !access.ContainsKey(a.DepartmentId)));
            foreach (var (dept, row) in access)
            {
                var existing = accessRows.FirstOrDefault(a => a.DepartmentId == dept);
                if (existing is null)
                {
                    db.Add(new StaffDepartmentAccess
                    {
                        StaffId = staff.Id, DepartmentId = dept,
                        RoleId = row.RoleId, AlertsEnabled = row.Alerts,
                    });
                }
                else
                {
                    existing.RoleId = row.RoleId;
                    existing.AlertsEnabled = row.Alerts;
                }
            }

            // ---- Team membership reconciliation (B4, teams-port contract mirrored) ---
            var memberships = await db.Set<TeamMember>()
                .Where(m => m.StaffId == staff.Id).ToListAsync(ct);
            db.RemoveRange(memberships.Where(m => !wantedTeams.Contains(m.TeamId)));
            foreach (var teamId in wantedTeams)
            {
                var member = memberships.FirstOrDefault(m => m.TeamId == teamId);
                if (member is null)
                    db.Add(new TeamMember { TeamId = teamId, StaffId = staff.Id, AlertsEnabled = teamAlerts.Contains(teamId) });
                else
                    member.AlertsEnabled = teamAlerts.Contains(teamId);
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["StaffToast"] = id is null ? "sf.toastCreated" : "sf.toastSaved";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// dlg-password submit on the EDIT page (B2: validate → save → close → toast).
    /// Admin path: Identity reset-token round-trip, no old password needed. Refused
    /// for non-local backends (the select gates the dialog client-side too).
    /// </summary>
    [HttpPost("/admin/staff-edit/password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPassword(
        int id, string? pw1, string? pw2, bool pwRequireChange, CancellationToken ct)
    {
        var actor = await db.ResolveStaffAsync(User, ct);
        if (actor is null)
            return Forbid();

        var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null)
            return NotFound();
        if (staff.AuthBackend != "local")
            return EditToastBack(id, "se.errBackend");
        if (string.IsNullOrEmpty(pw1) || pw1 != pw2)
            return EditToastBack(id, "se.errPasswordMismatch");
        if (!PasswordLooksStrong(pw1))
            return EditToastBack(id, "se.errPasswordWeak");
        if (staff.IdentityUserId is not { } uid
            || await users.FindByIdAsync(uid.ToString()) is not { } user)
            return EditToastBack(id, "se.errBackend");

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, pw1);
        if (!result.Succeeded)
            return EditToastBack(id, "se.errPasswordWeak");

        staff.PasswordChangedAt = DateTimeOffset.UtcNow;
        staff.RequirePasswordChange = pwRequireChange;
        using (ActorContext.ForStaff(actor, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await db.SaveChangesAsync(ct);

        return EditToastBack(id, "se.toastPassword", error: false);
    }

    /// <summary>
    /// 2FA "Sıfırla" (se.twofaHelp): drops the enrollment; the agent sets 2FA up
    /// again on their next sign-in (admins are forced through /admin/2fa-setup).
    /// </summary>
    [HttpPost("/admin/staff-edit/reset-2fa")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset2fa(int id, CancellationToken ct)
    {
        var actor = await db.ResolveStaffAsync(User, ct);
        if (actor is null)
            return Forbid();

        var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null)
            return NotFound();

        if (staff.IdentityUserId is { } uid && await users.FindByIdAsync(uid.ToString()) is { } user)
        {
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
        }
        staff.TwoFactorMethod = TwoFactorMethod.None;
        using (ActorContext.ForStaff(actor, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await db.SaveChangesAsync(ct);

        return EditToastBack(id, "se.toast2faReset", error: false);
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>B1 core: search + dept/team filters + sort, shared by paging, count and export.</summary>
    private IQueryable<StaffRowVm> BuildQuery(
        string? q, int? dept, int? team, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.Staff.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.FirstName + " " + s.LastName, pattern)
                || EF.Functions.ILike(s.Username, pattern)
                || EF.Functions.ILike(s.Email ?? "", pattern));
        }
        // Department filter matches the row's Departman column (primary department).
        if (dept is { } deptFilter)
            query = query.Where(s => s.DepartmentId == deptFilter);
        if (team is { } teamFilter)
            query = query.Where(s => db.Set<TeamMember>().Any(m => m.StaffId == s.Id && m.TeamId == teamFilter));

        var projected = query.Select(s => new
        {
            s.Id,
            Name = s.FirstName + " " + s.LastName,
            s.Username, s.IsActive, s.OnVacation, s.IsAdmin,
            DeptName = s.Department!.Name,
            RoleName = s.Role!.Name,
            s.LastLoginAt,
            // Status pill rank: locked > vacation > active (mockup pill order).
            StatusRank = !s.IsActive ? 2 : s.OnVacation ? 1 : 0,
        });

        // The mockup's sort indicator sits on the name column (sorted-desc).
        var key = sortKey = SortKeys.Contains(sort) ? sort! : "name";
        var d = desc = dir == "asc" ? false : dir == "desc" || key is "name" or "login";
        projected = (key, d) switch
        {
            ("username", true) => projected.OrderByDescending(s => s.Username).ThenBy(s => s.Id),
            ("username", false) => projected.OrderBy(s => s.Username).ThenBy(s => s.Id),
            ("status", true) => projected.OrderByDescending(s => s.StatusRank).ThenBy(s => s.Name),
            ("status", false) => projected.OrderBy(s => s.StatusRank).ThenBy(s => s.Name),
            ("dept", true) => projected.OrderByDescending(s => s.DeptName).ThenBy(s => s.Name),
            ("dept", false) => projected.OrderBy(s => s.DeptName).ThenBy(s => s.Name),
            ("role", true) => projected.OrderByDescending(s => s.RoleName).ThenBy(s => s.Name),
            ("role", false) => projected.OrderBy(s => s.RoleName).ThenBy(s => s.Name),
            ("login", true) => projected.OrderByDescending(s => s.LastLoginAt).ThenByDescending(s => s.Id),
            ("login", false) => projected.OrderBy(s => s.LastLoginAt).ThenBy(s => s.Id),
            (_, true) => projected.OrderByDescending(s => s.Name).ThenBy(s => s.Id),
            _ => projected.OrderBy(s => s.Name).ThenBy(s => s.Id),
        };
        return projected.Select(s => new StaffRowVm(
            s.Id, s.Name, s.Username, s.IsActive, s.OnVacation, s.IsAdmin,
            s.DeptName, s.RoleName, s.LastLoginAt));
    }

    private async Task<StaffEditVm> BuildEditVmAsync(Staff? staff, CancellationToken ct)
    {
        var roles = await db.Roles.OrderBy(r => r.Id)
            .Select(r => new RoleOptionVm(r.Id, r.Name, r.Permissions)).ToListAsync(ct);
        var depts = await db.Departments.OrderBy(d => d.Path)
            .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct);
        var teams = await db.Teams.Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct);

        // Matrix prefill: the explicit per-staff override, else the primary role's
        // staff-matrix grants (create: the first role option — the select's default).
        var roleId = staff?.RoleId ?? roles.FirstOrDefault()?.Id ?? 0;
        var granted = staff?.Permissions?.ToHashSet()
            ?? roles.FirstOrDefault(r => r.Id == roleId)?.Permissions
                .Where(AllMatrixKeys.Contains).ToHashSet()
            ?? [];

        var accessRows = staff is null ? [] : await db.Set<StaffDepartmentAccess>()
            .Where(a => a.StaffId == staff.Id)
            .OrderBy(a => a.DepartmentId)
            .Select(a => new StaffAccessRowVm(a.DepartmentId, a.RoleId, a.AlertsEnabled))
            .ToListAsync(ct);

        var memberships = staff is null ? [] : await db.Set<TeamMember>()
            .Where(m => m.StaffId == staff.Id)
            .OrderBy(m => m.Team!.Name)
            .Select(m => new StaffTeamRowVm(m.TeamId, m.Team!.Name, m.AlertsEnabled))
            .ToListAsync(ct);

        var twoFactorOn = false;
        if (staff?.IdentityUserId is { } uid)
            twoFactorOn = await db.StaffUsers.Where(u => u.Id == uid)
                .Select(u => u.TwoFactorEnabled).FirstOrDefaultAsync(ct);

        return new StaffEditVm(staff, twoFactorOn, granted, depts, roles, teams, accessRows, memberships);
    }

    /// <summary>Deletion reference guard: assignments and structural references
    /// (thread entries are authored history — never orphaned by a delete).</summary>
    private async Task<bool> IsReferencedAsync(int staffId, CancellationToken ct) =>
        await db.Tickets.AnyAsync(t => t.StaffId == staffId, ct)
        || await db.TaskItems.AnyAsync(t => t.StaffId == staffId, ct)
        || await db.ThreadEntries.AnyAsync(e => e.StaffId == staffId, ct)
        || await db.Departments.AnyAsync(d => d.ManagerStaffId == staffId, ct)
        || await db.Teams.AnyAsync(t => t.LeadStaffId == staffId, ct);

    /// <summary>
    /// Rebuilds the per-row alert flags from the marker sequence: every access row
    /// posts a hidden "row"; its checkbox appends "on" when checked (unchecked
    /// checkboxes post nothing, which would desync a plain parallel array).
    /// </summary>
    private static List<bool> ParseRowAlerts(string[] markers)
    {
        var rows = new List<bool>();
        foreach (var m in markers)
        {
            if (m == "row")
                rows.Add(false);
            else if (m == "on" && rows.Count > 0)
                rows[^1] = true;
        }
        return rows;
    }

    /// <summary>rd.js data-pw-strength twin: ≥8 chars with letters + digits.</summary>
    private static bool PasswordLooksStrong(string pw) =>
        pw.Length >= 8 && pw.Any(char.IsLetter) && pw.Any(char.IsDigit);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["StaffToast"] = key;
        if (error)
            TempData["StaffToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>PRG back to the editor with a toast (error by default — B3 refusals write nothing).</summary>
    private IActionResult EditToastBack(int? id, string key, bool error = true)
    {
        TempData["StaffEditToast"] = key;
        if (error)
            TempData["StaffEditToastError"] = true;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record StaffIndexVm(
    string? Query,
    int? Dept,
    int? Team,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<StaffRowVm> Rows,
    IReadOnlyList<OptionVm> DeptOptions,
    IReadOnlyList<OptionVm> TeamOptions);

public sealed record StaffRowVm(
    int Id,
    string Name,
    string Username,
    bool Active,
    bool Vacation,
    bool IsAdmin,
    string DeptName,
    string RoleName,
    DateTimeOffset? LastLogin);

public sealed record StaffEditVm(
    Staff? Staff,
    bool TwoFactorOn,
    IReadOnlySet<string> Granted,
    IReadOnlyList<OptionVm> Departments,
    IReadOnlyList<RoleOptionVm> Roles,
    IReadOnlyList<OptionVm> Teams,
    IReadOnlyList<StaffAccessRowVm> AccessRows,
    IReadOnlyList<StaffTeamRowVm> Memberships);

public sealed record RoleOptionVm(int Id, string Name, List<string> Permissions);

public sealed record StaffAccessRowVm(int DeptId, int RoleId, bool Alerts);

public sealed record StaffTeamRowVm(int TeamId, string Name, bool Alerts);
