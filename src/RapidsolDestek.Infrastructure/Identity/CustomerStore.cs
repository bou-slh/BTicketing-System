using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// EF Core user store for <see cref="CustomerUser"/> (no role support).
/// </summary>
public class CustomerStore : UserOnlyStore<CustomerUser, AppDbContext, Guid, CustomerUserClaim, CustomerUserLogin, CustomerUserToken>
{
    public CustomerStore(AppDbContext context, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
    }
}
