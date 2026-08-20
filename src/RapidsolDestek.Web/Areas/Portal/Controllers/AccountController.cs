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
using RapidsolDestek.Infrastructure.Services;
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
    IWebHostEnvironment env,
    ISettingsService settings,
    IHtmlSanitizerService sanitizer,
    IMailLinkTokenService tokens,
    IPortalAccountMailer accountMail) : Controller
{
    // ---- Login -------------------------------------------------------------
    // TODO(S7): optional TOTP 2FA step for portal customers (ROADMAP B6; the
    // portal/login.html mockup has no 2FA UI — staff logins already 2FA via
    // their Login2fa views, and 2FA stays mandatory for admins only).

    [HttpGet("/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl)
    {
        // settings-users su.regMode: the register link only renders while
        // self-registration is public (the /register route is gated the same way).
        ViewData["ShowRegister"] = (await settings.GetUsersAsync()).RegistrationMode == "public";
        return View(new LoginVm { ReturnUrl = returnUrl });
    }

    [HttpPost("/login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm)
    {
        ViewData["ShowRegister"] = (await settings.GetUsersAsync()).RegistrationMode == "public";
        if (!ModelState.IsValid) return View(vm);

        var user = await users.FindByEmailAsync(vm.User) ?? await users.FindByNameAsync(vm.User);
        if (user is not null)
        {
            var result = await signIn.PasswordSignInAsync(user, vm.Password, vm.Remember, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                // users/email_verify (osTicket): an unverified account may hold correct
                // credentials and still not get in — the check sits AFTER the password
                // so an attacker learns nothing new. Accounts that predate the switch,
                // and every account registered while it was off, are already confirmed.
                if ((await settings.GetUsersAsync()).EmailVerify && !user.EmailConfirmed)
                {
                    await signIn.SignOutAsync();
                    ViewData["ResendEmail"] = user.Email;
                    ModelState.AddModelError(string.Empty, "emailUnverified");
                    return View(vm);
                }
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
            // B6 customer lockout policy owned by admin/settings-users
            // (su.maxAttempts / su.lockDuration).
            var policy = await settings.GetUsersAsync();
            if (await LoginLockoutPolicy.ApplyAsync(users, user, policy.MaxLoginAttempts, policy.LockoutMinutes))
            {
                // admin/system-logs (S7): a tripped customer lock is a syslog error;
                // stored per system/log_level.
                await SysLogAsync(SystemLogType.Error,
                    $"Hesap kilitlendi: '{user.UserName}' kullanıcısı (art arda başarısız girişler)");
                ModelState.AddModelError(string.Empty, "lockedOut");
                return View(vm);
            }
            await SysLogAsync(SystemLogType.Warning,
                $"Başarısız giriş denemesi: '{user.UserName}' kullanıcısı " +
                $"({await users.GetAccessFailedCountAsync(user)}. deneme)");
        }
        else
        {
            await SysLogAsync(SystemLogType.Warning,
                $"Başarısız giriş denemesi: bilinmeyen kullanıcı '{vm.User}'");
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

    /// <summary>
    /// admin/system-logs writer (S7): portal auth failures warn like the staff ones
    /// (StaffAccountControllerBase twin); system/log_level decides storage.
    /// </summary>
    private Task SysLogAsync(SystemLogType type, string title) =>
        HttpContext.RequestServices.GetRequiredService<ISystemLogService>().LogAsync(
            type, title,
            ip: HttpContext.Connection.RemoteIpAddress?.ToString(), logger: "auth");

    // ---- Register ----------------------------------------------------------

    [HttpGet("/register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register() =>
        await RegistrationClosedAsync() ? NotFound() : View(new RegisterVm());

    [HttpPost("/register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterVm vm)
    {
        if (await RegistrationClosedAsync()) return NotFound();
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
        var domainUser = await LinkDomainUserAsync(user, vm);

        // users/email_verify (osTicket): with verification on, the account exists but
        // cannot sign in until the mailed link is followed. With it off, the account is
        // confirmed on the spot — so switching the setting on later never locks out
        // people who registered under the old rule.
        if ((await settings.GetUsersAsync()).EmailVerify)
        {
            await SendVerificationAsync(user, domainUser.Id, vm.Name.Trim());
            return RedirectToAction(nameof(VerifySent));
        }

        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await signIn.SignInAsync(user, isPersistent: false);
        return Redirect("/");
    }

    // ---- Email verification (users/email_verify) ----------------------------

    /// <summary>"We mailed you a link" page, with the resend form.</summary>
    [HttpGet("/register/sent")]
    [AllowAnonymous]
    public IActionResult VerifySent() => View();

    /// <summary>
    /// Follows the mailed verification link: the one-shot token is consumed, the
    /// account is confirmed and the visitor is signed straight in (osTicket's
    /// "account confirmed" page). A dead link — expired, already used, or issued for
    /// another purpose — lands on the same page with an honest failure state, never a
    /// silent success.
    /// </summary>
    [HttpGet("/register/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> Verify(string? token, CancellationToken ct)
    {
        var userId = await tokens.RedeemAsync(MailTokenPurpose.EmailVerify, token, ct);
        if (userId is null)
            return View("Verify", false);

        var identityId = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.IdentityUserId).SingleOrDefaultAsync(ct);
        var identity = identityId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        if (identity is null)
            return View("Verify", false);

        identity.EmailConfirmed = true;
        await users.UpdateAsync(identity);
        await signIn.SignInAsync(identity, isPersistent: false);
        return View("Verify", true);
    }

    /// <summary>Resend path: a fresh token invalidates the previous link. Always
    /// reports "sent" — an unknown address must not be distinguishable.</summary>
    [HttpPost("/register/resend")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyResend(string? email, CancellationToken ct)
    {
        var identity = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (identity is { EmailConfirmed: false }
            && await db.Users.Where(u => u.IdentityUserId == identity.Id)
                .Select(u => (int?)u.Id).SingleOrDefaultAsync(ct) is { } domainUserId)
        {
            await SendVerificationAsync(identity, domainUserId, identity.FullName ?? identity.Email ?? "");
        }
        return RedirectToAction(nameof(VerifySent));
    }

    private async Task SendVerificationAsync(CustomerUser identity, int domainUserId, string name)
    {
        var token = await tokens.IssueAsync(MailTokenPurpose.EmailVerify, domainUserId,
            MailLinkTokenService.VerifyLifetime);
        var link = $"{Request.Scheme}://{Request.Host}/register/verify?token={Uri.EscapeDataString(token)}";
        await accountMail.SendVerificationAsync(identity.Email!, name, link);
        if (env.IsDevelopment()) TempData["DevVerifyLink"] = link;
    }

    // ---- Invitation (guest → portal account) --------------------------------

    /// <summary>
    /// The invited guest's landing page: the token is only PEEKED at here, so a
    /// refreshed or bookmarked form still works — it is consumed on the POST that
    /// actually creates the account.
    /// </summary>
    [HttpGet("/invite")]
    [AllowAnonymous]
    public async Task<IActionResult> Invite(string? token, CancellationToken ct)
    {
        var userId = await tokens.PeekAsync(MailTokenPurpose.Invite, token, ct);
        if (userId is null)
            return View(new InviteVm { Invalid = true });

        var invitee = await db.Users.Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails.Where(e => e.Id == u.DefaultEmailId).Select(e => e.Address).FirstOrDefault()
                    ?? u.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        return invitee?.Email is null
            ? View(new InviteVm { Invalid = true })
            : View(new InviteVm { Token = token!, Name = invitee.Name, Email = invitee.Email });
    }

    /// <summary>
    /// Redeems the invitation: creates the Identity account for the EXISTING domain
    /// user (agent- or mail-created guest) and links the two, confirmed immediately —
    /// following the mailed link already proves the address. Registration mode does not
    /// gate this route: "invite" exists precisely so invited people can join while
    /// self-registration is shut.
    /// </summary>
    [HttpPost("/invite")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(InviteVm vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(await RefillInviteAsync(vm, ct));

        // Consume FIRST: a token that survives a failed password attempt would be a
        // second live invitation.
        var userId = await tokens.RedeemAsync(MailTokenPurpose.Invite, vm.Token, ct);
        if (userId is null)
            return View(new InviteVm { Invalid = true });

        var domainUser = await db.Users.Include(u => u.Emails).SingleOrDefaultAsync(u => u.Id == userId, ct);
        var address = domainUser?.Emails.FirstOrDefault(e => e.Id == domainUser.DefaultEmailId)?.Address
            ?? domainUser?.Emails.FirstOrDefault()?.Address;
        if (domainUser is null || address is null || domainUser.IdentityUserId is not null)
            return View(new InviteVm { Invalid = true });

        var identity = new CustomerUser
        {
            UserName = address,
            Email = address,
            FullName = domainUser.Name,
            PhoneNumber = domainUser.Phone,
            EmailConfirmed = true,
            TimeZone = "Europe/Istanbul",
        };
        var result = await users.CreateAsync(identity, vm.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(string.Empty, e.Code switch
                {
                    "DuplicateUserName" or "DuplicateEmail" => "emailTaken",
                    _ when e.Code.StartsWith("Password") => "passwordWeak",
                    _ => e.Description,
                });
            // The token is spent; a failed create leaves the invitation unusable, so
            // say so instead of showing a form that can no longer succeed.
            return View(new InviteVm { Invalid = true });
        }

        domainUser.IdentityUserId = identity.Id;
        await db.SaveChangesAsync(ct);
        await signIn.SignInAsync(identity, isPersistent: false);
        return Redirect("/");
    }

    private async Task<InviteVm> RefillInviteAsync(InviteVm vm, CancellationToken ct)
    {
        var userId = await tokens.PeekAsync(MailTokenPurpose.Invite, vm.Token, ct);
        if (userId is null)
            return new InviteVm { Invalid = true };
        var invitee = await db.Users.Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails.Where(e => e.Id == u.DefaultEmailId).Select(e => e.Address).FirstOrDefault()
                    ?? u.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        return vm with { Name = invitee?.Name ?? vm.Name, Email = invitee?.Email ?? vm.Email };
    }

    /// <summary>
    /// settings-users su.regMode (users/registration_mode): "public" self-serves;
    /// "closed" and "invite" hide the route honestly (404 — the settings-kb enable_kb
    /// precedent). "invite" refuses SELF-registration exactly like "closed"; invited
    /// people arrive through /invite with a signed token instead, which is what makes
    /// the mode different from "closed" (S8 slice 5).
    /// </summary>
    private async Task<bool> RegistrationClosedAsync() =>
        (await settings.GetUsersAsync()).RegistrationMode != "public";

    /// <summary>
    /// Registration must end with a domain <see cref="User"/> row — every portal page
    /// resolves the principal via User.IdentityUserId, and /open forbids without one
    /// (osTicket user_account ↔ user split). Claims an existing unlinked user carrying
    /// this address (agent/guest-created records), else creates one, auto-linking the
    /// organization by email domain (Organization.Domain, comma-separated).
    /// </summary>
    private async Task<User> LinkDomainUserAsync(CustomerUser identity, RegisterVm vm)
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
            return existing;
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
        return domainUser;
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
    public async Task<IActionResult> Offline(CancellationToken ct)
    {
        // Pages port (S7, "pages served on portal"): the CONFIGURED offline
        // SitePage body replaces the S5 static copy while maintenance mode holds.
        // Resolution mirrors settings-company: company/offline_page_id, unset ⇒
        // the first Offline-type page. An INACTIVE page (the seeded "Bakım Modu"
        // canon ships disabled) falls back to the static copy — activating it on
        // admin/pages is the switch that puts the authored content live.
        var company = await settings.GetSectionAsync("company", ct);
        var configuredId = int.TryParse(company.GetValueOrDefault("offline_page_id"), out var i) ? i : 0;
        var offlinePages = db.SitePages.AsNoTracking().Where(p => p.Type == SitePageType.Offline);
        var page = configuredId > 0
            ? await offlinePages.SingleOrDefaultAsync(p => p.Id == configuredId, ct)
            : await offlinePages.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (page is { IsActive: true })
            ViewData["OfflineBody"] = sanitizer.Sanitize(page.Body);
        return View();
    }

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

/// <summary>The /invite set-password form. <see cref="Invalid"/> renders the dead-link
/// state instead of the form (expired, already redeemed, or foreign token).</summary>
public record InviteVm
{
    public string Token { get; init; } = "";

    /// <summary>Display only — the account is created for the invited domain user,
    /// never for whatever address a poster might submit.</summary>
    public string? Name { get; init; }

    public string? Email { get; init; }

    public bool Invalid { get; init; }

    [Required(ErrorMessage = "passwordWeak"), MinLength(8, ErrorMessage = "passwordWeak")]
    public string Password { get; init; } = "";

    [Compare(nameof(Password), ErrorMessage = "passwordMismatch")]
    public string Password2 { get; init; } = "";
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
