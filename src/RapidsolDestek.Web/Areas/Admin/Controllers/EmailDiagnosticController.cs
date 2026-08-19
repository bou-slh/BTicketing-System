using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin email diagnostic (mockups/admin/email-diagnostic.html, ROADMAP §6.3 +
/// §4 B10): a REAL test send through the chosen account's SMTP channel with honest
/// pending → success/failure states over HTTP — submit PRGs to ?job=, the page
/// renders the pending banner and polls <see cref="Status"/> until the terminal
/// state swaps it for the success banner (the mockup's ed.success, which S0 stopped
/// showing unconditionally) or the typed failure banner (invented UI — the mockup
/// defines no failure state, B10 demands one).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class EmailDiagnosticController(AppDbContext db, IEmailDiagnosticService diagnostic) : Controller
{
    [HttpGet("/admin/email-diagnostic")]
    [NavKey("email-diagnostic")]
    public async Task<IActionResult> Index(Guid? job, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        return View(new EmailDiagnosticVm(
            await db.EmailAccounts.OrderBy(a => a.Id)
                .Select(a => new OptionVm(a.Id, a.Address)).ToListAsync(ct),
            staff?.Email,
            job,
            job is { } id ? diagnostic.Get(id) : null));
    }

    /// <summary>Submit = validate → start the real send → PRG to the pending state
    /// (a refresh keeps resolving the job server-side, JS-free).</summary>
    [HttpPost("/admin/email-diagnostic/send")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(
        int from, string? to, string? subject, string? message, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        to = (to ?? "").Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(to, out _))
        {
            TempData["EdToast"] = "ed.errTo";
            TempData["EdToastError"] = true;
            return Redirect("/admin/email-diagnostic");
        }
        if (!await db.EmailAccounts.AnyAsync(a => a.Id == from, ct))
        {
            TempData["EdToast"] = "ed.errFrom";
            TempData["EdToastError"] = true;
            return Redirect("/admin/email-diagnostic");
        }

        var id = diagnostic.Start(from, to, (subject ?? "").Trim(), message ?? "");
        return Redirect($"/admin/email-diagnostic?job={id}");
    }

    /// <summary>B10 poll target: pending | success | failure (+ typed stage/detail).
    /// An unknown/expired id reports as a failure instead of hanging the page.</summary>
    [HttpGet("/admin/email-diagnostic/status")]
    public IActionResult Status(Guid id)
    {
        var job = diagnostic.Get(id) ?? new DiagnosticJob("failure", "unknown", null);
        return Json(new { state = job.State, stage = job.Stage, detail = job.Detail });
    }
}

public sealed record EmailDiagnosticVm(
    IReadOnlyList<OptionVm> Accounts,
    string? StaffEmail,
    Guid? JobId,
    DiagnosticJob? Job);
