using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Areas.Portal.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Controllers;

/// <summary>
/// Shared staff (agent/admin) auth flow. The two area controllers only differ in paths,
/// the admin-only role gate, and mandatory 2FA for administrators.
/// </summary>
public abstract class StaffAccountControllerBase(
    UserManager<StaffUser> users,
    StaffSignInManager signIn,
    IAppEmailSender mail,
    IWebHostEnvironment env) : Controller
{
    protected UserManager<StaffUser> Users => users;
    protected StaffSignInManager StaffSignIn => signIn;

    protected abstract string AreaPrefix { get; }   // "/agent" or "/admin"
    protected abstract bool RequireAdmin { get; }

    // ---- Login -------------------------------------------------------------

    protected async Task<IActionResult> LoginCore(StaffLoginVm vm)
    {
        if (!ModelState.IsValid) return View("Login", vm);

        var user = await users.FindByNameAsync(vm.User) ?? await users.FindByEmailAsync(vm.User);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "invalidCredentials");
            return View("Login", vm);
        }
        if (RequireAdmin && !await users.IsInRoleAsync(user, "Admin"))
        {
            ModelState.AddModelError(string.Empty, "notAdmin");
            return View("Login", vm);
        }

        var result = await signIn.PasswordSignInAsync(user, vm.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
            return Redirect($"{AreaPrefix}/login/2fa?returnUrl={Uri.EscapeDataString(vm.ReturnUrl ?? "")}");
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "lockedOut");
            return View("Login", vm);
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "invalidCredentials");
            return View("Login", vm);
        }

        // Password-only success. Admins must use 2FA: force enrollment before entering the panel.
        if (RequireAdmin && !user.TwoFactorEnabled)
            return Redirect($"{AreaPrefix}/2fa-setup");

        return LocalRedirect(SafeReturnUrl(vm.ReturnUrl));
    }

    protected async Task<IActionResult> Login2faCore(StaffLogin2faVm vm)
    {
        if (!ModelState.IsValid) return View("Login2fa", vm);

        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return Redirect($"{AreaPrefix}/login");

        var code = vm.Code.Replace(" ", "").Replace("-", "");
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "lockedOut");
            return View("Login2fa", vm);
        }
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "invalidCode");
            return View("Login2fa", vm);
        }
        return LocalRedirect(SafeReturnUrl(vm.ReturnUrl));
    }

    protected async Task<IActionResult> LogoutCore()
    {
        await signIn.SignOutAsync();
        return Redirect($"{AreaPrefix}/login");
    }

    // ---- Password reset -----------------------------------------------------

    protected async Task<IActionResult> PwresetCore(string email)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null && (!RequireAdmin || await users.IsInRoleAsync(user, "Admin")))
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = $"{Request.Scheme}://{Request.Host}{AreaPrefix}/pwreset/new" +
                       $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
            await mail.SendAsync(email, "RapidsolDestek", link);
            if (env.IsDevelopment()) TempData["DevResetLink"] = link;
        }
        TempData["PwresetRequested"] = true;
        return Redirect($"{AreaPrefix}/pwreset");
    }

    protected IActionResult PwresetNewCore(string email, string token)
    {
        ViewData["PwresetPostUrl"] = $"{AreaPrefix}/pwreset/new";
        ViewData["PwresetBackUrl"] = $"{AreaPrefix}/login";
        return View("~/Areas/Portal/Views/Account/PwresetNew.cshtml", new PwresetNewVm { Email = email, Token = token });
    }

    protected async Task<IActionResult> PwresetNewPostCore(PwresetNewVm vm)
    {
        ViewData["PwresetPostUrl"] = $"{AreaPrefix}/pwreset/new";
        ViewData["PwresetBackUrl"] = $"{AreaPrefix}/login";
        if (!ModelState.IsValid) return View("~/Areas/Portal/Views/Account/PwresetNew.cshtml", vm);

        var user = await users.FindByEmailAsync(vm.Email);
        if (user is not null)
        {
            var result = await users.ResetPasswordAsync(user, vm.Token, vm.Password);
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
                return View("~/Areas/Portal/Views/Account/PwresetNew.cshtml", vm);
            }
        }
        return Redirect($"{AreaPrefix}/login");
    }

    private string SafeReturnUrl(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : $"{AreaPrefix}/dashboard";
}

public class StaffLoginVm
{
    [Required] public string User { get; set; } = "";
    [Required] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class StaffLogin2faVm
{
    [Required] public string Code { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
