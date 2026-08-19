using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Requested API capability, mapped to the ApiKey permission flags
/// (admin/apikeys ak.permTickets / ak.permCron).</summary>
public enum ApiKeyService
{
    /// <summary>Create tickets over the JSON REST API (osTicket can_create_tickets).</summary>
    CreateTickets,
    /// <summary>Trigger background jobs remotely (osTicket can_exec_cron).</summary>
    TriggerJobs,
}

public enum ApiKeyAuthStatus
{
    Success,
    /// <summary>No key header / unknown key value.</summary>
    UnknownKey,
    /// <summary>Key exists but is disabled.</summary>
    Inactive,
    /// <summary>Key exists but the request came from a different address
    /// (ak.ipHelp: the key is only valid from its registered IP).</summary>
    IpMismatch,
    /// <summary>Key valid but lacks the requested service permission.</summary>
    Forbidden,
}

public sealed record ApiKeyAuthResult(ApiKeyAuthStatus Status, ApiKey? Key)
{
    public bool Succeeded => Status == ApiKeyAuthStatus.Success;
}

public interface IApiKeyAuthenticator
{
    /// <summary>Validates a presented key + caller address against the ApiKey
    /// table: the key must exist, be active, match its registered IP exactly and
    /// carry the requested service permission.</summary>
    Task<ApiKeyAuthResult> AuthenticateAsync(
        string? key, string? remoteIp, ApiKeyService service, CancellationToken ct = default);
}

/// <summary>
/// osTicket class.api parity, adapted to the JSON REST plan (§3 "adapted"): key
/// lookup is exact (keys are stored uppercase hex), the IP restriction is
/// mandatory and enforced here — this service is the single gate every public
/// API endpoint must call.
/// TODO(S8): no public REST endpoint exists yet (the dispatcher ships with the
/// mail/API stage) — until then the admin/apikeys page manages the keys and this
/// authenticator is the enforcement point the S8 endpoints consume.
/// </summary>
public sealed class ApiKeyAuthenticator(AppDbContext db) : IApiKeyAuthenticator
{
    public async Task<ApiKeyAuthResult> AuthenticateAsync(
        string? key, string? remoteIp, ApiKeyService service, CancellationToken ct = default)
    {
        key = key?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key))
            return new ApiKeyAuthResult(ApiKeyAuthStatus.UnknownKey, null);

        var found = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Key == key, ct);
        if (found is null)
            return new ApiKeyAuthResult(ApiKeyAuthStatus.UnknownKey, null);
        if (!found.IsActive)
            return new ApiKeyAuthResult(ApiKeyAuthStatus.Inactive, found);
        // Mandatory IP pin (ak.ipHelp): exact address match, no CIDR ranges —
        // osTicket api_key.ipaddr parity.
        if (!string.Equals(found.IpAddress, remoteIp?.Trim(), StringComparison.OrdinalIgnoreCase))
            return new ApiKeyAuthResult(ApiKeyAuthStatus.IpMismatch, found);

        var allowed = service switch
        {
            ApiKeyService.CreateTickets => found.CanCreateTickets,
            ApiKeyService.TriggerJobs => found.CanTriggerJobs,
            _ => false,
        };
        return new ApiKeyAuthResult(allowed ? ApiKeyAuthStatus.Success : ApiKeyAuthStatus.Forbidden, found);
    }

    /// <summary>Server-side key generation (ak.keyHelp "otomatik oluşturulur"):
    /// 128 crypto-random bits as 32 uppercase hex chars — the seeded canon format.</summary>
    public static string GenerateKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
