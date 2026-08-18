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

/// <summary>
/// Typed view over the "tickets" behavior namespace (admin/settings-tickets, S7).
/// Defaults follow osTicket where the engine consumes the key; mockup checked
/// states are sample data. Keys without an engine consumer yet are annotated at
/// their read site in <c>SettingsTicketsController</c>.
/// </summary>
public sealed record TicketBehaviorSettings(
    string LockMode,            // "disabled" | "view" | "activity" — TODO(S7+): consumed by the composer lock UI (IThreadService lock API exists, no composer wiring yet)
    int MaxOpenPerUser,         // 0 = unlimited; enforced in TicketService.CreateAsync for end-user creates
    bool Captcha,               // TODO(S9): consumed by guest/register rate limiting (§3 captcha row)
    bool ClaimOnResponse,       // enforced in ThreadService.PostAsync (staff response auto-claims)
    bool AutoReferOnClose,      // TODO(S7+): consumed by the referral mechanism (ticket-view Yönlendirmeler)
    bool RequireTopicToClose,   // enforced in TicketService.TransitionStatusAsync
    bool AllowExternalImages,   // TODO(S8): consumed by the email thread renderer
    bool CollabVisibility,      // TODO: portal collaborator scope — open decision #7.9 (ROADMAP)
    bool TopLevelCounts,        // enforced by the agent tickets queue-tree count badges
    int DefaultQueueId);        // 0 = built-in default; enforced by agent tickets initial queue

public interface ISettingsService
{
    Task<string?> GetAsync(string ns, string key, CancellationToken ct = default);
    Task SetAsync(string ns, string key, string value, CancellationToken ct = default);

    /// <summary>Whole namespace snapshot (admin settings pages render + save per section).</summary>
    Task<IReadOnlyDictionary<string, string>> GetSectionAsync(string ns, CancellationToken ct = default);

    Task<EffortSettings> GetEffortAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTicketNumberingAsync(CancellationToken ct = default);
    Task<NumberingSettings> GetTaskNumberingAsync(CancellationToken ct = default);
    Task<TicketBehaviorSettings> GetTicketBehaviorAsync(CancellationToken ct = default);
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

    public async Task<IReadOnlyDictionary<string, string>> GetSectionAsync(string ns, CancellationToken ct = default) =>
        await LoadAsync(ns, ct);

    public async Task<TicketBehaviorSettings> GetTicketBehaviorAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync("tickets", ct);
        return new TicketBehaviorSettings(
            LockMode: s.GetValueOrDefault("lock_mode", "activity"),
            MaxOpenPerUser: Int(s, "max_open_per_user", 0),
            Captcha: Bool(s, "captcha", true),
            ClaimOnResponse: Bool(s, "claim_on_response", true),
            AutoReferOnClose: Bool(s, "auto_refer_on_close", false),
            // osTicket parity: "require help topic to close" ships off; the mockup's
            // checked switch is sample state (flagged on the ROADMAP row).
            RequireTopicToClose: Bool(s, "require_topic_to_close", false),
            AllowExternalImages: Bool(s, "allow_external_images", false),
            CollabVisibility: Bool(s, "collab_visibility", true),
            TopLevelCounts: Bool(s, "top_level_counts", true),
            DefaultQueueId: Int(s, "default_queue_id", 0));
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
