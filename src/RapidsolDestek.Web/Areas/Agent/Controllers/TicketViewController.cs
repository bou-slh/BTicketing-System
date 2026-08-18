using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent ticket detail (mockups/agent/ticket-view.html, ROADMAP §6.2): full thread
/// (Messages + Responses + internal Notes + timeline events interleaved), the B8
/// effort loop on the staff side (propose / revise / withdraw + the three banner
/// states), B5 reply/note composers with canned-response insert and attachments,
/// and the header/Diğer actions that map onto existing S4 services. Visibility =
/// the QueueEngine's department scope (same query the list pages use).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class TicketViewController(
    AppDbContext db,
    IQueueEngine queueEngine,
    IThreadService threads,
    ITicketService ticketService,
    IEffortProposalService efforts,
    ICannedResponseService cannedService,
    IPermissionService permissions,
    ISettingsService settings,
    IFileStore files) : Controller
{
    /// <summary>After-reply status choices, exactly the mockup's composer select.</summary>
    private static readonly string[] ComposerStatusKeys = ["open", "wait", "solved"];

    [HttpGet("/agent/ticket-view")]
    [NavKey("tickets")]
    public async Task<IActionResult> Index(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        var visible = await queueEngine.BuildAsync(QueueCriteria.Empty, actor, ct);
        var ticket = await visible
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id, t.Number, t.Subject, t.CreatedAt, t.ThreadId, t.StatusId,
                t.DepartmentId, t.Source, t.IsOverdue, t.StaffId, t.TeamId, t.ParentId,
                StatusKey = t.Status!.Key,
                StatusState = t.Status!.State,
                PriorityKey = t.Priority != null ? t.Priority.Key : null,
                DeptName = t.Department!.Name,
                DeptSignature = t.Department!.Signature,
                DeptEmailAccountId = t.Department!.EmailAccountId,
                SlaName = t.Sla != null ? t.Sla.Name : null,
                SlaGrace = t.Sla != null ? (int?)t.Sla.GracePeriodHours : null,
                Due = t.DueDate ?? t.EstimatedDueDate,
                t.UserId,
                UserName = t.User!.Name,
                UserEmail = t.User.Emails
                    .Where(e => e.Id == t.User.DefaultEmailId)
                    .Select(e => e.Address).FirstOrDefault(),
                OrgId = t.User.OrganizationId,
                OrgName = t.User.Organization != null ? t.User.Organization.Name : null,
                TopicName = t.HelpTopic != null ? t.HelpTopic.Name : null,
                AssigneeName = t.Staff != null ? t.Staff.FullName : null,
                TeamName = t.Team != null ? t.Team.Name : null,
            })
            .SingleOrDefaultAsync(ct);
        if (ticket is null)
            return NotFound();

        // ---- Thread: ALL entry types (agent side shows internal Notes too) ----------
        var entries = await db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id, e.Type, e.Poster, e.CreatedAt, e.Body, e.Format, e.Title, e.UserId,
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

        // ---- Timeline events (all kinds; interleaved with entries by time) ----------
        var events = await db.ThreadEvents
            .Where(ev => ev.ThreadId == ticket.ThreadId && !ev.Annulled)
            .OrderBy(ev => ev.OccurredAt).ThenBy(ev => ev.Id)
            .Select(ev => new
            {
                Kind = ev.EventType!.Name, ev.OccurredAt, ev.Data,
                EventStaffName = db.Staff.Where(s => s.Id == ev.StaffId)
                    .Select(s => s.FirstName + " " + s.LastName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var deptNames = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var staffNames = await db.Staff
            .ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName, ct);

        var items = entries
            .Select(e => new AgentThreadItemVm(
                e.CreatedAt,
                new AgentEntryVm(
                    e.Id, e.Type,
                    e.StaffName ?? e.Poster,
                    // Meta label next to the poster: user entries show their org,
                    // staff responses the helpdesk company (mockup "RapidSol").
                    e.Type == ThreadEntryType.Response ? "RapidSol"
                        : e.UserId is not null ? ticket.OrgName : null,
                    e.Body, e.Format,
                    attachments.Where(a => a.EntryId == e.Id)
                        .Select(a => new AgentAttachmentVm(a.Id, a.Name)).ToList()),
                Event: null))
            .Concat(events.Select(ev => new AgentThreadItemVm(
                ev.OccurredAt,
                Entry: null,
                new AgentEventVm(
                    ev.Kind,
                    AssigneeFromEventData(ev.Data, staffNames) ?? ev.EventStaffName,
                    HoursFromEventData(ev.Data),
                    TargetDeptFromEventData(ev.Data, deptNames),
                    IsReleased(ev.Kind, ev.Data)))))
            .OrderBy(i => i.At)
            .ToList();

        // ---- Effort loop state (latest revision, EffortProposalService semantics) ----
        var effort = await db.EffortProposals
            .Where(p => p.TicketId == ticket.Id)
            .OrderByDescending(p => p.RevisionNo)
            .Select(p => new AgentEffortVm(
                p.Hours, p.Note, p.State, p.RevisionNo,
                db.Staff.Where(s => s.Id == p.ProposedByStaffId)
                    .Select(s => s.FirstName + " " + s.LastName).First(),
                p.CreatedAt, p.DecisionNote))
            .FirstOrDefaultAsync(ct);
        var effortSettings = await settings.GetEffortAsync(ct);

        // ---- Tabs data ---------------------------------------------------------------
        var tasks = await db.TaskItems
            .Where(t => t.TicketId == ticket.Id)
            .OrderBy(t => t.Number)
            .Select(t => new AgentTaskRowVm(
                t.Id, t.Number, t.Title,
                t.ClosedAt != null ? "closed"
                    : t.IsOverdue || (t.DueDate != null && t.DueDate < DateTimeOffset.UtcNow) ? "overdue" : "open",
                t.Staff != null ? t.Staff.FullName : null,
                t.DueDate))
            .ToListAsync(ct);

        // Related tickets: merge/link family via ParentId (osTicket ticket_pid) —
        // parent, children and siblings. The seed links none; the LINK dialog itself
        // is TODO(S6) (no merge/link service yet).
        var related = await db.Tickets
            .Where(t => t.Id != ticket.Id
                && (t.ParentId == ticket.Id
                    || (ticket.ParentId != null && (t.Id == ticket.ParentId || t.ParentId == ticket.ParentId))))
            .Select(t => new AgentRelatedVm(t.Id, t.Number, t.Subject, t.Status!.Key))
            .ToListAsync(ct);

        // ---- Composer data -----------------------------------------------------------
        var cannedList = (await cannedService.ListForAsync(ticket.DepartmentId, ct))
            .Select(c => new CannedOptionVm(c.Id, c.Title)).ToList();

        var fromAccounts = await db.EmailAccounts
            .OrderBy(a => a.Id)
            .Select(a => new FromAccountVm(a.Id, a.DisplayName, a.Address))
            .ToListAsync(ct);

        var statusRows = await db.TicketStatuses
            .Where(s => ComposerStatusKeys.Contains(s.Key))
            .OrderBy(s => s.Sort)
            .Select(s => new StatusOptionVm(s.Id, s.Key))
            .ToListAsync(ct);

        // Vacation guard (profile Tatil Modu): agents on vacation leave the dlg-assign
        // list (mockup lists only available agents); the current assignee stays visible
        // so the select keeps showing reality. TicketService guards the POST too.
        var assignees = (await db.Staff
                .Where(s => s.IsActive && (!s.OnVacation || s.Id == ticket.StaffId))
                .OrderBy(s => s.FirstName)
                .Select(s => new { s.Id, s.FullName })
                .ToListAsync(ct))
            .Select(s => new AssigneeOptionVm($"s:{s.Id}", s.FullName,
                ticket.StaffId == s.Id))
            .Concat((await db.Teams.OrderBy(t => t.Id).Select(t => new { t.Id, t.Name }).ToListAsync(ct))
                .Select(t => new AssigneeOptionVm($"t:{t.Id}", t.Name,
                    ticket.StaffId == null && ticket.TeamId == t.Id)))
            .ToList();

        var departments = await db.Departments
            .OrderBy(d => d.Id)
            .Select(d => new DeptOptionVm(d.Id, d.Name))
            .ToListAsync(ct);

        var vm = new AgentTicketViewVm(
            ticket.Id, ticket.Number, ticket.Subject,
            // Header/status-row pill: real status with the overdue derivation only —
            // the effort pseudo-status has its own "Efor Durumu" row on this page.
            ticket.IsOverdue && ticket.StatusState == TicketState.Open ? "overdue" : ticket.StatusKey,
            ticket.PriorityKey, ticket.DeptName, ticket.CreatedAt,
            ticket.SlaName, ticket.SlaGrace, ticket.Due, ticket.Source,
            ticket.UserId, ticket.UserName, ticket.UserEmail, ticket.OrgId, ticket.OrgName,
            ticket.TopicName, ticket.AssigneeName ?? ticket.TeamName,
            effort, effortSettings.Enabled,
            items, tasks, related, cannedList,
            fromAccounts, ticket.DeptEmailAccountId,
            statusRows, ticket.StatusId,
            assignees, departments, ticket.DepartmentId,
            staff.Signature, ticket.DeptSignature);
        return View(vm);
    }

    // ---- B5 composers ----------------------------------------------------------------

    /// <summary>Reply: staff Response through IThreadService (B8 work gate applies) + attachments + after-reply status.</summary>
    [HttpPost("/agent/ticket-view/reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string? body, int? fromAccountId, string? sig,
        int? statusId, List<IFormFile> attachments, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var (staff, ticket) = loaded.Value;
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (string.IsNullOrWhiteSpace(body))
            return RedirectBack(id, "tv.errEmpty", error: true);

        // Recipients snapshot (osTicket recipients): the ticket owner + chosen From
        // identity. NOTE(S8): the signature choice ("sig") applies to the outbound
        // email render — nothing to persist until the mail subsystem lands.
        var to = await db.Users.Where(u => u.Id == ticket.UserId)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails.Where(e => e.Id == u.DefaultEmailId).Select(e => e.Address).FirstOrDefault(),
            })
            .SingleAsync(ct);
        var from = await db.EmailAccounts.Where(a => a.Id == fromAccountId)
            .Select(a => a.Address).FirstOrDefaultAsync(ct);
        var recipients = JsonSerializer.Serialize(new { to = new[] { $"{to.Name} <{to.Email}>" }, from });

        ThreadEntry entry;
        try
        {
            entry = await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Response, body, actor,
                new PostOptions { Format = "text", Recipients = recipients }, ct);
        }
        catch (WorkBlockedByEffortException)
        {
            return RedirectBack(id, "tv.errWorkBlocked", error: true);
        }

        await SaveAttachmentsAsync(entry.Id, attachments, actor, ct);
        return await ApplyStatusAsync(id, ticket, statusId, actor, "tv.toastReply", ct);
    }

    /// <summary>Internal note (never customer-visible) + optional status change.</summary>
    [HttpPost("/agent/ticket-view/note")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Note(int id, string? title, string? body, int? statusId, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var (staff, ticket) = loaded.Value;
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (string.IsNullOrWhiteSpace(body))
            return RedirectBack(id, "tv.errEmpty", error: true);

        await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Note, body, actor,
            new PostOptions { Format = "text", Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() }, ct);
        return await ApplyStatusAsync(id, ticket, statusId, actor, "tv.toastNote", ct);
    }

    /// <summary>
    /// Canned-response insert (B5): expanded text for the composer. what = a canned id,
    /// or "orig"/"last" for the mockup's original/last-message quote options.
    /// </summary>
    [HttpGet("/agent/ticket-view/canned")]
    public async Task<IActionResult> Canned(int id, string what, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var ticket = loaded.Value.Ticket;

        string? text;
        if (what is "orig" or "last")
        {
            var messages = db.ThreadEntries
                .Where(e => e.ThreadId == ticket.ThreadId && e.Type == ThreadEntryType.Message);
            var entry = what == "orig"
                ? await messages.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).FirstOrDefaultAsync(ct)
                : await messages.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
            if (entry is null)
                return NotFound();
            text = entry.Format == "html" ? HtmlToComposerText(entry.Body) : entry.Body;
        }
        else if (int.TryParse(what, out var cannedId))
        {
            try
            {
                text = HtmlToComposerText(await cannedService.ExpandAsync(cannedId, id, ct));
            }
            catch (DomainNotFoundException)
            {
                return NotFound();
            }
        }
        else
        {
            return BadRequest();
        }

        return Json(new { text });
    }

    // ---- B8 effort loop (flagship) -----------------------------------------------------

    /// <summary>
    /// dlg-effort submit: proposes, or revises when a proposal is pending — the same
    /// dialog serves the header button, the banner's "Revize Et" and the rejected
    /// banner's "Yeni Öneri Gönder" (mockup wiring).
    /// </summary>
    [HttpPost("/agent/ticket-view/effort")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Effort(int id, string hours, string? note, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        // Browsers post number inputs with an invariant dot regardless of UI culture.
        if (!decimal.TryParse(hours, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedHours))
        {
            return RedirectBack(id, "tv.errEffort", error: true);
        }

        try
        {
            var active = await efforts.GetActiveAsync(id, ct);
            if (active?.State == EffortState.Pending)
            {
                await efforts.ReviseAsync(id, parsedHours, note, actor, ct);
                return RedirectBack(id, "tv.toastEffortRevised");
            }
            await efforts.ProposeAsync(id, parsedHours, note, actor, ct);
            return RedirectBack(id, "tv.toastEffort");
        }
        catch (EffortException)
        {
            return RedirectBack(id, "tv.errEffort", error: true);
        }
        catch (PermissionDeniedException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RedirectBack(id, "tv.errEffort", error: true);
        }
    }

    /// <summary>Pending banner "Geri Çek".</summary>
    [HttpPost("/agent/ticket-view/withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await efforts.WithdrawAsync(id, actor, ct);
            return RedirectBack(id, "tv.toastWithdrawn");
        }
        catch (EffortException)
        {
            return RedirectBack(id, "tv.errEffort", error: true);
        }
        catch (PermissionDeniedException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }
    }

    // ---- Header + Diğer actions -------------------------------------------------------

    /// <summary>dlg-assign: "s:{staffId}" or "t:{teamId}" + optional comment (posted as a note).</summary>
    [HttpPost("/agent/ticket-view/assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, string assignee, string? comment, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        int? staffId = null, teamId = null;
        if (assignee.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var sid))
            staffId = sid;
        else if (assignee.StartsWith("t:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var tid))
            teamId = tid;
        else
            return BadRequest();

        try
        {
            await ticketService.AssignAsync(id, staffId, teamId, actor, ct);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }
        await PostCommentNoteAsync(loaded.Value.Ticket.ThreadId, comment, actor, ct);
        return RedirectBack(id, "tv.toastAssigned");
    }

    /// <summary>dlg-transfer: department move + optional comment note.</summary>
    [HttpPost("/agent/ticket-view/transfer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(int id, int departmentId, string? comment, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await ticketService.TransferAsync(id, departmentId, actor, ct);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }
        await PostCommentNoteAsync(loaded.Value.Ticket.ThreadId, comment, actor, ct);
        return RedirectBack(id, "tv.toastTransferred");
    }

    /// <summary>Diğer → Serbest Bırak: clears staff + team via the assignment service.</summary>
    [HttpPost("/agent/ticket-view/release")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Release(int id, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var actor = ActorContext.ForStaff(loaded.Value.Staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await ticketService.AssignAsync(id, null, null, actor, ct);
        }
        catch (DomainException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }
        return RedirectBack(id, "tv.toastReleased");
    }

    /// <summary>
    /// Diğer → Gecikmiş İşaretle. No dedicated S4 service exists for the flag —
    /// the mutation runs under the actor's audit scope (AuditEvent via interceptor)
    /// with the ticket.edit permission check and writes the "overdue" thread event.
    /// </summary>
    [HttpPost("/agent/ticket-view/mark-overdue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkOverdue(int id, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var (staff, ticket) = loaded.Value;
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await permissions.EnsureAsync(actor, PermissionKeys.TicketEdit, ticket.DepartmentId, ct);
        }
        catch (PermissionDeniedException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }

        ticket.IsOverdue = true;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        await threads.AddEventAsync(ticket.ThreadId, "overdue", actor, null, ct);
        return RedirectBack(id, "tv.toastOverdue");
    }

    /// <summary>Diğer → Yanıtlandı İşaretle (osTicket mark-answered flag).</summary>
    [HttpPost("/agent/ticket-view/mark-answered")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAnswered(int id, CancellationToken ct)
    {
        var loaded = await LoadForActionAsync(id, ct);
        if (loaded is null)
            return NotFound();
        var (staff, ticket) = loaded.Value;
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await permissions.EnsureAsync(actor, PermissionKeys.TicketMarkAnswered, ticket.DepartmentId, ct);
        }
        catch (PermissionDeniedException)
        {
            return RedirectBack(id, "tv.errDenied", error: true);
        }

        ticket.IsAnswered = true;
        ticket.LastUpdateAt = DateTimeOffset.UtcNow;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return RedirectBack(id, "tv.toastAnswered");
    }

    /// <summary>Thread-entry attachment download inside the staff's visibility scope.</summary>
    [HttpGet("/agent/ticket-view/attachment")]
    public async Task<IActionResult> Attachment(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var attachment = await db.Attachments
            .Include(a => a.File)
            .SingleOrDefaultAsync(a => a.Id == id && a.ObjectType == AttachmentObjectType.ThreadEntry, ct);
        if (attachment is null)
            return NotFound();

        // Visibility: the owning ticket must be inside the staff's QueueEngine scope.
        var threadId = await db.ThreadEntries
            .Where(e => e.Id == attachment.ObjectId)
            .Select(e => (int?)e.ThreadId)
            .FirstOrDefaultAsync(ct);
        var visible = await queueEngine.BuildAsync(QueueCriteria.Empty, ActorContext.ForStaff(staff), ct);
        if (threadId is null || !await visible.AnyAsync(t => t.ThreadId == threadId, ct))
            return NotFound();

        var file = attachment.File!;
        var content = await files.OpenAsync(file, ct);
        return File(content, file.MimeType, attachment.Name ?? file.Name);
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Resolves the staff actor and the ticket within their visibility scope.</summary>
    private async Task<(Staff Staff, Ticket Ticket)?> LoadForActionAsync(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return null;
        var visible = await queueEngine.BuildAsync(QueueCriteria.Empty, ActorContext.ForStaff(staff), ct);
        var ticket = await visible.SingleOrDefaultAsync(t => t.Id == id, ct);
        return ticket is null ? null : (staff, ticket);
    }

    private IActionResult RedirectBack(int id, string toastKey, bool error = false)
    {
        TempData["TvToast"] = toastKey;
        if (error)
            TempData["TvToastError"] = true;
        return RedirectToAction(nameof(Index), new { id });
    }

    private async Task SaveAttachmentsAsync(int entryId, List<IFormFile> uploads, ActorContext actor, CancellationToken ct)
    {
        var any = false;
        foreach (var upload in uploads.Where(f => f.Length > 0))
        {
            await using var stream = upload.OpenReadStream();
            var file = await files.SaveAsync(stream, Path.GetFileName(upload.FileName),
                string.IsNullOrEmpty(upload.ContentType) ? "application/octet-stream" : upload.ContentType, ct);
            db.StoredFiles.Add(file);
            db.Attachments.Add(new Attachment
            {
                ObjectType = AttachmentObjectType.ThreadEntry,
                ObjectId = entryId,
                File = file,
            });
            any = true;
        }
        if (any)
        {
            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Applies the after-reply/note status select, downgrading failures to an error toast.</summary>
    private async Task<IActionResult> ApplyStatusAsync(int id, Ticket ticket, int? statusId,
        ActorContext actor, string successKey, CancellationToken ct)
    {
        if (statusId is { } target && target != ticket.StatusId)
        {
            try
            {
                await ticketService.TransitionStatusAsync(id, target, actor, ct);
            }
            catch (DomainException)
            {
                // Entry landed; the transition was refused (permission / B8 close gate).
                return RedirectBack(id, "tv.errStatus", error: true);
            }
        }
        return RedirectBack(id, successKey);
    }

    /// <summary>Optional dialog comment → internal note (osTicket comment-on-action parity).</summary>
    private async Task PostCommentNoteAsync(int threadId, string? comment, ActorContext actor, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(comment))
        {
            await threads.PostAsync(threadId, ThreadEntryType.Note, comment.Trim(), actor,
                new PostOptions { Format = "text" }, ct);
        }
    }

    /// <summary>Sanitized canned/thread HTML → composer plain text (textarea composer, "text" entries).</summary>
    private static string HtmlToComposerText(string html)
    {
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>\\s*", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    // Event Data helpers (portal TicketViewController parity): assignee lives in Data
    // as "staff" (seeded canon name) or "staffId"/"teamId" (TicketService).
    private static string? AssigneeFromEventData(string? data, Dictionary<int, string> staffNames)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            if (root.TryGetProperty("staff", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
            if (root.TryGetProperty("staffId", out var sid) && sid.ValueKind == JsonValueKind.Number
                && staffNames.TryGetValue(sid.GetInt32(), out var resolved))
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

    private static string? TargetDeptFromEventData(string? data, Dictionary<int, string> deptNames)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            return JsonDocument.Parse(data).RootElement.TryGetProperty("to", out var to)
                && to.ValueKind == JsonValueKind.Number
                && deptNames.TryGetValue(to.GetInt32(), out var name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An "assigned" event whose staff AND team are null is a release.</summary>
    private static bool IsReleased(string kind, string? data)
    {
        if (kind != "assigned" || string.IsNullOrEmpty(data))
            return false;
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            bool NullProp(string name) =>
                !root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;
            return NullProp("staff") && NullProp("staffId") && NullProp("teamId");
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record AgentTicketViewVm(
    int Id,
    string Number,
    string Subject,
    string StatusKey,
    string? PriorityKey,
    string DeptName,
    DateTimeOffset CreatedAt,
    string? SlaName,
    int? SlaGraceHours,
    DateTimeOffset? Due,
    TicketSource Source,
    int UserId,
    string UserName,
    string? UserEmail,
    int? OrgId,
    string? OrgName,
    string? TopicName,
    string? AssigneeName,
    AgentEffortVm? Effort,
    bool EffortEnabled,
    IReadOnlyList<AgentThreadItemVm> Thread,
    IReadOnlyList<AgentTaskRowVm> Tasks,
    IReadOnlyList<AgentRelatedVm> Related,
    IReadOnlyList<CannedOptionVm> Canned,
    IReadOnlyList<FromAccountVm> FromAccounts,
    int? DefaultFromAccountId,
    IReadOnlyList<StatusOptionVm> Statuses,
    int CurrentStatusId,
    IReadOnlyList<AssigneeOptionVm> Assignees,
    IReadOnlyList<DeptOptionVm> Departments,
    int CurrentDeptId,
    string StaffSignature,
    string DeptSignature);

public sealed record AgentEffortVm(
    decimal Hours,
    string? Note,
    EffortState State,
    int RevisionNo,
    string ProposedBy,
    DateTimeOffset ProposedAt,
    string? DecisionNote);

/// <summary>Thread timeline item: exactly one of Entry / Event is set.</summary>
public sealed record AgentThreadItemVm(DateTimeOffset At, AgentEntryVm? Entry, AgentEventVm? Event);

public sealed record AgentEntryVm(
    int Id,
    ThreadEntryType Type,
    string Poster,
    string? OrgLabel,
    string Body,
    string Format,
    IReadOnlyList<AgentAttachmentVm> Attachments);

/// <summary>Kind: created | assigned | transferred | effort-* | closed | reopened | overdue | edited …</summary>
public sealed record AgentEventVm(string Kind, string? Assignee, decimal? Hours, string? TargetDept, bool Released);

public sealed record AgentAttachmentVm(int Id, string Name);

public sealed record AgentTaskRowVm(int Id, string Number, string Title, string StatusKey, string? Assignee, DateTimeOffset? Due);

public sealed record AgentRelatedVm(int Id, string Number, string Subject, string StatusKey);

public sealed record CannedOptionVm(int Id, string Title);

public sealed record FromAccountVm(int Id, string? DisplayName, string Address);

public sealed record AssigneeOptionVm(string Value, string Label, bool Selected);

public sealed record DeptOptionVm(int Id, string Name);
