using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// EF Core role store for <see cref="StaffRole"/>.
/// </summary>
public class StaffRoleStore : RoleStore<StaffRole, AppDbContext, Guid, StaffUserRole, StaffRoleClaim>
{
    public StaffRoleStore(AppDbContext context, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
    }
}
