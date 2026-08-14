using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal profile page (mockups/portal/profile.html): contact details + preferences
/// save for real, the language preference switches the culture cookie server-side,
/// and the optional password change goes through Identity (ROADMAP §6.1 profile
/// row; B3 validation).
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class ProfileController(
    UserManager<CustomerUser> users,
    CustomerSignInManager signIn,
    AppDbContext db) : Controller
{
    /// <summary>Mockup's time-zone options (register.html + profile.html share the list).</summary>
    public static readonly (string Id, string Label)[] TimeZones =
    [
        ("Europe/Istanbul", "Europe/Istanbul (GMT+3)"),
        ("Europe/Berlin", "Europe/Berlin (GMT+2)"),
        ("Europe/London", "Europe/London (GMT+1)"),
        ("UTC", "UTC"),
    ];

    [HttpGet("/profile")]
    [NavKey("home")] // mockup body carries data-nav="home"; the page itself is reached from the account menu
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge(AuthSchemes.Customer);

        return View(new ProfileVm
        {
            Email = user.Email ?? "",
            Name = user.FullName,
            Phone = user.PhoneNumber,
            TimeZone = user.TimeZone ?? "Europe/Istanbul",
            Language = user.Language ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        });
    }

    [HttpPost("/profile")]
    [NavKey("home")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ProfileVm vm)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge(AuthSchemes.Customer);

        vm.Email = user.Email ?? ""; // display-only; never taken from the post
        if (!ModelState.IsValid) return View(vm);

        // ---- Optional password change first (B3): nothing is applied if it fails.
        if (!string.IsNullOrEmpty(vm.NewPassword))
        {
            var pw = await users.ChangePasswordAsync(user, vm.CurrentPassword!, vm.NewPassword);
            if (!pw.Succeeded)
            {
                // Identity error codes → view-localized markers (Register pattern).
                foreach (var e in pw.Errors)
                    ModelState.AddModelError(string.Empty, e.Code switch
                    {
                        "PasswordMismatch" => "currentWrong",
                        _ when e.Code.StartsWith("Password") => "passwordWeak",
                        _ => e.Description,
                    });
                return View(vm);
            }
        }

        // ---- Contact + preference fields with a backing store.
        user.FullName = vm.Name.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(vm.Phone) ? null : vm.Phone.Trim();
        if (TimeZones.Any(t => t.Id == vm.TimeZone)) user.TimeZone = vm.TimeZone;
        if (CultureController.Supported.Contains(vm.Language)) user.Language = vm.Language;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            foreach (var e in updated.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(vm);
        }

        // Header chrome reads the FullName claim (persisted customer_user_claim).
        var claims = await users.GetClaimsAsync(user);
        var nameClaim = claims.FirstOrDefault(c => c.Type == "FullName");
        if (nameClaim is null)
            await users.AddClaimAsync(user, new Claim("FullName", user.FullName));
        else if (nameClaim.Value != user.FullName)
            await users.ReplaceClaimAsync(user, nameClaim, new Claim("FullName", user.FullName));

        // Keep the domain User record (ticket owner display, inbound-mail match) in sync.
        var domainUser = await db.Users.SingleOrDefaultAsync(u => u.IdentityUserId == user.Id);
        if (domainUser is not null)
        {
            domainUser.Name = user.FullName;
            domainUser.Phone = user.PhoneNumber;
            await db.SaveChangesAsync();
        }

        // Rebuild the auth cookie so the header shows the new name immediately.
        await signIn.RefreshSignInAsync(user);

        // Language preference takes effect on the redirect response itself.
        if (user.Language is { } lang)
            CultureController.ApplyCultureCookie(Response, lang);

        TempData["ProfileSaved"] = true;
        return Redirect("/profile");
    }
}

public class ProfileVm : IValidatableObject
{
    /// <summary>Display-only (mockup: readonly input + "cannot be changed" help).</summary>
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "errName")]
    public string Name { get; set; } = "";

    public string? Phone { get; set; }

    public string TimeZone { get; set; } = "Europe/Istanbul";

    public string Language { get; set; } = "tr";

    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
    public string? NewPassword2 { get; set; }

    /// <summary>Password change is optional: all three empty = no change (mockup help text).</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(NewPassword) && string.IsNullOrEmpty(NewPassword2) && string.IsNullOrEmpty(CurrentPassword))
            yield break;

        if (string.IsNullOrEmpty(CurrentPassword))
            yield return new ValidationResult("errCurrentRequired", [nameof(CurrentPassword)]);
        if (string.IsNullOrEmpty(NewPassword) || NewPassword.Length < 8)
            yield return new ValidationResult("passwordWeak", [nameof(NewPassword)]);
        if (NewPassword != NewPassword2)
            yield return new ValidationResult("passwordMismatch", [nameof(NewPassword2)]);
    }
}
