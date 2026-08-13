using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// Role for staff members (agents and administrators).
/// </summary>
public class StaffRole : IdentityRole<Guid>
{
    public StaffRole()
    {
    }

    public StaffRole(string roleName) : base(roleName)
    {
    }
}
