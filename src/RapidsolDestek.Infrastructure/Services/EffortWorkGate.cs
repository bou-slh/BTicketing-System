using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// B8 settings gate effort.block_work_until_approved, shared by TicketService
/// (status transitions) and ThreadService (staff responses) without coupling them.
/// Work is blocked while the ticket's latest effort revision is Pending.
/// </summary>
internal static class EffortWorkGate
{
    public static async Task<bool> IsWorkAllowedAsync(
        AppDbContext db, ISettingsService settings, int ticketId, CancellationToken ct)
    {
        var effort = await settings.GetEffortAsync(ct);
        if (!effort.Enabled || !effort.BlockWorkUntilApproved)
            return true;

        var latestState = await db.EffortProposals
            .Where(p => p.TicketId == ticketId)
            .OrderByDescending(p => p.RevisionNo)
            .Select(p => (EffortState?)p.State)
            .FirstOrDefaultAsync(ct);

        return latestState != EffortState.Pending;
    }
}
