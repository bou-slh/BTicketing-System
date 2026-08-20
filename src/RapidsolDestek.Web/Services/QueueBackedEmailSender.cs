using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// S8: <see cref="IAppEmailSender"/> backed by the persistent mail queue — Identity
/// account mails and the check-status guest link ride the outbox + Hangfire send job
/// instead of the old dev logger. No explicit from-account: the email settings
/// default (default_smtp / default_email_id) decides at send time.
/// </summary>
public sealed class QueueBackedEmailSender(IMailQueue queue) : IAppEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
        await queue.EnqueueAsync(new OutboundEmailRequest(to, subject, htmlBody), ct);
}

/// <summary>Web-side adapter: the send/fetch jobs and the S8 slice 6 OAuth2 token
/// service read and write channel secrets through the DataProtection-based
/// <see cref="IEmailSecretProtector"/> (the keyring lives here, not in
/// Infrastructure).</summary>
public sealed class EmailChannelCredentialResolver(IEmailSecretProtector secrets) : IMailCredentialResolver
{
    public string? ResolvePassword(EmailChannel channel) => secrets.Unprotect(channel.PasswordProtected);

    public string? Unprotect(string? protectedValue) => secrets.Unprotect(protectedValue);

    public string Protect(string plaintext) => secrets.Protect(plaintext);
}
