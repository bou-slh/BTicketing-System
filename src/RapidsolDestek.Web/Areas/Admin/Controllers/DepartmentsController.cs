using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Web.Areas.Agent.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin departments list + editor (mockups/admin/departments.html +
/// department-edit.html, ROADMAP §6.3). List: the B1 engine over the REAL Department
/// hierarchy — tree order with children indented under their parent (search
/// flattens), client-side collapse carets on parent rows, toolbar bulk
/// enable/disable/delete with a reference guard (children / staff / tickets / tasks
/// / help topics / canned responses block deletion; the mockup defines no reassign
/// dialog, so referenced rows are skipped — teams precedent, flagged). Editor: every
/// mockup control persists to a real Department column; the Erişim tab manages
/// primary members (Staff.DepartmentId, "Birincil" badge — role edits their primary
/// RoleId) and extended access rows (StaffDepartmentAccess with a per-department
/// role the PermissionService resolves on the next request) via the rd.js
/// data-roster contract (B4); the member table exports as CSV.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class DepartmentsController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "status", "type", "manager"];

    // ---- departments.html (B1 list over the tree) -----------------------------------------

    [HttpGet("/admin/departments")]
    [NavKey("departments")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        // Departments are few — materialize once, then order as a tree in memory
        // (children under their parent; the sort key orders SIBLINGS). A search
        // flattens the tree (an indented child row without its parent is noise).
        var all = await db.Departments
            .Select(d => new DeptRowVm(
                d.Id, d.Name, d.ParentId, d.Path,
                d.IsArchived ? DeptStatus.Archived : d.IsActive ? DeptStatus.Active : DeptStatus.Disabled,
                d.IsPublic,
                db.Staff.Count(s => s.DepartmentId == d.Id)
                    + db.Staff.Count(s => s.DepartmentId != d.Id && s.DepartmentAccess.Any(a => a.DepartmentId == d.Id)),
                db.EmailAccounts.Where(e => e.Id == d.EmailAccountId).Select(e => e.Address).FirstOrDefault(),
                db.Staff.Where(s => s.Id == d.ManagerStaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
                0))
            .ToListAsync(ct);

        // The mockup's sort indicator sits on the name column (sorted-desc) — and
        // descending Turkish collation is exactly its row order (Destek → Danışmanlık).
        var sortKey = SortKeys.Contains(sort) ? sort! : "name";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey == "name";

        List<DeptRowVm> ordered;
        var searching = !string.IsNullOrWhiteSpace(q);
        if (searching)
        {
            var needle = q!.Trim();
            ordered = [.. SortSiblings(all.Where(d =>
                    d.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                    || (d.Email?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (d.Manager?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false)),
                sortKey, desc)];
        }
        else
        {
            ordered = [];
            void Walk(int? parentId, int depth)
            {
                foreach (var d in SortSiblings(all.Where(x => x.ParentId == parentId), sortKey, desc))
                {
                    ordered.Add(d with { Depth = depth, HasChildren = all.Any(x => x.ParentId == d.Id) });
                    Walk(d.Id, depth + 1);
                }
            }
            Walk(null, 0);
            // Orphan safety: rows whose parent chain is broken still render (flat).
            ordered.AddRange(all.Where(d => !ordered.Any(o => o.Id == d.Id)));
        }

        var total = ordered.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return View(new DeptsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows, Flat: searching));
    }

    /// <summary>
    /// Toolbar bulk actions. Delete guard: a department referenced by child
    /// departments, staff (primary), tickets, tasks, help topics or canned responses
    /// is skipped and reported in the partial toast (the mockup has no reassign
    /// dialog — guard-skip per the teams precedent, flagged for canon sign-off).
    /// </summary>
    [HttpPost("/admin/departments/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("dp.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var depts = await db.Departments.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - depts.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var dept in depts)
            {
                switch (act)
                {
                    case "enable":
                        dept.IsActive = true;
                        dept.IsArchived = false;
                        ok++;
                        break;
                    case "disable":
                        dept.IsActive = false;
                        dept.IsArchived = false;
                        ok++;
                        break;
                    case "delete":
                        var inUse = await db.Departments.AnyAsync(d => d.ParentId == dept.Id, ct)
                            || await db.Staff.AnyAsync(s => s.DepartmentId == dept.Id, ct)
                            || await db.Tickets.AnyAsync(t => t.DepartmentId == dept.Id, ct)
                            || await db.TaskItems.AnyAsync(t => t.DepartmentId == dept.Id, ct)
                            || await db.HelpTopics.AnyAsync(t => t.DepartmentId == dept.Id, ct)
                            || await db.CannedResponses.AnyAsync(c => c.DepartmentId == dept.Id, ct);
                        if (inUse)
                        {
                            skipped++;
                        }
                        else
                        {
                            db.Departments.Remove(dept); // access rows cascade; email refs SetNull
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["DeptsToastOk"] = ok;
        TempData["DeptsToastSkipped"] = skipped;
        TempData["DeptsToast"] = skipped > 0 ? "dp.bulkPartial" : "dp.bulkDone";
        if (skipped > 0)
            TempData["DeptsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- department-edit.html -------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Departman" create form
    /// (the mockup's new-button links straight to department-edit.html).</summary>
    [HttpGet("/admin/department-edit")]
    [NavKey("departments")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        Department? dept = null;
        if (id is not null)
        {
            dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (dept is null)
                return NotFound();
        }

        return View(await BuildEditVmAsync(dept, ct));
    }

    /// <summary>
    /// Whole-page save (B3): general/email/autoresponse/alerts/signature columns plus
    /// the Erişim tab's member rows in one post. memberIds/memberRoles are parallel
    /// arrays in DOM order (one hidden id + one role select per row); alertIds carry
    /// the checked staff ids. Primary members (Staff.DepartmentId == this dept)
    /// cannot be removed here — a dropped primary row is restored and reported.
    /// </summary>
    [HttpPost("/admin/department-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, int? parentId, string? name, string? status, bool isPublic,
        int? slaId, int? scheduleId, int? managerId, string? assignMode,
        bool disableClaim, bool disableReopenAssign,
        int? emailId, int? templateSetId,
        bool arDisableNew, bool arDisableMsg, int? arEmailId,
        string? alertGroup, string? signature,
        int[] memberIds, int[] memberRoles, int[] alertIds,
        CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "de.errName");
        if (await db.Departments.AnyAsync(d => d.Name == name && d.ParentId == parentId && d.Id != id, ct))
            return EditToastBack(id, "de.errNameInUse");

        // Parent guard: must exist; never self or a descendant (cycle via Path).
        Department? parent = null;
        if (parentId is not null)
        {
            parent = await db.Departments.SingleOrDefaultAsync(d => d.Id == parentId, ct);
            if (parent is null)
                return EditToastBack(id, "de.errParent");
            if (id is not null && (parent.Id == id || parent.Path.Contains($"/{id}/")))
                return EditToastBack(id, "de.errParentCycle");
        }

        // Soft-ref selects: silently drop ids that no longer exist (row deleted since render).
        if (slaId is { } sla && !await db.SlaPlans.AnyAsync(s => s.Id == sla, ct)) slaId = null;
        if (scheduleId is { } sch && !await db.Schedules.AnyAsync(s => s.Id == sch, ct)) scheduleId = null;
        if (managerId is { } mgr && !await db.Staff.AnyAsync(s => s.Id == mgr, ct)) managerId = null;
        if (emailId is { } em && !await db.EmailAccounts.AnyAsync(e => e.Id == em, ct)) emailId = null;
        if (arEmailId is { } arem && !await db.EmailAccounts.AnyAsync(e => e.Id == arem, ct)) arEmailId = null;
        if (templateSetId is { } ts && !await db.EmailTemplateSets.AnyAsync(t => t.Id == ts, ct)) templateSetId = null;

        // Member rows: pair ids with roles by DOM order; unknown staff/roles are dropped.
        var posted = new Dictionary<int, int>(); // staffId → roleId
        for (var i = 0; i < memberIds.Length && i < memberRoles.Length; i++)
            posted[memberIds[i]] = memberRoles[i];
        var validStaff = await db.Staff.Where(s => posted.Keys.Contains(s.Id))
            .Select(s => new { s.Id, s.DepartmentId }).ToListAsync(ct);
        var validRoles = (await db.Roles.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var alerts = alertIds.ToHashSet();

        var primaryKept = false;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Department dept;
            if (id is null)
            {
                dept = new Department { Name = name };
                db.Departments.Add(dept);
            }
            else
            {
                var found = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct);
                if (found is null)
                    return NotFound();
                dept = found;
            }

            dept.Name = name;
            dept.ParentId = parentId;
            (dept.IsActive, dept.IsArchived) = status switch
            {
                "disabled" => (false, false),
                "archived" => (false, true),
                _ => (true, false),
            };
            dept.IsPublic = isPublic;
            dept.SlaId = slaId;
            dept.ScheduleId = scheduleId;
            dept.ManagerStaffId = managerId;
            (dept.AssignMembersOnly, dept.AssignPrimaryOnly) = assignMode switch
            {
                "members" => (true, false),
                "primary" => (false, true),
                _ => (false, false),
            };
            dept.DisableAutoClaim = disableClaim;
            dept.DisableReopenAutoAssign = disableReopenAssign;
            dept.EmailAccountId = emailId;
            dept.TemplateSetId = templateSetId;
            // The mockup switches are "disable …" — inverted flags.
            dept.TicketAutoResponse = !arDisableNew;
            dept.MessageAutoResponse = !arDisableMsg;
            dept.AutoResponseEmailAccountId = arEmailId;
            dept.AlertGroup = alertGroup switch
            {
                "manager" => DepartmentAlertGroup.ManagerOnly,
                "membersPrimary" => DepartmentAlertGroup.MembersAndPrimary,
                _ => DepartmentAlertGroup.All,
            };
            dept.Signature = signature ?? "";

            await db.SaveChangesAsync(ct); // dept.Id materializes for create

            // Materialized path for the subtree (osTicket dept path semantics):
            // Path = "/<ancestors>/<selfId>/"; a parent change rewrites every
            // descendant's prefix.
            var newPath = $"{parent?.Path ?? "/"}{dept.Id}/";
            if (dept.Path != newPath)
            {
                var oldPath = dept.Path;
                dept.Path = newPath;
                if (id is not null && oldPath != "/")
                {
                    var descendants = await db.Departments
                        .Where(d => d.Id != dept.Id && d.Path.StartsWith(oldPath))
                        .ToListAsync(ct);
                    foreach (var d in descendants)
                        d.Path = string.Concat(newPath, d.Path.AsSpan(oldPath.Length));
                }
            }

            // ---- Erişim reconciliation ------------------------------------------------
            var primaries = await db.Staff.Where(s => s.DepartmentId == dept.Id).ToListAsync(ct);
            var accessRows = await db.Set<StaffDepartmentAccess>()
                .Where(a => a.DepartmentId == dept.Id).ToListAsync(ct);

            foreach (var member in primaries)
            {
                if (posted.TryGetValue(member.Id, out var roleId))
                {
                    // The role column IS the member's authority here — for a primary
                    // member that is their primary role (mockup de.membersHelp).
                    if (validRoles.Contains(roleId))
                        member.RoleId = roleId;
                    member.PrimaryDepartmentAlerts = alerts.Contains(member.Id);
                }
                else
                {
                    primaryKept = true; // dropped row restored — reported in the toast
                }
            }

            var primaryIds = primaries.Select(p => p.Id).ToHashSet();
            var wanted = validStaff.Where(s => !primaryIds.Contains(s.Id)).ToList();
            db.RemoveRange(accessRows.Where(a => !wanted.Any(w => w.Id == a.StaffId)));
            foreach (var s in wanted)
            {
                var row = accessRows.FirstOrDefault(a => a.StaffId == s.Id);
                if (row is null)
                {
                    if (validRoles.Contains(posted[s.Id]))
                    {
                        db.Add(new StaffDepartmentAccess
                        {
                            StaffId = s.Id, DepartmentId = dept.Id,
                            RoleId = posted[s.Id], AlertsEnabled = alerts.Contains(s.Id),
                        });
                    }
                }
                else
                {
                    if (validRoles.Contains(posted[s.Id]))
                        row.RoleId = posted[s.Id];
                    row.AlertsEnabled = alerts.Contains(s.Id);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["DeptsToast"] = primaryKept ? "dp.toastPrimaryKept"
            : id is null ? "dp.toastCreated" : "dp.toastSaved";
        if (primaryKept)
            TempData["DeptsToastError"] = true;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Erişim tab export button: the member table as CSV (UTF-8 BOM, orgs precedent).</summary>
    [HttpGet("/admin/department-edit/export")]
    public async Task<IActionResult> ExportMembers(
        int id,
        [FromServices] IStringLocalizerFactory localizerFactory,
        [FromServices] IStringLocalizer<SharedResources> sl,
        CancellationToken ct = default)
    {
        var dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (dept is null)
            return NotFound();

        var members = await LoadMembersAsync(id, ct);

        var pageL = localizerFactory.Create("Areas.Admin.Views.Departments.Edit",
            typeof(Program).Assembly.GetName().Name!);
        string[] headers =
            [pageL["de.colAgent"], pageL["de.primaryBadge"], pageL["de.colRole"], pageL["de.colAlerts"]];

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=departman-uyeleri.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", headers.Select(TicketListEngine.Csv)));
        foreach (var m in members)
        {
            string[] cells =
            [
                m.Name,
                m.IsPrimary ? sl["common.yes"] : sl["common.no"],
                m.RoleName,
                m.Alerts ? sl["common.yes"] : sl["common.no"],
            ];
            await writer.WriteLineAsync(string.Join(",", cells.Select(TicketListEngine.Csv)));
        }
        return new EmptyResult();
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<DeptEditVm> BuildEditVmAsync(Department? dept, CancellationToken ct)
    {
        // Parent select: every department except self and its descendants (cycle guard).
        var parents = await db.Departments
            .OrderBy(d => d.Path)
            .Select(d => new DeptOptionVm(d.Id, d.Name, d.Path))
            .ToListAsync(ct);
        if (dept is not null)
            parents.RemoveAll(p => p.Id == dept.Id || p.Path.Contains($"/{dept.Id}/"));

        var members = dept is null ? [] : await LoadMembersAsync(dept.Id, ct);

        // Editor h1 "Destek / Bordro" = the ancestor chain + own name.
        string? crumb = null;
        if (dept is not null)
        {
            var byId = parents.ToDictionary(p => p.Id, p => p.Name);
            var chain = dept.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .Where(pid => pid != dept.Id && byId.ContainsKey(pid))
                .Select(pid => byId[pid]);
            crumb = string.Join(" / ", chain.Append(dept.Name));
        }

        var currentScheduleId = dept?.ScheduleId;
        return new DeptEditVm(
            dept, crumb, parents,
            await db.SlaPlans.OrderBy(s => s.Name).Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            // Inactive schedules stay pickable only while currently selected (S7
            // schedules bulk enable/disable).
            await db.Schedules.Where(s => s.IsActive || s.Id == currentScheduleId)
                .OrderBy(s => s.Name).Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            await db.Staff.Where(s => s.IsActive).OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new OptionVm(s.Id, s.FirstName + " " + s.LastName)).ToListAsync(ct),
            await db.EmailAccounts.OrderBy(e => e.Address).Select(e => new OptionVm(e.Id, e.Address)).ToListAsync(ct),
            await db.EmailTemplateSets.OrderBy(t => t.Id).Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct),
            // Role option order = the seed/mockup order (Yönetici → Salt Okunur).
            await db.Roles.OrderBy(r => r.Id).Select(r => new OptionVm(r.Id, r.Name)).ToListAsync(ct),
            members);
    }

    /// <summary>Member table rows: primary members first (badge), then extended access.</summary>
    private async Task<List<DeptMemberVm>> LoadMembersAsync(int deptId, CancellationToken ct)
    {
        var primaries = await db.Staff.Where(s => s.DepartmentId == deptId)
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new DeptMemberVm(
                s.Id, s.FirstName + " " + s.LastName, true, s.RoleId,
                s.Role!.Name, s.PrimaryDepartmentAlerts))
            .ToListAsync(ct);
        var extended = await db.Set<StaffDepartmentAccess>()
            .Where(a => a.DepartmentId == deptId)
            .OrderBy(a => a.Staff!.FirstName).ThenBy(a => a.Staff!.LastName)
            .Select(a => new DeptMemberVm(
                a.StaffId, a.Staff!.FirstName + " " + a.Staff.LastName, false, a.RoleId,
                a.Role!.Name, a.AlertsEnabled))
            .ToListAsync(ct);
        return [.. primaries, .. extended];
    }

    private static IEnumerable<DeptRowVm> SortSiblings(IEnumerable<DeptRowVm> rows, string key, bool desc)
    {
        var tr = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
        IOrderedEnumerable<DeptRowVm> sorted = key switch
        {
            "status" => desc ? rows.OrderByDescending(d => d.Status) : rows.OrderBy(d => d.Status),
            "type" => desc ? rows.OrderByDescending(d => d.IsPublic) : rows.OrderBy(d => d.IsPublic),
            "manager" => desc
                ? rows.OrderByDescending(d => d.Manager ?? "", tr)
                : rows.OrderBy(d => d.Manager ?? "", tr),
            _ => desc ? rows.OrderByDescending(d => d.Name, tr) : rows.OrderBy(d => d.Name, tr),
        };
        return sorted.ThenBy(d => d.Id);
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["DeptsToast"] = key;
        if (error)
            TempData["DeptsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["DeptEditToast"] = key;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public enum DeptStatus
{
    Active,
    Disabled,
    Archived,
}

public sealed record DeptsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<DeptRowVm> Rows,
    bool Flat);

public sealed record DeptRowVm(
    int Id,
    string Name,
    int? ParentId,
    string Path,
    DeptStatus Status,
    bool IsPublic,
    int Agents,
    string? Email,
    string? Manager,
    int Depth)
{
    public bool HasChildren { get; init; }
}

public sealed record DeptEditVm(
    Department? Dept,
    string? Crumb,
    IReadOnlyList<DeptOptionVm> Parents,
    IReadOnlyList<OptionVm> Slas,
    IReadOnlyList<OptionVm> Schedules,
    IReadOnlyList<OptionVm> Managers,
    IReadOnlyList<OptionVm> Emails,
    IReadOnlyList<OptionVm> TemplateSets,
    IReadOnlyList<OptionVm> Roles,
    IReadOnlyList<DeptMemberVm> Members);

public sealed record DeptOptionVm(int Id, string Name, string Path);

public sealed record DeptMemberVm(
    int StaffId, string Name, bool IsPrimary, int RoleId, string RoleName, bool Alerts);
