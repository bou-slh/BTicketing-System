using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One live connection attempt for the admin/email-edit "Bağlantıyı Sına"
/// button (S7 invented UI, ROADMAP-flagged): the posted, UNSAVED settings.
/// <see cref="AccessToken"/> is the S8 slice 6 exception to "unsaved": an OAuth2
/// access token can only come from the SAVED channel's stored consent, so the
/// controller resolves it before probing.</summary>
public sealed record MailTestRequest(
    EmailChannelKind Kind,
    MailProtocol Protocol,
    string Host,
    int Port,
    MailAuthKind Auth,
    string? Username,
    string? Password,
    string? AccessToken = null);

/// <summary>
/// Typed outcome. <see cref="Stage"/> is a stable key the page maps to i18n:
/// "input" (rejected before any network I/O), "dns" (host did not resolve),
/// "connect" (TCP refused / timed out), "tls" (SSL/STARTTLS handshake failed),
/// "auth" (server rejected the credentials), "ok" (authenticated), or
/// "ok-noauth" (connected + TLS fine, but no credential was configured at all).
/// S8 slice 6 adds the OAuth2 stages: "oauth-config" (client id/secret missing),
/// "oauth-consent" (no valid grant — an administrator must authorize) and
/// "oauth-refresh" (the provider refused this token request).
/// </summary>
public sealed record MailTestResult(bool Success, string Stage, string? Detail = null)
{
    public static MailTestResult Ok(bool authenticated) => new(true, authenticated ? "ok" : "ok-noauth");
    public static MailTestResult Fail(string stage, string? detail = null) => new(false, stage, detail);
}

public interface IMailConnectionTester
{
    Task<MailTestResult> TestAsync(MailTestRequest request, CancellationToken ct = default);
}

/// <summary>
/// Real MailKit connection probe with a short timeout (no fakes — the S8 pipeline
/// will reuse the same client setup). Encryption follows the mockup's field set:
/// there is no security select, so <see cref="SecureSocketOptions.Auto"/> decides
/// (implicit TLS on 465/993/995, opportunistic STARTTLS elsewhere).
/// </summary>
public sealed class MailConnectionTester : IMailConnectionTester
{
    /// <summary>Overall budget per probe; keeps the admin page (and tests) fast.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<MailTestResult> TestAsync(MailTestRequest request, CancellationToken ct = default)
    {
        var host = (request.Host ?? "").Trim();
        if (host.Length == 0 || request.Port is < 1 or > 65535)
            return MailTestResult.Fail("input");
        if (request.Kind == EmailChannelKind.Smtp ? request.Protocol != MailProtocol.Smtp
            : request.Protocol is not (MailProtocol.Imap or MailProtocol.Pop))
            return MailTestResult.Fail("input");
        // S8 slice 6: an OAuth2 channel with no resolved token cannot possibly sign in
        // — say so before dialing, instead of reporting a half-truth about the server.
        if (request.Auth == MailAuthKind.OAuth2 && string.IsNullOrEmpty(request.AccessToken))
            return MailTestResult.Fail(MailOAuthException.ConsentStage);

        using var client = CreateClient(request.Protocol);
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
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException)
        {
            return MailTestResult.Fail("connect", ex.Message);
        }
        catch (ProtocolException ex)
        {
            return MailTestResult.Fail("connect", ex.Message);
        }

        try
        {
            // S8 slice 6: an OAuth2 channel now performs a REAL XOAUTH2 sign-in with
            // the access token the caller resolved from the stored consent. A missing
            // token is never downgraded to an unauthenticated "ok" — it is the honest
            // "authorize this channel first" answer.
            if (request.Auth == MailAuthKind.OAuth2)
            {
                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(request.Username ?? "", request.AccessToken), cts.Token);
                return MailTestResult.Ok(authenticated: true);
            }

            if (string.IsNullOrEmpty(request.Username))
                return MailTestResult.Ok(authenticated: false);

            await client.AuthenticateAsync(request.Username, request.Password ?? "", cts.Token);
            return MailTestResult.Ok(authenticated: true);
        }
        catch (AuthenticationException ex)
        {
            return MailTestResult.Fail("auth", ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or ProtocolException)
        {
            return MailTestResult.Fail("auth", ex.Message);
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
                // Best-effort teardown only — the probe result is already decided.
            }
        }
    }

    private static IMailService CreateClient(MailProtocol protocol) => protocol switch
    {
        MailProtocol.Imap => new ImapClient(),
        MailProtocol.Pop => new Pop3Client(),
        _ => new SmtpClient(),
    };
}
