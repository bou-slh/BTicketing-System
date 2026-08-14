using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// B9 topbar global search: tickets (number prefix + tsvector full-text via the
/// queue engine, so department visibility applies), users (name/email) and
/// organizations (name). Returns an HTML fragment the topbar dropdown fills.
/// The mockups define no search-results UI — the dropdown design is flagged for
/// canon sign-off on the ROADMAP dashboard row.
/// </summary>
// TODO(S7): settings search (B9 lists it) is admin scope — add when the admin
// area ports and give the admin topbar its own endpoint.
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class SearchController(AppDbContext db, IQueueEngine queue) : Controller
{
    private const int GroupLimit = 5;

    [HttpGet("/agent/search")]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return PartialView("_Results", new SearchResultsVm([], [], []));

        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        // Number prefix over the actor's visible scope ("R716" finds the family) —
        // the tsvector index only matches whole lexemes, so prefixes need ILike.
        var visible = await queue.BuildAsync(QueueCriteria.Empty, actor, ct);
        var byNumber = await SelectTickets(visible.Where(t => EF.Functions.ILike(t.Number, term + "%")))
            .Take(GroupLimit).ToListAsync(ct);

        // Full-text (subject + thread bodies) via the queue engine's tsvector search.
        var byText = await SelectTickets(await queue.SearchAsync(term, actor, ct))
            .Take(GroupLimit).ToListAsync(ct);

        var tickets = byNumber.Concat(byText)
            .DistinctBy(t => t.Id).Take(GroupLimit).ToList();

        var like = $"%{term}%";
        var users = await db.Users
            .Where(u => EF.Functions.ILike(u.Name, like)
                || u.Emails.Any(e => EF.Functions.ILike(e.Address, like)))
            .OrderBy(u => u.Name)
            .Take(GroupLimit)
            .Select(u => new SearchUserVm(
                u.Id, u.Name,
                u.DefaultEmail != null ? u.DefaultEmail.Address : null))
            .ToListAsync(ct);

        var orgs = await db.Organizations
            .Where(o => EF.Functions.ILike(o.Name, like))
            .OrderBy(o => o.Name)
            .Take(GroupLimit)
            .Select(o => new SearchOrgVm(o.Id, o.Name))
            .ToListAsync(ct);

        return PartialView("_Results", new SearchResultsVm(tickets, users, orgs));
    }

    private static IQueryable<SearchTicketVm> SelectTickets(IQueryable<Ticket> tickets) =>
        tickets
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)
            .Select(t => new SearchTicketVm(t.Id, t.Number, t.Subject, t.User!.Name));
}

public sealed record SearchResultsVm(
    IReadOnlyList<SearchTicketVm> Tickets,
    IReadOnlyList<SearchUserVm> Users,
    IReadOnlyList<SearchOrgVm> Orgs)
{
    public bool IsEmpty => Tickets.Count == 0 && Users.Count == 0 && Orgs.Count == 0;
}

public sealed record SearchTicketVm(int Id, string Number, string Subject, string UserName);

public sealed record SearchUserVm(int Id, string Name, string? Email);

public sealed record SearchOrgVm(int Id, string Name);
