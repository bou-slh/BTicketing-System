using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace RapidsolDestek.Web.Services;

/// <summary>What a verified consent <c>state</c> parameter proves (S8 slice 6).</summary>
/// <param name="ChannelId">The EmailChannel the consent was started for.</param>
/// <param name="StaffId">The admin who started it — the callback refuses a state
/// minted for somebody else's session.</param>
/// <param name="CodeVerifier">PKCE verifier, carried inside the encrypted state so no
/// server-side session storage is needed for the round trip.</param>
public sealed record MailOAuthStateData(int ChannelId, int StaffId, string CodeVerifier);

/// <summary>
/// Mints and verifies the OAuth2 <c>state</c> parameter of the admin consent flow.
///
/// The payload is an ASP.NET Core DataProtection ciphertext with a 10-minute lifetime,
/// which gives authentication (any tampering fails the MAC), confidentiality (the PKCE
/// verifier travels through the browser but never in the clear) and expiry in one
/// primitive — a hand-rolled HMAC string would have delivered only the first.
/// CSRF-wise this is the classic OAuth2 defence: a callback whose state does not
/// decrypt, has expired, or names a different admin is refused before any code is
/// exchanged (and the callback endpoint is admin-authenticated on top).
/// </summary>
public interface IMailOAuthStateService
{
    /// <summary>Returns the opaque state value and the PKCE code challenge derived
    /// from the verifier hidden inside it.</summary>
    (string State, string CodeChallenge) Create(int channelId, int staffId);

    /// <summary>Null when the state is missing, tampered with, or expired.</summary>
    MailOAuthStateData? Validate(string? state);
}

public sealed class MailOAuthStateService(IDataProtectionProvider provider) : IMailOAuthStateService
{
    /// <summary>Consent must complete inside this window; a stale browser tab is
    /// refused rather than silently re-authorized.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("RapidsolDestek.EmailChannel.OAuthState").ToTimeLimitedDataProtector();

    public (string State, string CodeChallenge) Create(int channelId, int staffId)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var payload = $"{channelId}|{staffId}|{verifier}";
        var state = _protector.Protect(payload, Lifetime);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (state, challenge);
    }

    public MailOAuthStateData? Validate(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;
        string payload;
        try
        {
            payload = _protector.Unprotect(state);
        }
        catch (CryptographicException)
        {
            return null; // tampered, foreign keyring, or past its lifetime
        }

        var parts = payload.Split('|');
        return parts.Length == 3
            && int.TryParse(parts[0], out var channelId)
            && int.TryParse(parts[1], out var staffId)
            && parts[2].Length > 0
                ? new MailOAuthStateData(channelId, staffId, parts[2])
                : null;
    }

    /// <summary>RFC 7636 base64url (no padding) — the encoding PKCE mandates.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
