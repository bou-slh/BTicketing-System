using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Writes <see cref="SystemLogEntry"/> rows (osTicket syslog) shown on
/// admin/system-logs. The system/log_level Setting (admin/settings-system
/// ss.logLevel — LIVE since the S7 system-logs port, resolving that page's TODO)
/// decides which levels are stored, cumulative like osTicket's log_level:
/// "none" stores nothing, "error" only errors, "warn" errors+warnings,
/// "debug" everything. Callers today (honest wiring, small on purpose —
/// scope flagged on the ROADMAP row): staff+portal login failures/lockouts
/// (auth) and the system-logs purge itself (syslog). The mail pipeline joins
/// in S8. Titles/messages are stored DATA in product Turkish (sample-data
/// convention), not UI strings.
/// </summary>
public interface ISystemLogService
{
    Task LogAsync(
        SystemLogType type,
        string title,
        string message = "",
        string? ip = null,
        string? logger = null,
        CancellationToken ct = default);
}

public sealed class SystemLogService(AppDbContext db, ISettingsService settings) : ISystemLogService
{
    public async Task LogAsync(
        SystemLogType type, string title, string message = "", string? ip = null,
        string? logger = null, CancellationToken ct = default)
    {
        // Unknown/empty stored values fall back to the "warn" default (the
        // settings-system save validates the choices; raw rows may still be blank).
        var raw = await settings.GetAsync("system", "log_level", ct);
        var level = raw is "none" or "error" or "warn" or "debug" ? raw : "warn";
        var stored = type switch
        {
            SystemLogType.Error => level is "error" or "warn" or "debug",
            SystemLogType.Warning => level is "warn" or "debug",
            _ => level is "debug",
        };
        if (!stored)
            return;

        db.SystemLogEntries.Add(new SystemLogEntry
        {
            Type = type,
            Title = title,
            Log = message,
            Logger = logger,
            IpAddress = ip,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
