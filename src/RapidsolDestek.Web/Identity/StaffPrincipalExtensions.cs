using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;

namespace RapidsolDestek.Web.Identity;

/// <summary>
/// Staff-cookie principal → domain <see cref="Staff"/> row (via
/// <see cref="Staff.IdentityUserId"/>), the staff-side twin of the portal
/// controllers' User.IdentityUserId lookups.
/// </summary>
public static class StaffPrincipalExtensions
{
    public static Task<Staff?> ResolveStaffAsync(this AppDbContext db, ClaimsPrincipal principal, CancellationToken ct = default)
        => Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var identityId)
            ? db.Staff.SingleOrDefaultAsync(s => s.IdentityUserId == identityId, ct)
            : Task.FromResult<Staff?>(null);
}
