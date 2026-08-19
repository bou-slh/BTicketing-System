using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin roles list + role editor (mockups/admin/roles.html + role-edit.html, ROADMAP
/// §6.3): the B1 engine over Role rows, toolbar bulk enable/disable/delete (delete
/// guarded — a role held by any staff, primary or extended department access, is
/// skipped), and the 42-box permission matrix (36 child checkboxes + 6 tri-state
/// group masters, B3 — masters are pure UI, only children post). The matrix
/// reads/writes the REAL <see cref="PermissionKeys"/> the S3 PermissionService
/// enforces on every service call; PermissionService is scoped with a per-request
/// memo and reads Role.Permissions from the database, so a saved matrix takes
/// effect on the very next request — no cache to invalidate.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class RolesController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "status", "updated"];

    /// <summary>
    /// The role-edit matrix, group by group in mockup order. Box label keys are the
    /// mockup's re.p* i18n keys; permission keys are the seeded osTicket-parity canon
    /// (PermissionKeys). 14 + 7 + 5 + 3 + 2 + 5 = 36 boxes; with the 6 group masters
    /// the mockup's "42-box" count.
    /// </summary>
    public static readonly PermGroup[] Matrix =
    [
        new("tickets", "re.grpTickets", "re.grpTicketsHelp",
        [
            new(PermissionKeys.TicketCreate, "re.pCreate"),
            new(PermissionKeys.TicketEdit, "re.pEdit"),
            new(PermissionKeys.TicketAssign, "re.pAssign"),
            new(PermissionKeys.TicketRelease, "re.pRelease"),
            new(PermissionKeys.TicketTransfer, "re.pTransfer"),
            new(PermissionKeys.TicketRefer, "re.pRefer"),
            new(PermissionKeys.TicketMerge, "re.pMerge"),
            new(PermissionKeys.TicketLink, "re.pLink"),
            new(PermissionKeys.TicketReply, "re.pReplySend"),
            new(PermissionKeys.EffortPropose, "re.pEffortPropose"),
            new(PermissionKeys.TicketMarkAnswered, "re.pMarkAnswered"),
            new(PermissionKeys.TicketClose, "re.pClose"),
            new(PermissionKeys.TicketDelete, "re.pDelete"),
            new(PermissionKeys.ThreadEdit, "re.pEditThread"),
        ]),
        new("tasks", "re.grpTasks", "re.grpTasksHelp",
        [
            new(PermissionKeys.TaskCreate, "re.pCreate"),
            new(PermissionKeys.TaskEdit, "re.pEdit"),
            new(PermissionKeys.TaskAssign, "re.pAssign"),
            new(PermissionKeys.TaskTransfer, "re.pTransfer"),
            new(PermissionKeys.TaskReply, "re.pReply"),
            new(PermissionKeys.TaskClose, "re.pClose"),
            new(PermissionKeys.TaskDelete, "re.pDelete"),
        ]),
        new("users", "re.grpUsers", "re.grpUsersHelp",
        [
            new(PermissionKeys.UserCreate, "re.pCreate"),
            new(PermissionKeys.UserEdit, "re.pEdit"),
            new(PermissionKeys.UserDelete, "re.pDelete"),
            new(PermissionKeys.UserManage, "re.pManageAccount"),
            new(PermissionKeys.UserDirectory, "re.pDirectory"),
        ]),
        new("orgs", "re.grpOrgs", "re.grpOrgsHelp",
        [
            new(PermissionKeys.OrgCreate, "re.pCreate"),
            new(PermissionKeys.OrgEdit, "re.pEdit"),
            new(PermissionKeys.OrgDelete, "re.pDelete"),
        ]),
        new("kb", "re.grpKb", "re.grpKbHelp",
        [
            new(PermissionKeys.FaqManage, "re.pFaq"),
            new(PermissionKeys.CannedManage, "re.pCanned"),
        ]),
        new("misc", "re.grpMisc", "re.grpMiscHelp",
        [
            new(PermissionKeys.DeptManage, "re.pDept"),
            new(PermissionKeys.StaffManage, "re.pStaff"),
            new(PermissionKeys.BanlistManage, "re.pBanlist"),
            new(PermissionKeys.StatsView, "re.pStats"),
            new(PermissionKeys.SearchAdvanced, "re.pSearch"),
        ]),
    ];

    /// <summary>Every persistable matrix key — unknown posted values are dropped.</summary>
    public static readonly HashSet<string> AllMatrixKeys =
        [.. Matrix.SelectMany(g => g.Boxes).Select(b => b.Key)];

    // ---- roles.html (B1 list) -----------------------------------------------------------

    [HttpGet("/admin/roles")]
    [NavKey("roles")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new RolesIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>
    /// Toolbar bulk actions over the selection (canned/users precedent). Delete is
    /// guarded: a role referenced by staff (primary role or extended department
    /// access) is skipped and reported in the partial toast.
    /// </summary>
    [HttpPost("/admin/roles/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("rl.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var roles = await db.Roles.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - roles.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var role in roles)
            {
                switch (act)
                {
                    case "enable":
                        role.IsEnabled = true;
                        ok++;
                        break;
                    case "disable":
                        role.IsEnabled = false;
                        ok++;
                        break;
                    case "delete":
                        var inUse = await db.Staff.AnyAsync(
                            s => s.RoleId == role.Id || s.DepartmentAccess.Any(a => a.RoleId == role.Id), ct);
                        if (inUse)
                        {
                            skipped++;
                        }
                        else
                        {
                            db.Roles.Remove(role);
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["RolesToastOk"] = ok;
        TempData["RolesToastSkipped"] = skipped;
        TempData["RolesToast"] = skipped > 0 ? "rl.bulkPartial" : "rl.bulkDone";
        if (skipped > 0)
            TempData["RolesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- role-edit.html (B3 matrix editor) -----------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Rol" create form (the
    /// mockup's new-role button links straight to role-edit.html).</summary>
    [HttpGet("/admin/role-edit")]
    [NavKey("roles")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        if (id is null)
            return View(new RoleEditVm(null, "", null, new HashSet<string>()));

        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
            return NotFound();
        return View(new RoleEditVm(role.Id, role.Name, role.Notes, role.Permissions.ToHashSet()));
    }

    /// <summary>
    /// Saves name/notes + the permission matrix. B3: nothing persists on a validation
    /// failure. Only the 36 canonical child keys are persistable — masters post
    /// nothing, unknown keys are dropped. IsEnabled is untouched (the mockup's editor
    /// has no status control; status is driven by the list's bulk enable/disable).
    /// </summary>
    [HttpPost("/admin/role-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, string? notes, string[] perms, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "re.errName");
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct))
            return EditToastBack(id, "re.errNameInUse");

        var granted = perms.Where(AllMatrixKeys.Contains).Distinct().ToList();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Role role;
            if (id is null)
            {
                role = new Role { Name = name };
                db.Roles.Add(role);
            }
            else
            {
                var found = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
                if (found is null)
                    return NotFound();
                role = found;
            }

            role.Name = name;
            role.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            role.Permissions = granted;
            await db.SaveChangesAsync(ct);
        }

        TempData["RolesToast"] = id is null ? "rl.toastCreated" : "rl.toastSaved";
        return RedirectToAction(nameof(Index));
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>B1 core: search + agent counts + sort, shared by paging and count.</summary>
    private IQueryable<RoleRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.Roles.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern));
        }

        // "Temsilci Sayısı" = distinct staff holding the role in ANY department
        // (primary role or an extended-access row).
        var projected = query.Select(r => new
        {
            r.Id, r.Name, r.IsEnabled,
            Agents = db.Staff.Count(s => s.RoleId == r.Id || s.DepartmentAccess.Any(a => a.RoleId == r.Id)),
            Updated = r.UpdatedAt ?? r.CreatedAt,
        });

        // The mockup's sort indicator sits on the name column (sorted-desc).
        var key = sortKey = SortKeys.Contains(sort) ? sort! : "name";
        var d = desc = dir == "asc" ? false : dir == "desc" || key is "name" or "updated";
        projected = (key, d) switch
        {
            ("status", true) => projected.OrderByDescending(r => r.IsEnabled).ThenBy(r => r.Name),
            ("status", false) => projected.OrderBy(r => r.IsEnabled).ThenBy(r => r.Name),
            ("updated", true) => projected.OrderByDescending(r => r.Updated).ThenByDescending(r => r.Id),
            ("updated", false) => projected.OrderBy(r => r.Updated).ThenBy(r => r.Id),
            (_, true) => projected.OrderByDescending(r => r.Name).ThenBy(r => r.Id),
            _ => projected.OrderBy(r => r.Name).ThenBy(r => r.Id),
        };
        return projected.Select(r => new RoleRowVm(r.Id, r.Name, r.IsEnabled, r.Agents, r.Updated));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["RolesToast"] = key;
        if (error)
            TempData["RolesToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["RoleEditToast"] = key;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record PermBox(string Key, string LabelKey);

public sealed record PermGroup(string Id, string LabelKey, string HelpKey, PermBox[] Boxes);

public sealed record RolesIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<RoleRowVm> Rows);

public sealed record RoleRowVm(int Id, string Name, bool IsEnabled, int Agents, DateTimeOffset Updated);

public sealed record RoleEditVm(int? Id, string Name, string? Notes, IReadOnlySet<string> Granted);
