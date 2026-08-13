using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

[Area("Portal")]
public class AccountController(
    UserManager<CustomerUser> users,
    CustomerSignInManager signIn,
    IAppEmailSender mail,
    IWebHostEnvironment env) : Controller
{
    // ---- Login -------------------------------------------------------------

    [HttpGet("/login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl) => View(new LoginVm { ReturnUrl = returnUrl });

    [HttpPost("/login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var user = await users.FindByEmailAsync(vm.User) ?? await users.FindByNameAsync(vm.User);
        if (user is not null)
        {
            var result = await signIn.PasswordSignInAsync(user, vm.Password, vm.Remember, lockoutOnFailure: true);
            if (result.Succeeded)
                return LocalRedirect(Url.IsLocalUrl(vm.ReturnUrl) ? vm.ReturnUrl! : "/tickets");
            if (result.IsLockedOut)
            {
                ModelState.AddModelError(string.Empty, "lockedOut");
                return View(vm);
            }
        }
        ModelState.AddModelError(string.Empty, "invalidCredentials");
        return View(vm);
    }

    [HttpPost("/logout")]
    [Authorize(Policy = "PortalUser")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return Redirect("/login");
    }

    // ---- Register ----------------------------------------------------------

    [HttpGet("/register")]
    [AllowAnonymous]
    public IActionResult Register() => View(new RegisterVm());

    [HttpPost("/register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterVm vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var user = new CustomerUser
        {
            UserName = vm.Email,
            Email = vm.Email,
            FullName = vm.Name,
            PhoneNumber = vm.Phone,
        };
        var result = await users.CreateAsync(user, vm.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(vm);
        }
        await signIn.SignInAsync(user, isPersistent: false);
        return Redirect("/");
    }

    // ---- Password reset (3-step flow) --------------------------------------

    [HttpGet("/pwreset")]
    [AllowAnonymous]
    public IActionResult Pwreset() => View();

    [HttpPost("/pwreset")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pwreset([EmailAddress, Required] string email)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = Url.ActionLink(nameof(PwresetNew), values: new { email, token })!;
            await mail.SendAsync(email, "RapidsolDestek", link);
            if (env.IsDevelopment()) TempData["DevResetLink"] = link;
        }
        // Do not reveal whether the email exists.
        return RedirectToAction(nameof(PwresetSent));
    }

    [HttpGet("/pwreset/sent")]
    [AllowAnonymous]
    public IActionResult PwresetSent() => View();

    [HttpGet("/pwreset/new")]
    [AllowAnonymous]
    public IActionResult PwresetNew(string email, string token) =>
        View(new PwresetNewVm { Email = email, Token = token });

    [HttpPost("/pwreset/new")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PwresetNew(PwresetNewVm vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var user = await users.FindByEmailAsync(vm.Email);
        if (user is not null)
        {
            var result = await users.ResetPasswordAsync(user, vm.Token, vm.Password);
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
                return View(vm);
            }
        }
        return Redirect("/login");
    }

    // ---- Static-ish pages ---------------------------------------------------

    [HttpGet("/offline")]
    [AllowAnonymous]
    public IActionResult Offline() => View();

}

public class LoginVm
{
    [Required] public string User { get; set; } = "";
    [Required] public string Password { get; set; } = "";
    public bool Remember { get; set; } = true;
    public string? ReturnUrl { get; set; }
}

public class RegisterVm
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required] public string Name { get; set; } = "";
    public string? Phone { get; set; }
    [Required, MinLength(8)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password))] public string Password2 { get; set; } = "";
}

public class PwresetNewVm
{
    [Required] public string Email { get; set; } = "";
    [Required] public string Token { get; set; } = "";
    [Required, MinLength(8)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password))] public string Password2 { get; set; } = "";
}
