using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

public sealed record SettingsTasksVm(
    string NumberFormat,
    string NumberMode,                     // "sequential" | "random"
    IReadOnlyList<SequenceRowVm> Sequences,
    string DefaultPriority,
    IReadOnlyList<string> PriorityKeys,
    IReadOnlyDictionary<string, bool> Alerts);

/// <summary>
/// Admin task settings (mockups/admin/settings-tasks.html, ROADMAP §6.3 + §4 B3/B4):
/// the settings-tickets treatment — every control persists into the Setting table
/// through <see cref="ISettingsService"/>. LIVE: number format + sequence mode
/// (TaskService.DrawNumberAsync) and the dlg-seq sequence CRUD (the settings-tickets
/// SequenceNumberService.SaveAsync with the same guards) and the five task alert
/// masters + recipient checkboxes (S8 TaskMailHandler fan-out). Persisted-only:
/// default priority (TaskItem has no priority column — TaskSettings annotation).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsTasksController(
    AppDbContext db,
    ISettingsService settings,
    ISequenceNumberService sequences) : Controller
{
    // ---- task alert switches (ns "alerts", task_* keys — no collision with the
    // ticket alert keys); ALL LIVE (S8): TaskMailHandler fan-out over the seeded
    // task.* templates (task.alert / task.activity.alert / task.assigned.alert /
    // task.transfer.alert / task.overdue.alert — the full catalog set is seeded).
    // Internal: the handler reads the same key defaults (settings-tickets precedent).
    // Defaults mirror the mockup's checked states.
    internal static readonly (string Field, string Key, bool Default)[] AlertsMap =
    [
        ("tal_new", "task_new", true),
        ("tal_new_admin", "task_new_admin", true),
        ("tal_new_dept_manager", "task_new_dept_manager", true),
        ("tal_new_dept_members", "task_new_dept_members", false),
        ("tal_activity", "task_activity", true),
        ("tal_activity_last_respondent", "task_activity_last_respondent", true),
        ("tal_activity_assigned", "task_activity_assigned", true),
        ("tal_activity_dept_manager", "task_activity_dept_manager", false),
        ("tal_assignment", "task_assignment", true),
        ("tal_assignment_assigned", "task_assignment_assigned", true),
        ("tal_assignment_team_lead", "task_assignment_team_lead", true),
        ("tal_assignment_team_members", "task_assignment_team_members", false),
        ("tal_transfer", "task_transfer", true),
        ("tal_transfer_assigned", "task_transfer_assigned", true),
        ("tal_transfer_dept_manager", "task_transfer_dept_manager", true),
        ("tal_overdue", "task_overdue", true),
        ("tal_overdue_assigned", "task_overdue_assigned", true),
        ("tal_overdue_dept_manager", "task_overdue_dept_manager", true),
        ("tal_overdue_dept_members", "task_overdue_dept_members", false),
    ];

    [HttpGet("/admin/settings-tasks")]
    [NavKey("settings-tasks")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var numbering = await settings.GetTaskNumberingAsync(ct);
        var tasks = await settings.GetTasksAsync(ct);

        // dlg-seq lists every sequence (the mockup shows the ticket + task counters;
        // its "Destek 716556"/"Görevler 4022" rows are sample state — DB truth wins,
        // the settings-tickets honest-defaults precedent).
        var sequenceRows = await db.Sequences.OrderBy(s => s.Id)
            .Select(s => new SequenceRowVm(s.Id, s.Name, s.Next))
            .ToListAsync(ct);

        var priorityKeys = await db.TicketPriorities
            .OrderByDescending(p => p.Urgency).Select(p => p.Key).ToListAsync(ct);

        var alerts = await SectionAsync("alerts", AlertsMap, ct);

        return View(new SettingsTasksVm(
            numbering.NumberFormat,
            tasks.NumberMode,
            sequenceRows,
            tasks.DefaultPriorityKey,
            priorityKeys,
            alerts));
    }

    // ---- save-all (main form) --------------------------------------------------------

    [HttpPost("/admin/settings-tasks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();
        bool Chk(string name) => form[name].Contains("true");

        // ---- validate (B3 server side; nothing persists on failure) -----------------
        var numberFormat = Str("number_format");
        if (!numberFormat.Contains('#'))
            return SaveResult("tg.errFormat", error: true);

        var numberMode = Str("number_mode") == "random" ? "random" : "sequential";

        var priorityKey = Str("default_priority");
        if (!await db.TicketPriorities.AnyAsync(p => p.Key == priorityKey, ct))
            return SaveResult("tg.errValues", error: true);

        // ---- persist (Setting rows are INotAudited by design) ------------------------
        // Numbering (LIVE: TaskService.DrawNumberAsync — sequential advances the task
        // sequence, random draws collision-checked digits and leaves the counter alone).
        await settings.SetAsync("tasks", "number_format", numberFormat, ct);
        await settings.SetAsync("tasks", "number_mode", numberMode, ct);

        // Persisted-only: TaskItem carries no priority column yet (TaskSettings
        // annotation) — TODO(S8): consumed once task priority lands.
        await settings.SetAsync("tasks", "default_priority", priorityKey, ct);

        // LIVE (S8): TaskMailHandler alert fan-out (seeded task.* templates).
        foreach (var (field, key, _) in AlertsMap)
            await settings.SetAsync("alerts", key, Chk(field).ToString(), ct);

        return SaveResult("tg.toastSaved");
    }

    // ---- dlg-seq sequence CRUD (B4 — the settings-tickets endpoint pattern over the
    // same SequenceNumberService.SaveAsync guards; only the redirect target differs) ----

    [HttpPost("/admin/settings-tasks/sequences")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sequences(int[] seqId, string[] seqName, long[] seqNext, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (seqId.Length != seqName.Length || seqId.Length != seqNext.Length)
            return SaveResult("tg.errValues", error: true);

        var rows = seqId.Select((id, i) => new SequenceRow(id, seqName[i], seqNext[i])).ToList();
        try
        {
            await sequences.SaveAsync(rows, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return SaveResult(ex.Code switch
            {
                "sequence-in-use" => "tg.errSeqInUse",
                "sequence-next-below-current" or "sequence-next-invalid" => "tg.errSeqNext",
                _ => "tg.errValues",
            }, error: true);
        }

        return SaveResult("tg.toastSeqSaved");
    }

    // ---- helpers ----------------------------------------------------------------------

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["TgToast"] = toastKey;
        if (error)
            TempData["TgToastError"] = true;
        return Redirect("/admin/settings-tasks");
    }

    private async Task<IReadOnlyDictionary<string, bool>> SectionAsync(
        string ns, (string Field, string Key, bool Default)[] map, CancellationToken ct)
    {
        var stored = await settings.GetSectionAsync(ns, ct);
        return map.ToDictionary(
            m => m.Key,
            m => stored.TryGetValue(m.Key, out var v) && bool.TryParse(v, out var b) ? b : m.Default);
    }
}
