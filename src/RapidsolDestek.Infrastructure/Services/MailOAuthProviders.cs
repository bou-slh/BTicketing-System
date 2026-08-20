using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Endpoints + authorize-request quirks of one supported identity provider
/// (S8 slice 6). Presets rather than free-form authority strings: the XOAUTH2
/// dialect an IMAP/SMTP server accepts is provider-specific, so an unverifiable
/// "custom" option would be a false promise (ROADMAP email-cluster note).
/// </summary>
public sealed record MailOAuthPreset(
    string AuthorizeEndpoint,
    string TokenEndpoint,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParameters);

/// <summary>
/// The two providers helpdesk mailboxes actually live on. Microsoft 365 uses the
/// Entra ID v2.0 endpoints under a tenant authority; Google Workspace uses the
/// Google Identity endpoints. Scope defaults are DELEGATED (authorization-code)
/// scopes — deliberately explicit rather than the <c>.default</c> form, which is a
/// client-credentials/static-consent idiom and would ask for every permission ever
/// granted to the app registration instead of the one protocol this channel needs.
/// </summary>
public static class MailOAuthProviders
{
    /// <summary>Tenant used when the channel leaves <c>OAuthTenant</c> empty:
    /// multi-tenant + personal accounts (Entra's own default authority).</summary>
    public const string DefaultMicrosoftTenant = "common";

    /// <summary>Microsoft's mail resource for delegated IMAP/POP/SMTP scopes.</summary>
    private const string OutlookResource = "https://outlook.office.com/";

    /// <summary>Google's full-mailbox scope (the only one Gmail's IMAP/SMTP
    /// XOAUTH2 accepts — the narrower gmail.send/gmail.readonly scopes are REST-only).</summary>
    private const string GmailScope = "https://mail.google.com/";

    /// <summary>OIDC scopes requested alongside the mail scopes: <c>offline_access</c>
    /// (Microsoft) is what makes the code exchange return a refresh token at all, and
    /// <c>openid email</c> gives us the id_token whose email claim we store as
    /// evidence of WHICH mailbox an admin consented for.</summary>
    private const string MicrosoftBaseScopes = "offline_access openid email";

    private const string GoogleBaseScopes = "openid email";

    public static MailOAuthPreset For(MailOAuthProvider provider, string? tenant) => provider switch
    {
        MailOAuthProvider.Google => new MailOAuthPreset(
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            // Google only issues a refresh token for an offline-access request, and
            // only re-issues one when consent is forced; without both, a second
            // consent silently yields an access token we cannot renew.
            new Dictionary<string, string>
            {
                ["access_type"] = "offline",
                ["prompt"] = "consent",
            }),
        _ => new MailOAuthPreset(
            $"https://login.microsoftonline.com/{Tenant(tenant)}/oauth2/v2.0/authorize",
            $"https://login.microsoftonline.com/{Tenant(tenant)}/oauth2/v2.0/token",
            new Dictionary<string, string>
            {
                ["response_mode"] = "query",
                // Force the consent screen so a re-authorization of a revoked grant
                // actually returns a fresh refresh token instead of silently reusing
                // the dead one.
                ["prompt"] = "consent",
            }),
    };

    /// <summary>
    /// Scopes for one channel: its stored override when set, else the preset's
    /// per-protocol defaults. The mailbox channel asks for IMAP or POP, the SMTP
    /// channel for SMTP.Send — the two channels of one address consent separately,
    /// so a compromised send credential cannot read the mailbox.
    /// </summary>
    public static string ScopesFor(EmailChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.OAuthScopes))
            return channel.OAuthScopes.Trim();

        if (channel.OAuthProvider == MailOAuthProvider.Google)
            return $"{GoogleBaseScopes} {GmailScope}";

        var resourceScope = channel.Kind == EmailChannelKind.Smtp
            ? "SMTP.Send"
            : channel.Protocol == MailProtocol.Pop
                ? "POP.AccessAsUser.All"
                : "IMAP.AccessAsUser.All";
        return $"{MicrosoftBaseScopes} {OutlookResource}{resourceScope}";
    }

    /// <summary>
    /// The provider URL an admin is redirected to when consent starts. PKCE (S256) is
    /// always sent: both providers support it, and it removes the value of an
    /// intercepted authorization code even though this is a confidential client.
    /// </summary>
    public static string BuildAuthorizeUrl(
        EmailChannel channel, string redirectUri, string state, string codeChallenge)
    {
        var preset = For(channel.OAuthProvider, channel.OAuthTenant);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = channel.OAuthClientId ?? "",
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = ScopesFor(channel),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Pre-selects the mailbox on the consent screen when we already know it
            // (re-consent after a revoke); harmless on a first authorization.
            ["login_hint"] = channel.OAuthConsentAccount ?? channel.Username ?? "",
        };
        foreach (var (key, value) in preset.ExtraAuthorizeParameters)
            query[key] = value;

        var encoded = string.Join('&', query
            .Where(p => p.Value.Length > 0)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{preset.AuthorizeEndpoint}?{encoded}";
    }

    private static string Tenant(string? tenant) =>
        string.IsNullOrWhiteSpace(tenant) ? DefaultMicrosoftTenant : Uri.EscapeDataString(tenant.Trim());
}
