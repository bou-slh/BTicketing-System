using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// S8 recurring inbound fetch (osTicket cron MailFetcher, modeled not copied). The
/// job ticks every minute; each active Mailbox channel decides its own cadence via
/// FetchFrequencyMinutes against LastActivityAt (osTicket fetchfreq). Master
/// switches: email/fetch_enabled kills fetching entirely, email/fetch_auto_cron
/// gates this scheduled path (both S7-persisted keys go LIVE here). Per channel:
/// connect through <see cref="IInboundMailClient"/> (MailKit IMAP/POP3; secrets via
/// IMailCredentialResolver), list up to FetchMax pending messages, hand each to
/// <see cref="InboundMailProcessor"/>, then apply the channel's persisted post-fetch
/// action (mark seen / archive-move / delete). Bookkeeping mirrors the S7 admin
/// email cluster: success stamps LastActivityAt and clears the error counters, a
/// connect/listing failure increments ErrorCount + LastErrorMessage/At and lands in
/// the syslog. A message the processor blows up on is left un-post-fetched (retried
/// next pass) and aborts the channel's pass — no poison-loop hammering.
/// </summary>
public sealed class MailFetchJob(
    AppDbContext db,
    ISettingsService settings,
    IInboundMailClient client,
    IMailCredentialResolver credentials,
    InboundMailProcessor processor,
    ISystemLogService syslog,
    TimeProvider clock,
    ILogger<MailFetchJob> logger)
{
    /// <summary>Every minute — the per-channel FetchFrequencyMinutes is the real cadence.</summary>
    public const string Cron = "* * * * *";

    public async Task RunAsync(CancellationToken ct)
    {
        var email = await settings.GetEmailAsync(ct);
        if (!email.FetchEnabled || !email.FetchAutoCron)
            return;

        var now = clock.GetUtcNow();
        var channels = await db.Set<EmailChannel>()
            .Include(c => c.EmailAccount)
            .Where(c => c.Kind == EmailChannelKind.Mailbox && c.IsActive && c.Host != "")
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        foreach (var channel in channels)
        {
            if (channel.EmailAccount is null
                || (channel.LastActivityAt is { } last
                    && last.AddMinutes(Math.Max(1, channel.FetchFrequencyMinutes)) > now))
            {
                continue; // not due yet
            }
            await FetchChannelAsync(channel, now, ct);
        }
    }

    private async Task FetchChannelAsync(EmailChannel channel, DateTimeOffset now, CancellationToken ct)
    {
        var account = channel.EmailAccount!;
        var processed = 0;
        try
        {
            await using var session = await client.ConnectAsync(new InboundConnection(
                channel.Protocol, channel.Host, channel.Port, channel.AuthKind,
                channel.Username, credentials.ResolvePassword(channel), channel.Folder), ct);

            foreach (var uid in await session.ListPendingUidsAsync(Math.Max(1, channel.FetchMax), ct))
            {
                // Cheap pre-check; the processor re-guards (Message-Id backstop).
                if (await db.EmailInbounds.AnyAsync(i => i.EmailChannelId == channel.Id && i.Uid == uid, ct))
                {
                    await session.ApplyPostFetchAsync(uid, channel.PostFetch, channel.ArchiveFolder, ct);
                    continue;
                }

                var message = await session.DownloadAsync(uid, ct);
                await processor.ProcessAsync(account, channel, uid, message, ct);
                processed++;
                // Post-fetch only after the outcome is bookkept — a crash between
                // the two re-processes (and dedupes) rather than losing the mail.
                await session.ApplyPostFetchAsync(uid, channel.PostFetch, channel.ArchiveFolder, ct);
            }

            channel.LastActivityAt = now;
            channel.ErrorCount = 0;
            channel.LastErrorMessage = null;
            await db.SaveChangesAsync(ct);
            logger.LogDebug("Mail fetch {Address}: {Count} messages processed", account.Address, processed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            channel.ErrorCount++;
            channel.LastErrorMessage = ex.Message;
            channel.LastErrorAt = now;
            channel.LastActivityAt = now; // failed pass still consumed the slot — retry next cadence
            await db.SaveChangesAsync(CancellationToken.None);
            await syslog.LogAsync(SystemLogType.Error,
                $"Posta getirme hatası ({account.Address})", ex.Message, logger: "mail",
                ct: CancellationToken.None);
            logger.LogWarning(ex, "Mail fetch failed for {Address} ({Processed} processed before failure)",
                account.Address, processed);
        }
    }
}
