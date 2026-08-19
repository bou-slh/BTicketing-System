using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

public sealed record SettingsAgentsVm(
    AgentSettings Settings,
    IReadOnlyList<SystemTemplateRow> Templates);

/// <summary>
/// Admin agent settings (mockups/admin/settings-agents.html, ROADMAP §6.3 + §4 B2/B3/
/// B6): every control persists into the "agents" namespace via
/// <see cref="ISettingsService"/> (PRG toasts; B3 validation writes NOTHING on
/// failure). LIVE: max_login_attempts + lockout_minutes = the STAFF-WIDE lockout
/// policy (StaffAccountControllerBase.ApplyStaffLockoutAsync at both sign-ins — the
/// B6 row's core promise), reset_window_minutes owns the staff reset-link lifespan
/// (StaffResetTokenProvider; the save syncs the options value in-process),
/// allow_pwreset gates the agent/admin pwreset routes, require_twofa forces the
/// email-code second step for un-enrolled staff (StaffSignInManager). Persisted-only
/// keys are annotated on <see cref="AgentSettings"/>. B2: the four sa.tpl* rows get
/// per-row Edit dialogs server-prefilled from <see cref="ISystemTemplateService"/>
/// and saved back (subject + TR/EN bodies).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsAgentsController(
    AppDbContext db,
    ISettingsService settings,
    ISystemTemplateService templates,
    IOptions<StaffResetTokenProviderOptions> resetOptions) : Controller
{
    /// <summary>The page's template rows (sa.tplWelcome/Banner/Pwreset/2fa order).</summary>
    internal static readonly string[] TemplateCodes =
        ["staff.welcome", "staff.banner", "staff.pwreset", "staff.2fa"];

    [HttpGet("/admin/settings-agents")]
    [NavKey("settings-agents")]
    public async Task<IActionResult> Index(CancellationToken ct = default) =>
        View(new SettingsAgentsVm(
            await settings.GetAgentsAsync(ct),
            await templates.GetAsync(TemplateCodes, ct)));

    [HttpPost("/admin/settings-agents")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();
        bool Chk(string name) => form[name].Contains("true");

        // ---- validate (B3 server side; nothing persists on failure) -----------------
        string[] nameFormats = ["full", "lastfirst", "short", "username"];
        string[] avatarSources = ["initials", "gravatar"];
        string[] pwPolicies = ["none", "basic", "strong"];
        int[] attemptOptions = [3, 5, 10];
        int[] durationOptions = [15, 30, 60];

        var nameFormat = Str("name_format");
        var avatarSource = Str("avatar_source");
        var pwPolicy = Str("password_policy");
        if (!nameFormats.Contains(nameFormat) || !avatarSources.Contains(avatarSource)
            || !pwPolicies.Contains(pwPolicy)
            || !int.TryParse(Str("max_login_attempts"), out var attempts) || !attemptOptions.Contains(attempts)
            || !int.TryParse(Str("lockout_minutes"), out var lockMinutes) || !durationOptions.Contains(lockMinutes)
            || !int.TryParse(Str("reset_window_minutes"), out var resetWindow) || resetWindow < 5
            || !int.TryParse(Str("session_timeout_minutes"), out var session) || session < 0)
        {
            return SaveResult("sa.errValues", error: true);
        }

        // ---- persist (Setting rows are INotAudited by design) ------------------------
        // Persisted-only (AgentSettings annotations): name format / masking / avatar /
        // block-collab / password policy / session timeout / IP binding.
        await settings.SetAsync("agents", "name_format", nameFormat, ct);
        await settings.SetAsync("agents", "identity_masking", Chk("identity_masking").ToString(), ct);
        await settings.SetAsync("agents", "avatar_source", avatarSource, ct);
        await settings.SetAsync("agents", "block_collab", Chk("block_collab").ToString(), ct);
        await settings.SetAsync("agents", "password_policy", pwPolicy, ct);
        await settings.SetAsync("agents", "session_timeout_minutes", session.ToString(), ct);
        await settings.SetAsync("agents", "ip_binding", Chk("ip_binding").ToString(), ct);

        // LIVE: staff pwreset flow gate + reset-link lifespan + forced email-code 2FA +
        // the staff-wide lockout policy (see the class doc for the consumers).
        await settings.SetAsync("agents", "allow_pwreset", Chk("allow_pwreset").ToString(), ct);
        await settings.SetAsync("agents", "reset_window_minutes", resetWindow.ToString(), ct);
        await settings.SetAsync("agents", "require_twofa", Chk("require_twofa").ToString(), ct);
        await settings.SetAsync("agents", "max_login_attempts", attempts.ToString(), ct);
        await settings.SetAsync("agents", "lockout_minutes", lockMinutes.ToString(), ct);

        // StaffResetTokenProvider reads its lifespan from the cached options instance
        // (Program.cs seeds it from this Setting lazily); keep it in sync in-process.
        resetOptions.Value.TokenLifespan = TimeSpan.FromMinutes(resetWindow);

        return SaveResult("sa.toastSaved");
    }

    // ---- dlg-tpl per-row template dialogs (B2: prefilled + save back) ----------------

    [HttpPost("/admin/settings-agents/template")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Template(string code, string name, string body_tr, string body_en, CancellationToken ct = default) =>
        SaveTemplateAsync(code, name, body_tr, body_en, ct);

    private async Task<IActionResult> SaveTemplateAsync(string code, string? name, string? bodyTr, string? bodyEn, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (!TemplateCodes.Contains(code) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(bodyTr) || string.IsNullOrWhiteSpace(bodyEn))
        {
            return SaveResult("sa.errValues", error: true);
        }

        try
        {
            await templates.SaveAsync(code, name.Trim(), bodyTr.Trim(), bodyEn.Trim(),
                ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        }
        catch (DomainRuleException)
        {
            return SaveResult("sa.errValues", error: true);
        }

        return SaveResult("sa.toastTpl");
    }

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["SaToast"] = toastKey;
        if (error)
            TempData["SaToastError"] = true;
        return Redirect("/admin/settings-agents");
    }
}
