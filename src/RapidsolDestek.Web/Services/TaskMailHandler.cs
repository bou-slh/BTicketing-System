using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Areas.Admin.Controllers;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// S8 slice 2, task leg: staff alert fan-out over the seeded task.* catalog templates
/// (settings-tasks masters + recipient checkboxes, alerts/task_* keys), subscribing
/// to the TaskService domain events — osTicket class.task.php alert semantics
/// modeled (onNewTask / onActivity / onAssignment / transfer / overdue).
/// Tasks have no end-user side, so there are no autoresponses here.
///
/// Rendering: the task catalog templates carry the ticket.* variable pills
/// (EmailTemplateCatalog TicketVars) — for task mail those fill FROM THE TASK
/// (number/title/dept/assignee/dates) via Extra overrides, alongside task.* twins;
/// a parent ticket is deliberately NOT expanded (the task link is the subject).
/// From-account: email/alert_email_id else the transport default (ticket alerts
/// precedent). Skip-actor + dedupe + availability rules match TicketMailHandler.
/// </summary>
public sealed class TaskMailHandler(
    AppDbContext db,
    IEmailTemplateRenderer renderer,
    ISettingsService settings,
    IAlertRecipientResolver recipients,
    IMailQueue queue,
    ILogger<TaskMailHandler> logger) :
    IDomainEventHandler<TaskCreated>,
    IDomainEventHandler<TaskAssigned>,
    IDomainEventHandler<TaskTransferred>,
    IDomainEventHandler<TaskOverdue>,
    IDomainEventHandler<ThreadEntryAdded>
{
    private static readonly Dictionary<string, bool> AlertDefaults =
        SettingsTasksController.AlertsMap.ToDictionary(m => m.Key, m => m.Default);

    private IReadOnlyDictionary<string, string>? _alerts;

    public async Task HandleAsync(TaskCreated evt, CancellationToken ct = default)
    {
        var t = await SnapshotAsync(evt.TaskId, ct);
        if (t is null || !await OnAsync("task_new", ct))
            return;

        var list = new List<MailRecipient?>();
        // Dept members only while the task is unassigned (new-ticket alert parity).
        if (await OnAsync("task_new_dept_members", ct) && t.StaffId is null && t.TeamId is null)
            list.AddRange(await recipients.DeptMembersAsync(t.DepartmentId, ct));
        if (await OnAsync("task_new_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));
        if (await OnAsync("task_new_admin", ct))
            list.Add(await AdminRecipientAsync(ct));

        await FanoutAsync("task.alert", list, evt.ActorStaffId, t, ct);
    }

    public async Task HandleAsync(TaskAssigned evt, CancellationToken ct = default)
    {
        if (evt.StaffId is null && evt.TeamId is null)
            return; // unassignment
        var t = await SnapshotAsync(evt.TaskId, ct);
        if (t is null || !await OnAsync("task_assignment", ct))
            return;

        var list = new List<MailRecipient?>();
        if (evt.StaffId is { } staffId)
        {
            if (await OnAsync("task_assignment_assigned", ct))
                list.Add(await recipients.StaffAsync(staffId, ct));
        }
        else if (evt.TeamId is { } teamId)
        {
            list.AddRange(await recipients.TeamAsync(teamId,
                members: await OnAsync("task_assignment_team_members", ct),
                lead: await OnAsync("task_assignment_team_lead", ct), ct));
        }

        await FanoutAsync("task.assigned.alert", list, evt.ActorStaffId, t, ct);
    }

    public async Task HandleAsync(TaskTransferred evt, CancellationToken ct = default)
    {
        var t = await SnapshotAsync(evt.TaskId, ct); // already carries the NEW dept
        if (t is null || !await OnAsync("task_transfer", ct))
            return;

        var list = new List<MailRecipient?>();
        if (await OnAsync("task_transfer_assigned", ct))
        {
            if (t.StaffId is { } staffId)
                list.Add(await recipients.StaffAsync(staffId, ct));
            else if (t.TeamId is { } teamId)
                list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
        }
        if (await OnAsync("task_transfer_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(evt.NewDepartmentId, ct));

        await FanoutAsync("task.transfer.alert", list, evt.ActorStaffId, t, ct);
    }

    public async Task HandleAsync(TaskOverdue evt, CancellationToken ct = default)
    {
        var t = await SnapshotAsync(evt.TaskId, ct);
        if (t is null || !await OnAsync("task_overdue", ct))
            return;

        var list = new List<MailRecipient?>();
        if (t.StaffId is not null || t.TeamId is not null)
        {
            if (await OnAsync("task_overdue_assigned", ct))
            {
                if (t.StaffId is { } staffId)
                    list.Add(await recipients.StaffAsync(staffId, ct));
                else if (t.TeamId is { } teamId)
                    list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
            }
        }
        else if (await OnAsync("task_overdue_dept_members", ct))
        {
            list.AddRange(await recipients.DeptMembersAsync(t.DepartmentId, ct));
        }
        if (await OnAsync("task_overdue_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(t.DepartmentId, ct));

        await FanoutAsync("task.overdue.alert", list, actorStaffId: null, t, ct);
    }

    /// <summary>New activity on a TASK thread (osTicket task onActivity →
    /// task.activity.alert). Ticket threads are TicketMailHandler's turf.</summary>
    public async Task HandleAsync(ThreadEntryAdded evt, CancellationToken ct = default)
    {
        if (evt.TicketId is not null)
            return;
        var t = await db.TaskItems.Where(x => x.ThreadId == evt.ThreadId)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
        if (t is not { } taskId)
            return;

        // The first entry is the create's description — the task_new alert covers it.
        if (!await db.ThreadEntries.AnyAsync(e => e.ThreadId == evt.ThreadId && e.Id < evt.EntryId, ct))
            return;
        if (!await OnAsync("task_activity", ct))
            return;

        var snapshot = await SnapshotAsync(taskId, ct);
        var entry = await db.ThreadEntries.Where(e => e.Id == evt.EntryId)
            .Select(e => new { e.StaffId, e.Body, e.Format })
            .SingleOrDefaultAsync(ct);
        if (snapshot is null || entry is null)
            return;

        var list = new List<MailRecipient?>();
        if (await OnAsync("task_activity_last_respondent", ct))
            list.Add(await recipients.LastRespondentAsync(evt.ThreadId, ct));
        if (await OnAsync("task_activity_assigned", ct))
        {
            if (snapshot.StaffId is { } staffId)
                list.Add(await recipients.StaffAsync(staffId, ct));
            if (snapshot.TeamId is { } teamId)
                list.AddRange(await recipients.TeamAsync(teamId, members: true, lead: false, ct));
        }
        if (await OnAsync("task_activity_dept_manager", ct))
            list.Add(await recipients.DeptManagerAsync(snapshot.DepartmentId, ct));

        await FanoutAsync("task.activity.alert", list, entry.StaffId, snapshot, ct,
            messageHtml: entry.Format == "html" ? entry.Body : System.Net.WebUtility.HtmlEncode(entry.Body).Replace("\n", "<br>"));
    }

    // ---- shared plumbing --------------------------------------------------------------

    private sealed record TaskSnapshot(
        int Id, string Number, string Title, int DepartmentId, string DeptName,
        int? StaffId, int? TeamId, string? AssigneeName,
        DateTimeOffset CreatedAt, DateTimeOffset? DueDate);

    private Task<TaskSnapshot?> SnapshotAsync(int taskId, CancellationToken ct) =>
        db.TaskItems.Where(t => t.Id == taskId)
            .Select(t => (TaskSnapshot?)new TaskSnapshot(
                t.Id, t.Number, t.Title, t.DepartmentId, t.Department!.Name,
                t.StaffId, t.TeamId,
                t.Staff != null ? t.Staff.FirstName + " " + t.Staff.LastName : (t.Team != null ? t.Team.Name : null),
                t.CreatedAt, t.DueDate))
            .SingleOrDefaultAsync(ct);

    private async Task<bool> OnAsync(string key, CancellationToken ct)
    {
        _alerts ??= await settings.GetSectionAsync("alerts", ct);
        return _alerts.TryGetValue(key, out var v) && bool.TryParse(v, out var b)
            ? b
            : AlertDefaults.GetValueOrDefault(key);
    }

    private async Task<MailRecipient?> AdminRecipientAsync(CancellationToken ct)
    {
        var admin = (await settings.GetEmailAsync(ct)).AdminEmail;
        return string.IsNullOrWhiteSpace(admin) ? null : new MailRecipient("Admin", admin);
    }

    private async Task FanoutAsync(string templateCode, IReadOnlyList<MailRecipient?> list, int? actorStaffId,
        TaskSnapshot task, CancellationToken ct, string? messageHtml = null)
    {
        var alertAccountId = (await settings.GetEmailAsync(ct)).AlertEmailAccountId;
        var from = alertAccountId > 0 ? (int?)alertAccountId : null;

        // Task field bag: the catalog's ticket.* pills fill from the task (see class
        // doc), task.* twins ride along for osTicket-canon template authors.
        var baseUrl = (await settings.GetAsync("core", "helpdesk_url", ct)
            ?? "https://destek.rapidsol.com.tr/").TrimEnd('/');
        var link = $"{baseUrl}/task-view?id={task.Id}";
        var extra = new Dictionary<string, string?>
        {
            ["ticket.number"] = task.Number,
            ["ticket.subject"] = task.Title,
            ["ticket.dept"] = task.DeptName,
            ["ticket.dept.name"] = task.DeptName,
            ["ticket.staff.name"] = task.AssigneeName,
            ["ticket.assignee"] = task.AssigneeName,
            ["staff.name"] = task.AssigneeName,
            ["ticket.create_date"] = task.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
            ["ticket.due_date"] = task.DueDate?.ToString("dd.MM.yyyy HH:mm"),
            ["ticket.link"] = link,
            ["task.number"] = task.Number,
            ["task.title"] = task.Title,
            ["task.dept"] = task.DeptName,
            ["task.assignee"] = task.AssigneeName,
            ["task.due_date"] = task.DueDate?.ToString("dd.MM.yyyy HH:mm"),
            ["task.link"] = link,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in list)
        {
            if (recipient is null
                || (recipient.StaffId is { } staffId && staffId == actorStaffId)
                || !seen.Add(recipient.Email))
            {
                continue;
            }

            var rendered = await renderer.RenderAsync(templateCode, new EmailRenderContext
            {
                RecipientName = recipient.Name,
                RecipientEmail = recipient.Email,
                MessageHtml = messageHtml,
                Extra = extra,
            }, ct);
            if (rendered is null)
            {
                logger.LogWarning("Task template {Code} missing; mail to {Email} skipped (task {TaskId})",
                    templateCode, recipient.Email, task.Id);
                continue;
            }
            await queue.EnqueueAsync(new OutboundEmailRequest(recipient.Email, rendered.Subject, rendered.HtmlBody)
            {
                FromEmailAccountId = from,
            }, ct);
        }
    }
}
