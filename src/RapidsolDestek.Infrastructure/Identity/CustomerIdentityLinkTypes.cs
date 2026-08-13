using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Infrastructure.Identity;

public class CustomerUserClaim : IdentityUserClaim<Guid>
{
}

public class CustomerUserLogin : IdentityUserLogin<Guid>
{
}

public class CustomerUserToken : IdentityUserToken<Guid>
{
}
