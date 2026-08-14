using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

[Area("Portal")]
public class AccountController(
    UserManager<CustomerUser> users,
    CustomerSignInManager signIn,
    AppDbContext db,
    IAppEmailSender mail,
    IWebHostEnvironment env) : Controller
{
    // ---- Login -------------------------------------------------------------
    // TODO(S7): optional TOTP 2FA step for portal customers (ROADMAP B6; the
    // portal/login.html mockup has no 2FA UI — staff logins already 2FA via
    // their Login2fa views, and 2FA stays mandatory for admins only).

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
            {
                // Saved language preference (profile page) wins over the cookie default.
                if (user.Language is { } lang && CultureController.Supported.Contains(lang))
                    CultureController.ApplyCultureCookie(Response, lang);
                return LocalRedirect(Url.IsLocalUrl(vm.ReturnUrl) ? vm.ReturnUrl! : "/tickets");
            }
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
        if (!vm.Kvkk) ModelState.AddModelError(nameof(vm.Kvkk), "errKvkk");
        if (!ModelState.IsValid) return View(vm);

        var user = new CustomerUser
        {
            UserName = vm.Email,
            Email = vm.Email,
            FullName = vm.Name,
            PhoneNumber = vm.Phone,
            // Persisted since the profile port (ROADMAP §6.1 profile row); /profile edits it.
            TimeZone = ProfileController.TimeZones.Any(t => t.Id == vm.TimeZone) ? vm.TimeZone : "Europe/Istanbul",
        };
        var result = await users.CreateAsync(user, vm.Password);
        if (!result.Succeeded)
        {
            // Identity error codes → view-localized markers (Open-page pattern).
            foreach (var e in result.Errors)
                ModelState.AddModelError(string.Empty, e.Code switch
                {
                    "DuplicateUserName" or "DuplicateEmail" => "emailTaken",
                    "InvalidEmail" or "InvalidUserName" => "errEmail",
                    _ when e.Code.StartsWith("Password") => "passwordWeak",
                    _ => e.Description,
                });
            return View(vm);
        }
        await LinkDomainUserAsync(user, vm);
        await signIn.SignInAsync(user, isPersistent: false);
        return Redirect("/");
    }

    /// <summary>
    /// Registration must end with a domain <see cref="User"/> row — every portal page
    /// resolves the principal via User.IdentityUserId, and /open forbids without one
    /// (osTicket user_account ↔ user split). Claims an existing unlinked user carrying
    /// this address (agent/guest-created records), else creates one, auto-linking the
    /// organization by email domain (Organization.Domain, comma-separated).
    /// </summary>
    private async Task LinkDomainUserAsync(CustomerUser identity, RegisterVm vm)
    {
        var address = vm.Email.Trim();
        using var _ = new ActorContext(ActorType.User, null, vm.Name.Trim()).BeginAuditScope();

        var existing = await db.UserEmails
            .Where(e => e.Address.ToLower() == address.ToLower())
            .Select(e => e.User!)
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            if (existing.IdentityUserId is null)
            {
                existing.IdentityUserId = identity.Id;
                existing.Phone ??= vm.Phone;
                await db.SaveChangesAsync();
            }
            return;
        }

        var host = address[(address.IndexOf('@') + 1)..];
        var orgId = (await db.Organizations
                .Where(o => o.Domain != null)
                .Select(o => new { o.Id, o.Domain })
                .ToListAsync())
            .FirstOrDefault(o => o.Domain!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(d => d.Equals(host, StringComparison.OrdinalIgnoreCase)))?.Id;

        var domainUser = new User
        {
            Name = vm.Name.Trim(),
            Phone = vm.Phone,
            IdentityUserId = identity.Id,
            OrganizationId = orgId,
            Emails = [new UserEmail { Address = address }],
        };
        db.Users.Add(domainUser);
        await db.SaveChangesAsync();
        domainUser.DefaultEmailId = domainUser.Emails[0].Id;
        await db.SaveChangesAsync();
    }

    // ---- Password reset (3-step flow) --------------------------------------

    [HttpGet("/pwreset")]
    [AllowAnonymous]
    public IActionResult Pwreset() => View();

    [HttpPost("/pwreset")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pwreset(
        [EmailAddress(ErrorMessage = "errEmail"), Required(ErrorMessage = "errEmail")] string email)
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
                foreach (var e in result.Errors)
                    ModelState.AddModelError(string.Empty, e.Code switch
                    {
                        "InvalidToken" => "invalidToken",
                        _ when e.Code.StartsWith("Password") => "passwordWeak",
                        _ => e.Description,
                    });
                return View(vm);
            }
        }
        // Unknown email falls through to /login without a hint (enumeration safety).
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
    [Required(ErrorMessage = "errEmail"), EmailAddress(ErrorMessage = "errEmail")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "errName")]
    public string Name { get; set; } = "";

    public string? Phone { get; set; }

    /// <summary>IANA id from the mockup's preference select; stored on CustomerUser.TimeZone.</summary>
    public string TimeZone { get; set; } = "Europe/Istanbul";

    [Required(ErrorMessage = "passwordWeak"), MinLength(8, ErrorMessage = "passwordWeak")]
    public string Password { get; set; } = "";

    [Compare(nameof(Password), ErrorMessage = "passwordMismatch")]
    public string Password2 { get; set; } = "";

    /// <summary>KVKK (Turkish DPA) consent — must be ticked to register.</summary>
    public bool Kvkk { get; set; }
}

public class PwresetNewVm
{
    [Required] public string Email { get; set; } = "";
    [Required] public string Token { get; set; } = "";

    [Required(ErrorMessage = "passwordWeak"), MinLength(8, ErrorMessage = "passwordWeak")]
    public string Password { get; set; } = "";

    [Compare(nameof(Password), ErrorMessage = "passwordMismatch")]
    public string Password2 { get; set; } = "";
}
