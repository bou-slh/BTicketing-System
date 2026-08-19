using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin system-wide email settings (mockups/admin/email-settings.html, ROADMAP
/// §6.3): the "email" namespace via <see cref="ISettingsService"/> — three B9
/// scroll-spy sections whose selects list REAL rows (template sets, email accounts,
/// SMTP-capable accounts). LIVE: default_template_set_id — the outgoing effort
/// emails render from the chosen set (EffortEmailHandler). Everything else is
/// persisted-only until the S8 mail pipeline consumes it (annotated on
/// <see cref="EmailSettings"/>). Footer button order follows the S0-normalized
/// settings canon (reset → save; the mockup still carries the pre-S0 order — flagged).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class EmailSettingsController(AppDbContext db, ISettingsService settings) : Controller
{
    [HttpGet("/admin/email-settings")]
    [NavKey("email-settings")]
    public async Task<IActionResult> Index(CancellationToken ct = default) =>
        View(new EmailSettingsVm(
            await settings.GetEmailAsync(ct),
            await db.EmailTemplateSets.Where(s => s.IsActive).OrderBy(s => s.Id)
                .Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            await db.EmailAccounts.OrderBy(a => a.Id)
                .Select(a => new OptionVm(a.Id, a.Address)).ToListAsync(ct),
            // Default-SMTP options: accounts that actually carry an SMTP channel
            // (the mockup's single "destek@… — SMTP" row is exactly the seeded one).
            await db.EmailAccounts
                .Where(a => a.Channels.Any(c => c.Kind == EmailChannelKind.Smtp && c.Host != ""))
                .OrderBy(a => a.Id)
                .Select(a => new OptionVm(a.Id, a.Address)).ToListAsync(ct)));

    [HttpPost("/admin/email-settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        string Str(string name) => Request.Form[name].ToString().Trim();
        bool Chk(string name) => Request.Form[name].Contains("true");

        // B3 server validation: the admin address must be a real email when set.
        var adminEmail = Str("admin_email");
        if (adminEmail.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(adminEmail, out _))
            return Toast("es.errAdminEmail", error: true);

        // Selects: silently drop ids that no longer exist (helptopics precedent).
        var templateSetId = int.TryParse(Str("default_template_set_id"), out var ts)
            && await db.EmailTemplateSets.AnyAsync(s => s.Id == ts && s.IsActive, ct) ? ts : 0;
        var defaultEmailId = int.TryParse(Str("default_email_id"), out var de)
            && await db.EmailAccounts.AnyAsync(a => a.Id == de, ct) ? de : 0;
        var alertEmailId = int.TryParse(Str("alert_email_id"), out var ae)
            && await db.EmailAccounts.AnyAsync(a => a.Id == ae, ct) ? ae : 0;
        var defaultSmtp = int.TryParse(Str("default_smtp"), out var ds)
            && await db.EmailAccounts.AnyAsync(a => a.Id == ds
                && a.Channels.Any(c => c.Kind == EmailChannelKind.Smtp), ct)
            ? ds.ToString() : "system";

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            await settings.SetAsync("email", "default_template_set_id", templateSetId.ToString(), ct);
            await settings.SetAsync("email", "default_email_id", defaultEmailId.ToString(), ct);
            await settings.SetAsync("email", "alert_email_id", alertEmailId.ToString(), ct);
            await settings.SetAsync("email", "admin_email", adminEmail, ct);
            await settings.SetAsync("email", "verify_domain", Chk("verify_domain").ToString(), ct);
            await settings.SetAsync("email", "fetch_enabled", Chk("fetch_enabled").ToString(), ct);
            await settings.SetAsync("email", "fetch_auto_cron", Chk("fetch_auto_cron").ToString(), ct);
            await settings.SetAsync("email", "strip_quoted", Chk("strip_quoted").ToString(), ct);
            await settings.SetAsync("email", "reply_separator", Str("reply_separator"), ct);
            await settings.SetAsync("email", "use_email_priority", Chk("use_email_priority").ToString(), ct);
            await settings.SetAsync("email", "accept_unregistered", Chk("accept_unregistered").ToString(), ct);
            await settings.SetAsync("email", "auto_add_collabs", Chk("auto_add_collabs").ToString(), ct);
            await settings.SetAsync("email", "default_smtp", defaultSmtp, ct);
            await settings.SetAsync("email", "attachments_in_email", Chk("attachments_in_email").ToString(), ct);
        }

        return Toast("es.toastSaved");
    }

    private IActionResult Toast(string key, bool error = false)
    {
        TempData["EsToast"] = key;
        if (error)
            TempData["EsToastError"] = true;
        return Redirect("/admin/email-settings");
    }
}

public sealed record EmailSettingsVm(
    EmailSettings Settings,
    IReadOnlyList<OptionVm> TemplateSets,
    IReadOnlyList<OptionVm> Accounts,
    IReadOnlyList<OptionVm> SmtpAccounts);
