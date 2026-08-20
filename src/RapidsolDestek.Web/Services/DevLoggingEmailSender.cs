using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>Outbound app mail (reset links, guest access links etc.). S8: backed by
/// the persistent mail queue (<see cref="QueueBackedEmailSender"/>).</summary>
public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// Development logging sender. Since S8 it is no longer the IAppEmailSender itself —
/// it is the queue's last-resort <see cref="IMailFallbackSender"/>: when no usable
/// SMTP account is configured (fresh dev database), the send job logs the mail here
/// and marks the outbox row Sent with an honest note. Registered only in
/// Development; production without a transport keeps the honest Failed row.
/// </summary>
public sealed class DevLoggingEmailSender(ILogger<DevLoggingEmailSender> logger) : IMailFallbackSender
{
    public Task SendAsync(string to, string? cc, string subject, string htmlBody, CancellationToken ct = default)
    {
        logger.LogInformation("MAIL (dev, not sent) to={To} cc={Cc} subject={Subject} body={Body}",
            to, cc, subject, htmlBody);
        return Task.CompletedTask;
    }
}
