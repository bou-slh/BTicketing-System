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
/// Agent user detail (mockups/agent/user-view.html, ROADMAP §6.2): profile + account
/// cards from data with the org-inherited fields ("Şirketten" badge + "Geçersiz kıl"
/// per-user override, osTicket user/organization inheritance), tabs from data
/// (tickets for this user, notes), the B5 note composer onto UserNote rows, and the
/// header actions (Düzenle / Hesabı Yönet / Sil) as B2 dialogs through UserService.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class UserViewController(AppDbContext db, IUserService userService) : Controller
{
    [HttpGet("/agent/user-view")]
    [NavKey("users")]
    public async Task<IActionResult> Index(int id, string? tab, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var user = await db.Users
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.Id, u.Name, u.Phone, u.Address, u.IsBlocked, u.CreatedAt,
                u.IdentityUserId, u.OrganizationId,
                Email = u.DefaultEmail != null
                    ? u.DefaultEmail.Address
                    : u.Emails.Select(e => e.Address).FirstOrDefault(),
                OrgName = u.Organization != null ? u.Organization.Name : null,
                OrgPhone = u.Organization != null ? u.Organization.Phone : null,
                OrgAddress = u.Organization != null ? u.Organization.Address : null,
            })
            .SingleOrDefaultAsync(ct);
        if (user is null)
            return NotFound();

        // ---- Account card: portal Identity principal (osTicket user_account split) ---
        var identity = user.IdentityUserId is { } identityId
            ? await db.CustomerUsers
                .Where(c => c.Id == identityId)
                .Select(c => new { c.TimeZone, c.TwoFactorEnabled })
                .SingleOrDefaultAsync(ct)
            : null;

        // ---- Tabs from data ---------------------------------------------------------
        var tickets = await db.Tickets
            .Where(t => t.UserId == id)
            .OrderByDescending(t => t.UpdatedAt ?? t.CreatedAt)
            .Select(t => new UserTicketRowVm(
                t.Id, t.Number, t.Subject,
                // Derived status (§2 canon, tickets-page precedent): overdue flag wins
                // on open tickets, else the status key.
                t.IsOverdue && t.Status!.State == TicketState.Open ? "overdue" : t.Status!.Key,
                t.UpdatedAt ?? t.CreatedAt))
            .ToListAsync(ct);

        var notes = await db.UserNotes
            .Where(n => n.UserId == id)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(n => new UserNoteVm(n.AuthorName, n.CreatedAt, n.Body))
            .ToListAsync(ct);

        var organizations = await db.Organizations
            .OrderBy(o => o.Id)
            .Select(o => new OrgOptionVm(o.Id, o.Name))
            .ToListAsync(ct);

        return View(new UserViewVm(
            user.Id, user.Name, user.Email,
            user.OrganizationId, user.OrgName,
            // Inheritance (osTicket user/organization parity): a per-user value
            // overrides; null falls back to the organization with the badge.
            new InheritedFieldVm(user.Phone ?? user.OrgPhone, user.Phone is null && user.OrgPhone is not null, user.OrgPhone),
            new InheritedFieldVm(user.Address ?? user.OrgAddress, user.Address is null && user.OrgAddress is not null, user.OrgAddress),
            user.Phone, user.Address,
            user.IsBlocked ? "locked" : user.IdentityUserId != null ? "active" : "guest",
            user.CreatedAt,
            identity?.TimeZone,
            user.IdentityUserId is not null,
            identity?.TwoFactorEnabled == true,
            tab == "notes" ? "notes" : "tickets",
            tickets, notes, organizations));
    }

    // ---- B5 note composer -------------------------------------------------------------

    /// <summary>Note composer: appends a UserNote (B5) and reopens the Notlar tab.</summary>
    [HttpPost("/agent/user-view/note")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Note(int id, string? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
            return RedirectBack(id, "uv.errEmpty", error: true, tab: "notes");
        return await RunAsync(id, "uv.toastNote",
            (actor, c) => userService.AddNoteAsync(id, body, actor, c), ct, tab: "notes");
    }

    // ---- Header actions + overrides (B2/B3 → UserService) -----------------------------

    /// <summary>"Geçersiz kıl" inline form: stores a per-user phone/address override.</summary>
    [HttpPost("/agent/user-view/override")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Override(int id, string field, string? value, CancellationToken ct) =>
        RunAsync(id, "uv.toastSaved", (actor, c) => userService.OverrideAsync(id, field, value, actor, c), ct);

    /// <summary>dlg-edit: name + phone/address (blank = inherit from the company) + company.</summary>
    [HttpPost("/agent/user-view/edit")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(
        int id, string? name, string? phone, string? address, int? orgId, CancellationToken ct) =>
        RunAsync(id, "uv.toastSaved", (actor, c) => userService.UpdateAsync(id, new UserProfileUpdate
        {
            Name = name ?? "",
            Phone = phone,
            Address = address,
            OrganizationId = orgId,
        }, actor, c), ct);

    /// <summary>dlg-manage: Kilitle / Kilidi Aç (IsBlocked, osTicket lock bit).</summary>
    [HttpPost("/agent/user-view/manage")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Manage(int id, string act, CancellationToken ct) =>
        act switch
        {
            "lock" => RunAsync(id, "uv.toastBlocked", (actor, c) => userService.SetBlockedAsync(id, true, actor, c), ct),
            "unlock" => RunAsync(id, "uv.toastUnblocked", (actor, c) => userService.SetBlockedAsync(id, false, actor, c), ct),
            _ => Task.FromResult<IActionResult>(BadRequest()),
        };

    /// <summary>dlg-delete: hard delete (guarded — users with tickets are refused), back to the list.</summary>
    [HttpPost("/agent/user-view/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await userService.DeleteAsync(id, actor, ct);
        }
        catch (DomainRuleException)
        {
            return RedirectBack(id, "uv.errHasTickets", error: true);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "uv.errDenied", error: true);
        }

        TempData["UsersToast"] = "us.toastDeleted";
        return Redirect("/agent/users");
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<IActionResult> RunAsync(
        int id, string? successKey, Func<ActorContext, CancellationToken, Task> action,
        CancellationToken ct, string? tab = null)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        if (!await db.Users.AnyAsync(u => u.Id == id, ct))
            return NotFound();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await action(actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return RedirectBack(id, ex.Code == "invalid" ? "uv.errEmpty" : "uv.errDenied", error: true, tab);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "uv.errDenied", error: true, tab);
        }
        return RedirectBack(id, successKey, error: false, tab);
    }

    private IActionResult RedirectBack(int id, string? toastKey, bool error = false, string? tab = null)
    {
        if (toastKey is not null)
        {
            TempData["UvToast"] = toastKey;
            if (error)
                TempData["UvToastError"] = true;
        }
        return RedirectToAction(nameof(Index), tab is null ? new { id } : new { id, tab });
    }
}

public sealed record UserViewVm(
    int Id,
    string Name,
    string? Email,
    int? OrgId,
    string? OrgName,
    InheritedFieldVm Phone,
    InheritedFieldVm Address,
    string? OwnPhone,
    string? OwnAddress,
    string StatusKey,
    DateTimeOffset Registered,
    string? TimeZoneId,
    bool HasIdentity,
    bool TwoFactorEnabled,
    string ActiveTab,
    IReadOnlyList<UserTicketRowVm> Tickets,
    IReadOnlyList<UserNoteVm> Notes,
    IReadOnlyList<OrgOptionVm> Organizations);

/// <summary>Value shown on the profile row; Inherited=true renders the "Şirketten" badge + override affordance.</summary>
public sealed record InheritedFieldVm(string? Value, bool Inherited, string? OrgValue);

public sealed record UserTicketRowVm(int Id, string Number, string Subject, string StatusKey, DateTimeOffset Updated);

public sealed record UserNoteVm(string AuthorName, DateTimeOffset At, string Body);
