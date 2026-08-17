using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent organization detail (mockups/agent/org-view.html, ROADMAP §6.2): profile +
/// sync cards from data — the sync card's switches persist the three osTicket
/// collaboration/assignment flags and the success banner reflects the actual saved
/// state instead of the mockup's static show-on-any-click — tabs from data (members
/// with user-view deep links, org-wide tickets, notes), the B5 note composer onto
/// OrgNote rows, and the header actions (Düzenle / Sil) as B2 dialogs through
/// OrgService.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class OrgViewController(AppDbContext db, IOrgService orgService) : Controller
{
    /// <summary>Members-tab page size (mockup pagination: "1–5 / 14").</summary>
    public const int MemberPageSize = 5;

    [HttpGet("/agent/org-view")]
    [NavKey("orgs")]
    public async Task<IActionResult> Index(int id, string? tab, int mpage = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var org = await db.Organizations
            .Where(o => o.Id == id)
            .Select(o => new
            {
                o.Id, o.Name, o.Domain, o.Sector, o.Phone, o.Address, o.ManagerStaffId,
                o.ShareTicketsWithMembers, o.CcPrimaryContacts, o.AssignToManager,
                o.CreatedAt, o.UpdatedAt,
                ManagerName = db.Staff.Where(s => s.Id == o.ManagerStaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (org is null)
            return NotFound();

        // ---- Tabs from data ---------------------------------------------------------
        var memberQuery = db.Users.Where(u => u.OrganizationId == id);
        var memberTotal = await memberQuery.CountAsync(ct);
        var memberPages = Math.Max(1, (int)Math.Ceiling(memberTotal / (double)MemberPageSize));
        mpage = Math.Clamp(mpage, 1, memberPages);
        var members = await memberQuery
            .OrderBy(u => u.Name).ThenBy(u => u.Id)
            .Skip((mpage - 1) * MemberPageSize)
            .Take(MemberPageSize)
            .Select(u => new OrgMemberRowVm(
                u.Id, u.Name,
                u.DefaultEmail != null
                    ? u.DefaultEmail.Address
                    : u.Emails.Select(e => e.Address).FirstOrDefault(),
                // Phone inheritance (user-view parity): null inherits the org phone
                // with the "Şirketten" badge.
                u.Phone, u.Phone == null,
                u.IsBlocked ? "locked" : u.IdentityUserId != null ? "active" : "guest"))
            .ToListAsync(ct);

        var tickets = await db.Tickets
            .Where(t => t.User!.OrganizationId == id)
            .OrderByDescending(t => t.UpdatedAt ?? t.CreatedAt)
            .Select(t => new UserTicketRowVm(
                t.Id, t.Number, t.Subject,
                // Derived status (§2 canon, user-view precedent): overdue wins on open.
                t.IsOverdue && t.Status!.State == TicketState.Open ? "overdue" : t.Status!.Key,
                t.UpdatedAt ?? t.CreatedAt))
            .ToListAsync(ct);

        var notes = await db.OrgNotes
            .Where(n => n.OrganizationId == id)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(n => new UserNoteVm(n.AuthorName, n.CreatedAt, n.Body))
            .ToListAsync(ct);

        var openCount = await db.Tickets
            .CountAsync(t => t.User!.OrganizationId == id && t.Status!.State == TicketState.Open, ct);

        return View(new OrgViewVm(
            org.Id, org.Name, org.Domain, org.Sector, org.Phone, org.Address,
            org.ManagerStaffId, org.ManagerName,
            org.ShareTicketsWithMembers, org.CcPrimaryContacts, org.AssignToManager,
            org.UpdatedAt ?? org.CreatedAt,
            memberTotal, openCount,
            tab is "tickets" or "notes" ? tab : "users",
            mpage, memberPages,
            members, tickets, notes,
            await db.Staff.Where(s => s.IsActive)
                .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new StaffOptionVm(s.Id, s.FirstName + " " + s.LastName))
                .ToListAsync(ct)));
    }

    // ---- Sync card (B3) ---------------------------------------------------------------

    /// <summary>
    /// Sync switches: persists the three flags; the PRG banner then renders from the
    /// actual saved values (the mockup's banner shows on any checkbox click).
    /// </summary>
    [HttpPost("/agent/org-view/sync")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Sync(int id, bool share, bool cc, bool assign, CancellationToken ct) =>
        RunAsync(id, null, async (actor, c) =>
        {
            await orgService.SetSyncFlagsAsync(id, share, cc, assign, actor, c);
            TempData["OvSynced"] = true;
        }, ct);

    // ---- B5 note composer -------------------------------------------------------------

    /// <summary>Note composer: appends an OrgNote (B5) and reopens the Notlar tab.</summary>
    [HttpPost("/agent/org-view/note")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Note(int id, string? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
            return RedirectBack(id, "ov.errEmpty", error: true, tab: "notes");
        return await RunAsync(id, "ov.toastNote",
            (actor, c) => orgService.AddNoteAsync(id, body, actor, c), ct, tab: "notes");
    }

    // ---- Header actions (B2 → OrgService) ---------------------------------------------

    /// <summary>dlg-edit: profile fields (name/domain/sector/phone/address/manager).</summary>
    [HttpPost("/agent/org-view/edit")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(
        int id, string? name, string? domain, string? sector, string? phone, string? address,
        int? managerId, CancellationToken ct) =>
        RunAsync(id, "ov.toastSaved", (actor, c) => orgService.UpdateAsync(id, new OrgProfileUpdate
        {
            Name = name ?? "",
            Domain = domain,
            Sector = sector,
            Phone = phone,
            Address = address,
            ManagerStaffId = managerId,
        }, actor, c), ct);

    /// <summary>dlg-delete: hard delete (guarded — orgs with members/tickets are refused), back to the list.</summary>
    [HttpPost("/agent/org-view/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await orgService.DeleteAsync(id, actor, ct);
        }
        catch (DomainRuleException)
        {
            return RedirectBack(id, "ov.errHasMembers", error: true);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "ov.errDenied", error: true);
        }

        TempData["OrgsToast"] = "og.toastDeleted";
        return Redirect("/agent/orgs");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<IActionResult> RunAsync(
        int id, string? successKey, Func<ActorContext, CancellationToken, Task> action,
        CancellationToken ct, string? tab = null)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        if (!await db.Organizations.AnyAsync(o => o.Id == id, ct))
            return NotFound();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await action(actor, ct);
        }
        catch (DomainRuleException ex)
        {
            var key = ex.Code switch
            {
                "name-in-use" => "og.errNameInUse",
                "invalid" => "ov.errEmpty",
                _ => "ov.errDenied",
            };
            return RedirectBack(id, key, error: true, tab);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "ov.errDenied", error: true, tab);
        }
        return RedirectBack(id, successKey, error: false, tab);
    }

    private IActionResult RedirectBack(int id, string? toastKey, bool error = false, string? tab = null)
    {
        if (toastKey is not null)
        {
            TempData["OvToast"] = toastKey;
            if (error)
                TempData["OvToastError"] = true;
        }
        return RedirectToAction(nameof(Index), tab is null ? new { id } : new { id, tab });
    }
}

public sealed record OrgViewVm(
    int Id,
    string Name,
    string? Domain,
    string? Sector,
    string? Phone,
    string? Address,
    int? ManagerStaffId,
    string? ManagerName,
    bool ShareTickets,
    bool CcPrimary,
    bool AssignManager,
    DateTimeOffset LastSync,
    int MemberTotal,
    int OpenCount,
    string ActiveTab,
    int MemberPage,
    int MemberPageCount,
    IReadOnlyList<OrgMemberRowVm> Members,
    IReadOnlyList<UserTicketRowVm> Tickets,
    IReadOnlyList<UserNoteVm> Notes,
    IReadOnlyList<StaffOptionVm> StaffOptions);

/// <summary>PhoneInherited=true renders the org phone with the "Şirketten" badge (user-view parity).</summary>
public sealed record OrgMemberRowVm(
    int Id,
    string Name,
    string? Email,
    string? OwnPhone,
    bool PhoneInherited,
    string StatusKey);
