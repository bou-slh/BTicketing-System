using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Areas.Portal.Controllers;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// S8 slice 5 — the signed links the mail pipeline sends, in one place.
///
/// Two shapes, deliberately different:
/// <list type="bullet">
/// <item><b>Ticket access</b> (osTicket <c>allow_auth_tokens</c>): the auto-login link
/// a customer follows from ticket mail straight into the portal ticket view. It is the
/// S5 check-status guest token, unchanged and reused — same DataProtection purpose,
/// same payload, validated by the same portal ticket-view guard — only the lifetime differs
/// (<see cref="TicketLinkLifetime"/>: a mail sits in an inbox far longer than the
/// one-hour check-status hand-off). Stateless and therefore replayable while it lives,
/// which is what a link in a mail has to be. osTicket's authtoken never expires and
/// grants full client access; ours expires and is read-only — flagged.</item>
/// <item><b>One-shot grants</b> (email verification, guest invitations): the protected
/// payload carries a jti recorded as a <see cref="MailToken"/> row, so redemption is
/// single-use and revocable. Purpose is inside the signature AND re-checked on the
/// row, so a verification token can never be presented as an invitation.</item>
/// </list>
/// </summary>
public interface IMailLinkTokenService
{
    /// <summary>
    /// Absolute auto-login URL for a ticket, or the plain ticket URL when
    /// users/auth_tokens is off. <paramref name="baseUrl"/> is core/helpdesk_url.
    /// </summary>
    Task<string> TicketLinkAsync(int ticketId, string baseUrl, CancellationToken ct = default);

    /// <summary>Issues a single-use token for the user and records its ledger row.
    /// Any outstanding token of the SAME purpose for that user is consumed first, so a
    /// resend invalidates the previous link (osTicket reissue semantics).</summary>
    Task<string> IssueAsync(MailTokenPurpose purpose, int userId, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Validates and CONSUMES a one-shot token: null for a tampered/foreign token, a
    /// wrong purpose, an expired one, or one already redeemed. The consuming update is
    /// conditional on the row still being unused, so two simultaneous redemptions
    /// cannot both win.
    /// </summary>
    Task<int?> RedeemAsync(MailTokenPurpose purpose, string? token, CancellationToken ct = default);

    /// <summary>Validity check WITHOUT consuming — for rendering the set-password /
    /// confirmation form before the user posts it back.</summary>
    Task<int?> PeekAsync(MailTokenPurpose purpose, string? token, CancellationToken ct = default);
}

public sealed class MailLinkTokenService(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    Infrastructure.Services.ISettingsService settings) : IMailLinkTokenService
{
    /// <summary>How long a mailed ticket auto-login link stays usable. The S5
    /// check-status hand-off is one hour because the requester is at the keyboard;
    /// a link inside a notification mail is read whenever the customer gets to it.</summary>
    public static readonly TimeSpan TicketLinkLifetime = TimeSpan.FromDays(7);

    /// <summary>Registration verification link lifetime (osTicket mails a link with no
    /// stated expiry; a bounded one plus the resend path is the honest equivalent).</summary>
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromDays(2);

    /// <summary>Invitation lifetime — longer than verification: the invitee did not
    /// ask for the mail and may not act on it the same day.</summary>
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    private const string OneShotPurpose = "RapidsolDestek.Mail.OneShotToken";

    public async Task<string> TicketLinkAsync(int ticketId, string baseUrl, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/ticket-view?id={ticketId}";
        if (!(await settings.GetUsersAsync(ct)).AuthTokens)
            return url;
        var token = TicketViewController.CreateGuestToken(dataProtection, ticketId, TicketLinkLifetime);
        return $"{url}&token={Uri.EscapeDataString(token)}";
    }

    public async Task<string> IssueAsync(MailTokenPurpose purpose, int userId, TimeSpan lifetime,
        CancellationToken ct = default)
    {
        // Reissue invalidates the outstanding one: two live links for the same grant
        // would make "single-use" meaningless.
        await db.MailTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, DateTimeOffset.UtcNow), ct);

        var row = new MailToken
        {
            Jti = Guid.NewGuid(),
            Purpose = purpose,
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime),
        };
        db.MailTokens.Add(row);
        await db.SaveChangesAsync(ct);

        return dataProtection.CreateProtector(OneShotPurpose).Protect(
            FormattableString.Invariant($"{(int)purpose}|{row.Jti:N}|{userId}|{row.ExpiresAt.UtcTicks}"));
    }

    public async Task<int?> RedeemAsync(MailTokenPurpose purpose, string? token, CancellationToken ct = default)
    {
        if (Unprotect(purpose, token) is not { } decoded)
            return null;
        var (jti, userId) = decoded;

        // Conditional consume: the WHERE carries the "still unused" test, so the
        // update either claims the token or reports zero rows — no read-then-write race.
        var claimed = await db.MailTokens
            .Where(t => t.Jti == jti && t.Purpose == purpose && t.ConsumedAt == null
                && t.ExpiresAt > DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, DateTimeOffset.UtcNow), ct);
        return claimed == 1 ? userId : null;
    }

    public async Task<int?> PeekAsync(MailTokenPurpose purpose, string? token, CancellationToken ct = default)
    {
        if (Unprotect(purpose, token) is not { } decoded)
            return null;
        var (jti, userId) = decoded;
        return await db.MailTokens.AnyAsync(
            t => t.Jti == jti && t.Purpose == purpose && t.ConsumedAt == null
                && t.ExpiresAt > DateTimeOffset.UtcNow, ct)
            ? userId
            : null;
    }

    /// <summary>Signature + shape + purpose + expiry checks on the payload alone.
    /// Returns null unless every one of them holds.</summary>
    private (Guid Jti, int UserId)? Unprotect(MailTokenPurpose purpose, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        string payload;
        try
        {
            payload = dataProtection.CreateProtector(OneShotPurpose).Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null; // forged, truncated, or signed by a rotated-out key
        }

        var parts = payload.Split('|');
        return parts.Length == 4
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var kind) && kind == (int)purpose
            && Guid.TryParseExact(parts[1], "N", out var jti)
            && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var userId)
            && long.TryParse(parts[3], CultureInfo.InvariantCulture, out var expiry)
            && new DateTimeOffset(expiry, TimeSpan.Zero) > DateTimeOffset.UtcNow
            ? (jti, userId)
            : null;
    }
}
