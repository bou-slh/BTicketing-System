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
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent user directory (mockups/agent/users.html, ROADMAP §6.2): the B1 engine
/// (sort/search/pagination/selection) over all end-users, the Tümü/Arşiv tab bar,
/// dlg-adduser creating a real User (+email, org auto-link) through UserService,
/// the "Diğer" bulk menu (Kilitle / Kilidi Aç / Şirkete Ekle / Sil) over the row
/// selection, and CSV import (name,email[,org]).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class UsersController(
    AppDbContext db,
    IUserService userService,
    IMailLinkTokenService tokens,
    IPortalAccountMailer accountMail) : Controller
{
    public const int PageSize = 8;

    /// <summary>Mockup tab order (us-panel-all / us-panel-archive).</summary>
    public static readonly string[] Tabs = ["all", "archive"];

    private static readonly string[] SortKeys = ["name", "email", "org", "reg", "updated"];

    [HttpGet("/agent/users")]
    [NavKey("users")]
    public async Task<IActionResult> Index(
        string? tab, string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var activeTab = Tabs.Contains(tab) ? tab! : "all";

        // ---- Live tab counts. The entity has no archive flag (deletes are hard,
        // osTicket parity) — the Arşiv tab honestly counts 0 and renders the
        // mockup's own empty state; flagged in ROADMAP. -----------------------------
        var counts = new Dictionary<string, int>
        {
            ["all"] = await db.Users.CountAsync(ct),
            ["archive"] = 0,
        };

        // ---- B1 list: quick search, sort, pagination --------------------------------
        var query = db.Users.AsQueryable();
        if (activeTab == "archive")
            query = query.Where(_ => false);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Name, pattern)
                || u.Emails.Any(e => EF.Functions.ILike(e.Address, pattern)));
        }

        var sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        // Text columns read naturally ascending; date columns default newest-first.
        var desc = dir == "desc" || (dir != "asc" && sortKey is "reg" or "updated");
        query = (sortKey, desc) switch
        {
            ("name", true) => query.OrderByDescending(u => u.Name),
            ("name", false) => query.OrderBy(u => u.Name),
            ("email", true) => query.OrderByDescending(u => u.DefaultEmail!.Address),
            ("email", false) => query.OrderBy(u => u.DefaultEmail!.Address),
            ("org", true) => query.OrderByDescending(u => u.Organization != null ? u.Organization.Name : ""),
            ("org", false) => query.OrderBy(u => u.Organization != null ? u.Organization.Name : ""),
            ("reg", true) => query.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id),
            ("reg", false) => query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id),
            (_, false) => query.OrderBy(u => u.UpdatedAt ?? u.CreatedAt).ThenBy(u => u.Id),
            _ => query.OrderByDescending(u => u.UpdatedAt ?? u.CreatedAt).ThenByDescending(u => u.Id),
        };

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .Select(u => new UserRowVm(
                u.Id, u.Name,
                u.DefaultEmail != null
                    ? u.DefaultEmail.Address
                    : u.Emails.Select(e => e.Address).FirstOrDefault(),
                u.OrganizationId,
                u.Organization != null ? u.Organization.Name : null,
                // Status pill (§2 canon pills): Kilitli > Aktif (portal account) > Misafir.
                u.IsBlocked ? "locked" : u.IdentityUserId != null ? "active" : "guest",
                u.CreatedAt,
                u.UpdatedAt ?? u.CreatedAt))
            .ToListAsync(ct);

        var organizations = await db.Organizations
            .OrderBy(o => o.Id)
            .Select(o => new OrgOptionVm(o.Id, o.Name))
            .ToListAsync(ct);

        return View(new UsersIndexVm(
            activeTab, counts, q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            rows, organizations));
    }

    /// <summary>dlg-adduser submit: creates the User (+email, org auto-link) and lands on its user-view.</summary>
    [HttpPost("/agent/users/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? name, string? email, int? orgId, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        Domain.Entities.User created;
        try
        {
            created = await userService.CreateAsync(new UserCreateRequest
            {
                Name = name ?? "",
                Email = email ?? "",
                OrganizationId = orgId,
            }, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return ToastBack(ex.Code == "email-in-use" ? "us.errEmailInUse" : "us.errInvalid", error: true);
        }
        catch (DomainException)
        {
            return ToastBack("us.errDenied", error: true);
        }

        TempData["UvToast"] = "uv.toastCreated";
        return Redirect($"/agent/user-view?id={created.Id}");
    }

    /// <summary>
    /// dlg-more bulk actions over the selection: Kilitle / Kilidi Aç (IsBlocked),
    /// Şirkete Ekle (org select dialog), Sil (confirm dialog; users with tickets are
    /// skipped and reported — the DB FK restricts the delete).
    /// </summary>
    [HttpPost("/agent/users/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, int? orgId, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (ids.Length == 0 || (act == "addorg" && orgId is null))
        {
            TempData["UsersToast"] = "us.bulkNone";
            TempData["UsersToastError"] = true;
            return LocalRedirectOrIndex(returnUrl);
        }

        int ok = 0, blocked = 0, skipped = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (act)
                {
                    case "lock":
                        await userService.SetBlockedAsync(id, true, actor, ct);
                        ok++;
                        break;
                    case "unlock":
                        await userService.SetBlockedAsync(id, false, actor, ct);
                        ok++;
                        break;
                    case "addorg":
                        await userService.SetOrganizationAsync(id, orgId, actor, ct);
                        ok++;
                        break;
                    case "delete":
                        await userService.DeleteAsync(id, actor, ct);
                        ok++;
                        break;
                    case "invite":
                        // "Kaydet" (guest → portal account): mails a signed, one-shot
                        // activation link. Guests who already have an account, or who
                        // have no address to mail, are skipped rather than failed —
                        // the bulk menu runs over a whole selection.
                        if (await InviteAsync(id, ct))
                            ok++;
                        else
                            skipped++;
                        break;
                    default:
                        return LocalRedirectOrIndex(returnUrl);
                }
            }
            catch (DomainRuleException)
            {
                // Delete refused: the user still has tickets / collaborator rows.
                blocked++;
            }
            catch (DomainException)
            {
                // Permission denied / unknown id — count and report.
                skipped++;
            }
        }

        // Two plain ints (tasks-page precedent): TempData's serializer has no array support.
        TempData["UsersToastOk"] = ok;
        TempData["UsersToastSkipped"] = blocked + skipped;
        if (act == "delete" && blocked > 0)
        {
            TempData["UsersToast"] = "us.bulkDeleteSkipped";
            TempData["UsersToastError"] = true;
        }
        else
        {
            TempData["UsersToast"] = blocked + skipped > 0 ? "us.bulkPartial" : "us.bulkDone";
        }
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>
    /// Issues one portal-account invitation (S8 slice 5, resolving the users-page
    /// "Kaydet" marker): a signed one-shot token mailed to the guest's default address, which
    /// opens the /invite set-password flow and links the new Identity account to THIS
    /// domain user. False = nothing to invite (no address, or the user already signed
    /// up); re-inviting is allowed and invalidates the previous link.
    /// </summary>
    private async Task<bool> InviteAsync(int userId, CancellationToken ct)
    {
        var target = await db.Users.Where(u => u.Id == userId && u.IdentityUserId == null)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails.Where(e => e.Id == u.DefaultEmailId).Select(e => e.Address).FirstOrDefault()
                    ?? u.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target?.Email is null)
            return false;

        var token = await tokens.IssueAsync(MailTokenPurpose.Invite, userId,
            MailLinkTokenService.InviteLifetime, ct);
        var link = $"{Request.Scheme}://{Request.Host}/invite?token={Uri.EscapeDataString(token)}";
        await accountMail.SendInviteAsync(target.Email, target.Name, link, ct);
        return true;
    }

    /// <summary>dlg-import submit: strict name,email[,org] CSV through UserService; reports created/skipped.</summary>
    [HttpPost("/agent/users/import")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (file is null || file.Length == 0)
            return ToastBack("us.importNone", error: true);

        CsvImportResult result;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            result = await userService.ImportCsvAsync(reader, actor, ct);
        }
        catch (DomainException)
        {
            return ToastBack("us.errDenied", error: true);
        }

        TempData["UsersToast"] = "us.importDone";
        TempData["UsersToastOk"] = result.Created;
        TempData["UsersToastSkipped"] = result.Skipped;
        return RedirectToAction(nameof(Index));
    }

    private IActionResult ToastBack(string key, bool error = false)
    {
        TempData["UsersToast"] = key;
        if (error)
            TempData["UsersToastError"] = true;
        return RedirectToAction(nameof(Index));
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        returnUrl is not null && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Index));
}

public sealed record UsersIndexVm(
    string Tab,
    IReadOnlyDictionary<string, int> Counts,
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<UserRowVm> Rows,
    IReadOnlyList<OrgOptionVm> Organizations);

/// <summary>StatusKey: active | guest | locked (us.stActive/stGuest/stLocked pills).</summary>
public sealed record UserRowVm(
    int Id,
    string Name,
    string? Email,
    int? OrgId,
    string? OrgName,
    string StatusKey,
    DateTimeOffset Registered,
    DateTimeOffset Updated);

public sealed record OrgOptionVm(int Id, string Name);
