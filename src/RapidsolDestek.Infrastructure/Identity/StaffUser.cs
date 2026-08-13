using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// Identity principal for staff members (agents and administrators).
/// </summary>
public class StaffUser : IdentityUser<Guid>
{
    public required string FullName { get; set; }
}
