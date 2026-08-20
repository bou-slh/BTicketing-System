using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// An OAuth2 mailbox channel could not produce a usable access token.
/// <see cref="Stage"/> is the stable key every surface maps to i18n and to outbox
/// LastError: "oauth-config" (client id/secret missing), "oauth-consent" (nobody has
/// authorized this channel yet — or the grant was revoked and only re-consent fixes
/// it) and "oauth-refresh" (the provider refused or was unreachable this time; a
/// retry may still succeed).
/// </summary>
public sealed class MailOAuthException(string stage, string message) : Exception(message)
{
    public string Stage { get; } = stage;

    public const string ConfigStage = "oauth-config";
    public const string ConsentStage = "oauth-consent";
    public const string RefreshStage = "oauth-refresh";
}

/// <summary>Outcome of one completed authorization-code consent.</summary>
public sealed record MailOAuthConsentResult(string? Account, DateTimeOffset ConsentAt);

/// <summary>
/// Per-channel OAuth2 token custody (S8 slice 6): redeems the admin consent code into
/// a stored refresh token, and hands every connect path a valid access token —
/// refreshing through the refresh token when the cached one is inside the safety
/// margin. Both connect directions of an address consent separately.
/// </summary>
public interface IMailOAuthTokenService
{
    /// <summary>A currently valid access token for the channel. Throws
    /// <see cref="MailOAuthException"/> — an OAuth2 channel that cannot authenticate
    /// must fail loudly, never silently degrade to an unauthenticated connect.</summary>
    Task<string> GetAccessTokenAsync(EmailChannel channel, CancellationToken ct = default);

    /// <summary>Exchanges an authorization code and persists the resulting refresh +
    /// access tokens against the channel.</summary>
    Task<MailOAuthConsentResult> RedeemCodeAsync(
        EmailChannel channel, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default);
}

/// <summary>
/// Implementation over the <see cref="IMailOAuthTokenClient"/> HTTP seam.
///
/// Persistence deliberately uses <c>ExecuteUpdateAsync</c> rather than the change
/// tracker: the callers (OutboundMailJob, MailFetchJob, the admin tester) share their
/// scope's DbContext with unrelated pending work, and a token refresh must not flush
/// that work early — nor should high-churn token columns ride the audit interceptor.
///
/// Concurrency: refreshes are serialized per channel by an in-process gate, and the
/// holder re-reads the row before deciding, so the second Hangfire worker to arrive
/// reuses what the first just persisted instead of burning a second refresh. Across
/// PROCESSES two workers can still refresh simultaneously — benign for both providers
/// (previously issued access tokens stay valid until expiry, and neither rotates the
/// refresh token on every use), and flagged rather than papered over with a lock table.
/// </summary>
public sealed class MailOAuthTokenService(
    AppDbContext db,
    IMailOAuthTokenClient client,
    IMailCredentialResolver credentials,
    TimeProvider clock) : IMailOAuthTokenService
{
    /// <summary>A cached token this close to expiry is treated as already expired —
    /// a long IMAP fetch must not have the token die mid-pass.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    public async Task<string> GetAccessTokenAsync(EmailChannel channel, CancellationToken ct = default)
    {
        if (channel.AuthKind != MailAuthKind.OAuth2)
            throw new MailOAuthException(MailOAuthException.ConfigStage, "Channel is not an OAuth2 channel.");
        if (string.IsNullOrWhiteSpace(channel.OAuthClientId))
            throw new MailOAuthException(MailOAuthException.ConfigStage, "OAuth2 client id is not configured.");

        var cached = Usable(channel.OAuthAccessTokenProtected, channel.OAuthAccessTokenExpiresAt);
        if (cached is not null)
            return cached;

        var gate = Gates.GetOrAdd(channel.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Someone may have refreshed while we queued: decide on DB state, not on
            // the possibly stale entity our caller handed us.
            var current = await db.Set<EmailChannel>().AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == channel.Id, ct)
                ?? throw new MailOAuthException(MailOAuthException.ConfigStage, "Channel no longer exists.");

            var fresh = Usable(current.OAuthAccessTokenProtected, current.OAuthAccessTokenExpiresAt);
            if (fresh is not null)
            {
                Apply(channel, current.OAuthAccessTokenProtected, current.OAuthAccessTokenExpiresAt,
                    current.OAuthRefreshTokenProtected);
                return fresh;
            }

            var refreshToken = credentials.Unprotect(current.OAuthRefreshTokenProtected);
            if (string.IsNullOrEmpty(refreshToken))
            {
                throw new MailOAuthException(MailOAuthException.ConsentStage,
                    "OAuth2 channel has no refresh token — an administrator must grant consent.");
            }

            var preset = MailOAuthProviders.For(current.OAuthProvider, current.OAuthTenant);
            var response = await client.RequestAsync(new MailOAuthTokenRequest(preset.TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = current.OAuthClientId ?? "",
                    ["client_secret"] = credentials.Unprotect(current.OAuthClientSecretProtected) ?? "",
                    ["scope"] = MailOAuthProviders.ScopesFor(current),
                }), ct);

            if (!response.Success)
            {
                if (response.IsInvalidGrant)
                {
                    // The grant is dead: drop the stored tokens so the UI stops
                    // claiming consent exists and every later attempt fails fast at
                    // "oauth-consent" instead of hammering the provider.
                    await db.Set<EmailChannel>().Where(c => c.Id == current.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(c => c.OAuthRefreshTokenProtected, (string?)null)
                            .SetProperty(c => c.OAuthAccessTokenProtected, (string?)null)
                            .SetProperty(c => c.OAuthAccessTokenExpiresAt, (DateTimeOffset?)null)
                            .SetProperty(c => c.OAuthConsentAt, (DateTimeOffset?)null), ct);
                    Apply(channel, null, null, null);
                    channel.OAuthConsentAt = null;
                    throw new MailOAuthException(MailOAuthException.ConsentStage,
                        Describe("OAuth2 refresh token rejected (revoked or expired)", response));
                }
                throw new MailOAuthException(MailOAuthException.RefreshStage,
                    Describe("OAuth2 token refresh failed", response));
            }

            return await PersistAsync(channel, current, response, refreshToken, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MailOAuthConsentResult> RedeemCodeAsync(
        EmailChannel channel, string code, string redirectUri, string codeVerifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channel.OAuthClientId))
            throw new MailOAuthException(MailOAuthException.ConfigStage, "OAuth2 client id is not configured.");

        var preset = MailOAuthProviders.For(channel.OAuthProvider, channel.OAuthTenant);
        var response = await client.RequestAsync(new MailOAuthTokenRequest(preset.TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = channel.OAuthClientId,
                ["client_secret"] = credentials.Unprotect(channel.OAuthClientSecretProtected) ?? "",
                ["code_verifier"] = codeVerifier,
                ["scope"] = MailOAuthProviders.ScopesFor(channel),
            }), ct);

        if (!response.Success)
        {
            throw new MailOAuthException(MailOAuthException.RefreshStage,
                Describe("OAuth2 authorization code exchange failed", response));
        }
        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            // Without a refresh token the grant dies in an hour and every later job
            // fails — refuse the consent instead of storing a booby trap.
            throw new MailOAuthException(MailOAuthException.ConsentStage,
                "Provider returned no refresh token; re-run consent with offline access enabled.");
        }

        var account = ReadEmailClaim(response.IdToken) ?? channel.OAuthConsentAccount;
        var now = clock.GetUtcNow();
        var expiresAt = (DateTimeOffset?)now.AddSeconds(response.ExpiresInSeconds);
        var accessProtected = credentials.Protect(response.AccessToken!);
        var refreshProtected = credentials.Protect(response.RefreshToken);

        await db.Set<EmailChannel>().Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.OAuthRefreshTokenProtected, refreshProtected)
                .SetProperty(c => c.OAuthAccessTokenProtected, accessProtected)
                .SetProperty(c => c.OAuthAccessTokenExpiresAt, expiresAt)
                .SetProperty(c => c.OAuthConsentAt, (DateTimeOffset?)now)
                .SetProperty(c => c.OAuthConsentAccount, account), ct);

        Apply(channel, accessProtected, expiresAt, refreshProtected);
        channel.OAuthConsentAt = now;
        channel.OAuthConsentAccount = account;
        return new MailOAuthConsentResult(account, now);
    }

    // ---- helpers ---------------------------------------------------------------------

    private async Task<string> PersistAsync(
        EmailChannel channel, EmailChannel current, MailOAuthTokenResponse response,
        string usedRefreshToken, CancellationToken ct)
    {
        var expiresAt = clock.GetUtcNow().AddSeconds(response.ExpiresInSeconds);
        var accessProtected = credentials.Protect(response.AccessToken!);
        // Google rotates the refresh token only occasionally, Microsoft returns a new
        // one on every refresh — keep whatever came back, else keep what worked.
        var refreshProtected = string.IsNullOrEmpty(response.RefreshToken)
            || response.RefreshToken == usedRefreshToken
                ? current.OAuthRefreshTokenProtected
                : credentials.Protect(response.RefreshToken);

        await db.Set<EmailChannel>().Where(c => c.Id == current.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.OAuthAccessTokenProtected, accessProtected)
                .SetProperty(c => c.OAuthAccessTokenExpiresAt, (DateTimeOffset?)expiresAt)
                .SetProperty(c => c.OAuthRefreshTokenProtected, refreshProtected), ct);

        Apply(channel, accessProtected, expiresAt, refreshProtected);
        return response.AccessToken!;
    }

    /// <summary>Mirrors persisted token state onto the caller's entity so a tracked
    /// instance does not keep serving the stale cached token within this scope.</summary>
    private static void Apply(EmailChannel channel, string? accessProtected,
        DateTimeOffset? expiresAt, string? refreshProtected)
    {
        channel.OAuthAccessTokenProtected = accessProtected;
        channel.OAuthAccessTokenExpiresAt = expiresAt;
        channel.OAuthRefreshTokenProtected = refreshProtected;
    }

    /// <summary>Decrypted cached token when it is still comfortably valid, else null.</summary>
    private string? Usable(string? accessProtected, DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrEmpty(accessProtected) || expiresAt is null)
            return null;
        if (expiresAt.Value - RefreshMargin <= clock.GetUtcNow())
            return null;
        var token = credentials.Unprotect(accessProtected);
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static string Describe(string prefix, MailOAuthTokenResponse response) =>
        string.IsNullOrEmpty(response.ErrorDescription)
            ? $"{prefix}: {response.Error}"
            : $"{prefix}: {response.Error} — {response.ErrorDescription}";

    /// <summary>
    /// The <c>email</c> (or Entra's <c>preferred_username</c>) claim of the id_token,
    /// used only as human-readable evidence of which mailbox was consented. The token
    /// signature is NOT validated: it arrived over TLS directly from the token endpoint
    /// in response to our own client-authenticated request, and nothing is authorized
    /// on the strength of this string.
    /// </summary>
    internal static string? ReadEmailClaim(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken))
            return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return null;
        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            var payload = Convert.FromBase64String(segment.PadRight(
                segment.Length + (4 - segment.Length % 4) % 4, '='));
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            foreach (var claim in new[] { "email", "preferred_username", "upn" })
            {
                if (json.RootElement.TryGetProperty(claim, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } address)
                {
                    return address;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            // Optional evidence only — an unparseable id_token never fails consent.
        }
        return null;
    }
}
