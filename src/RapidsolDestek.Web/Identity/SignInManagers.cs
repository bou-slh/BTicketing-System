using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;

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
    private readonly ISettingsService _settings;

    public StaffSignInManager(
        UserManager<StaffUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<StaffUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<StaffUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<StaffUser> confirmation,
        ISettingsService settings)
        : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        AuthenticationScheme = AuthSchemes.Staff;
        _settings = settings;
    }

    /// <summary>
    /// B6 "2FA zorunlu" (admin/settings-agents sa.twofa, agents/require_twofa): while
    /// the policy is on, EVERY staff password sign-in runs the second step — staff
    /// without an enrollment verify via the promised email code
    /// (StaffAccountControllerBase.EffectiveTwoFactorMethodAsync picks the provider).
    /// </summary>
    public override async Task<bool> IsTwoFactorEnabledAsync(StaffUser user) =>
        await base.IsTwoFactorEnabledAsync(user)
        || (await _settings.GetAgentsAsync()).RequireTwofa;
}

public static class AuthSchemes
{
    public const string Customer = "Identity.Customer";
    public const string Staff = "Identity.Staff";
}
