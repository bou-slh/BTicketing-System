using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// EF Core user store for <see cref="StaffUser"/> (with role support).
/// </summary>
public class StaffStore : UserStore<StaffUser, StaffRole, AppDbContext, Guid, StaffUserClaim, StaffUserRole, StaffUserLogin, StaffUserToken, StaffRoleClaim>
{
    public StaffStore(AppDbContext context, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
    }
}
