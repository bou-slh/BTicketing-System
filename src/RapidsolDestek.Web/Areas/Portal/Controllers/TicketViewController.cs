using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal ticket detail (mockups/portal/ticket-view.html) — the golden-path page:
/// thread from data, the B8 effort card with Onayla/Reddet driving
/// <see cref="IEffortProposalService"/>, and the B5 reply composer appending real
/// Message entries with attachments. Signed-in owners see their own tickets only
/// (TicketsController parity); a signed one-hour guest token from check-status
/// grants a read-only view of that single ticket (§6.1 check-status stage note).
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class TicketViewController(
    AppDbContext db,
    IThreadService threads,
    IEffortProposalService efforts,
    IFileStore files,
    ISettingsService settings,
    IDataProtectionProvider dataProtection) : Controller
{
    /// <summary>Data-protection purpose shared with CheckStatusController's access-link mail.</summary>
    public const string GuestTokenPurpose = "RapidsolDestek.Portal.TicketView.Guest";

    /// <summary>Creates the signed "ticketId|expiryUtcTicks" guest token for access-link mails.</summary>
    public static string CreateGuestToken(IDataProtectionProvider provider, int ticketId, TimeSpan lifetime) =>
        provider.CreateProtector(GuestTokenPurpose)
            .Protect(FormattableString.Invariant($"{ticketId}|{DateTimeOffset.UtcNow.Add(lifetime).UtcTicks}"));

    private bool IsValidGuestToken(string? token, int ticketId)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        try
        {
            var payload = dataProtection.CreateProtector(GuestTokenPurpose).Unprotect(token);
            var parts = payload.Split('|');
            return parts.Length == 2
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var tid) && tid == ticketId
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var expiry)
                && new DateTimeOffset(expiry, TimeSpan.Zero) > DateTimeOffset.UtcNow;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // tampered/expired-key token
        }
    }

    /// <summary>Principal → domain User (HomeController/TicketsController parity).</summary>
    private async Task<User?> CurrentUserAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var identityId))
            return null;
        return await db.Users.SingleOrDefaultAsync(u => u.IdentityUserId == identityId, ct);
    }

    [HttpGet("/ticket-view")]
    [NavKey("tickets")]
    [AllowAnonymous] // guest-token path; owners still authenticate via the Contextual default scheme
    public async Task<IActionResult> Index(int id, string? token, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var guest = user is null && IsValidGuestToken(token, id);
        if (user is null && !guest)
            return Challenge(AuthSchemes.Customer);

        var ticket = await db.Tickets
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id, t.Number, t.Subject, t.CreatedAt, t.UserId, t.ThreadId,
                StatusKey = t.Status!.Key,
                PriorityKey = t.Priority != null ? t.Priority.Key : null,
                TopicName = t.HelpTopic != null ? t.HelpTopic.Name : null,
                AgentName = t.Staff != null ? t.Staff.FullName : null,
            })
            .SingleOrDefaultAsync(ct);

        // Ownership: signed-in portal users see only their OWN tickets — 404 otherwise.
        if (ticket is null || (!guest && ticket.UserId != user!.Id))
            return NotFound();

        // Thread: Message/Response entries only — Notes are internal and never
        // reach the portal (checklist §2); mockup order is oldest → newest.
        var entries = await db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId
                && (e.Type == ThreadEntryType.Message || e.Type == ThreadEntryType.Response))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.Type, e.Poster, e.CreatedAt, e.Body, e.Format,
                StaffName = db.Staff.Where(s => s.Id == e.StaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var entryIds = entries.Select(e => e.Id).ToList();
        var attachments = await db.Attachments
            .Where(a => a.ObjectType == AttachmentObjectType.ThreadEntry && entryIds.Contains(a.ObjectId))
            .Join(db.StoredFiles, a => a.FileId, f => f.Id,
                (a, f) => new { a.Id, EntryId = a.ObjectId, Name = a.Name ?? f.Name })
            .ToListAsync(ct);

        // Effort card (B8): the latest revision is the loop state (EffortProposalService.GetActiveAsync semantics).
        var proposal = await db.EffortProposals
            .Where(p => p.TicketId == ticket.Id)
            .OrderByDescending(p => p.RevisionNo)
            .Select(p => new EffortCardVm(
                p.Hours, p.Note, p.RevisionNo, p.State,
                db.Staff.Where(s => s.Id == p.ProposedByStaffId)
                    .Select(s => s.FirstName + " " + s.LastName).First(),
                p.DecisionNote))
            .FirstOrDefaultAsync(ct);

        // History timeline: only event kinds the portal mockup shows, newest first,
        // closed by the synthetic "Talep oluşturuldu" row from Ticket.CreatedAt.
        var portalEventKinds = new[]
        {
            "assigned", "effort-proposed", "effort-revised", "effort-approved", "effort-rejected",
        };
        var events = await db.ThreadEvents
            .Where(ev => ev.ThreadId == ticket.ThreadId && !ev.Annulled
                && portalEventKinds.Contains(ev.EventType!.Name))
            .OrderByDescending(ev => ev.OccurredAt)
            .Select(ev => new
            {
                Kind = ev.EventType!.Name, ev.OccurredAt, ev.Data,
                StaffName = db.Staff.Where(s => s.Id == ev.StaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Event StaffId is the ACTOR (null for system auto-assign); the assignee lives in
        // Data as "staff" (seeded canon, a name) or "staffId" (TicketService).
        var dataStaffIds = events
            .Where(ev => ev.StaffName is null)
            .Select(ev => StaffIdFromEventData(ev.Data))
            .OfType<int>()
            .Distinct()
            .ToList();
        var dataStaffNames = dataStaffIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.Where(s => dataStaffIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName, ct);

        var timeline = events
            .Select(ev => new TimelineItemVm(
                ev.Kind, ev.OccurredAt,
                ev.StaffName ?? ActorFromEventData(ev.Data, dataStaffNames),
                HoursFromEventData(ev.Data)))
            .Append(new TimelineItemVm("created", ticket.CreatedAt, null, null))
            .ToList();

        var vm = new TicketViewVm(
            ticket.Id,
            ticket.Number,
            ticket.Subject,
            ticket.CreatedAt,
            // Derived pseudo-status: pending proposal shows as effortWait (Home/Tickets parity).
            proposal?.State == EffortState.Pending ? "effortWait" : ticket.StatusKey,
            IsGuest: guest,
            ticket.PriorityKey,
            ticket.AgentName,
            ticket.TopicName,
            proposal,
            entries.Select(e => new ThreadEntryVm(
                e.Id,
                IsAgent: e.Type == ThreadEntryType.Response,
                e.StaffName ?? e.Poster,
                e.CreatedAt,
                e.Body,
                e.Format,
                attachments.Where(a => a.EntryId == e.Id)
                    .Select(a => new TicketAttachmentVm(a.Id, a.Name)).ToList())).ToList(),
            timeline);
        return View(vm);
    }

    private static int? StaffIdFromEventData(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            return JsonDocument.Parse(data).RootElement.TryGetProperty("staffId", out var id)
                && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ActorFromEventData(string? data, Dictionary<int, string> staffNames)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            if (root.TryGetProperty("staff", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
            if (root.TryGetProperty("staffId", out var id) && id.ValueKind == JsonValueKind.Number
                && staffNames.TryGetValue(id.GetInt32(), out var resolved))
                return resolved;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? HoursFromEventData(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            return JsonDocument.Parse(data).RootElement.TryGetProperty("hours", out var h)
                && h.ValueKind == JsonValueKind.Number ? h.GetDecimal() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>B5: appends a real Message entry (sanitized at ingress) + attachments, then redirects back.</summary>
    [HttpPost("/ticket-view/reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string? body, List<IFormFile> attachments, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        var ticket = user is null ? null
            : await db.Tickets.SingleOrDefaultAsync(t => t.Id == id && t.UserId == user.Id, ct);
        if (ticket is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(body))
            return RedirectToAction(nameof(Index), new { id });

        // Attachment size cap (admin/settings-system Ekler, S7): refuse the whole
        // reply before anything posts — surfaced as an error toast (PRG).
        var attachLimits = await settings.GetAttachmentsAsync(ct);
        if (attachments.Any(f => f.Length > attachLimits.MaxSizeBytes))
        {
            TempData["TvToast"] = "tv.errAttachTooBig";
            TempData["TvToastError"] = true;
            return RedirectToAction(nameof(Index), new { id });
        }

        var actor = ActorContext.ForUser(user!, HttpContext.Connection.RemoteIpAddress?.ToString());
        // The composer textarea produces plain text (the rd.js editor inserts
        // markdown-ish markers, not HTML) — store as "text", render encoded.
        var entry = await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Message, body, actor,
            new PostOptions { Format = "text" }, ct);

        foreach (var upload in attachments.Where(f => f.Length > 0))
        {
            await using var stream = upload.OpenReadStream();
            var file = await files.SaveAsync(stream, Path.GetFileName(upload.FileName),
                string.IsNullOrEmpty(upload.ContentType) ? "application/octet-stream" : upload.ContentType, ct);
            db.StoredFiles.Add(file);
            db.Attachments.Add(new Attachment
            {
                ObjectType = AttachmentObjectType.ThreadEntry,
                ObjectId = entry.Id,
                File = file,
            });
        }
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Index), new { id });
    }

    /// <summary>B8 "Onayla" — one click, the portal user is the decider.</summary>
    [HttpPost("/ticket-view/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        return await DecideAsync(id, approve: true, note: null, ct);
    }

    /// <summary>B8 "Reddet" — dialog with a mandatory note (settings gate enforces it server-side too).</summary>
    [HttpPost("/ticket-view/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? note, CancellationToken ct)
    {
        return await DecideAsync(id, approve: false, note, ct);
    }

    private async Task<IActionResult> DecideAsync(int id, bool approve, string? note, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null || !await db.Tickets.AnyAsync(t => t.Id == id && t.UserId == user.Id, ct))
            return NotFound();

        var actor = ActorContext.ForUser(user, HttpContext.Connection.RemoteIpAddress?.ToString());
        try
        {
            if (approve)
                await efforts.ApproveAsync(id, actor, ct);
            else
                await efforts.RejectAsync(id, note, actor, ct);
        }
        catch (EffortException)
        {
            // Raced/illegal transition (e.g. proposal already decided or withdrawn):
            // fall through — the reloaded page renders the actual loop state.
        }
        return RedirectToAction(nameof(Index), new { id });
    }

    /// <summary>
    /// Thread-entry attachment download, gated to tickets the signed-in user owns
    /// (KbController.Attachment pattern at ticket scope).
    /// </summary>
    [HttpGet("/ticket-view/attachment")]
    public async Task<IActionResult> Attachment(int id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null)
            return NotFound();

        var attachment = await db.Attachments
            .Include(a => a.File)
            .SingleOrDefaultAsync(a =>
                a.Id == id
                && a.ObjectType == AttachmentObjectType.ThreadEntry
                && db.ThreadEntries.Any(e => e.Id == a.ObjectId
                    && db.Tickets.Any(t => t.ThreadId == e.ThreadId && t.UserId == user.Id)), ct);
        if (attachment is null)
            return NotFound();

        var file = attachment.File!;
        var content = await files.OpenAsync(file, ct);
        return File(content, file.MimeType, attachment.Name ?? file.Name);
    }
}

public sealed record TicketViewVm(
    int Id,
    string Number,
    string Subject,
    DateTimeOffset CreatedAt,
    string StatusKey,
    bool IsGuest,
    string? PriorityKey,
    string? AgentName,
    string? TopicName,
    EffortCardVm? Effort,
    IReadOnlyList<ThreadEntryVm> Entries,
    IReadOnlyList<TimelineItemVm> Timeline);

public sealed record EffortCardVm(
    decimal Hours,
    string? Note,
    int RevisionNo,
    EffortState State,
    string ProposedBy,
    string? DecisionNote);

public sealed record ThreadEntryVm(
    int Id,
    bool IsAgent,
    string Poster,
    DateTimeOffset At,
    string Body,
    string Format,
    IReadOnlyList<TicketAttachmentVm> Attachments);

public sealed record TicketAttachmentVm(int Id, string Name);

/// <summary>Kind: created | assigned | effort-proposed | effort-revised | effort-approved | effort-rejected.</summary>
public sealed record TimelineItemVm(string Kind, DateTimeOffset At, string? Actor, decimal? Hours);
