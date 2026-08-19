using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Web.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent's own profile (mockups/agent/profile.html, ROADMAP §6.2 profile row): the
/// three-tab form saves for real, the password dialog goes through Identity (portal
/// B3 precedent), "Yapılandır" runs the TOTP enrollment, the vacation switch feeds
/// the assignment guard (StaffAvailability), the signature editor persists the text
/// the ticket-view/ticket-open composers consume (data-sig-*), and the language
/// select switches the culture cookie server-side (portal profile precedent).
/// Own profile only — no staff-id parameter; other agents' records are S7
/// admin/staff-edit territory.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class ProfileController(
    AppDbContext db,
    UserManager<StaffUser> users,
    StaffSignInManager signIn) : Controller
{
    /// <summary>This mockup's time-zone options (order and labels differ from the portal list).</summary>
    public static readonly (string Id, string Label)[] TimeZones =
    [
        ("Europe/Istanbul", "Europe/Istanbul (GMT+3)"),
        ("Europe/London", "Europe/London (GMT+1)"),
        ("Europe/Berlin", "Europe/Berlin (GMT+2)"),
    ];

    // TODO(S7): PageSize/AutoRefreshMinutes/DefaultQueue/ThreadOrderNewestFirst/
    // Use24HourTime persist here but the list engines / thread rendering still use
    // their fixed values — consumption lands with the admin settings work
    // (ROADMAP §6.3 "every setting round-trips"). DefaultSignatureType likewise
    // persists; preselecting the composer sig radio from it is deferred with them.

    [HttpGet("/agent/profile")]
    [NavKey("profile")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (staff, user) = await LoadAsync(ct);
        if (staff is null || user is null)
            return Challenge(AuthSchemes.Staff);

        return View(await BuildVmAsync(staff, user));
    }

    [HttpPost("/agent/profile")]
    [NavKey("profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(AgentProfileVm vm, CancellationToken ct)
    {
        var (staff, user) = await LoadAsync(ct);
        if (staff is null || user is null)
            return Challenge(AuthSchemes.Staff);

        async Task<IActionResult> FailAsync(string key)
        {
            var fresh = await BuildVmAsync(staff, user);
            CopyEditableFields(vm, fresh);
            ViewData["PfToastKey"] = key; // view maps to L[...] + error variant (users precedent)
            return View(fresh);
        }

        if (!ModelState.IsValid || !ValidOptions(vm))
            return await FailAsync("pf.errInvalid");

        // Unique-email check up front so nothing is half-applied when it fails.
        var email = vm.Email.Trim();
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null && existing.Id != user.Id)
            return await FailAsync("pf.errEmailInUse");

        // ---- 2FA method (pf-2fa select). "App" is only reachable through the
        // Yapılandır enrollment dialog (Enable2fa) — selecting it without a confirmed
        // authenticator fails loudly instead of pretending 2FA is on.
        var method = ParseTwoFactor(vm.TwoFactor);
        if (method != staff.TwoFactorMethod)
        {
            if (method == TwoFactorMethod.None && staff.IsAdmin)
                return await FailAsync("pf.err2faAdminRequired"); // admin 2FA is mandatory (AdminOnly policy)
            if (method == TwoFactorMethod.App)
                return await FailAsync("pf.err2faNeedsSetup");

            await users.SetTwoFactorEnabledAsync(user, method != TwoFactorMethod.None);
            staff.TwoFactorMethod = method;
        }

        // ---- Identity email sync (login + pwreset find staff by email).
        if (!string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            user.Email = email;
            user.EmailConfirmed = true; // dev parity with the seed; real confirmation flow is S7+
        }

        user.FullName = $"{vm.FirstName.Trim()} {vm.LastName.Trim()}";
        var updated = await users.UpdateAsync(user); // validates unique email
        if (!updated.Succeeded)
            return await FailAsync(updated.Errors.Any(e => e.Code.Contains("Email")) ? "pf.errEmailInUse" : "pf.errInvalid");

        // ---- Domain Staff row: contact, status, preferences, signature.
        staff.FirstName = vm.FirstName.Trim();
        staff.LastName = vm.LastName.Trim();
        staff.Email = email;
        staff.Phone = NullIfEmpty(vm.Phone);
        staff.PhoneExt = NullIfEmpty(vm.PhoneExt);
        staff.Mobile = NullIfEmpty(vm.Mobile);
        staff.OnVacation = vm.OnVacation;
        staff.PageSize = vm.PageSize;
        staff.AutoRefreshMinutes = vm.AutoRefresh;
        staff.DefaultQueue = vm.DefaultQueue == "mine" ? AgentDefaultQueue.Mine : AgentDefaultQueue.Open;
        staff.ThreadOrderNewestFirst = vm.ThreadOrder != "oldest";
        staff.DefaultSignatureType = vm.SigDefault switch
        {
            "dept" => SignatureType.Department,
            "none" => SignatureType.None,
            _ => SignatureType.Mine,
        };
        staff.Timezone = vm.TimeZone;
        staff.Use24HourTime = vm.TimeFormat != "12";
        staff.Language = vm.Language;
        staff.Signature = (vm.Signature ?? "").Replace("\r\n", "\n").Trim();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await db.SaveChangesAsync(ct);

        // Topbar chrome reads the FullName claim — keep it and the cookie in sync
        // (portal profile precedent).
        var claims = await users.GetClaimsAsync(user);
        var nameClaim = claims.FirstOrDefault(c => c.Type == "FullName");
        if (nameClaim is null)
            await users.AddClaimAsync(user, new Claim("FullName", user.FullName));
        else if (nameClaim.Value != user.FullName)
            await users.ReplaceClaimAsync(user, nameClaim, new Claim("FullName", user.FullName));
        await signIn.RefreshSignInAsync(user);

        // Language preference takes effect on the redirect response itself
        // (data-lang-switch submits this form; portal profile precedent).
        CultureController.ApplyCultureCookie(Response, vm.Language);

        TempData["PfToast"] = "pf.toastSaved";
        return Redirect("/agent/profile");
    }

    /// <summary>dlg-pass submit (B3, portal precedent). PRG with toast either way — the dialog has no inline error slot.</summary>
    [HttpPost("/agent/profile/password")]
    [NavKey("profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(string? currentPassword, string? newPassword, string? newPassword2, CancellationToken ct)
    {
        var (staff, user) = await LoadAsync(ct);
        if (staff is null || user is null)
            return Challenge(AuthSchemes.Staff);

        IActionResult Fail(string key)
        {
            TempData["PfToast"] = key;
            TempData["PfToastError"] = true;
            return Redirect("/agent/profile");
        }

        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(newPassword2))
            return Fail("pf.errInvalid");
        if (newPassword != newPassword2)
            return Fail("pf.errPasswordMismatch");

        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return Fail(result.Errors.Any(e => e.Code == "PasswordMismatch")
                ? "pf.errCurrentWrong"
                : "pf.errPasswordWeak");
        }

        staff.PasswordChangedAt = DateTimeOffset.UtcNow; // pf.passHelp "Son değişiklik"
        staff.RequirePasswordChange = false; // admin/staff-edit dlg-password flag satisfied
        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await signIn.RefreshSignInAsync(user);
        TempData["PfToast"] = "pf.toastPass";
        return Redirect("/agent/profile");
    }

    /// <summary>
    /// dlg-2fa submit ("Yapılandır" enrollment): verifies the entered TOTP code against
    /// the authenticator key shown in the dialog, then enables app-based 2FA (admin
    /// 2fa-setup precedent, surfaced in the profile per the ROADMAP row).
    /// </summary>
    [HttpPost("/agent/profile/2fa/enable")]
    [NavKey("profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable2fa(string? code, CancellationToken ct)
    {
        var (staff, user) = await LoadAsync(ct);
        if (staff is null || user is null)
            return Challenge(AuthSchemes.Staff);

        var normalized = (code ?? "").Replace(" ", "").Replace("-", "");
        var valid = normalized.Length > 0 && await users.VerifyTwoFactorTokenAsync(
            user, users.Options.Tokens.AuthenticatorTokenProvider, normalized);
        if (!valid)
        {
            TempData["PfToast"] = "pf.err2faCode";
            TempData["PfToastError"] = true;
            return Redirect("/agent/profile");
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        staff.TwoFactorMethod = TwoFactorMethod.App;
        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await db.SaveChangesAsync(ct);

        TempData["PfToast"] = "pf.toast2faOn";
        return Redirect("/agent/profile");
    }

    // ---- Helpers ----------------------------------------------------------------------

    private async Task<(Staff? Staff, StaffUser? User)> LoadAsync(CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        var user = await users.GetUserAsync(User);
        return (staff, user);
    }

    private async Task<AgentProfileVm> BuildVmAsync(Staff staff, StaffUser user)
    {
        // Authenticator key for the dlg-2fa enrollment: reuse the existing key (a
        // reset would invalidate a live enrollment); create one only when absent.
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        return new AgentProfileVm
        {
            FirstName = staff.FirstName,
            LastName = staff.LastName,
            Email = staff.Email ?? user.Email ?? "",
            Phone = staff.Phone,
            PhoneExt = staff.PhoneExt,
            Mobile = staff.Mobile,
            TwoFactor = staff.TwoFactorMethod switch
            {
                TwoFactorMethod.App => "app",
                TwoFactorMethod.Email => "email",
                _ => "none",
            },
            OnVacation = staff.OnVacation,
            PageSize = staff.PageSize,
            AutoRefresh = staff.AutoRefreshMinutes,
            DefaultQueue = staff.DefaultQueue == AgentDefaultQueue.Mine ? "mine" : "open",
            ThreadOrder = staff.ThreadOrderNewestFirst ? "newest" : "oldest",
            SigDefault = staff.DefaultSignatureType switch
            {
                SignatureType.Department => "dept",
                SignatureType.None => "none",
                _ => "mine",
            },
            TimeZone = staff.Timezone ?? "Europe/Istanbul",
            TimeFormat = staff.Use24HourTime ? "24" : "12",
            Language = staff.Language
                ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            Signature = staff.Signature,
            Username = staff.Username,
            PasswordChangedAt = staff.PasswordChangedAt,
            AuthenticatorKey = key ?? "",
            OtpauthUri = $"otpauth://totp/RapidsolDestek:{Uri.EscapeDataString(staff.Username)}" +
                         $"?secret={key}&issuer=RapidsolDestek",
        };
    }

    /// <summary>Re-render after a failed save: keep the user's edits, refresh display-only fields.</summary>
    private static void CopyEditableFields(AgentProfileVm from, AgentProfileVm into)
    {
        into.FirstName = from.FirstName;
        into.LastName = from.LastName;
        into.Email = from.Email;
        into.Phone = from.Phone;
        into.PhoneExt = from.PhoneExt;
        into.Mobile = from.Mobile;
        into.TwoFactor = from.TwoFactor;
        into.OnVacation = from.OnVacation;
        into.PageSize = from.PageSize;
        into.AutoRefresh = from.AutoRefresh;
        into.DefaultQueue = from.DefaultQueue;
        into.ThreadOrder = from.ThreadOrder;
        into.SigDefault = from.SigDefault;
        into.TimeZone = from.TimeZone;
        into.TimeFormat = from.TimeFormat;
        into.Language = from.Language;
        into.Signature = from.Signature;
    }

    private static bool ValidOptions(AgentProfileVm vm) =>
        new[] { 10, 25, 50 }.Contains(vm.PageSize)
        && new[] { 0, 1, 3 }.Contains(vm.AutoRefresh)
        && new[] { "open", "mine" }.Contains(vm.DefaultQueue)
        && new[] { "newest", "oldest" }.Contains(vm.ThreadOrder)
        && new[] { "mine", "dept", "none" }.Contains(vm.SigDefault)
        && TimeZones.Any(t => t.Id == vm.TimeZone)
        && new[] { "24", "12" }.Contains(vm.TimeFormat)
        && CultureController.Supported.Contains(vm.Language)
        && new[] { "none", "app", "email" }.Contains(vm.TwoFactor);

    private static TwoFactorMethod ParseTwoFactor(string value) => value switch
    {
        "app" => TwoFactorMethod.App,
        "email" => TwoFactorMethod.Email,
        _ => TwoFactorMethod.None,
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public class AgentProfileVm
{
    // ---- Account tab ----
    [Required] public string FirstName { get; set; } = "";
    [Required] public string LastName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? PhoneExt { get; set; }
    public string? Mobile { get; set; }

    /// <summary>pf-2fa select: "none" | "app" | "email".</summary>
    public string TwoFactor { get; set; } = "none";

    public bool OnVacation { get; set; }

    // ---- Preferences tab ----
    public int PageSize { get; set; } = 25;
    public int AutoRefresh { get; set; }
    public string DefaultQueue { get; set; } = "open";
    public string ThreadOrder { get; set; } = "newest";
    public string SigDefault { get; set; } = "mine";
    public string TimeZone { get; set; } = "Europe/Istanbul";
    public string TimeFormat { get; set; } = "24";
    public string Language { get; set; } = "tr";

    // ---- Signature tab ----
    public string? Signature { get; set; }

    // ---- Display-only (never taken from the post) ----
    public string Username { get; set; } = "";
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public string AuthenticatorKey { get; set; } = "";
    public string OtpauthUri { get; set; } = "";
}
