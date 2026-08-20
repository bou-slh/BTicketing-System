using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

public sealed record SettingsUsersVm(
    UserSettings Settings,
    IReadOnlyList<SystemTemplateRow> Templates);

/// <summary>
/// Admin user settings (mockups/admin/settings-users.html, ROADMAP §6.3 + §4 B2/B3):
/// every control persists into the "users" namespace via
/// <see cref="ISettingsService"/> (PRG toasts; B3 validation writes NOTHING on
/// failure). LIVE: registration_mode gates the portal /register route + the login
/// page's register link (the §6.3 row's core promise — portal AccountController),
/// and max_login_attempts + lockout_minutes are the customer lockout policy (portal
/// login via LoginLockoutPolicy). S8 slice 5 adds two more: auth_tokens puts a signed
/// auto-login token on the ticket links in customer mail, and email_verify turns
/// registration into a verify-by-mail flow over the su.tplConfirmEmail row below.
/// Persisted-only keys are annotated on
/// <see cref="UserSettings"/>. B2: the six su.tpl* rows get per-row Edit dialogs
/// server-prefilled from <see cref="ISystemTemplateService"/> and saved back
/// (subject + TR/EN bodies).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsUsersController(
    AppDbContext db,
    ISettingsService settings,
    ISystemTemplateService templates) : Controller
{
    /// <summary>The page's template rows (su.tplGuest/Login/Pwreset/VerifyPage/ConfirmEmail/ConfirmedPage order).</summary>
    internal static readonly string[] TemplateCodes =
        ["user.access.link", "user.banner", "user.pwreset", "user.verify.page", "user.confirm.email", "user.confirmed.page"];

    [HttpGet("/admin/settings-users")]
    [NavKey("settings-users")]
    public async Task<IActionResult> Index(CancellationToken ct = default) =>
        View(new SettingsUsersVm(
            await settings.GetUsersAsync(ct),
            await templates.GetAsync(TemplateCodes, ct)));

    [HttpPost("/admin/settings-users")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();
        bool Chk(string name) => form[name].Contains("true");

        // ---- validate (B3 server side; nothing persists on failure) -----------------
        string[] nameFormats = ["full", "lastfirst", "short"];
        string[] avatarSources = ["initials", "gravatar"];
        string[] regModes = ["closed", "public", "invite"];
        string[] pwPolicies = ["none", "basic", "strong"];
        int[] attemptOptions = [3, 5, 10];
        int[] durationOptions = [15, 30, 60];

        var nameFormat = Str("name_format");
        var avatarSource = Str("avatar_source");
        var regMode = Str("registration_mode");
        var pwPolicy = Str("password_policy");
        if (!nameFormats.Contains(nameFormat) || !avatarSources.Contains(avatarSource)
            || !regModes.Contains(regMode) || !pwPolicies.Contains(pwPolicy)
            || !int.TryParse(Str("max_login_attempts"), out var attempts) || !attemptOptions.Contains(attempts)
            || !int.TryParse(Str("lockout_minutes"), out var lockMinutes) || !durationOptions.Contains(lockMinutes)
            || !int.TryParse(Str("session_timeout_minutes"), out var session) || session < 0)
        {
            return SaveResult("su.errValues", error: true);
        }

        // ---- persist (Setting rows are INotAudited by design) ------------------------
        // Persisted-only (UserSettings annotations): name format / avatar /
        // registration-required / password policy / session timeout / auth tokens /
        // email verification.
        await settings.SetAsync("users", "name_format", nameFormat, ct);
        await settings.SetAsync("users", "avatar_source", avatarSource, ct);
        await settings.SetAsync("users", "registration_required", Chk("registration_required").ToString(), ct);
        await settings.SetAsync("users", "password_policy", pwPolicy, ct);
        await settings.SetAsync("users", "session_timeout_minutes", session.ToString(), ct);
        await settings.SetAsync("users", "auth_tokens", Chk("auth_tokens").ToString(), ct);
        await settings.SetAsync("users", "email_verify", Chk("email_verify").ToString(), ct);

        // LIVE: portal register gate + the customer lockout policy (class doc).
        await settings.SetAsync("users", "registration_mode", regMode, ct);
        await settings.SetAsync("users", "max_login_attempts", attempts.ToString(), ct);
        await settings.SetAsync("users", "lockout_minutes", lockMinutes.ToString(), ct);

        return SaveResult("su.toastSaved");
    }

    // ---- dlg-tpl per-row template dialogs (B2: prefilled + save back) ----------------

    [HttpPost("/admin/settings-users/template")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Template(string code, string name, string body_tr, string body_en, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (!TemplateCodes.Contains(code) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(body_tr) || string.IsNullOrWhiteSpace(body_en))
        {
            return SaveResult("su.errValues", error: true);
        }

        try
        {
            await templates.SaveAsync(code, name.Trim(), body_tr.Trim(), body_en.Trim(),
                ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        }
        catch (DomainRuleException)
        {
            return SaveResult("su.errValues", error: true);
        }

        return SaveResult("su.toastTpl");
    }

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["SuToast"] = toastKey;
        if (error)
            TempData["SuToastError"] = true;
        return Redirect("/admin/settings-users");
    }
}
