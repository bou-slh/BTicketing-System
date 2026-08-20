using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One resolved SMTP hop for a queued send (channel settings + decrypted secret).</summary>
public sealed record SmtpTransportSettings(
    string Host,
    int Port,
    MailAuthKind Auth,
    string? Username,
    string? Password);

/// <summary>One file to attach to an outbound message (S8 slice 5,
/// email/attachments_in_email): decoded bytes plus the original name and type.</summary>
public sealed record OutboundAttachment(string FileName, string MimeType, byte[] Content);

/// <summary>One outbound message as the S8 queue hands it to the transport.
/// <c>MessageId</c> (no angle brackets) is the signed reply token for ticket mail —
/// null lets MimeKit generate a plain id; <c>InReplyTo</c> is the ticket's last
/// inbound Message-Id so recipients' clients thread our reply.</summary>
public sealed record OutboundSmtpMessage(
    string FromAddress,
    string? FromName,
    string To,
    string? Cc,
    string Subject,
    string HtmlBody,
    string? MessageId = null,
    string? InReplyTo = null)
{
    /// <summary>Reply-To for ticket mail: the mailbox we actually fetch, so a
    /// customer's reply reaches the inbound pipeline even when From carries a
    /// different display identity (osTicket sets the department address as the
    /// sending identity for the same reason).</summary>
    public string? ReplyTo { get; init; }

    /// <summary>
    /// RFC 3834 <c>Auto-Submitted: auto-generated</c> is stamped only when true.
    /// Human-authored mail (the agent reply) MUST go out without it: the header tells
    /// the recipient's mail system not to respond, which would suppress exactly the
    /// customer replies the ticket thread depends on (osTicket stamps it on its
    /// 'notice'/'autoreply' sends only — never on postReply).
    /// </summary>
    public bool AutoSubmitted { get; init; } = true;

    public IReadOnlyList<OutboundAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// The S8 outbound pipeline's SMTP seam: the Hangfire send job talks to this, the
/// real implementation is MailKit, and tests swap in a capturing fake so queued
/// sends are observable without network I/O (AppFactory).
/// </summary>
public interface ISmtpMailTransport
{
    /// <summary>Never throws — the typed <see cref="MailTestResult"/> stage IS the
    /// outcome ("input"/"dns"/"connect"/"tls"/"auth"/"send", or success "sent").</summary>
    Task<MailTestResult> SendAsync(SmtpTransportSettings smtp, OutboundSmtpMessage message, CancellationToken ct = default);
}

/// <summary>
/// Development-only stand-in used by the send job when NO usable SMTP account is
/// configured (fresh dev database): the mail is logged, the outbox row is marked
/// Sent with an honest note. Registered only in Development (Program.cs); its
/// absence in production makes a transportless send an honest Failed row.
/// </summary>
public interface IMailFallbackSender
{
    Task SendAsync(string to, string? cc, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>Decrypts an <see cref="EmailChannel"/>'s stored basic-auth password.
/// Implemented in Web over the DataProtection-based IEmailSecretProtector (the
/// keyring lives with the app); Infrastructure only consumes plaintext at send.</summary>
public interface IMailCredentialResolver
{
    string? ResolvePassword(EmailChannel channel);
}

/// <summary>
/// Real MailKit SMTP submission for the S8 queue: MailDiagnosticSender's transport
/// pattern (typed stages, Auto TLS, hard timeout) carried to HTML bodies + CC.
/// </summary>
public sealed class MailKitSmtpTransport : ISmtpMailTransport
{
    /// <summary>Overall budget per attempt — the Hangfire job retries, so fail fast.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<MailTestResult> SendAsync(SmtpTransportSettings smtp, OutboundSmtpMessage message, CancellationToken ct = default)
    {
        var host = (smtp.Host ?? "").Trim();
        if (host.Length == 0 || smtp.Port is < 1 or > 65535)
            return MailTestResult.Fail("input");
        if (!MailboxAddress.TryParse(message.To, out var to)
            || !MailboxAddress.TryParse(message.FromAddress, out var from))
            return MailTestResult.Fail("input");
        from.Name = message.FromName ?? "";

        var mime = new MimeMessage();
        mime.From.Add(from);
        mime.To.Add(to);
        foreach (var cc in (message.Cc ?? "").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (MailboxAddress.TryParse(cc, out var ccAddress))
                mime.Cc.Add(ccAddress);
        }
        if (MailboxAddress.TryParse(message.ReplyTo ?? "", out var replyTo))
            mime.ReplyTo.Add(replyTo);
        mime.Subject = message.Subject;

        var body = new BodyBuilder { HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments)
        {
            // ContentType.Parse throws on garbage stored types — fall back to octet-stream.
            ContentType type;
            try
            {
                type = ContentType.Parse(attachment.MimeType);
            }
            catch (ParseException)
            {
                type = new ContentType("application", "octet-stream");
            }
            body.Attachments.Add(attachment.FileName, attachment.Content, type);
        }
        mime.Body = body.ToMessageBody();

        // S8 inbound slice — loop/threading headers on EVERY outbound mail: the loop
        // tag lets the fetch pipeline recognize mail that came back to us
        // (MailPipelineHeaders.MaxPasses), and the signed Message-Id + References
        // carry the ticket reply token.
        mime.Headers.Add(MailPipelineHeaders.LoopTag, "1");
        // Auto-Submitted is PER MESSAGE (S8 slice 5 fix): automated notifications say
        // "auto-generated" so remote autoresponders stay quiet; human-authored agent
        // replies carry no such header, because it would tell the customer's mail
        // system not to answer the very message that invites an answer.
        if (message.AutoSubmitted)
            mime.Headers.Add(HeaderId.AutoSubmitted, "auto-generated");
        if (!string.IsNullOrEmpty(message.MessageId))
            mime.MessageId = message.MessageId;
        if (!string.IsNullOrEmpty(message.InReplyTo))
        {
            var parent = message.InReplyTo.Trim().Trim('<', '>');
            mime.InReplyTo = parent;
            mime.References.Add(parent);
        }

        using var client = new SmtpClient();
        client.Timeout = (int)Timeout.TotalMilliseconds;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout + TimeSpan.FromSeconds(1)); // hard stop behind MailKit's own timeout

        try
        {
            await client.ConnectAsync(host, smtp.Port, SecureSocketOptions.Auto, cts.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            return MailTestResult.Fail("dns", ex.Message);
        }
        catch (SslHandshakeException ex)
        {
            return MailTestResult.Fail("tls", ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or ProtocolException)
        {
            return MailTestResult.Fail("connect", ex.Message);
        }

        try
        {
            // OAuth2: no token flow exists yet (S8 later slice) — submit where the
            // server allows unauthenticated relay, otherwise the rejection reports
            // as the honest "send" stage (MailDiagnosticSender precedent).
            if (smtp.Auth == MailAuthKind.Basic && !string.IsNullOrEmpty(smtp.Username))
            {
                try
                {
                    await client.AuthenticateAsync(smtp.Username, smtp.Password ?? "", cts.Token);
                }
                catch (AuthenticationException ex)
                {
                    return MailTestResult.Fail("auth", ex.Message);
                }
            }

            await client.SendAsync(mime, cts.Token);
            return new MailTestResult(true, "sent");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or ProtocolException or CommandException or InvalidOperationException)
        {
            return MailTestResult.Fail("send", ex.Message);
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true, CancellationToken.None);
            }
            catch
            {
                // Best-effort teardown only — the outcome is already decided.
            }
        }
    }
}
