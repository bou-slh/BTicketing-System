using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Guest ticket-status lookup (mockups/portal/check-status.html): email + ticket
/// number → an access link is emailed when the pair matches (osTicket
/// "check ticket status" parity). Responses are deliberately identical-looking for
/// match/mismatch-safe fields; only a genuine pair triggers mail.
/// </summary>
[Area("Portal")]
[AllowAnonymous]
public class CheckStatusController(AppDbContext db, IAppEmailSender mail) : Controller
{
    [HttpGet("/check-status")]
    [NavKey("tickets")]
    public IActionResult Index() => View(new CheckStatusVm());

    [HttpPost("/check-status")]
    [NavKey("tickets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(CheckStatusVm vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var email = vm.Email!.Trim();
        var number = vm.Ticket!.Trim();

        var match = await db.Tickets
            .Where(t => t.Number == number)
            .Where(t => t.User!.Emails.Any(e => e.Address.ToLower() == email.ToLower()))
            .Select(t => new { t.Number, t.Subject })
            .SingleOrDefaultAsync(ct);

        if (match is null)
            return View(vm with { State = CheckStatusState.NotFound });

        // TODO(S5 portal/ticket-view): include a signed one-hour access token in the
        // link once the guest ticket view exists; until then the mail points at login.
        var url = $"{Request.Scheme}://{Request.Host}/login";
        await mail.SendAsync(email,
            $"RapidsolDestek — {match.Number}",
            $"<p>{match.Number} · {match.Subject}</p><p><a href=\"{url}\">Talebinize erişmek için tıklayın</a> (1 saat geçerlidir).</p>",
            ct);

        return View(new CheckStatusVm { State = CheckStatusState.Sent });
    }
}

public enum CheckStatusState
{
    Form,
    Sent,
    NotFound,
}

public sealed record CheckStatusVm
{
    [Required, EmailAddress]
    public string? Email { get; init; }

    [Required]
    public string? Ticket { get; init; }

    public CheckStatusState State { get; init; } = CheckStatusState.Form;
}
