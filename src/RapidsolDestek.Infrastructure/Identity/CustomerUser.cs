using Microsoft.AspNetCore.Identity;

namespace RapidsolDestek.Infrastructure.Identity;

/// <summary>
/// Identity principal for customer portal users.
/// </summary>
public class CustomerUser : IdentityUser<Guid>
{
    public required string FullName { get; set; }

    /// <summary>
    /// Placeholder until the S3 Organization entity introduces a proper FK.
    /// </summary>
    public string? OrganizationName { get; set; }

    /// <summary>IANA time-zone id preference (portal register/profile selects).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Preferred UI language ("tr"/"en"); applied to the culture cookie on login/profile save.</summary>
    public string? Language { get; set; }
}
