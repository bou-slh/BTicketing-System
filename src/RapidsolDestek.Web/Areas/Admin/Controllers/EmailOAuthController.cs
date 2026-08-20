using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// The OAuth2 authorization-code consent flow behind admin/email-edit's "Yetkilendir"
/// button (S8 slice 6; INVENTED UI — the mockup's dlg-auth collects app credentials
/// only, flagged in the ROADMAP email row).
///
/// Start (POST, antiforgery, AdminOnly) mints a signed+encrypted state carrying the
/// PKCE verifier and redirects the admin to the provider. Callback (GET, AdminOnly)
/// verifies that state — refusing anything tampered with, expired, or minted for a
/// different admin — exchanges the code for tokens and stores the refresh token
/// against the channel. Tokens never reach a view: the page shows only whether
/// consent exists, and for which mailbox.
///
/// The redirect URI is derived from core/helpdesk_url, so it matches whatever the
/// installation registered with the provider; a mismatch is the provider's own error,
/// surfaced as a toast rather than guessed around.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class EmailOAuthController(
    AppDbContext db,
    ISettingsService settings,
    IMailOAuthStateService stateCodec,
    IMailOAuthTokenService tokens,
    ISystemLogService syslog) : Controller
{
    /// <summary>Path the provider redirects back to; must match the app registration.</summary>
    public const string CallbackPath = "admin/email-oauth/callback";

    [HttpPost("/admin/email-oauth/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int id, string? kind, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff?.Id is not { } staffId)
            return Forbid();

        var channelKind = kind == "out" ? EmailChannelKind.Smtp : EmailChannelKind.Mailbox;
        var channel = await db.EmailAccounts.Where(a => a.Id == id)
            .SelectMany(a => a.Channels).AsNoTracking()
            .SingleOrDefaultAsync(c => c.Kind == channelKind, ct);
        if (channel is null)
            return Back(id, "ee.oauthErrUnsaved");
        if (channel.AuthKind != MailAuthKind.OAuth2 || string.IsNullOrWhiteSpace(channel.OAuthClientId))
            return Back(id, "ee.oauthErrConfig");

        var (state, challenge) = stateCodec.Create(channel.Id, staffId);
        return Redirect(MailOAuthProviders.BuildAuthorizeUrl(
            channel, await RedirectUriAsync(ct), state, challenge));
    }

    /// <summary>
    /// Provider redirect target. Everything is refused before the code is exchanged:
    /// a provider-reported error (admin declined), an unverifiable state, a state
    /// belonging to another admin's session, or a vanished channel.
    /// </summary>
    [HttpGet("/admin/email-oauth/callback")]
    public async Task<IActionResult> Callback(
        string? code, string? state, string? error, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff?.Id is not { } staffId)
            return Forbid();

        var verified = stateCodec.Validate(state);
        if (verified is null || verified.StaffId != staffId)
        {
            // No account id to return to — the state is exactly what carried it.
            await syslog.LogAsync(SystemLogType.Warning, "OAuth2 posta yetkilendirme durumu doğrulanamadı",
                "state parameter missing, tampered, expired, or issued to another session",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString(), logger: "mail", ct: ct);
            return BackToList("em.oauthErrState");
        }

        var channel = await db.Set<EmailChannel>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == verified.ChannelId, ct);
        if (channel is null)
            return BackToList("em.oauthErrState");

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Back(channel.EmailAccountId, "ee.oauthErrProvider");

        try
        {
            var result = await tokens.RedeemCodeAsync(
                channel, code, await RedirectUriAsync(ct), verified.CodeVerifier, ct);
            await syslog.LogAsync(SystemLogType.Debug, "OAuth2 posta yetkilendirmesi tamamlandı",
                $"channel {channel.Id} ({channel.Kind}) → {result.Account ?? "?"}",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString(), logger: "mail", ct: ct);
            return Back(channel.EmailAccountId, "ee.oauthToastOk", error: false);
        }
        catch (MailOAuthException ex)
        {
            await syslog.LogAsync(SystemLogType.Error, "OAuth2 posta yetkilendirmesi başarısız",
                $"channel {channel.Id}: {ex.Stage}: {ex.Message}",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString(), logger: "mail", ct: ct);
            return Back(channel.EmailAccountId,
                ex.Stage == MailOAuthException.ConfigStage ? "ee.oauthErrConfig" : "ee.oauthErrExchange");
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>core/helpdesk_url + the fixed callback path (the value an admin
    /// registers with Microsoft/Google). Falls back to the current request's origin
    /// when the setting is empty, so a fresh install is not silently broken.</summary>
    private async Task<string> RedirectUriAsync(CancellationToken ct)
    {
        var baseUrl = (await settings.GetAsync("core", "helpdesk_url", ct))?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/";
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        return new Uri(new Uri(baseUrl), CallbackPath).ToString();
    }

    private IActionResult Back(int accountId, string key, bool error = true)
    {
        TempData["EmailEditToast"] = key;
        if (error)
            TempData["EmailEditToastError"] = true;
        return Redirect($"/admin/email-edit?id={accountId}");
    }

    /// <summary>Nowhere to return to: the account id lived in the state we just
    /// refused, so the list page carries the refusal.</summary>
    private IActionResult BackToList(string key)
    {
        TempData["EmailsToast"] = key;
        TempData["EmailsToastError"] = true;
        return Redirect("/admin/emails");
    }
}
