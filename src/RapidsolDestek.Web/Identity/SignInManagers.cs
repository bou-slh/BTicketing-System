using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RapidsolDestek.Infrastructure.Identity;

namespace RapidsolDestek.Web.Identity;

/// <summary>Customer principal signs in against the "Identity.Customer" cookie scheme.</summary>
public sealed class CustomerSignInManager : SignInManager<CustomerUser>
{
    public CustomerSignInManager(
        UserManager<CustomerUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<CustomerUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<CustomerUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<CustomerUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        AuthenticationScheme = AuthSchemes.Customer;
    }
}

/// <summary>Staff principal (agents + admins) signs in against the "Identity.Staff" cookie scheme.</summary>
public sealed class StaffSignInManager : SignInManager<StaffUser>
{
    public StaffSignInManager(
        UserManager<StaffUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<StaffUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<StaffUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<StaffUser> confirmation)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        AuthenticationScheme = AuthSchemes.Staff;
    }
}

public static class AuthSchemes
{
    public const string Customer = "Identity.Customer";
    public const string Staff = "Identity.Staff";
}
