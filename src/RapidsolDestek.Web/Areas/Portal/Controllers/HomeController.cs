using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal home (mockups/portal/index.html): live tile counts, recent tickets and the
/// KB mini-search for the logged-in portal user (ROADMAP §6.1 index.html row + B9).
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class HomeController(AppDbContext db) : Controller
{
    [HttpGet("/")]
    [NavKey("home")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Principal → domain User exactly like the queue engine's portal base scope:
        // the customer Identity id links via User.IdentityUserId, and a portal user
        // sees only their own tickets (QueueEngine.VisibleTicketsAsync parity).
        int? userId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var identityId))
            userId = await db.Users
                .Where(u => u.IdentityUserId == identityId)
                .Select(u => (int?)u.Id)
                .SingleOrDefaultAsync(ct);

        if (userId is null)
            return View(HomeVm.Empty); // portal login not linked to a domain User yet

        var mine = db.Tickets.Where(t => t.UserId == userId);

        var now = DateTimeOffset.Now;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        // Tile mapping (mockup status keys over TicketStatus rows/states):
        //   open      → any status in the Open state
        //   waiting   → status "wait" OR a pending effort proposal (derived effortWait)
        //   closed    → status "solved" closed this month ("çözümlenerek tamamlandı")
        //   cancelled → status "closed" closed this month ("çözüm uygulanmadan")
        var open = await mine.CountAsync(t => t.Status!.State == TicketState.Open, ct);
        var inProgress = await mine.CountAsync(
            t => t.Status!.State == TicketState.Open && t.StaffId != null, ct);
        var waiting = await mine.CountAsync(t =>
            t.Status!.State == TicketState.Open
            && (t.Status.Key == "wait"
                || t.EffortProposals.Any(p => p.State == EffortState.Pending)), ct);
        var solvedThisMonth = await mine.CountAsync(
            t => t.Status!.Key == "solved" && t.ClosedAt >= monthStart, ct);
        var cancelledThisMonth = await mine.CountAsync(
            t => t.Status!.Key == "closed" && t.ClosedAt >= monthStart, ct);

        var recent = await mine
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)
            .Take(3)
            .Select(t => new HomeTicketRowVm(
                t.Id,
                t.Number,
                t.Subject,
                t.LastUpdateAt ?? t.CreatedAt,
                // Derived pseudo-status: a pending effort proposal shows as effortWait
                // (see TicketStatus doc — deliberately not a status row).
                t.EffortProposals.Any(p => p.State == EffortState.Pending)
                    ? "effortWait"
                    : t.Status!.Key,
                t.Staff != null ? t.Staff.FullName : null))
            .ToListAsync(ct);

        return View(new HomeVm(open, inProgress, waiting, solvedThisMonth, cancelledThisMonth, recent));
    }
}

public sealed record HomeVm(
    int OpenCount,
    int InProgressCount,
    int WaitingCount,
    int SolvedThisMonth,
    int CancelledThisMonth,
    IReadOnlyList<HomeTicketRowVm> Recent)
{
    public static readonly HomeVm Empty = new(0, 0, 0, 0, 0, []);
}

public sealed record HomeTicketRowVm(
    int Id,
    string Number,
    string Subject,
    DateTimeOffset UpdatedAt,
    string StatusKey,
    string? AgentName);
