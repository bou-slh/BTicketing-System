using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// S8 daily retention job (osTicket cron PurgeLogs → $ost-&gt;purgeLogs, modeled):
/// system log rows older than system/log_purge_months are deleted; 0 (mockup "Asla")
/// keeps everything. The same months knob also prunes SENT outbox rows
/// (EmailOutbound) older than the cutoff — a deliberate decision: osTicket has no
/// outbox at all, and one retention dial is honest where two invented ones are not
/// (Pending/Failed rows are never purged — they are open work / evidence). The purge
/// reports itself to the syslog as a debug event (admin system-logs delete
/// precedent), stored only while system/log_level is "debug".
/// </summary>
public sealed class RetentionPurgeJob(
    AppDbContext db,
    ISettingsService settings,
    ISystemLogService syslog,
    TimeProvider clock,
    ILogger<RetentionPurgeJob> logger)
{
    /// <summary>Daily at 03:30 (Hangfire default UTC) — off-peak housekeeping.</summary>
    public const string Cron = "30 3 * * *";

    public async Task RunAsync(CancellationToken ct)
    {
        var raw = await settings.GetAsync("system", "log_purge_months", ct);
        var months = int.TryParse(raw, out var m) ? m : 3; // settings-system default
        if (months <= 0)
            return; // "Asla" — keep forever

        var cutoff = clock.GetUtcNow().AddMonths(-months);
        var logs = await db.SystemLogEntries
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        var mails = await db.EmailOutbounds
            .Where(o => o.Status == EmailOutboundStatus.Sent && o.SentAt != null && o.SentAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (logs == 0 && mails == 0)
            return;

        await syslog.LogAsync(SystemLogType.Debug,
            $"Otomatik kayıt temizliği ({logs} günlük kaydı, {mails} e-posta kaydı)",
            $"{months} aydan eski kayıtlar silindi.", logger: "retention", ct: ct);
        logger.LogInformation("Retention purge: {Logs} log rows, {Mails} sent outbox rows deleted", logs, mails);
    }
}
