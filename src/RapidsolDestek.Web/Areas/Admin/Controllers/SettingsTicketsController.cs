using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record OptionVm(int Id, string Label);
public sealed record SlaOptionVm(int Id, string Name, int Hours);
public sealed record SequenceRowVm(int Id, string Name, long Next);
public sealed record QueueTableRowVm(string Title, bool IsSystem);

public sealed record SettingsTicketsVm(
    string NumberFormat,
    string NumberMode,                     // "sequential" | "random"
    IReadOnlyList<SequenceRowVm> Sequences,
    string DefaultStatus,
    IReadOnlyList<string> StatusKeys,
    string DefaultPriority,
    IReadOnlyList<string> PriorityKeys,
    int DefaultSlaId,
    IReadOnlyList<SlaOptionVm> Slas,
    int DefaultTopicId,
    IReadOnlyList<OptionVm> Topics,
    int DefaultQueueId,
    IReadOnlyList<OptionVm> Queues,
    TicketBehaviorSettings Behavior,
    EffortSettings Effort,
    string EffortUnit,                     // "hours" | "days"
    IReadOnlyDictionary<string, bool> Autoresp,
    IReadOnlyDictionary<string, bool> Alerts,
    IReadOnlyList<QueueTableRowVm> QueueTable);

/// <summary>
/// Admin ticket settings (mockups/admin/settings-tickets.html, ROADMAP §6.3 + §4 B3/B4/B8):
/// every control persists into the Setting table through <see cref="ISettingsService"/>,
/// the effort section drives the live S4 B8 gates (incl. block-work-until-approved — the
/// S7 exit-gate flip), and the dlg-seq dialog is real sequence CRUD with delete/Next
/// guards. Live-vs-persisted-only per switch is annotated on the setting maps below
/// and on the ROADMAP row.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class SettingsTicketsController(
    AppDbContext db,
    ISettingsService settings,
    ISequenceNumberService sequences) : Controller
{
    // ---- autoresponse switches (ns "autoresp"); ALL LIVE (S8): TicketMailHandler
    // autoresponses + EffortEmailHandler effort.request. The maps are internal —
    // the mail handlers read the same key defaults (single source of truth).
    // Defaults mirror the mockup's checked states (osTicket autoresponder defaults).
    internal static readonly (string Field, string Key, bool Default)[] AutorespMap =
    [
        ("ar_new_ticket", "new_ticket", true),            // LIVE: ticket.autoresp to owner (TicketMailHandler)
        ("ar_agent_new_ticket", "agent_new_ticket", true),// LIVE: ticket.notice on agent-opened tickets
        ("ar_new_message", "new_message", false),         // LIVE: message.autoresp to the poster
        ("ar_new_message_collab", "new_message_collab", true), // LIVE: ticket.activity.notice participant copies
        ("ar_overlimit", "overlimit", true),              // LIVE: ticket.overlimit to the refused user
        ("ar_effort", "effort_proposal", true),           // LIVE: gates EffortEmailHandler effort.request
    ];

    // ---- agent alert switches (ns "alerts"); ALL LIVE (S8): TicketMailHandler alert
    // fan-out masters + recipient checkboxes; effort_* gate EffortEmailHandler.
    // system_errors_* persist only (no error-alert producer yet — master stays
    // checked+disabled in the mockup).
    internal static readonly (string Field, string Key, bool Default)[] AlertsMap =
    [
        ("al_new_ticket", "new_ticket", true),
        ("al_new_ticket_admin", "new_ticket_admin", true),
        ("al_new_ticket_dept_manager", "new_ticket_dept_manager", true),
        ("al_new_ticket_dept_members", "new_ticket_dept_members", false),
        ("al_new_ticket_account_manager", "new_ticket_account_manager", true),
        ("al_new_message", "new_message", true),
        ("al_new_message_last_respondent", "new_message_last_respondent", true),
        ("al_new_message_assigned", "new_message_assigned", true),
        ("al_new_message_dept_manager", "new_message_dept_manager", false),
        ("al_new_message_account_manager", "new_message_account_manager", false),
        ("al_new_activity", "new_activity", false),
        ("al_new_activity_last_respondent", "new_activity_last_respondent", true),
        ("al_new_activity_assigned", "new_activity_assigned", true),
        ("al_new_activity_dept_manager", "new_activity_dept_manager", false),
        ("al_assignment", "assignment", true),
        ("al_assignment_assigned", "assignment_assigned", true),
        ("al_assignment_team_lead", "assignment_team_lead", true),
        ("al_assignment_team_members", "assignment_team_members", false),
        ("al_transfer", "transfer", true),
        ("al_transfer_assigned", "transfer_assigned", true),
        ("al_transfer_dept_manager", "transfer_dept_manager", true),
        ("al_transfer_dept_members", "transfer_dept_members", false),
        ("al_overdue", "overdue", true),
        ("al_overdue_assigned", "overdue_assigned", true),
        ("al_overdue_dept_manager", "overdue_dept_manager", true),
        ("al_overdue_dept_members", "overdue_dept_members", false),
        ("al_effort", "effort_response", true),           // LIVE master: gates EffortEmailHandler effort.response
        ("al_effort_assigned", "effort_response_assigned", true),
        ("al_effort_dept_manager", "effort_response_dept_manager", false),
        // System-errors master is always on (mockup: checked+disabled) — not persisted.
        ("al_system_errors_sql", "system_errors_sql", true),
        ("al_system_errors_login", "system_errors_login", true),
    ];

    [HttpGet("/admin/settings-tickets")]
    [NavKey("settings-tickets")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var numbering = await settings.GetTicketNumberingAsync(ct);
        var behavior = await settings.GetTicketBehaviorAsync(ct);
        var effort = await settings.GetEffortAsync(ct);

        var sequenceRows = await db.Sequences.OrderBy(s => s.Id)
            .Select(s => new SequenceRowVm(s.Id, s.Name, s.Next))
            .ToListAsync(ct);

        // The mockup's status select offers exactly open + wait.
        var statusKeys = await db.TicketStatuses
            .Where(s => s.Key == "open" || s.Key == "wait")
            .OrderBy(s => s.Sort).Select(s => s.Key).ToListAsync(ct);
        var priorityKeys = await db.TicketPriorities
            .OrderByDescending(p => p.Urgency).Select(p => p.Key).ToListAsync(ct);
        var slas = await db.SlaPlans.Where(s => s.IsActive).OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Name, s.GracePeriodHours }).ToListAsync(ct);
        var topics = await db.HelpTopics.Where(t => t.IsActive)
            .OrderBy(t => t.Sort)
            .Select(t => new OptionVm(t.Id, t.Parent != null ? t.Parent.Name + " / " + t.Name : t.Name))
            .ToListAsync(ct);

        // Default-queue candidates: shared queues only (a personal queue cannot be
        // the global landing queue — flagged on the ROADMAP row; the mockup's
        // "SLA Riskli VIP" option is sample data).
        var sharedQueues = await db.SavedQueues
            .Where(q => q.StaffId == null && q.IsEnabled)
            .OrderBy(q => q.ParentId == null ? q.Id : q.ParentId).ThenBy(q => q.Sort)
            .Select(q => new { q.Id, q.Title, q.ParentId, ParentTitle = q.Parent!.Title, q.Criteria })
            .ToListAsync(ct);
        var queueOptions = sharedQueues
            .Select(q => new OptionVm(q.Id, q.ParentId is null ? q.Title : $"{q.ParentTitle} / {q.Title}"))
            .ToList();
        // Unset ⇒ show the built-in default the agent panel actually lands on
        // (the assignee:me child — TicketsController.ResolveActiveQueue).
        var effectiveQueueId = behavior.DefaultQueueId != 0
            ? behavior.DefaultQueueId
            : sharedQueues.FirstOrDefault(q =>
                q.ParentId != null && QueueCriteria.Parse(q.Criteria).Assignee == "me")?.Id ?? 0;

        // Queues section table: top-level queues; System pill = the real IsSystem
        // flag (seeded canon rows; the S7 queue builder guards their deletion) —
        // builder-created shared queues and personal saved searches render Custom.
        var queueTable = await db.SavedQueues.Where(q => q.ParentId == null)
            .OrderBy(q => q.StaffId == null ? 0 : 1).ThenBy(q => q.Sort).ThenBy(q => q.Id)
            .Select(q => new QueueTableRowVm(q.Title, q.IsSystem))
            .ToListAsync(ct);

        var autoresp = await SectionAsync("autoresp", AutorespMap, ct);
        var alerts = await SectionAsync("alerts", AlertsMap, ct);

        return View(new SettingsTicketsVm(
            numbering.NumberFormat,
            await settings.GetAsync("tickets", "number_mode", ct) == "random" ? "random" : "sequential",
            sequenceRows,
            numbering.DefaultStatusKey ?? "open",
            statusKeys,
            await settings.GetAsync("core", "default_priority", ct) ?? "normal",
            priorityKeys,
            ParseOrZero(await settings.GetAsync("core", "default_sla_id", ct)),
            slas.Select(s => new SlaOptionVm(s.Id, s.Name, s.GracePeriodHours)).ToList(),
            // TODO(S8): core.default_topic_id is consumed by the inbound mail pipeline
            // (email-created tickets without a chosen topic).
            ParseOrZero(await settings.GetAsync("core", "default_topic_id", ct)),
            topics,
            effectiveQueueId,
            queueOptions,
            behavior,
            effort,
            // TODO: effort.unit is persisted-only — proposals are entered in hours
            // everywhere (dlg-effort, portal card); flagged for canon.
            await settings.GetAsync("effort", "unit", ct) == "days" ? "days" : "hours",
            autoresp,
            alerts,
            queueTable));
    }

    // ---- save-all (main form) --------------------------------------------------------

    [HttpPost("/admin/settings-tickets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CancellationToken ct = default)
    {
        var form = Request.Form;
        string Str(string name) => (form[name].ToString() ?? "").Trim();
        bool Chk(string name) => form[name].Contains("true");

        // ---- validate (B3 server side; the client mirrors via input constraints) ----
        var numberFormat = Str("number_format");
        if (!numberFormat.Contains('#'))
            return SaveResult("st.errFormat", error: true);

        var numberMode = Str("number_mode") == "random" ? "random" : "sequential";
        var lockMode = Str("lock_mode") is "disabled" or "view" or "activity" ? Str("lock_mode") : null;
        var effortUnit = Str("ef_unit") == "days" ? "days" : "hours";

        if (lockMode is null
            || !int.TryParse(Str("max_open"), out var maxOpen) || maxOpen < 0
            || !int.TryParse(Str("ef_reminder"), out var reminder) || reminder < 0
            || !decimal.TryParse(Str("ef_autoapprove"), System.Globalization.CultureInfo.InvariantCulture,
                out var autoApprove) || autoApprove < 0
            || !int.TryParse(Str("ef_revlimit"), out var revLimit) || revLimit < 1)
        {
            return SaveResult("st.errValues", error: true);
        }

        var statusKey = Str("default_status");
        if (!await db.TicketStatuses.AnyAsync(s => s.Key == statusKey && (s.Key == "open" || s.Key == "wait"), ct))
            return SaveResult("st.errValues", error: true);
        var priorityKey = Str("default_priority");
        if (!await db.TicketPriorities.AnyAsync(p => p.Key == priorityKey, ct))
            return SaveResult("st.errValues", error: true);
        if (!int.TryParse(Str("default_sla"), out var slaId)
            || !await db.SlaPlans.AnyAsync(s => s.Id == slaId && s.IsActive, ct))
            return SaveResult("st.errValues", error: true);
        if (!int.TryParse(Str("default_topic"), out var topicId)
            || !await db.HelpTopics.AnyAsync(t => t.Id == topicId && t.IsActive, ct))
            return SaveResult("st.errValues", error: true);
        if (!int.TryParse(Str("default_queue"), out var queueId)
            || !await db.SavedQueues.AnyAsync(q => q.Id == queueId && q.StaffId == null && q.IsEnabled, ct))
            return SaveResult("st.errValues", error: true);

        // ---- persist (Setting rows are INotAudited by design) ------------------------
        // Numbering (LIVE: TicketService.DrawNumberAsync / SequenceNumberService).
        await settings.SetAsync("tickets", "number_format", numberFormat, ct);
        await settings.SetAsync("tickets", "number_mode", numberMode, ct);

        // Defaults (LIVE: TicketService.CreateAsync cascade; default_topic_id TODO(S8);
        // default_queue_id + top_level_counts LIVE: agent TicketsController).
        await settings.SetAsync("tickets", "default_status", statusKey, ct);
        await settings.SetAsync("core", "default_priority", priorityKey, ct);
        await settings.SetAsync("core", "default_sla_id", slaId.ToString(), ct);
        await settings.SetAsync("core", "default_topic_id", topicId.ToString(), ct);
        await settings.SetAsync("tickets", "default_queue_id", queueId.ToString(), ct);
        await settings.SetAsync("tickets", "top_level_counts", Chk("top_counts").ToString(), ct);

        // Behavior (LIVE: max_open_per_user, claim_on_response, require_topic_to_close;
        // persisted-only with consumers annotated on TicketBehaviorSettings: lock_mode,
        // captcha, auto_refer_on_close, allow_external_images, collab_visibility).
        await settings.SetAsync("tickets", "lock_mode", lockMode, ct);
        await settings.SetAsync("tickets", "max_open_per_user", maxOpen.ToString(), ct);
        await settings.SetAsync("tickets", "captcha", Chk("captcha").ToString(), ct);
        await settings.SetAsync("tickets", "claim_on_response", Chk("claim_on_response").ToString(), ct);
        await settings.SetAsync("tickets", "auto_refer_on_close", Chk("auto_refer").ToString(), ct);
        await settings.SetAsync("tickets", "require_topic_to_close", Chk("require_topic").ToString(), ct);
        await settings.SetAsync("tickets", "allow_external_images", Chk("external_images").ToString(), ct);
        await settings.SetAsync("tickets", "collab_visibility", Chk("collab_visibility").ToString(), ct);

        // Effort (B8 — ALL LIVE except unit: enabled + block_work + reject_note +
        // auto_approve + revision_limit gate EffortProposalService / EffortWorkGate
        // the moment they land; reminder_days drives the S8 EffortReminderJob
        // (daily Hangfire sweep, ReminderSentAt cadence guard); unit persisted-only,
        // flagged for canon).
        await settings.SetAsync("effort", "enabled", Chk("ef_enabled").ToString(), ct);
        await settings.SetAsync("effort", "block_work_until_approved", Chk("ef_block_work").ToString(), ct);
        await settings.SetAsync("effort", "mandatory_reject_note", Chk("ef_reject_note").ToString(), ct);
        await settings.SetAsync("effort", "unit", effortUnit, ct);
        await settings.SetAsync("effort", "reminder_days", reminder.ToString(), ct);
        await settings.SetAsync("effort", "auto_approve_threshold_hours",
            autoApprove.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await settings.SetAsync("effort", "revision_limit", revLimit.ToString(), ct);

        foreach (var (field, key, _) in AutorespMap)
            await settings.SetAsync("autoresp", key, Chk(field).ToString(), ct);
        foreach (var (field, key, _) in AlertsMap)
            await settings.SetAsync("alerts", key, Chk(field).ToString(), ct);

        return SaveResult("st.toastSaved");
    }

    // ---- dlg-seq sequence CRUD (B4) ---------------------------------------------------

    [HttpPost("/admin/settings-tickets/sequences")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sequences(int[] seqId, string[] seqName, long[] seqNext, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        if (seqId.Length != seqName.Length || seqId.Length != seqNext.Length)
            return SaveResult("st.errValues", error: true);

        var rows = seqId.Select((id, i) => new SequenceRow(id, seqName[i], seqNext[i])).ToList();
        try
        {
            await sequences.SaveAsync(rows, actor, ct);
        }
        catch (DomainRuleException ex)
        {
            return SaveResult(ex.Code switch
            {
                "sequence-in-use" => "st.errSeqInUse",
                "sequence-next-below-current" or "sequence-next-invalid" => "st.errSeqNext",
                _ => "st.errValues",
            }, error: true);
        }

        return SaveResult("st.toastSeqSaved");
    }

    // ---- helpers ----------------------------------------------------------------------

    private IActionResult SaveResult(string toastKey, bool error = false)
    {
        TempData["StToast"] = toastKey;
        if (error)
            TempData["StToastError"] = true;
        return Redirect("/admin/settings-tickets");
    }

    private async Task<IReadOnlyDictionary<string, bool>> SectionAsync(
        string ns, (string Field, string Key, bool Default)[] map, CancellationToken ct)
    {
        var stored = await settings.GetSectionAsync(ns, ct);
        return map.ToDictionary(
            m => m.Key,
            m => stored.TryGetValue(m.Key, out var v) && bool.TryParse(v, out var b) ? b : m.Default);
    }

    private static int ParseOrZero(string? value) => int.TryParse(value, out var i) ? i : 0;
}
