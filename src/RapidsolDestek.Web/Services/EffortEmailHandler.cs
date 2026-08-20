using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// Effort email plumbing (B8 → S8): renders the two effort catalog templates
/// (effort.request → ticket owner on propose/revise, effort.response → proposing
/// agent on approve/reject) through <see cref="IEmailTemplateRenderer"/> and rides
/// the persistent mail queue (<see cref="IMailQueue"/> → Hangfire send job). The
/// from-address is the ticket department's account (user-facing request prefers the
/// department's auto-response address, osTicket getAutoRespEmail precedent).
/// </summary>
public sealed class EffortEmailHandler(
    AppDbContext db,
    IEmailTemplateRenderer renderer,
    ISettingsService settings,
    IAlertRecipientResolver alertRecipients,
    IMailQueue queue,
    ILogger<EffortEmailHandler> logger) :
    IDomainEventHandler<EffortProposed>,
    IDomainEventHandler<EffortRevised>,
    IDomainEventHandler<EffortApproved>,
    IDomainEventHandler<EffortRejected>
{
    public Task HandleAsync(EffortProposed evt, CancellationToken ct = default) =>
        SendRequestIfStillPendingAsync(evt.TicketId, evt.ProposalId, ct);

    public Task HandleAsync(EffortRevised evt, CancellationToken ct = default) =>
        SendRequestIfStillPendingAsync(evt.TicketId, evt.ProposalId, ct);

    public Task HandleAsync(EffortApproved evt, CancellationToken ct = default) =>
        SendResponseAlertAsync(evt.TicketId, ct);

    public Task HandleAsync(EffortRejected evt, CancellationToken ct = default) =>
        SendResponseAlertAsync(evt.TicketId, ct);

    /// <summary>B8: an auto-approved proposal never asks the customer — skip the request.</summary>
    private async Task SendRequestIfStillPendingAsync(int ticketId, int proposalId, CancellationToken ct)
    {
        // autoresp.effort_proposal (S7 admin/settings-tickets "Efor önerisi →
        // kullanıcıya bildirim"); default on.
        if (await settings.GetAsync("autoresp", "effort_proposal", ct) == "false")
            return;
        var pending = await db.EffortProposals
            .AnyAsync(p => p.Id == proposalId && p.State == Domain.Entities.EffortState.Pending, ct);
        if (pending)
            await SendAsync(ticketId, "effort.request", toOwner: true, ct);
    }

    /// <summary>alerts.effort_response (S7 "Efor Yanıtı Uyarısı" master switch; default
    /// on). LIVE (S8): the recipient checkboxes join the fan-out — the proposing agent
    /// stays the primary recipient, effort_response_assigned (default on) adds the
    /// assigned agent, effort_response_dept_manager (default off) the department
    /// manager; deduped by email, unavailable staff skipped.</summary>
    private async Task SendResponseAlertAsync(int ticketId, CancellationToken ct)
    {
        if (await settings.GetAsync("alerts", "effort_response", ct) == "false")
            return;
        await SendAsync(ticketId, "effort.response", toOwner: false, ct);

        var ticket = await db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => new { t.StaffId, t.DepartmentId, ProposerId = db.EffortProposals
                .Where(p => p.TicketId == ticketId).OrderByDescending(p => p.RevisionNo)
                .Select(p => (int?)p.ProposedByStaffId).FirstOrDefault() })
            .SingleOrDefaultAsync(ct);
        if (ticket is null)
            return;

        var extras = new List<MailRecipient?>();
        if (await settings.GetAsync("alerts", "effort_response_assigned", ct) != "false"
            && ticket.StaffId is { } assignedId && assignedId != ticket.ProposerId)
        {
            extras.Add(await alertRecipients.StaffAsync(assignedId, ct));
        }
        if (await settings.GetAsync("alerts", "effort_response_dept_manager", ct) == "true")
            extras.Add(await alertRecipients.DeptManagerAsync(ticket.DepartmentId, ct));

        var proposerEmail = ticket.ProposerId is { } proposerId
            ? await db.Staff.Where(s => s.Id == proposerId).Select(s => s.Email).SingleOrDefaultAsync(ct)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (proposerEmail is not null)
            seen.Add(proposerEmail); // already mailed by SendAsync above
        foreach (var recipient in extras)
        {
            if (recipient is null || !seen.Add(recipient.Email))
                continue;
            await SendToAsync(ticketId, "effort.response", recipient.Name, recipient.Email, ct);
        }
    }

    private async Task SendAsync(int ticketId, string templateCode, bool toOwner, CancellationToken ct)
    {
        var recipient = toOwner
            ? await db.Tickets.Where(t => t.Id == ticketId)
                .Select(t => new
                {
                    Name = t.User!.Name,
                    Email = t.User!.Emails.Where(e => e.Id == t.User!.DefaultEmailId)
                        .Select(e => e.Address).FirstOrDefault()
                        ?? t.User!.Emails.Select(e => e.Address).FirstOrDefault(),
                })
                .SingleOrDefaultAsync(ct)
            : await db.EffortProposals.Where(p => p.TicketId == ticketId)
                .OrderByDescending(p => p.RevisionNo)
                .Join(db.Staff, p => p.ProposedByStaffId, s => s.Id,
                    (p, s) => new { Name = s.FirstName + " " + s.LastName, Email = (string?)s.Email })
                .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(recipient?.Email))
        {
            logger.LogWarning("No recipient for {Code} on ticket {TicketId}; email skipped", templateCode, ticketId);
            return;
        }

        await SendToAsync(ticketId, templateCode, recipient.Name, recipient.Email, ct, toOwner);
    }

    /// <summary>Renders + enqueues one copy (S8 fan-out entry point; staff copies
    /// ride the department's response account like the primary recipient).</summary>
    private async Task SendToAsync(int ticketId, string templateCode, string name, string email,
        CancellationToken ct, bool toOwner = false)
    {
        var rendered = await renderer.RenderAsync(templateCode, new EmailRenderContext
        {
            TicketId = ticketId,
            RecipientName = name,
            RecipientEmail = email,
        }, ct);
        if (rendered is null)
        {
            logger.LogWarning("Effort template {Code} missing; email skipped for ticket {TicketId}", templateCode, ticketId);
            return;
        }

        // From = the ticket department's address; the user-facing request prefers
        // the department's auto-response account (department-edit de-ar-email).
        var dept = await db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => new { t.Department!.EmailAccountId, t.Department!.AutoResponseEmailAccountId })
            .SingleAsync(ct);
        var fromAccountId = toOwner
            ? dept.AutoResponseEmailAccountId ?? dept.EmailAccountId
            : dept.EmailAccountId;

        await queue.EnqueueAsync(new OutboundEmailRequest(email, rendered.Subject, rendered.HtmlBody)
        {
            FromEmailAccountId = fromAccountId,
            TicketId = ticketId,
        }, ct);
    }
}
