using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Areas.Admin.Controllers;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// S8 slices 2 and 5: ticket autoresponses (customer-facing), the agent reply mail
/// (staff Response → owner + collaborators, see <c>OnResponseAsync</c>) and the
/// agent alert fan-out over the
/// catalog templates, subscribing to the S4 domain events (EffortEmailHandler style:
/// resolve settings → resolve recipients → render per recipient → enqueue on the
/// persistent mail queue). Recipient semantics model osTicket class.ticket.php
/// (onNewTicket / onMessage / notifyCollaborators / onActivity / onAssign /
/// transfer / onOverdue) — referenced for behavior only, never copied.
///
/// Suppression layers (osTicket parity):
/// - global autoresp.* / alerts.* master switches (settings-tickets, defaults from
///   the shared controller maps);
/// - Ticket.AutoResponseDisabled (filter action / inbound bounce flag);
/// - Department.TicketAutoResponse / MessageAutoResponse flags;
/// - agent ticket-open notify choice ("user" mutes alerts, "none" mutes everything
///   for the create — carried on TicketCreated/TicketAssigned);
/// - never alert the acting staff about their own action; recipients dedupe by
///   email; unavailable staff (inactive/vacation/no address) are skipped.
///
/// From-accounts: autoresponses ride the department's auto-response account
/// (?? dept account, EffortEmailHandler precedent); collaborator copies the dept
/// account; alerts email/alert_email_id (else the transport default chain).
/// </summary>
public sealed class TicketMailHandler(
    AppDbContext db,
    IEmailTemplateRenderer renderer,
    ISettingsService settings,
    IAlertRecipientResolver recipients,
    IMailQueue queue,
    IMailLinkTokenService links,
    ILogger<TicketMailHandler> logger) :
    IDomainEventHandler<TicketCreated>,
    IDomainEventHandler<ThreadEntryAdded>,
    IDomainEventHandler<TicketAssigned>,
    IDomainEventHandler<TicketTransferred>,
    IDomainEventHandler<TicketOverdue>
{
    /// <summary>Master/checkbox defaults — the settings-tickets maps are the single
    /// source of truth for what unset keys mean.</summary>
    private static readonly Dictionary<string, bool> AutorespDefaults =
        SettingsTicketsController.AutorespMap.ToDictionary(m => m.Key, m => m.Default);

    private static readonly Dictionary<string, bool> AlertDefaults =
        SettingsTicketsController.AlertsMap.ToDictionary(m => m.Key, m => m.Default);

    /// <summary>Markup-only encoder for text thread bodies (EmailTemplateRenderer's).</summary>
    private static readonly System.Text.Encodings.Web.HtmlEncoder BodyEncoder =
        System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    private IReadOnlyDictionary<string, string>? _autoresp;
    private IReadOnlyDictionary<string, string>? _alerts;

    // ---- new ticket ------------------------------------------------------------------

    public async Task HandleAsync(TicketCreated evt, CancellationToken ct = default)
    {
        var mode = evt.NotifyMode ?? "all";
        if (mode == "none")
            return; // ticket-open "Bildirim yok": no mail at all for this create.

        var t = await SnapshotAsync(evt.TicketId, ct);
        if (t is null)
            return;

        // Customer-facing confirmation/notice to the owner.
        if (!t.AutoResponseDisabled)
        {
            if (evt.ActorStaffId is null)
            {
                // End-user/system create → new-ticket confirmation (osTicket
                // ticket.autoresp), also gated by the department flag.
                if (await AutorespOnAsync("new_ticket", ct) && t.DeptTicketAutoResponse
                    && await recipients.OwnerAsync(evt.TicketId, ct) is { } owner)
                {
                    await SendAsync("ticket.autoresp", owner, t.AutoRespFrom, evt.TicketId, null, ct);
                }
            }
            else if (await AutorespOnAsync("agent_new_ticket", ct)
                && await recipients.OwnerAsync(evt.TicketId, ct) is { } owner)
            {
                // Agent-opened → "new ticket notice" (osTicket ticket.notice); the
                // dept flag governs end-user autoresponses only (osTicket parity).
                await SendAsync("ticket.notice", owner, t.AutoRespFrom, evt.TicketId, null, ct);
            }
        }

        // Staff alerts — the "user" notify choice mutes these.
        if (mode != "all" || !await AlertOnAsync("new_ticket", ct))
            return;

        var list = new List<MailRecipient?>();
        // Dept members only while the ticket is unassigned (osTicket onNewTicket).
        if (await AlertOnAsync("new_ticket_dept_members", ct) && t.StaffId is null && t.TeamId is null)
            list.AddRange(await recipients.DeptMembersAsync(t.DepartmentId, ct));
        if (await AlertOnAsync("new_ticket_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));
        if (await AlertOnAsync("new_ticket_account_manager", ct))
            list.Add(await recipients.AccountManagerAsync(t.UserId, ct));
        if (await AlertOnAsync("new_ticket_admin", ct))
            list.Add(await AdminRecipientAsync(ct));

        await FanoutAsync("ticket.alert", list, evt.ActorStaffId, evt.TicketId, null, ct);
    }

    // ---- new message / internal activity ---------------------------------------------

    public async Task HandleAsync(ThreadEntryAdded evt, CancellationToken ct = default)
    {
        if (evt.TicketId is not { } ticketId)
            return; // task threads are TaskMailHandler's turf.

        // The thread's first entry is the create's initial message — the
        // TicketCreated fan-out covers it (osTicket posts it with alerts off).
        if (!await db.ThreadEntries.AnyAsync(e => e.ThreadId == evt.ThreadId && e.Id < evt.EntryId, ct))
            return;

        var entry = await db.ThreadEntries.Where(e => e.Id == evt.EntryId)
            .Select(e => new { e.StaffId, e.UserId, e.Body, e.Format })
            .SingleOrDefaultAsync(ct);
        var t = await SnapshotAsync(ticketId, ct);
        if (entry is null || t is null)
            return;

        var messageHtml = AsHtml(entry.Body, entry.Format);
        if (evt.Type == ThreadEntryType.Message)
        {
            // osTicket postMessage: an auto-submitted inbound mail still threads, but
            // the whole notify cascade for that post stays silent.
            if (evt.SuppressNotifications)
                return;
            await OnNewMessageAsync(ticketId, t, entry.UserId, entry.StaffId, messageHtml, ct);
        }
        else if (evt.Type == ThreadEntryType.Note && await AlertOnAsync("new_activity", ct))
        {
            // Internal activity alert (osTicket onActivity → note.alert).
            var list = new List<MailRecipient?>();
            if (await AlertOnAsync("new_activity_last_respondent", ct))
                list.Add(await recipients.LastRespondentAsync(evt.ThreadId, ct));
            if (await AlertOnAsync("new_activity_assigned", ct))
            {
                if (t.StaffId is { } staffId)
                    list.Add(await recipients.StaffAsync(staffId, ct));
                if (t.TeamId is { } teamId)
                    list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
            }
            if (await AlertOnAsync("new_activity_dept_manager", ct))
                list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));

            await FanoutAsync("note.alert", list, entry.StaffId, ticketId, messageHtml, ct);
        }
        else if (evt.Type == ThreadEntryType.Response)
        {
            // Staff reply → the customer (osTicket postReply). No staff alert exists
            // for a response; the agent's own mail IS the notification.
            await OnResponseAsync(evt, ticketId, t, entry.StaffId, messageHtml, ct);
        }
    }

    /// <summary>
    /// The customer-facing reply mail (osTicket <c>Ticket::postReply</c>): owner in To,
    /// active collaborators in Cc, rendered from the catalog's ticket.reply template
    /// with %{response} carrying the sanitized reply HTML and the composer's chosen
    /// signature. From-account = the agent's "send from" choice, else the department's
    /// address (osTicket <c>$vars['from_email_id'] ?: $dept->getEmail()</c>).
    ///
    /// The mail is NOT gated by the autoresp.* switches — those govern machine-written
    /// confirmations; a reply is the agent talking, and osTicket sends it whenever
    /// postReply's <c>$alert</c> holds. Ticket.AutoResponseDisabled likewise does not
    /// suppress it. It is marked human-authored so no Auto-Submitted header goes out,
    /// and it carries the reply's attachments when email/attachments_in_email is on.
    ///
    /// Deviation from osTicket, flagged: osTicket puts the auth-token ticket link in
    /// only when the owner is the sole recipient of a ticket that HAS collaborators;
    /// here every recipient gets their own %{recipient.ticket_link}, since the link is
    /// scoped to the ticket either way and the narrower rule reads as an accident.
    /// </summary>
    private async Task OnResponseAsync(ThreadEntryAdded evt, int ticketId, TicketSnapshot t,
        int? posterStaffId, string responseHtml, CancellationToken ct)
    {
        if (posterStaffId is null)
            return; // only staff author Responses; a system-posted one mails nobody.

        var owner = await recipients.OwnerAsync(ticketId, ct);
        if (owner is null)
            return; // no deliverable address on the ticket owner — nothing to send.

        // Collaborators ride as Cc on the owner's message (osTicket MailingList: one
        // send, owner To + active collabs Cc), never as separate copies.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner.Email };
        var cc = (await recipients.CollaboratorsAsync(t.ThreadId, ct))
            .Where(c => seen.Add(c.Email))
            .Select(c => c.Email)
            .ToList();

        var email = await settings.GetEmailAsync(ct);
        await SendAsync("ticket.reply", owner, evt.FromEmailAccountId ?? t.DeptFrom, ticketId, null, ct,
            responseHtml: responseHtml,
            signature: evt.SignatureText,
            cc: cc.Count > 0 ? string.Join(", ", cc) : null,
            threadEntryId: evt.EntryId,
            isAutomated: false,
            includeAttachments: email.AttachmentsInEmail);
    }

    private async Task OnNewMessageAsync(int ticketId, TicketSnapshot t, int? posterUserId, int? posterStaffId,
        string messageHtml, CancellationToken ct)
    {
        // Customer-facing paths exist only for user-posted messages.
        if (posterUserId is { } userId && !t.AutoResponseDisabled)
        {
            var poster = await UserRecipientAsync(userId, ct);

            // Receipt confirmation to the poster (osTicket onMessage →
            // message.autoresp): global + department masters; never to an address
            // that is one of our own mail accounts (loop guard).
            if (poster is not null
                && await AutorespOnAsync("new_message", ct) && t.DeptMessageAutoResponse
                && !await db.EmailAccounts.AnyAsync(a => a.Address.ToLower() == poster.Email.ToLower(), ct))
            {
                await SendAsync("message.autoresp", poster, t.AutoRespFrom, ticketId, messageHtml, ct);
            }

            // Participant copies (osTicket notifyCollaborators → activity notice):
            // owner + active collaborators, minus the poster.
            if (await AutorespOnAsync("new_message_collab", ct))
            {
                var participants = new List<MailRecipient?> { await recipients.OwnerAsync(ticketId, ct) };
                participants.AddRange(await recipients.CollaboratorsAsync(t.ThreadId, ct));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var participant in participants)
                {
                    if (participant is null || participant.UserId == userId || !seen.Add(participant.Email))
                        continue;
                    await SendAsync("ticket.activity.notice", participant, t.DeptFrom, ticketId, messageHtml, ct);
                }
            }
        }

        // Staff alert (osTicket new message alert → message.alert).
        if (!await AlertOnAsync("new_message", ct))
            return;

        var list = new List<MailRecipient?>();
        if (await AlertOnAsync("new_message_last_respondent", ct))
            list.Add(await recipients.LastRespondentAsync(t.ThreadId, ct));
        if (await AlertOnAsync("new_message_assigned", ct))
        {
            if (t.StaffId is { } staffId)
                list.Add(await recipients.StaffAsync(staffId, ct));
            else if (t.TeamId is { } teamId)
                list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
        }
        if (await AlertOnAsync("new_message_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));
        if (await AlertOnAsync("new_message_account_manager", ct))
            list.Add(await recipients.AccountManagerAsync(t.UserId, ct));

        await FanoutAsync("message.alert", list, posterStaffId, ticketId, messageHtml, ct);
    }

    // ---- assignment / transfer / overdue ---------------------------------------------

    public async Task HandleAsync(TicketAssigned evt, CancellationToken ct = default)
    {
        if (evt.SuppressAlert                             // ticket-open notify choice
            || (evt.StaffId is null && evt.TeamId is null) // unassignment
            || !await AlertOnAsync("assignment", ct))
        {
            return;
        }

        var list = new List<MailRecipient?>();
        if (evt.StaffId is { } staffId)
        {
            // Staff assignment (osTicket onAssign): only the assignee is alerted.
            if (await AlertOnAsync("assignment_assigned", ct))
                list.Add(await recipients.StaffAsync(staffId, ct));
        }
        else if (evt.TeamId is { } teamId)
        {
            // Team assignment: members checkbox wins over lead (osTicket elseif);
            // Team.NoAlerts suppresses inside the resolver.
            list.AddRange(await recipients.TeamAsync(teamId,
                members: await AlertOnAsync("assignment_team_members", ct),
                lead: await AlertOnAsync("assignment_team_lead", ct), ct));
        }

        // Skip-actor makes a self-claim silent (osTicket claims alert nobody).
        await FanoutAsync("assigned.alert", list, evt.ActorStaffId, evt.TicketId, null, ct);
    }

    public async Task HandleAsync(TicketTransferred evt, CancellationToken ct = default)
    {
        if (!await AlertOnAsync("transfer", ct))
            return;
        var t = await SnapshotAsync(evt.TicketId, ct); // ticket already carries the NEW dept
        if (t is null)
            return;

        var list = new List<MailRecipient?>();
        if (t.StaffId is not null || t.TeamId is not null)
        {
            // Assigned staff/team… else dept members of the receiving department
            // (osTicket transfer: members only when NOT assigned).
            if (await AlertOnAsync("transfer_assigned", ct))
            {
                if (t.StaffId is { } staffId)
                    list.Add(await recipients.StaffAsync(staffId, ct));
                else if (t.TeamId is { } teamId)
                    list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
            }
        }
        else if (await AlertOnAsync("transfer_dept_members", ct))
        {
            list.AddRange(await recipients.DeptMembersAsync(evt.NewDepartmentId, ct));
        }
        if (await AlertOnAsync("transfer_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(evt.NewDepartmentId, ct));

        await FanoutAsync("transfer.alert", list, evt.ActorStaffId, evt.TicketId, null, ct);
    }

    public async Task HandleAsync(TicketOverdue evt, CancellationToken ct = default)
    {
        var t = await SnapshotAsync(evt.TicketId, ct);
        if (t is null || !await AlertOnAsync("overdue", ct))
            return;

        // The SLA plan can silence its own overdue alerts (admin/slas, osTicket flag 4).
        if (t.SlaId is { } slaId && await db.SlaPlans.Where(s => s.Id == slaId)
                .Select(s => s.DisableOverdueAlerts).SingleOrDefaultAsync(ct))
        {
            return;
        }

        var list = new List<MailRecipient?>();
        if (t.StaffId is not null || t.TeamId is not null)
        {
            if (await AlertOnAsync("overdue_assigned", ct))
            {
                if (t.StaffId is { } staffId)
                    list.Add(await recipients.StaffAsync(staffId, ct));
                else if (t.TeamId is { } teamId)
                    list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
            }
        }
        else if (await AlertOnAsync("overdue_dept_members", ct))
        {
            list.AddRange(await recipients.DeptMembersAsync(t.DepartmentId, ct));
        }
        if (await AlertOnAsync("overdue_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));

        await FanoutAsync("ticket.overdue", list, actorStaffId: null, evt.TicketId, null, ct);
    }

    // ---- overlimit notice (no ticket exists — called by the portal refusal path) ------

    /// <summary>
    /// Mails the "overlimit notice" (osTicket onOpenLimit) to a user whose create was
    /// refused by tickets.max_open_per_user. No ticket exists, so the from-account is
    /// resolved from the topic's department (?? core default) and only %{recipient.*}
    /// variables fill.
    /// </summary>
    public async Task SendOverlimitNoticeAsync(int userId, int? helpTopicId, CancellationToken ct = default)
    {
        if (!await AutorespOnAsync("overlimit", ct))
            return;
        var user = await UserRecipientAsync(userId, ct);
        if (user is null)
            return;

        var deptId = helpTopicId is { } topicId
            ? await db.HelpTopics.Where(h => h.Id == topicId).Select(h => h.DepartmentId).SingleOrDefaultAsync(ct)
            : null;
        deptId ??= int.TryParse(await settings.GetAsync("core", "default_dept_id", ct), out var d) ? d : null;
        var from = deptId is { } id
            ? await db.Departments.Where(x => x.Id == id)
                .Select(x => x.AutoResponseEmailAccountId ?? x.EmailAccountId).SingleOrDefaultAsync(ct)
            : null;

        await SendAsync("ticket.overlimit", user, from, ticketId: null, messageHtml: null, ct);
    }

    // ---- shared plumbing --------------------------------------------------------------

    private sealed record TicketSnapshot(
        int ThreadId, int UserId, int DepartmentId, int? StaffId, int? TeamId, int? SlaId,
        bool AutoResponseDisabled, bool DeptTicketAutoResponse, bool DeptMessageAutoResponse,
        int? DeptFrom, int? AutoRespFrom);

    private Task<TicketSnapshot?> SnapshotAsync(int ticketId, CancellationToken ct) =>
        db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => (TicketSnapshot?)new TicketSnapshot(
                t.ThreadId, t.UserId, t.DepartmentId, t.StaffId, t.TeamId, t.SlaId,
                t.AutoResponseDisabled,
                t.Department!.TicketAutoResponse,
                t.Department!.MessageAutoResponse,
                t.Department!.EmailAccountId,
                t.Department!.AutoResponseEmailAccountId ?? t.Department!.EmailAccountId))
            .SingleOrDefaultAsync(ct);

    private async Task<bool> AutorespOnAsync(string key, CancellationToken ct)
    {
        _autoresp ??= await settings.GetSectionAsync("autoresp", ct);
        return On(_autoresp, key, AutorespDefaults);
    }

    private async Task<bool> AlertOnAsync(string key, CancellationToken ct)
    {
        _alerts ??= await settings.GetSectionAsync("alerts", ct);
        return On(_alerts, key, AlertDefaults);
    }

    private static bool On(IReadOnlyDictionary<string, string> stored, string key, Dictionary<string, bool> defaults) =>
        stored.TryGetValue(key, out var v) && bool.TryParse(v, out var b)
            ? b
            : defaults.GetValueOrDefault(key);

    /// <summary>email/admin_email as a pseudo staff recipient (osTicket alertAdmin).</summary>
    private async Task<MailRecipient?> AdminRecipientAsync(CancellationToken ct)
    {
        var admin = (await settings.GetEmailAsync(ct)).AdminEmail;
        return string.IsNullOrWhiteSpace(admin) ? null : new MailRecipient("Admin", admin);
    }

    /// <summary>Alert from-account: email/alert_email_id, else the transport default.</summary>
    private async Task<int?> AlertFromAsync(CancellationToken ct)
    {
        var id = (await settings.GetEmailAsync(ct)).AlertEmailAccountId;
        return id > 0 ? id : null;
    }

    private async Task<MailRecipient?> UserRecipientAsync(int userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Name,
                Email = u.Emails.Where(e => e.Id == u.DefaultEmailId).Select(e => e.Address).FirstOrDefault()
                    ?? u.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .Where(x => x.Email != null)
            .Select(x => (MailRecipient?)new MailRecipient(x.Name, x.Email!, null, userId))
            .SingleOrDefaultAsync(ct);

    /// <summary>Dedupe + skip-actor alert fan-out over one catalog template.</summary>
    private async Task FanoutAsync(string templateCode, IReadOnlyList<MailRecipient?> list, int? actorStaffId,
        int ticketId, string? messageHtml, CancellationToken ct)
    {
        var from = await AlertFromAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in list)
        {
            if (recipient is null
                || (recipient.StaffId is { } staffId && staffId == actorStaffId)
                || !seen.Add(recipient.Email))
            {
                continue;
            }
            await SendAsync(templateCode, recipient, from, ticketId, messageHtml, ct);
        }
    }

    private async Task SendAsync(string templateCode, MailRecipient to, int? fromAccountId, int? ticketId,
        string? messageHtml, CancellationToken ct,
        string? responseHtml = null, string? signature = null, string? cc = null, int? threadEntryId = null,
        bool isAutomated = true, bool includeAttachments = false)
    {
        var rendered = await renderer.RenderAsync(templateCode, new EmailRenderContext
        {
            TicketId = ticketId,
            RecipientName = to.Name,
            RecipientEmail = to.Email,
            MessageHtml = messageHtml,
            ResponseHtml = responseHtml,
            SignatureText = signature,
            // users/auth_tokens (osTicket allow_auth_tokens): while on, the ticket
            // link in customer mail carries a signed access token so the recipient
            // lands on their ticket without signing in. Off ⇒ the renderer's plain
            // link stands, so the override is only computed when it changes something.
            Extra = ticketId is { } id ? await TicketLinkOverrideAsync(id, ct) : null,
        }, ct);
        if (rendered is null)
        {
            logger.LogWarning("Template {Code} missing; mail to {Email} skipped (ticket {TicketId})",
                templateCode, to.Email, ticketId);
            return;
        }
        await queue.EnqueueAsync(new OutboundEmailRequest(to.Email, rendered.Subject, rendered.HtmlBody)
        {
            Cc = cc,
            FromEmailAccountId = fromAccountId,
            TicketId = ticketId,
            ThreadEntryId = threadEntryId,
            IsAutomated = isAutomated,
            IncludeAttachments = includeAttachments,
        }, ct);
    }

    /// <summary>%{ticket.link} / %{recipient.ticket_link} with the auto-login token,
    /// or null while users/auth_tokens is off (renderer keeps its plain link).</summary>
    private async Task<IReadOnlyDictionary<string, string?>?> TicketLinkOverrideAsync(int ticketId, CancellationToken ct)
    {
        if (!(await settings.GetUsersAsync(ct)).AuthTokens)
            return null;
        var baseUrl = await settings.GetAsync("core", "helpdesk_url", ct) ?? "https://destek.rapidsol.com.tr/";
        var link = await links.TicketLinkAsync(ticketId, baseUrl, ct);
        return new Dictionary<string, string?>
        {
            ["ticket.link"] = link,
            ["recipient.ticket_link"] = link,
        };
    }

    /// <summary>Thread entry body as mail-safe HTML: html entries are already
    /// sanitized at ingress; text entries get encoded with newlines kept. Markup
    /// characters only — the EmailTemplateRenderer encoder, so Turkish stays literal
    /// in the mail instead of arriving as a wall of numeric entities.</summary>
    private static string AsHtml(string body, string? format) =>
        format == "html" ? body : BodyEncoder.Encode(body).Replace("\n", "<br>");
}
