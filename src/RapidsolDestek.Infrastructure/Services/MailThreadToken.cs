using System.Security.Cryptography;
using System.Text;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Signed reply-token codec for the S8 mail pipeline (osTicket Mailer::getMessageId /
/// decodeMessageId modeled, not copied). Outbound ticket mail is stamped with a
/// Message-Id of the form <c>RD1-{ticketId}-{rand}-{sig}@{domain}</c>; an inbound
/// In-Reply-To/References header carrying that shape and a valid HMAC signature
/// threads straight back to the ticket — no outbox-row lookup needed, so matching
/// survives the retention purge of old Sent rows (the EmailOutbounds.MessageId lookup
/// remains as a secondary path while rows live). The HMAC secret is generated once
/// and persisted as the mail/token_secret Setting (survives restarts; the signature
/// keeps third parties from forging thread injection via guessed ids —
/// osTicket SECRET_SALT parity).
/// </summary>
public interface IMailThreadTokenService
{
    /// <summary>Builds a signed Message-Id (without angle brackets) for a ticket.</summary>
    Task<string> CreateMessageIdAsync(int ticketId, string domain, CancellationToken ct = default);

    /// <summary>Ticket id when <paramref name="messageId"/> is one of ours (signature
    /// verified); null for foreign or tampered ids.</summary>
    Task<int?> TryDecodeTicketIdAsync(string messageId, CancellationToken ct = default);
}

public sealed class MailThreadTokenService(ISettingsService settings) : IMailThreadTokenService
{
    /// <summary>Version prefix of the token format (osTicket's leading version code).</summary>
    public const string Prefix = "RD1";

    private const string SecretNamespace = "mail";
    private const string SecretKey = "token_secret";

    private byte[]? _secret;

    public async Task<string> CreateMessageIdAsync(int ticketId, string domain, CancellationToken ct = default)
    {
        var secret = await SecretAsync(ct);
        var rand = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var host = string.IsNullOrWhiteSpace(domain) ? "rapidsoldestek.local" : domain.Trim();
        return $"{Prefix}-{ticketId}-{rand}-{Sign(secret, ticketId, rand)}@{host}";
    }

    public async Task<int?> TryDecodeTicketIdAsync(string messageId, CancellationToken ct = default)
    {
        var mid = messageId.Trim().Trim('<', '>');
        var at = mid.IndexOf('@');
        if (at > 0)
            mid = mid[..at];
        var parts = mid.Split('-');
        if (parts.Length != 4 || parts[0] != Prefix
            || !int.TryParse(parts[1], out var ticketId) || ticketId <= 0)
        {
            return null;
        }

        var secret = await SecretAsync(ct);
        var expected = Sign(secret, ticketId, parts[2]);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3]))
            ? ticketId
            : null;
    }

    private static string Sign(byte[] secret, int ticketId, string rand) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes($"{Prefix}-{ticketId}-{rand}")))[..12];

    /// <summary>Lazily creates the installation secret on first use (32 random bytes,
    /// hex in the settings table). A concurrent first-create races benignly: last
    /// write wins before any token built with the loser could have left the process.</summary>
    private async Task<byte[]> SecretAsync(CancellationToken ct)
    {
        if (_secret is not null)
            return _secret;
        var stored = await settings.GetAsync(SecretNamespace, SecretKey, ct);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await settings.SetAsync(SecretNamespace, SecretKey, stored, ct);
        }
        return _secret = Convert.FromHexString(stored);
    }
}
