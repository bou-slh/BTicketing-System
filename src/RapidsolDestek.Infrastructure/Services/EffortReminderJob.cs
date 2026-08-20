using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// S8 recurring effort reminder (settings-tickets effort/reminder_days — the B8
/// product-original flow, no osTicket counterpart). Pending proposals on OPEN tickets
/// whose last touch (reminder sent ?? proposed) is older than reminder_days get the
/// effort.request catalog template re-sent to the ticket owner — a deliberate re-ask,
/// not a new template code (the catalog has no dedicated reminder template; inventing
/// one is a canon decision, flagged). <see cref="EffortProposal.ReminderSentAt"/>
/// tracks the send, so a proposal is re-reminded every reminder_days, never daily.
/// Gates: effort/enabled + reminder_days &gt; 0 + the autoresp/effort_proposal master
/// (same switch that gates the original request — EffortEmailHandler). From-account:
/// the department's auto-response address (EffortEmailHandler precedent).
/// </summary>
public sealed class EffortReminderJob(
    AppDbContext db,
    ISettingsService settings,
    IEmailTemplateRenderer renderer,
    IMailQueue queue,
    TimeProvider clock,
    ILogger<EffortReminderJob> logger)
{
    /// <summary>Daily at 09:00 (server tz — Hangfire default UTC): reminders are a
    /// morning nudge, not a minute-level sweep.</summary>
    public const string Cron = "0 9 * * *";

    public async Task RunAsync(CancellationToken ct)
    {
        var effort = await settings.GetEffortAsync(ct);
        if (!effort.Enabled || effort.ReminderDays <= 0)
            return;
        // The proposal-mail master also silences reminders (one customer-facing switch).
        if (await settings.GetAsync("autoresp", "effort_proposal", ct) == "false")
            return;

        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-effort.ReminderDays);
        var stale = await db.EffortProposals
            .Where(p => p.State == EffortState.Pending
                && (p.ReminderSentAt ?? p.CreatedAt) <= cutoff
                && db.Tickets.Any(t => t.Id == p.TicketId
                    && db.TicketStatuses.Any(s => s.Id == t.StatusId && s.State == TicketState.Open)))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var proposal in stale)
        {
            var owner = await db.Tickets.Where(t => t.Id == proposal.TicketId)
                .Select(t => new
                {
                    t.User!.Name,
                    Email = t.User!.Emails.Where(e => e.Id == t.User!.DefaultEmailId)
                        .Select(e => e.Address).FirstOrDefault()
                        ?? t.User!.Emails.Select(e => e.Address).FirstOrDefault(),
                    From = t.Department!.AutoResponseEmailAccountId ?? t.Department!.EmailAccountId,
                })
                .SingleOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(owner?.Email))
            {
                logger.LogWarning("Effort reminder: no recipient for ticket {TicketId}; skipped",
                    proposal.TicketId);
                continue;
            }

            var rendered = await renderer.RenderAsync("effort.request", new EmailRenderContext
            {
                TicketId = proposal.TicketId,
                RecipientName = owner.Name,
                RecipientEmail = owner.Email,
            }, ct);
            if (rendered is null)
            {
                logger.LogWarning("Effort reminder: effort.request template missing; ticket {TicketId} skipped",
                    proposal.TicketId);
                continue;
            }

            await queue.EnqueueAsync(new OutboundEmailRequest(owner.Email, rendered.Subject, rendered.HtmlBody)
            {
                FromEmailAccountId = owner.From,
                TicketId = proposal.TicketId,
            }, ct);
            proposal.ReminderSentAt = now;
            sent++;
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Effort reminder: {Count} reminders sent", sent);
        }
    }
}
