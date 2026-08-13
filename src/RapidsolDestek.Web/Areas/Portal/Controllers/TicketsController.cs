using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal ticket list (mockups/portal/tickets.html): the signed-in user's own tickets
/// split into live Open/Closed tabs, with search + help-topic filters that compose
/// with the active tab (ROADMAP §6.1 tickets.html row; B1 at portal scope).
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class TicketsController(AppDbContext db) : Controller
{
    [HttpGet("/tickets")]
    [NavKey("tickets")]
    public async Task<IActionResult> Index(string? q, int? topic, string? tab, CancellationToken ct)
    {
        var closedTab = tab == "closed";

        // Principal → domain User via User.IdentityUserId (HomeController / queue-engine
        // portal base-scope parity): a portal user sees only their own tickets.
        int? userId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var identityId))
            userId = await db.Users
                .Where(u => u.IdentityUserId == identityId)
                .Select(u => (int?)u.Id)
                .SingleOrDefaultAsync(ct);

        // Topic filter options: public topics, mockup order (select lists real HelpTopics;
        // inactive ones stay listed — existing tickets still reference them).
        var topics = await db.HelpTopics
            .Where(t => t.IsPublic)
            .OrderBy(t => t.Sort)
            .Select(t => new TicketTopicVm(t.Id, t.Name))
            .ToListAsync(ct);

        if (userId is null)
            return View(new TicketsVm(q, topic, closedTab, 0, 0, topics, []));

        // Own tickets only; archived/deleted states never surface on the portal.
        var mine = db.Tickets.Where(t =>
            t.UserId == userId
            && (t.Status!.State == TicketState.Open || t.Status.State == TicketState.Closed));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            mine = mine.Where(t =>
                EF.Functions.ILike(t.Subject, term) || EF.Functions.ILike(t.Number, term));
        }

        if (topic is { } topicId)
            mine = mine.Where(t => t.HelpTopicId == topicId);

        // Tab counts are live over the filtered scope so search/topic compose with tabs.
        var openCount = await mine.CountAsync(t => t.Status!.State == TicketState.Open, ct);
        var closedCount = await mine.CountAsync(t => t.Status!.State == TicketState.Closed, ct);

        var state = closedTab ? TicketState.Closed : TicketState.Open;
        var rows = await mine
            .Where(t => t.Status!.State == state)
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)
            .Select(t => new TicketRowVm(
                t.Id,
                t.Number,
                t.Subject,
                t.User!.Organization != null ? t.User.Organization.Name : null,
                t.LastUpdateAt ?? t.CreatedAt,
                // Derived pseudo-status: a pending effort proposal shows as effortWait
                // (HomeController parity; deliberately not a TicketStatus row).
                t.EffortProposals.Any(p => p.State == EffortState.Pending)
                    ? "effortWait"
                    : t.Status!.Key,
                t.Staff != null ? t.Staff.FullName : null))
            .ToListAsync(ct);

        return View(new TicketsVm(q, topic, closedTab, openCount, closedCount, topics, rows));
    }
}

public sealed record TicketsVm(
    string? Query,
    int? TopicId,
    bool ClosedTab,
    int OpenCount,
    int ClosedCount,
    IReadOnlyList<TicketTopicVm> Topics,
    IReadOnlyList<TicketRowVm> Rows);

public sealed record TicketTopicVm(int Id, string Name);

public sealed record TicketRowVm(
    int Id,
    string Number,
    string Subject,
    string? OrgName,
    DateTimeOffset UpdatedAt,
    string StatusKey,
    string? AgentName);
