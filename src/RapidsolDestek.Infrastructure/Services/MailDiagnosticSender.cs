using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One real test-email submission for admin/email-diagnostic (S7, B10):
/// the chosen account's SMTP channel settings plus the form's message.</summary>
public sealed record MailSendRequest(
    string Host,
    int Port,
    MailAuthKind Auth,
    string? Username,
    string? Password,
    string FromAddress,
    string? FromName,
    string To,
    string Subject,
    string TextBody);

public interface IMailDiagnosticSender
{
    /// <summary>Attempts the send for real; never throws — the typed
    /// <see cref="MailTestResult"/> stage IS the diagnostic outcome
    /// ("input"/"dns"/"connect"/"tls"/"auth"/"send", or success "sent").</summary>
    Task<MailTestResult> SendAsync(MailSendRequest request, CancellationToken ct = default);
}

/// <summary>
/// Real MailKit SMTP submission (no fakes — MailConnectionTester's client setup,
/// carried through to an actual <c>MAIL FROM</c>/<c>DATA</c>). The email-diagnostic
/// page's pending/success/failure states report exactly what happened here; an
/// unreachable dev SMTP host therefore surfaces as an honest typed failure instead
/// of the mockup's always-on success banner. The S8 pipeline reuses this transport.
/// </summary>
public sealed class MailDiagnosticSender : IMailDiagnosticSender
{
    /// <summary>Overall budget per attempt; keeps the pending state short-lived.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<MailTestResult> SendAsync(MailSendRequest request, CancellationToken ct = default)
    {
        var host = (request.Host ?? "").Trim();
        if (host.Length == 0 || request.Port is < 1 or > 65535)
            return MailTestResult.Fail("input");
        if (!MailboxAddress.TryParse(request.To, out var to)
            || !MailboxAddress.TryParse(request.FromAddress, out var from))
            return MailTestResult.Fail("input");
        from.Name = request.FromName ?? "";

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = request.Subject;
        message.Body = new TextPart("plain") { Text = request.TextBody };

        using var client = new SmtpClient();
        client.Timeout = (int)Timeout.TotalMilliseconds;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout + TimeSpan.FromSeconds(1)); // hard stop behind MailKit's own timeout

        try
        {
            await client.ConnectAsync(host, request.Port, SecureSocketOptions.Auto, cts.Token);
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
            // OAuth2: no token exists before the S8 flow — an OAuth2-only channel
            // can still submit where the server allows unauthenticated relay;
            // otherwise the server's rejection reports as the honest "send" stage.
            if (request.Auth == MailAuthKind.Basic && !string.IsNullOrEmpty(request.Username))
            {
                try
                {
                    await client.AuthenticateAsync(request.Username, request.Password ?? "", cts.Token);
                }
                catch (AuthenticationException ex)
                {
                    return MailTestResult.Fail("auth", ex.Message);
                }
            }

            await client.SendAsync(message, cts.Token);
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
