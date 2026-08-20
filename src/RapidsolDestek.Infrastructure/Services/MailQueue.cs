using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One outbound mail for <see cref="IMailQueue.EnqueueAsync"/>.</summary>
public sealed record OutboundEmailRequest(string To, string Subject, string HtmlBody)
{
    public string? Cc { get; init; }

    /// <summary>Explicit from-account (dept address); null = email settings default.</summary>
    public int? FromEmailAccountId { get; init; }

    public int? TicketId { get; init; }

    public int? ThreadEntryId { get; init; }
}

/// <summary>
/// S8 outbound mail entry point: persists an <see cref="EmailOutbound"/> outbox row
/// and schedules the Hangfire send job. Callers never talk SMTP — transport
/// selection happens at send time (<see cref="OutboundMailJob"/>).
/// </summary>
public interface IMailQueue
{
    /// <summary>Persists the row and enqueues the send job. Returns the (detached)
    /// outbox row. The row is written in its OWN scope/connection: callers can sit
    /// inside an open transaction (effort events dispatch within the ticket-locked
    /// transaction), and the send job — a separate Hangfire worker — must see the
    /// committed row immediately. Deliberate trade-off: a caller rollback after
    /// enqueue does not unwind the mail (osTicket sends mid-request with no
    /// transactional guarantee either).</summary>
    Task<EmailOutbound> EnqueueAsync(OutboundEmailRequest request, CancellationToken ct = default);
}

public sealed class MailQueue(IServiceScopeFactory scopes, IBackgroundJobClient jobs) : IMailQueue
{
    public async Task<EmailOutbound> EnqueueAsync(OutboundEmailRequest request, CancellationToken ct = default)
    {
        var row = new EmailOutbound
        {
            ToAddress = request.To,
            CcAddresses = request.Cc,
            Subject = request.Subject,
            HtmlBody = request.HtmlBody,
            FromEmailAccountId = request.FromEmailAccountId,
            TicketId = request.TicketId,
            ThreadEntryId = request.ThreadEntryId,
            Status = EmailOutboundStatus.Pending,
        };
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EmailOutbounds.Add(row);
            await db.SaveChangesAsync(ct);
        }

        // Committed above — the job (fresh scope/worker) reads the row for real.
        jobs.Enqueue<OutboundMailJob>(j => j.SendAsync(row.Id, CancellationToken.None));
        return row;
    }
}
