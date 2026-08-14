using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Web.Navigation;

/// <summary>Provides live sidebar count badges (B9: counts fed by real queries).</summary>
public interface ISidebarBadgeService
{
    Task<string?> GetCountAsync(string countKey);
}

/// <summary>
/// Sidebar counts for the signed-in staff member, replacing the S2 stub's
/// hardcoded mockup values. Canon meaning of the badges (agent sidebar):
/// tickets = "Bana Atanan" (my open tickets), tasks = "Görevlerim" (my open tasks).
/// </summary>
public sealed class SidebarBadgeService(AppDbContext db, IHttpContextAccessor http) : ISidebarBadgeService
{
    private int? _staffId;
    private bool _staffResolved;

    public async Task<string?> GetCountAsync(string countKey)
    {
        var ct = http.HttpContext?.RequestAborted ?? CancellationToken.None;
        var staffId = await StaffIdAsync(ct);
        if (staffId is null)
            return null;

        return countKey switch
        {
            "tickets" => (await db.Tickets.CountAsync(
                t => t.StaffId == staffId && t.Status!.State == TicketState.Open, ct)).ToString(),
            "tasks" => (await db.TaskItems.CountAsync(
                t => t.StaffId == staffId && t.ClosedAt == null, ct)).ToString(),
            _ => null,
        };
    }

    private async Task<int?> StaffIdAsync(CancellationToken ct)
    {
        if (_staffResolved)
            return _staffId;
        _staffResolved = true;
        var principal = http.HttpContext?.User;
        if (principal is not null)
            _staffId = (await db.ResolveStaffAsync(principal, ct))?.Id;
        return _staffId;
    }
}
