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
    IWebHostEnvironment env) : StaffAccountControllerBase(users, signIn, mail, env)
{
    protected override string AreaPrefix => "/admin";
    protected override bool RequireAdmin => true;

    [HttpGet("/admin/login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) => View(new StaffLoginVm { ReturnUrl = returnUrl });

    [HttpPost("/admin/login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Login(StaffLoginVm vm) => LoginCore(vm);

    [HttpGet("/admin/login/2fa")]
    [AllowAnonymous]
    public IActionResult Login2fa(string? returnUrl) => View(new StaffLogin2faVm { ReturnUrl = returnUrl });

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

    // ---- Mandatory 2FA enrollment (minimal S2 page; full UX with QR in S6/S7) ----

    [HttpGet("/admin/2fa-setup")]
    [Authorize(Policy = "Staff", Roles = "Admin")]
    public async Task<IActionResult> TwoFactorSetup()
    {
        var user = (await Users.GetUserAsync(User))!;
        var key = await Users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await Users.ResetAuthenticatorKeyAsync(user);
            key = await Users.GetAuthenticatorKeyAsync(user);
        }
        ViewData["AuthenticatorKey"] = key;
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
            ViewData["AuthenticatorKey"] = await Users.GetAuthenticatorKeyAsync(user);
            return View("TwoFactorSetup");
        }
        await Users.SetTwoFactorEnabledAsync(user, true);
        // Re-authenticate so the session carries the amr=mfa claim required by AdminOnly.
        await StaffSignIn.SignOutAsync();
        return Redirect("/admin/login");
    }
}
