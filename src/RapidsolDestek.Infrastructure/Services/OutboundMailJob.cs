using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>A queued send failed at the SMTP transport; thrown so Hangfire's
/// AutomaticRetry reschedules the job (the outbox row already carries the stage).</summary>
public sealed class MailSendException(int outboundId, string error)
    : Exception($"Outbound mail {outboundId} failed: {error}");

/// <summary>
/// The Hangfire send job behind <see cref="IMailQueue"/>: resolves the transport,
/// submits via <see cref="ISmtpMailTransport"/> (MailKit) and drives the outbox row's
/// state machine. Retry/backoff is Hangfire's AutomaticRetry (honest choice — no
/// hand-rolled scheduler): a transport failure updates the row (Attempts+1, LastError,
/// back to Pending) and throws, Hangfire reschedules with the declared delays, and
/// the last allowed execution marks Failed without throwing. Transport selection:
/// explicit from-account (dept address) → email/default_smtp ("system" =
/// email/default_email_id, else an account id) → no usable SMTP account at all falls
/// back to the Development logging sender (row Sent + note) or, without a fallback
/// (production), an honest Failed. Per-row message shaping (S8 slice 5): ticket mail
/// gets Reply-To = the sending mailbox, <see cref="EmailOutbound.IsAutomated"/> decides
/// the RFC 3834 Auto-Submitted header, and <see cref="EmailOutbound.IncludeAttachments"/>
/// pulls the thread entry's files onto the message.
/// </summary>
public sealed class OutboundMailJob(
    AppDbContext db,
    ISettingsService settings,
    ISmtpMailTransport transport,
    IMailCredentialResolver credentials,
    IMailOAuthTokenService oauth,
    ISystemLogService syslog,
    IMailThreadTokenService threadToken,
    Domain.Services.IFileStore files,
    ILogger<OutboundMailJob> logger,
    IMailFallbackSender? fallback = null)
{
    /// <summary>Total executions before a row is Failed for good (1 + 4 retries —
    /// keep in sync with the AutomaticRetry attribute below).</summary>
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts - 1,
        DelaysInSeconds = new[] { 60, 300, 900, 3600 },
        OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task SendAsync(int outboundId, CancellationToken ct)
    {
        var row = await db.EmailOutbounds.SingleOrDefaultAsync(o => o.Id == outboundId, ct);
        if (row is null || row.Status == EmailOutboundStatus.Sent)
            return; // deleted or already delivered (job replay) — nothing to do

        row.Status = EmailOutboundStatus.Sending;
        row.Attempts++;
        await db.SaveChangesAsync(ct);

        var selected = await ResolveTransportAsync(row, ct);
        if (selected is null)
        {
            if (fallback is not null)
            {
                // Dev has no real SMTP: log instead of send, mark honestly.
                await fallback.SendAsync(row.ToAddress, row.CcAddresses, row.Subject, row.HtmlBody, ct);
                row.Status = EmailOutboundStatus.Sent;
                row.SentAt = DateTimeOffset.UtcNow;
                row.LastError = "dev-fallback: no usable SMTP account configured; mail logged, not sent";
            }
            else
            {
                row.Status = EmailOutboundStatus.Failed;
                row.LastError = "no-transport: no usable SMTP account configured";
                logger.LogWarning("Outbound mail {Id} has no usable SMTP transport; marked Failed", row.Id);
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var (account, smtp) = selected.Value;
        // From display name: account name, sender fallback = helpdesk_title.
        var fromName = string.IsNullOrWhiteSpace(account.DisplayName)
            ? await settings.GetAsync("core", "helpdesk_title", ct) ?? "RapidsolDestek"
            : account.DisplayName;

        // S8 inbound slice: ticket mail carries the signed reply token as its
        // Message-Id (inbound In-Reply-To/References thread back to the ticket) and
        // References the last inbound mail of the ticket so the recipient's client
        // threads our reply correctly. Non-ticket mail keeps a plain generated id.
        string? messageId = null;
        string? references = null;
        if (row.TicketId is { } ticketId)
        {
            var at = account.Address.IndexOf('@');
            messageId = await threadToken.CreateMessageIdAsync(
                ticketId, at >= 0 ? account.Address[(at + 1)..] : "", ct);
            row.MessageId = messageId; // persisted with the outcome save below
            references = await db.EmailInbounds
                .Where(i => i.TicketId == ticketId && i.MessageId != null)
                .OrderByDescending(i => i.Id).Select(i => i.MessageId).FirstOrDefaultAsync(ct);
        }

        // S8 slice 6: an OAuth2 SMTP channel needs a live access token before the
        // transport runs. A dead grant is not a transport failure — it is channel
        // state, so it gets the mailbox channels' bookkeeping (ErrorCount +
        // LastErrorMessage/At) and a syslog Error row, and a "consent" failure stops
        // retrying immediately (only an admin re-authorization can fix it).
        string? accessToken = null;
        if (smtp.AuthKind == MailAuthKind.OAuth2)
        {
            try
            {
                accessToken = await oauth.GetAccessTokenAsync(smtp, ct);
            }
            catch (MailOAuthException ex)
            {
                await RecordChannelOAuthFailureAsync(smtp.Id, account.Address, ex, ct);
                row.LastError = $"{ex.Stage}: {ex.Message}";
                var giveUp = ex.Stage != MailOAuthException.RefreshStage || row.Attempts >= MaxAttempts;
                row.Status = giveUp ? EmailOutboundStatus.Failed : EmailOutboundStatus.Pending;
                await db.SaveChangesAsync(ct);
                if (!giveUp)
                    throw new MailSendException(row.Id, row.LastError);
                return;
            }
        }

        var result = await transport.SendAsync(
            new SmtpTransportSettings(smtp.Host, smtp.Port, smtp.AuthKind, smtp.Username,
                credentials.ResolvePassword(smtp), accessToken),
            new OutboundSmtpMessage(account.Address, fromName,
                row.ToAddress, row.CcAddresses, row.Subject, row.HtmlBody,
                messageId, references)
            {
                // Ticket mail advertises the fetched mailbox as the reply target so a
                // customer answer reaches the inbound pipeline; account mail (resets,
                // access links) has no thread to return to.
                ReplyTo = row.TicketId is null ? null : account.Address,
                AutoSubmitted = row.IsAutomated,
                Attachments = row.IncludeAttachments && row.ThreadEntryId is { } entryId
                    ? await LoadAttachmentsAsync(entryId, ct)
                    : [],
            }, ct);

        if (result.Success)
        {
            row.Status = EmailOutboundStatus.Sent;
            row.SentAt = DateTimeOffset.UtcNow;
            row.LastError = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        row.LastError = string.IsNullOrEmpty(result.Detail) ? result.Stage : $"{result.Stage}: {result.Detail}";
        var exhausted = row.Attempts >= MaxAttempts;
        row.Status = exhausted ? EmailOutboundStatus.Failed : EmailOutboundStatus.Pending;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Outbound mail {Id} attempt {Attempt} failed at {Stage}: {Detail}",
            row.Id, row.Attempts, result.Stage, result.Detail);

        if (!exhausted)
            throw new MailSendException(row.Id, row.LastError); // AutomaticRetry reschedules
    }

    /// <summary>
    /// Channel error bookkeeping for an OAuth2 send failure, mirroring what
    /// MailFetchJob stamps on the mailbox side so both directions of an address report
    /// a dead grant the same way (admin/emails surfaces ErrorCount/LastErrorMessage).
    /// ExecuteUpdate keeps the outbox row's own pending changes out of it.
    /// </summary>
    private async Task RecordChannelOAuthFailureAsync(
        int channelId, string address, MailOAuthException ex, CancellationToken ct)
    {
        var message = $"{ex.Stage}: {ex.Message}";
        var now = (DateTimeOffset?)DateTimeOffset.UtcNow;
        await db.Set<EmailChannel>().Where(c => c.Id == channelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ErrorCount, c => c.ErrorCount + 1)
                .SetProperty(c => c.LastErrorMessage, message)
                .SetProperty(c => c.LastErrorAt, now), ct);
        await syslog.LogAsync(SystemLogType.Error,
            $"OAuth2 gönderim kimlik doğrulaması başarısız ({address})", message,
            logger: "mail", ct: CancellationToken.None);
        logger.LogWarning(ex, "Outbound OAuth2 authentication failed for {Address}", address);
    }

    /// <summary>
    /// The thread entry's stored files, decoded for the transport (S8 slice 5,
    /// email/attachments_in_email). Inline parts are attached too — the mail body is
    /// the sanitized entry HTML, whose inline references are already stripped, so an
    /// inline image would otherwise vanish. A file missing from the store is skipped
    /// with a warning: an unsendable attachment must not lose the reply itself.
    /// </summary>
    private async Task<IReadOnlyList<OutboundAttachment>> LoadAttachmentsAsync(int entryId, CancellationToken ct)
    {
        var rows = await db.Attachments
            .Where(a => a.ObjectType == AttachmentObjectType.ThreadEntry && a.ObjectId == entryId)
            .Join(db.StoredFiles, a => a.FileId, f => f.Id, (a, f) => new { Name = a.Name ?? f.Name, File = f })
            .AsNoTracking()
            .ToListAsync(ct);

        var list = new List<OutboundAttachment>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                await using var stream = await files.OpenAsync(row.File, ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                list.Add(new OutboundAttachment(row.Name, row.File.MimeType, buffer.ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Attachment {File} of entry {EntryId} could not be read; mail sent without it",
                    row.File.Id, entryId);
            }
        }
        return list;
    }

    /// <summary>Explicit account → default_smtp account id → "system" default_email_id;
    /// first candidate with an active, host-complete SMTP channel wins.</summary>
    private async Task<(EmailAccount Account, EmailChannel Smtp)?> ResolveTransportAsync(
        EmailOutbound row, CancellationToken ct)
    {
        var email = await settings.GetEmailAsync(ct);
        var candidates = new List<int>();
        if (row.FromEmailAccountId is { } explicitId)
            candidates.Add(explicitId);
        if (int.TryParse(email.DefaultSmtp, out var defaultSmtpId) && defaultSmtpId > 0)
            candidates.Add(defaultSmtpId);
        else if (email.DefaultEmailAccountId > 0) // "system"
            candidates.Add(email.DefaultEmailAccountId);

        foreach (var id in candidates.Distinct())
        {
            var account = await db.EmailAccounts.Include(a => a.Channels)
                .AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
            var smtp = account?.Channels.FirstOrDefault(c =>
                c.Kind == EmailChannelKind.Smtp && c.IsActive
                && !string.IsNullOrWhiteSpace(c.Host) && c.Port is >= 1 and <= 65535);
            if (account is not null && smtp is not null)
                return (account, smtp);
        }
        return null;
    }
}
