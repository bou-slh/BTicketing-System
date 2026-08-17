using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent organization list (mockups/agent/orgs.html, ROADMAP §6.2): the B1 engine
/// (sort/search/pagination) over all organizations with live member / open-ticket
/// counts, dlg-addorg creating a real Organization through OrgService, and CSV
/// export of the current list (tickets-page export precedent).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class OrgsController(AppDbContext db, IOrgService orgService) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "sector", "users", "open", "manager", "updated"];

    [HttpGet("/agent/orgs")]
    [NavKey("orgs")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var query = BuildQuery(q, sort, dir, out var sortKey, out var desc);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        return View(new OrgsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            rows, await StaffOptionsAsync(ct)));
    }

    /// <summary>dlg-addorg submit: creates the Organization and lands on its org-view.</summary>
    [HttpPost("/agent/orgs/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? name, string? domain, string? sector, int? managerId, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        Organization created;
        try
        {
            created = await orgService.CreateAsync(new OrgCreateRequest
            {
                Name = name ?? "",
                Domain = domain,
                Sector = sector,
                ManagerStaffId = managerId,
            }, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return ToastBack(ex.Code == "name-in-use" ? "og.errNameInUse" : "og.errInvalid", error: true);
        }
        catch (DomainException)
        {
            return ToastBack("og.errDenied", error: true);
        }

        TempData["OvToast"] = "ov.toastCreated";
        return Redirect($"/agent/org-view?id={created.Id}");
    }

    /// <summary>Streamed CSV of the current list — active search + sort, all pages.</summary>
    [HttpGet("/agent/orgs/export")]
    public async Task<IActionResult> Export(
        string? q, string? sort, string? dir,
        [FromServices] IStringLocalizerFactory localizerFactory,
        [FromServices] IStringLocalizer<SharedResources> sl,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var query = BuildQuery(q, sort, dir, out _, out _);

        // Page-resx headers (same keys the table renders with — tickets export precedent).
        var pageL = localizerFactory.Create("Areas.Agent.Views.Orgs.Index", typeof(Program).Assembly.GetName().Name!);
        string[] headers =
        [
            pageL["og.colName"], pageL["og.colSector"], pageL["og.colUsers"],
            pageL["og.colOpen"], pageL["og.colManager"], sl["common.updated"],
        ];

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=sirketler.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", headers.Select(TicketListEngine.Csv)));
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            string[] cells =
            [
                row.Name, row.Sector ?? "", row.UserCount.ToString(), row.OpenCount.ToString(),
                row.ManagerName ?? "", row.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ];
            await writer.WriteLineAsync(string.Join(",", cells.Select(TicketListEngine.Csv)));
        }
        return new EmptyResult();
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>B1 core: search + computed columns + sort, shared by Index and Export.</summary>
    private IQueryable<OrgRowVm> BuildQuery(string? q, string? sort, string? dir, out string sortKey, out bool desc)
    {
        var query = db.Organizations.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(o =>
                EF.Functions.ILike(o.Name, pattern)
                || (o.Sector != null && EF.Functions.ILike(o.Sector, pattern))
                || (o.Domain != null && EF.Functions.ILike(o.Domain, pattern)));
        }

        // Anonymous intermediate so the computed columns stay translatable under OrderBy
        // (constructor-projected records cannot be sorted server-side).
        var projected = query.Select(o => new
        {
            o.Id, o.Name, o.Sector,
            UserCount = o.Members.Count,
            OpenCount = db.Tickets.Count(t =>
                t.User!.OrganizationId == o.Id && t.Status!.State == TicketState.Open),
            ManagerName = db.Staff.Where(s => s.Id == o.ManagerStaffId)
                .Select(s => (string?)(s.FirstName + " " + s.LastName)).FirstOrDefault(),
            Updated = o.UpdatedAt ?? o.CreatedAt,
        });

        var key = sortKey = SortKeys.Contains(sort) ? sort! : "name";
        // Text columns read naturally ascending; count/date columns default high/new-first
        // (users-page convention).
        var d = desc = dir == "desc" || (dir != "asc" && key is "users" or "open" or "updated");
        projected = (key, d) switch
        {
            ("sector", true) => projected.OrderByDescending(r => r.Sector ?? "").ThenBy(r => r.Id),
            ("sector", false) => projected.OrderBy(r => r.Sector ?? "").ThenBy(r => r.Id),
            ("users", true) => projected.OrderByDescending(r => r.UserCount).ThenBy(r => r.Id),
            ("users", false) => projected.OrderBy(r => r.UserCount).ThenBy(r => r.Id),
            ("open", true) => projected.OrderByDescending(r => r.OpenCount).ThenBy(r => r.Id),
            ("open", false) => projected.OrderBy(r => r.OpenCount).ThenBy(r => r.Id),
            ("manager", true) => projected.OrderByDescending(r => r.ManagerName ?? "").ThenBy(r => r.Id),
            ("manager", false) => projected.OrderBy(r => r.ManagerName ?? "").ThenBy(r => r.Id),
            ("updated", true) => projected.OrderByDescending(r => r.Updated).ThenByDescending(r => r.Id),
            ("updated", false) => projected.OrderBy(r => r.Updated).ThenBy(r => r.Id),
            (_, true) => projected.OrderByDescending(r => r.Name),
            _ => projected.OrderBy(r => r.Name),
        };
        return projected.Select(r => new OrgRowVm(
            r.Id, r.Name, r.Sector, r.UserCount, r.OpenCount, r.ManagerName, r.Updated));
    }

    /// <summary>Manager select options: active staff, directory order (canon roster).</summary>
    private async Task<List<StaffOptionVm>> StaffOptionsAsync(CancellationToken ct) =>
        await db.Staff
            .Where(s => s.IsActive)
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new StaffOptionVm(s.Id, s.FirstName + " " + s.LastName))
            .ToListAsync(ct);

    private IActionResult ToastBack(string key, bool error = false)
    {
        TempData["OrgsToast"] = key;
        if (error)
            TempData["OrgsToastError"] = true;
        return RedirectToAction(nameof(Index));
    }
}

public sealed record OrgsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<OrgRowVm> Rows,
    IReadOnlyList<StaffOptionVm> StaffOptions);

public sealed record OrgRowVm(
    int Id,
    string Name,
    string? Sector,
    int UserCount,
    int OpenCount,
    string? ManagerName,
    DateTimeOffset Updated);

public sealed record StaffOptionVm(int Id, string Name);
