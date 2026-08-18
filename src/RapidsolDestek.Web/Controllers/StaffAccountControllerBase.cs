using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
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
    IWebHostEnvironment env,
    AppDbContext db,
    ISettingsService settings) : Controller
{
    protected UserManager<StaffUser> Users => users;
    protected StaffSignInManager StaffSignIn => signIn;

    protected abstract string AreaPrefix { get; }   // "/agent" or "/admin"
    protected abstract bool RequireAdmin { get; }

    /// <summary>
    /// View rendered for the reset-link "new password" step. Defaults to the portal view
    /// (admin still uses it); an area can override with its own view name to match its
    /// panel's auth-card conventions (agent does).
    /// </summary>
    protected virtual string PwresetNewViewName => "~/Areas/Portal/Views/Account/PwresetNew.cshtml";

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
        {
            // Profile pf-2fa "E-posta kodu": send the 6-digit code now; the shared
            // Login2fa card verifies it against the Email token provider.
            if (await TwoFactorMethodOfAsync(user) == TwoFactorMethod.Email && user.Email is { } to)
            {
                var code = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
                await mail.SendAsync(to, "RapidsolDestek", code);
            }
            return Redirect($"{AreaPrefix}/login/2fa?returnUrl={Uri.EscapeDataString(vm.ReturnUrl ?? "")}");
        }
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "lockedOut");
            return View("Login", vm);
        }
        if (!result.Succeeded)
        {
            // B6 admin-only lockout policy: stricter threshold/duration than the
            // staff-wide Identity defaults, applied only at the admin sign-in.
            ModelState.AddModelError(string.Empty,
                await ApplyAdminLockoutAsync(user) ? "lockedOut" : "invalidCredentials");
            return View("Login", vm);
        }

        await StampLastLoginAsync(user);

        // Password-only success. Admins must use 2FA: force enrollment before entering the panel.
        if (RequireAdmin && !user.TwoFactorEnabled)
            return Redirect($"{AreaPrefix}/2fa-setup");

        return LocalRedirect(SafeReturnUrl(vm.ReturnUrl));
    }

    /// <summary>
    /// Feeds the directory presence stub. ExecuteUpdate on purpose: a login is not a
    /// domain mutation, so it must not produce an AuditEvent via the interceptors.
    /// Also applies the persisted profile language to the culture cookie so the panel
    /// opens in the agent's preferred language (portal login/profile precedent).
    /// </summary>
    private async Task StampLastLoginAsync(StaffUser user)
    {
        await db.Staff.Where(s => s.IdentityUserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastLoginAt, DateTimeOffset.UtcNow));

        var language = await db.Staff.Where(s => s.IdentityUserId == user.Id)
            .Select(s => s.Language).FirstOrDefaultAsync();
        if (language is not null && CultureController.Supported.Contains(language))
            CultureController.ApplyCultureCookie(Response, language);
    }

    /// <summary>Staff row's 2FA method (agent profile pf-2fa): picks the login verification provider.</summary>
    protected Task<TwoFactorMethod> TwoFactorMethodOfAsync(StaffUser user) =>
        db.Staff.Where(s => s.IdentityUserId == user.Id)
            .Select(s => s.TwoFactorMethod).FirstOrDefaultAsync();

    /// <summary>
    /// Admin-only lockout policy (B6, admin/pwreset row): after a failed attempt at the
    /// ADMIN sign-in, lock earlier and longer than the staff-wide Identity defaults.
    /// Identity has already counted the failure (password and 2FA sign-ins both call
    /// AccessFailedAsync); this only turns the count into a lock at the admin threshold.
    /// Returns true when this attempt tripped the lock.
    /// </summary>
    private async Task<bool> ApplyAdminLockoutAsync(StaffUser user)
    {
        if (!RequireAdmin || !await users.GetLockoutEnabledAsync(user)) return false;

        // TODO(S7): settings-agents consumes — admin/settings-agents.html (sa.maxAttempts /
        // sa.lockDuration) will own these values; until that page ports, sensible defaults
        // (3 attempts, 30 min — the mockup select's strictest option + its selected duration)
        // hardcoded behind the Setting keys below. Flag for canon: the mockup's fields are
        // staff-wide; the ADMIN-only tightening (3 vs 5) is invented per the B6 row.
        var attempts = int.TryParse(await settings.GetAsync("agents", "admin_max_login_attempts"), out var a) ? a : 3;
        var minutes = int.TryParse(await settings.GetAsync("agents", "admin_lockout_minutes"), out var m) ? m : 30;

        if (await users.GetAccessFailedCountAsync(user) < attempts) return false;

        await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(minutes));
        await users.ResetAccessFailedCountAsync(user); // Identity's own lockout does the same
        return true;
    }

    /// <summary>GET login/2fa: flags the email-code variant so the card's help text stays honest.</summary>
    protected async Task<IActionResult> Login2faGetCore(string? returnUrl)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return Redirect($"{AreaPrefix}/login"); // no pending password step
        if (await TwoFactorMethodOfAsync(user) == TwoFactorMethod.Email)
            ViewData["TwofaEmail"] = true;
        return View("Login2fa", new StaffLogin2faVm { ReturnUrl = returnUrl });
    }

    protected async Task<IActionResult> Login2faCore(StaffLogin2faVm vm)
    {
        if (!ModelState.IsValid) return View("Login2fa", vm);

        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return Redirect($"{AreaPrefix}/login");

        var method = await TwoFactorMethodOfAsync(user);
        if (method == TwoFactorMethod.Email)
            ViewData["TwofaEmail"] = true; // keep the email help text on error re-renders

        var code = vm.Code.Replace(" ", "").Replace("-", "");
        var result = method == TwoFactorMethod.Email
            ? await signIn.TwoFactorSignInAsync(TokenOptions.DefaultEmailProvider, code, isPersistent: false, rememberClient: false)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "lockedOut");
            return View("Login2fa", vm);
        }
        if (!result.Succeeded)
        {
            // Wrong codes count as failed attempts too (Identity increments them);
            // the admin threshold may turn this one into a lock.
            ModelState.AddModelError(string.Empty,
                await ApplyAdminLockoutAsync(user) ? "lockedOut" : "invalidCode");
            return View("Login2fa", vm);
        }
        await StampLastLoginAsync(user);
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
        // Staff reset links now really expire in 30 minutes (StaffResetTokenProvider), matching
        // the agent + admin pw.help copy and settings-agents' sa.resetWindow default (30).
        // TODO(S7): settings-agents consumes — sa.resetWindow owns the value once that page
        // ports (config key StaffAuth:ResetWindowMinutes until then). Portal's 1-hour promise
        // is a separate customer-side canon item (still on Identity's 1-day default).
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
        return View(PwresetNewViewName, new PwresetNewVm { Email = email, Token = token });
    }

    protected async Task<IActionResult> PwresetNewPostCore(PwresetNewVm vm)
    {
        ViewData["PwresetPostUrl"] = $"{AreaPrefix}/pwreset/new";
        ViewData["PwresetBackUrl"] = $"{AreaPrefix}/login";
        if (!ModelState.IsValid) return View(PwresetNewViewName, vm);

        var user = await users.FindByEmailAsync(vm.Email);
        if (user is not null)
        {
            var result = await users.ResetPasswordAsync(user, vm.Token, vm.Password);
            if (!result.Succeeded)
            {
                // Same code → i18n mapping as the portal flow (shared PwresetNew view).
                foreach (var e in result.Errors)
                    ModelState.AddModelError(string.Empty, e.Code switch
                    {
                        "InvalidToken" => "invalidToken",
                        _ when e.Code.StartsWith("Password") => "passwordWeak",
                        _ => e.Description,
                    });
                return View(PwresetNewViewName, vm);
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
