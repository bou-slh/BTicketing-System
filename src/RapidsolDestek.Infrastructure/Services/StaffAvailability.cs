using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Vacation-mode assignment guard (agent/profile.html Tatil Modu, ROADMAP §6.2 profile
/// row): while <see cref="Domain.Entities.Staff.OnVacation"/> is on, no new tickets or
/// tasks may be assigned to the agent. The Ata dialogs additionally exclude vacationing
/// agents from their option lists (mockup ticket-view dlg-assign shows only available
/// agents); this service-level guard is the defense-in-depth behind them and also covers
/// self-assignment (Üstlen) and bulk actions.
/// </summary>
public static class StaffAvailability
{
    public const string OnVacationRuleCode = "staff-on-vacation";

    /// <summary>Throws when the target staff member does not exist or is on vacation.</summary>
    public static async Task EnsureAssignableAsync(AppDbContext db, int? staffId, CancellationToken ct)
    {
        if (staffId is not { } id)
            return;

        var onVacation = await db.Staff.Where(s => s.Id == id)
            .Select(s => (bool?)s.OnVacation).SingleOrDefaultAsync(ct)
            ?? throw new DomainNotFoundException("Staff", id);
        if (onVacation)
            throw new DomainRuleException(OnVacationRuleCode,
                $"Staff {id} is on vacation and cannot receive assignments.");
    }

    /// <summary>Auto-assignment variant (help-topic routing): silently drops the staff pin instead of failing the creation.</summary>
    public static async Task<int?> FilterAutoAssignAsync(AppDbContext db, int? staffId, CancellationToken ct)
    {
        if (staffId is not { } id)
            return null;
        return await db.Staff.AnyAsync(s => s.Id == id && s.OnVacation, ct) ? null : staffId;
    }
}
