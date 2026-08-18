using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// Interim B8 email plumbing: renders the two seeded effort templates
/// (effort.request → ticket owner on propose/revise, effort.response → proposing
/// agent on approve/reject) through <see cref="IAppEmailSender"/>. S8 replaces the
/// transport with the Hangfire + MailKit pipeline; the template contract stays.
/// </summary>
public sealed class EffortEmailHandler(
    AppDbContext db,
    ICannedResponseService variables,
    ISettingsService settings,
    IAppEmailSender mail,
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
            await SendAsync(ticketId, "effort.request", ToOwner: true, ct);
    }

    /// <summary>alerts.effort_response (S7 "Efor Yanıtı Uyarısı" master switch; default on).
    /// TODO(S8): recipient checkboxes (assigned / dept manager) join the alert fan-out.</summary>
    private async Task SendResponseAlertAsync(int ticketId, CancellationToken ct)
    {
        if (await settings.GetAsync("alerts", "effort_response", ct) == "false")
            return;
        await SendAsync(ticketId, "effort.response", ToOwner: false, ct);
    }

    private async Task SendAsync(int ticketId, string templateCode, bool ToOwner, CancellationToken ct)
    {
        var template = await db.EmailTemplateSets
            .Where(s => s.IsActive && s.Language == "tr")
            .SelectMany(s => s.Templates)
            .Where(t => t.CodeName == templateCode)
            .FirstOrDefaultAsync(ct);
        if (template is null)
        {
            logger.LogWarning("Effort template {Code} missing; email skipped for ticket {TicketId}", templateCode, ticketId);
            return;
        }

        var to = ToOwner
            ? await db.Tickets.Where(t => t.Id == ticketId)
                .Select(t => t.User!.Emails.Where(e => e.Id == t.User!.DefaultEmailId)
                    .Select(e => e.Address).FirstOrDefault() ?? t.User!.Emails.Select(e => e.Address).FirstOrDefault())
                .SingleOrDefaultAsync(ct)
            : await db.EffortProposals.Where(p => p.TicketId == ticketId)
                .OrderByDescending(p => p.RevisionNo)
                .Join(db.Staff, p => p.ProposedByStaffId, s => s.Id, (p, s) => s.Email)
                .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(to))
        {
            logger.LogWarning("No recipient for {Code} on ticket {TicketId}; email skipped", templateCode, ticketId);
            return;
        }

        var bag = await variables.BuildVariablesAsync(ticketId, ct);
        await mail.SendAsync(to,
            TemplateVariableExpander.Expand(template.Subject, bag),
            TemplateVariableExpander.Expand(template.Body, bag), ct);
    }
}
