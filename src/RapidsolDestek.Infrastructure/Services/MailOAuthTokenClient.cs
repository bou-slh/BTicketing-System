using System.Text.Json;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// One RFC 6749 token-endpoint POST (authorization_code or refresh_token). The form
/// is built by the caller so the two grant shapes stay readable at the call site.
/// </summary>
public sealed record MailOAuthTokenRequest(
    string TokenEndpoint,
    IReadOnlyDictionary<string, string> Form);

/// <summary>
/// The token endpoint's answer, normalized. On failure <see cref="Error"/> carries the
/// provider's OAuth2 error code — <c>invalid_grant</c> is the one that matters: it means
/// the refresh token was revoked/expired and only a fresh admin consent can fix it.
/// </summary>
public sealed record MailOAuthTokenResponse(
    bool Success,
    string? AccessToken = null,
    string? RefreshToken = null,
    int ExpiresInSeconds = 0,
    string? IdToken = null,
    string? Error = null,
    string? ErrorDescription = null)
{
    /// <summary>Provider says the grant itself is dead — re-consent required.</summary>
    public bool IsInvalidGrant => string.Equals(Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The HTTP seam in front of the provider token endpoints (S8 slice 6). Tests script
/// responses here instead of talking to Microsoft/Google — no live provider call ever
/// happens in the suite.
/// </summary>
public interface IMailOAuthTokenClient
{
    Task<MailOAuthTokenResponse> RequestAsync(MailOAuthTokenRequest request, CancellationToken ct = default);
}

/// <summary>
/// Hand-rolled token client (documented dependency decision, ROADMAP email cluster):
/// the whole provider contract we need is two form-encoded POSTs and a JSON body, so
/// MSAL / Google.Apis.Auth would add a large dependency plus their own token caches —
/// caches we could not use anyway, because our tokens must live in the
/// DataProtection-encrypted EmailChannel columns and be shared across Hangfire workers.
/// A thin client also keeps the test seam trivial.
/// </summary>
public sealed class HttpMailOAuthTokenClient(HttpClient http) : IMailOAuthTokenClient
{
    /// <summary>Named HttpClient registration key (Program.cs).</summary>
    public const string HttpClientName = "mail-oauth";

    public async Task<MailOAuthTokenResponse> RequestAsync(
        MailOAuthTokenRequest request, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        string payload;
        try
        {
            using var content = new FormUrlEncodedContent(request.Form);
            response = await http.PostAsync(request.TokenEndpoint, content, ct);
            payload = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Network-level failure is NOT invalid_grant: the grant may be perfectly
            // fine, so the caller must retry rather than demand re-consent.
            return new MailOAuthTokenResponse(false, Error: "network", ErrorDescription: ex.Message);
        }

        using (response)
        {
            try
            {
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (!response.IsSuccessStatusCode)
                {
                    return new MailOAuthTokenResponse(false,
                        Error: Str(root, "error") ?? $"http_{(int)response.StatusCode}",
                        ErrorDescription: Str(root, "error_description"));
                }

                var accessToken = Str(root, "access_token");
                if (string.IsNullOrEmpty(accessToken))
                    return new MailOAuthTokenResponse(false, Error: "no_access_token");

                return new MailOAuthTokenResponse(true,
                    accessToken,
                    Str(root, "refresh_token"),
                    root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                        ? seconds
                        : 3600, // provider omitted it — assume the common one-hour life
                    Str(root, "id_token"));
            }
            catch (JsonException)
            {
                return new MailOAuthTokenResponse(false,
                    Error: response.IsSuccessStatusCode ? "bad_response" : $"http_{(int)response.StatusCode}");
            }
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
