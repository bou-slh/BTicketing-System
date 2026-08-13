namespace RapidsolDestek.Web.Services;

/// <summary>Outbound app mail (reset links etc.). Real MailKit implementation arrives in S8.</summary>
public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>Dev/stage stand-in: logs every mail instead of sending. S8 replaces this with MailKit.</summary>
public sealed class DevLoggingEmailSender(ILogger<DevLoggingEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        logger.LogInformation("MAIL (dev, not sent) to={To} subject={Subject} body={Body}", to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
