using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Typed view over the effort namespace (B8 settings gates, seeded keys).</summary>
public sealed record EffortSettings(
    bool Enabled,
    bool BlockWorkUntilApproved,
    bool MandatoryRejectNote,
    int ReminderDays,
    decimal AutoApproveThresholdHours,
    int RevisionLimit);

/// <summary>Typed view over a numbering namespace ("tickets" / "tasks").</summary>
public sealed record NumberingSettings(string NumberFormat, int SequenceId, string? DefaultStatusKey);

public interface ISettingsService
{
    Task<string?> GetAsync(string ns, string key, CancellationToken ct = default);
    Task SetAsync(string ns, string key, string value, CancellationToken ct = default);
    Task<EffortSettings> GetEffortAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads/writes the osTicket-style config table (<see cref="Setting"/>). Values are
/// read fresh per scope (scoped lifetime + per-namespace memo) so tests and the S7
/// admin pages observe their own writes immediately.
/// </summary>
public sealed class SettingsService(AppDbContext db) : ISettingsService
{
    private readonly Dictionary<string, Dictionary<string, string>> _memo = [];

    public async Task<string?> GetAsync(string ns, string key, CancellationToken ct = default)
    {
        var section = await LoadAsync(ns, ct);
        return section.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetAsync(string ns, string key, string value, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Namespace == ns && s.Key == key, ct);
        if (row is null)
        {
            row = new Setting { Namespace = ns, Key = key };
            db.Settings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _memo.Remove(ns);
    }

    public async Task<EffortSettings> GetEffortAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("effort", ct);
        return new EffortSettings(
            Enabled: Bool(s, "enabled", true),
            BlockWorkUntilApproved: Bool(s, "block_work_until_approved", false),
            MandatoryRejectNote: Bool(s, "mandatory_reject_note", true),
            ReminderDays: Int(s, "reminder_days", 3),
            AutoApproveThresholdHours: Decimal(s, "auto_approve_threshold_hours", 0),
            RevisionLimit: Int(s, "revision_limit", 3));
    }

    public Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default) =>
        GetNumberingAsync("tickets", "R######", ct);

    public Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default) =>
        GetNumberingAsync("tasks", "T-####", ct);

    private async Task<NumberingSettings> GetNumberingAsync(string ns, string fallbackFormat, CancellationToken ct)
    {
        var s = await LoadAsync(ns, ct);
        return new NumberingSettings(
            NumberFormat: s.GetValueOrDefault("number_format", fallbackFormat),
            SequenceId: Int(s, "sequence_id", 0),
            DefaultStatusKey: s.GetValueOrDefault("default_status"));
    }

    private async Task<Dictionary<string, string>> LoadAsync(string ns, CancellationToken ct)
    {
        if (_memo.TryGetValue(ns, out var cached))
            return cached;

        var section = await db.Settings.Where(s => s.Namespace == ns)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        _memo[ns] = section;
        return section;
    }

    private static bool Bool(Dictionary<string, string> s, string key, bool fallback) =>
        s.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int Int(Dictionary<string, string> s, string key, int fallback) =>
        s.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    private static decimal Decimal(Dictionary<string, string> s, string key, decimal fallback) =>
        s.TryGetValue(key, out var v) && decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
