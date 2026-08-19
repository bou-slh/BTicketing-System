using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin teams list (mockups/admin/teams.html, ROADMAP §6.3): the B1 engine over Team
/// rows, per-row edit dialogs prefilled server-side (B2 — the mockup shares one
/// hardcoded dlg-team between the new-button and every row; split into a create
/// dialog + per-row dialogs, canned precedent), the member roster as add/remove rows
/// (B4, rd.js data-roster contract) against real Staff, and toolbar bulk
/// enable/disable/delete. Delete is guarded: a team referenced by tickets, tasks or
/// help-topic routing is skipped and reported in the partial toast.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class TeamsController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "status", "lead", "updated"];

    [HttpGet("/admin/teams")]
    [NavKey("teams")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        // Dialog data: every member row + the full staff option list (roster select
        // options for already-member staff render hidden — rd.js restores them on ✕).
        var ids = rows.Select(r => r.Id).ToArray();
        var members = await db.Teams
            .Where(t => ids.Contains(t.Id))
            .SelectMany(t => t.Members
                .OrderBy(m => m.Staff!.FirstName).ThenBy(m => m.Staff!.LastName)
                .Select(m => new TeamMemberVm(t.Id, m.StaffId,
                    m.Staff!.FirstName + " " + m.Staff.LastName, m.AlertsEnabled)))
            .ToListAsync(ct);
        var staffOptions = await db.Staff
            .Where(s => s.IsActive)
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new StaffOptionVm(s.Id, s.FirstName + " " + s.LastName))
            .ToListAsync(ct);

        return View(new TeamsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            rows, members.ToLookup(m => m.TeamId), staffOptions));
    }

    /// <summary>Create dialog submit: new team + roster in one post.</summary>
    [HttpPost("/admin/teams/create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(
        string? name, bool active, int? leadId, bool noAlerts, string? notes,
        int[] memberIds, int[] alertIds, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(null, name, active, leadId, noAlerts, notes, memberIds, alertIds, returnUrl, ct);

    /// <summary>Per-row edit dialog submit (B2): saves fields + the member roster.</summary>
    [HttpPost("/admin/teams/update")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Update(
        int id, string? name, bool active, int? leadId, bool noAlerts, string? notes,
        int[] memberIds, int[] alertIds, string? returnUrl, CancellationToken ct) =>
        UpsertAsync(id, name, active, leadId, noAlerts, notes, memberIds, alertIds, returnUrl, ct);

    /// <summary>
    /// Toolbar bulk actions over the selection. Delete guard: assignments (tickets,
    /// tasks) and help-topic routing reference teams by id — referenced teams are
    /// skipped (honest guard; thread-event history is a snapshot and does not block).
    /// </summary>
    [HttpPost("/admin/teams/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("tm.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var teams = await db.Teams.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - teams.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var team in teams)
            {
                switch (act)
                {
                    case "enable":
                        team.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        team.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        var inUse = await db.Tickets.AnyAsync(t => t.TeamId == team.Id, ct)
                            || await db.TaskItems.AnyAsync(t => t.TeamId == team.Id, ct)
                            || await db.HelpTopics.AnyAsync(t => t.TeamId == team.Id, ct);
                        if (inUse)
                        {
                            skipped++;
                        }
                        else
                        {
                            db.Teams.Remove(team); // members cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["TeamsToastOk"] = ok;
        TempData["TeamsToastSkipped"] = skipped;
        TempData["TeamsToast"] = skipped > 0 ? "tm.bulkPartial" : "tm.bulkDone";
        if (skipped > 0)
            TempData["TeamsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers --------------------------------------------------------------------------

    private async Task<IActionResult> UpsertAsync(
        int? id, string? name, bool active, int? leadId, bool noAlerts, string? notes,
        int[] memberIds, int[] alertIds, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return ToastBack("tm.errName", error: true, returnUrl: returnUrl);
        if (await db.Teams.AnyAsync(t => t.Name == name && t.Id != id, ct))
            return ToastBack("tm.errNameInUse", error: true, returnUrl: returnUrl);

        // Roster ids must be real staff; the lead select lists all active staff and
        // is optional per the mockup's help text (empty option invented, flagged).
        var wanted = memberIds.Distinct().ToArray();
        var validIds = await db.Staff.Where(s => wanted.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        if (leadId is { } lid && !await db.Staff.AnyAsync(s => s.Id == lid, ct))
            leadId = null;
        var alerts = alertIds.ToHashSet();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Team team;
            if (id is null)
            {
                team = new Team { Name = name };
                db.Teams.Add(team);
            }
            else
            {
                var found = await db.Teams.Include(t => t.Members)
                    .SingleOrDefaultAsync(t => t.Id == id, ct);
                if (found is null)
                    return NotFound();
                team = found;
            }

            team.Name = name;
            team.IsActive = active;
            team.LeadStaffId = leadId;
            team.NoAlerts = noAlerts;
            team.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            // Roster reconciliation (B4): remove missing rows, add new ones, sync the
            // per-member alert checkbox.
            team.Members.RemoveAll(m => !validIds.Contains(m.StaffId));
            foreach (var staffId in validIds)
            {
                var member = team.Members.FirstOrDefault(m => m.StaffId == staffId);
                if (member is null)
                    team.Members.Add(new TeamMember { StaffId = staffId, AlertsEnabled = alerts.Contains(staffId) });
                else
                    member.AlertsEnabled = alerts.Contains(staffId);
            }

            await db.SaveChangesAsync(ct);
        }

        return ToastBack(id is null ? "tm.toastCreated" : "tm.toastSaved", returnUrl: returnUrl);
    }

    /// <summary>B1 core: search + member counts + lead name + sort.</summary>
    private IQueryable<TeamRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.Teams.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern));
        }

        var projected = query.Select(t => new
        {
            t.Id, t.Name, t.IsActive, t.LeadStaffId, t.NoAlerts, t.Notes,
            MemberCount = t.Members.Count,
            Lead = db.Staff.Where(s => s.Id == t.LeadStaffId)
                .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            Updated = t.UpdatedAt ?? t.CreatedAt,
        });

        // The mockup's sort indicator sits on the name column (sorted-desc).
        var key = sortKey = SortKeys.Contains(sort) ? sort! : "name";
        var d = desc = dir == "asc" ? false : dir == "desc" || key is "name" or "updated";
        projected = (key, d) switch
        {
            ("status", true) => projected.OrderByDescending(t => t.IsActive).ThenBy(t => t.Name),
            ("status", false) => projected.OrderBy(t => t.IsActive).ThenBy(t => t.Name),
            ("lead", true) => projected.OrderByDescending(t => t.Lead ?? "").ThenBy(t => t.Id),
            ("lead", false) => projected.OrderBy(t => t.Lead ?? "").ThenBy(t => t.Id),
            ("updated", true) => projected.OrderByDescending(t => t.Updated).ThenByDescending(t => t.Id),
            ("updated", false) => projected.OrderBy(t => t.Updated).ThenBy(t => t.Id),
            (_, true) => projected.OrderByDescending(t => t.Name).ThenBy(t => t.Id),
            _ => projected.OrderBy(t => t.Name).ThenBy(t => t.Id),
        };
        return projected.Select(t => new TeamRowVm(
            t.Id, t.Name, t.IsActive, t.MemberCount, t.LeadStaffId, t.Lead, t.NoAlerts, t.Notes, t.Updated));
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["TeamsToast"] = key;
        if (error)
            TempData["TeamsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record TeamsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<TeamRowVm> Rows,
    ILookup<int, TeamMemberVm> Members,
    IReadOnlyList<StaffOptionVm> StaffOptions);

public sealed record TeamRowVm(
    int Id,
    string Name,
    bool IsActive,
    int MemberCount,
    int? LeadStaffId,
    string? LeadName,
    bool NoAlerts,
    string? Notes,
    DateTimeOffset Updated);

public sealed record TeamMemberVm(int TeamId, int StaffId, string Name, bool AlertsEnabled);

public sealed record StaffOptionVm(int Id, string Name);
