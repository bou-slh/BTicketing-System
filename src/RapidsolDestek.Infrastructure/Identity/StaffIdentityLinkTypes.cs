using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Infrastructure.Identity;

public class StaffUserClaim : IdentityUserClaim<Guid>
{
}

public class StaffUserRole : IdentityUserRole<Guid>
{
}

public class StaffUserLogin : IdentityUserLogin<Guid>
{
}

public class StaffUserToken : IdentityUserToken<Guid>
{
}

public class StaffRoleClaim : IdentityRoleClaim<Guid>
{
}
