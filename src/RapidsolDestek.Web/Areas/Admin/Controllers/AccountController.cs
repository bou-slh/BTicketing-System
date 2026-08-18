using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Areas.Portal.Controllers;
using RapidsolDestek.Web.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

[Area("Admin")]
public class AccountController(
    UserManager<StaffUser> users,
    StaffSignInManager signIn,
    IAppEmailSender mail,
    IWebHostEnvironment env,
    RapidsolDestek.Infrastructure.AppDbContext db,
    RapidsolDestek.Infrastructure.Services.ISettingsService settings)
    : StaffAccountControllerBase(users, signIn, mail, env, db, settings)
{
    protected override string AreaPrefix => "/admin";
    protected override bool RequireAdmin => true;

    // Admin panel has its own new-password card (agent precedent — rd-field conventions,
    // /admin back links; the mockup has no step card at all, flagged in ROADMAP).
    protected override string PwresetNewViewName => "PwresetNew";

    [HttpGet("/admin/login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) => View(new StaffLoginVm { ReturnUrl = returnUrl });

    [HttpPost("/admin/login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Login(StaffLoginVm vm) => LoginCore(vm);

    [HttpGet("/admin/login/2fa")]
    [AllowAnonymous]
    public Task<IActionResult> Login2fa(string? returnUrl) => Login2faGetCore(returnUrl);

    [HttpPost("/admin/login/2fa")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Login2fa(StaffLogin2faVm vm) => Login2faCore(vm);

    [HttpPost("/admin/logout")]
    [Authorize(Policy = "Staff")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Logout() => LogoutCore();

    [HttpGet("/admin/pwreset")]
    [AllowAnonymous]
    public IActionResult Pwreset() => View();

    [HttpPost("/admin/pwreset")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Pwreset(string email) => PwresetCore(email);

    [HttpGet("/admin/pwreset/new")]
    [AllowAnonymous]
    public IActionResult PwresetNew(string email, string token) => PwresetNewCore(email, token);

    [HttpPost("/admin/pwreset/new")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PwresetNew(PwresetNewVm vm) => PwresetNewPostCore(vm);

    // ---- Mandatory 2FA enrollment (B6 "mandatory 2FA": a password-only admin session
    // lands here — LoginCore redirects, and the staff cookie's access-denied handler
    // sends any /admin/* attempt back until the enrollment is confirmed) ----

    [HttpGet("/admin/2fa-setup")]
    [Authorize(Policy = "Staff", Roles = "Admin")]
    public async Task<IActionResult> TwoFactorSetup()
    {
        var user = (await Users.GetUserAsync(User))!;
        // Already enrolled (session carries mfa) — nothing to set up here; the profile
        // dialog owns re-enrollment.
        if (user.TwoFactorEnabled) return Redirect("/admin/dashboard");
        var key = await Users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await Users.ResetAuthenticatorKeyAsync(user);
            key = await Users.GetAuthenticatorKeyAsync(user);
        }
        SetKeyViewData(user, key);
        return View("TwoFactorSetup");
    }

    [HttpPost("/admin/2fa-setup")]
    [Authorize(Policy = "Staff", Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorSetup(string code)
    {
        var user = (await Users.GetUserAsync(User))!;
        var normalized = code.Replace(" ", "").Replace("-", "");
        var valid = await Users.VerifyTwoFactorTokenAsync(
            user, Users.Options.Tokens.AuthenticatorTokenProvider, normalized);
        if (!valid)
        {
            ModelState.AddModelError(string.Empty, "invalidCode");
            SetKeyViewData(user, await Users.GetAuthenticatorKeyAsync(user));
            return View("TwoFactorSetup");
        }
        await Users.SetTwoFactorEnabledAsync(user, true);
        // Re-authenticate so the session carries the amr=mfa claim required by AdminOnly.
        await StaffSignIn.SignOutAsync();
        return Redirect("/admin/login");
    }

    private void SetKeyViewData(StaffUser user, string? key)
    {
        ViewData["AuthenticatorKey"] = key;
        // Same otpauth link as the profile dlg-2fa precedent (no QR image; S7 if canon wants one).
        ViewData["OtpauthUri"] = $"otpauth://totp/RapidsolDestek:{Uri.EscapeDataString(user.UserName ?? "")}" +
                                 $"?secret={key}&issuer=RapidsolDestek";
    }
}
