using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Forms;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent new-ticket page (mockups/agent/ticket-open.html, ROADMAP §6.2): user
/// autocomplete + inline "Yeni Kullanıcı" (guest user, org auto-link by email
/// domain), help-topic → dept/priority/SLA cascade + designed form fields
/// (portal /open precedent), CC collaborators, attachments onto the initial
/// Message, optional first response / internal note (B3/B5) — a real ticket on
/// behalf of the user through ITicketService with the staff actor.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class TicketOpenController(
    AppDbContext db,
    ITicketService tickets,
    IThreadService threads,
    IPermissionService permissions,
    ISettingsService settings,
    IFileStore files) : Controller
{
    [HttpGet("/agent/ticket-open")]
    [NavKey("tickets")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        return View(await BuildVmAsync(staff, new TicketOpenForm(), null, ct));
    }

    /// <summary>
    /// User autocomplete (mockup search input; SearchController ILike patterns +
    /// phone, which the mockup placeholder promises). JSON for the page dropdown.
    /// </summary>
    [HttpGet("/agent/ticket-open/users")]
    public async Task<IActionResult> Users(string? q, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var term = (q ?? "").Trim();
        if (term.Length < 2)
            return Json(Array.Empty<object>());

        var like = $"%{term}%";
        var rows = await db.Users
            .Where(u => EF.Functions.ILike(u.Name, like)
                || u.Emails.Any(e => EF.Functions.ILike(e.Address, like))
                || (u.Phone != null && EF.Functions.ILike(u.Phone, like)))
            .OrderBy(u => u.Name)
            .Take(8)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = u.DefaultEmail != null
                    ? u.DefaultEmail.Address
                    : u.Emails.Select(e => e.Address).FirstOrDefault(),
                org = u.Organization != null ? u.Organization.Name : null,
            })
            .ToListAsync(ct);
        return Json(rows);
    }

    /// <summary>
    /// Canned insert for the first-response composer. No ticket exists yet, so the
    /// body is returned unexpanded — %{variables} resolve only against a ticket
    /// (ICannedResponseService.ExpandAsync). NOTE(S8): revisit if pre-create
    /// expansion (user/staff variables only) becomes canon.
    /// </summary>
    [HttpGet("/agent/ticket-open/canned")]
    public async Task<IActionResult> Canned(string what, CancellationToken ct)
    {
        // admin/settings-kb enable_canned off: refuse (the menu is hidden anyway).
        if (!(await settings.GetKbAsync(ct)).EnableCanned)
            return NotFound();

        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        if (!int.TryParse(what, out var cannedId))
            return BadRequest();

        var canned = await db.CannedResponses
            .SingleOrDefaultAsync(c => c.Id == cannedId && c.IsEnabled, ct);
        if (canned is null)
            return NotFound();
        return Json(new { text = HtmlToComposerText(canned.Response) });
    }

    [HttpPost("/agent/ticket-open")]
    [NavKey("tickets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(TicketOpenForm form, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        // ---- User: existing selection or the inline "Yeni Kullanıcı" block ----------
        User? user = null;
        if (form.NewUser)
        {
            var address = (form.NewUserEmail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(form.NewUserName) || !new EmailAddressAttribute().IsValid(address))
                ModelState.AddModelError(nameof(TicketOpenForm.NewUserEmail), "errNewUser");
            else if (await db.UserEmails.AnyAsync(e => e.Address.ToLower() == address.ToLower(), ct))
                ModelState.AddModelError(nameof(TicketOpenForm.NewUserEmail), "errEmailInUse");
        }
        else if (form.UserId is { } userId)
        {
            user = await db.Users.Include(u => u.Emails)
                .SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                ModelState.AddModelError(nameof(TicketOpenForm.UserId), "errUser");
            else if (user.IsBlocked)
                ModelState.AddModelError(nameof(TicketOpenForm.UserId), "errBlocked");
        }
        else
        {
            ModelState.AddModelError(nameof(TicketOpenForm.UserId), "errUser");
        }

        // ---- Topic (agent scope: active topics, public + private) -------------------
        var topic = form.Topic is { } topicId
            ? await db.HelpTopics
                .Include(t => t.Parent)
                .Include(t => t.Forms.OrderBy(f => f.Sort))
                    .ThenInclude(f => f.FormDefinition)
                        .ThenInclude(d => d!.Fields.OrderBy(x => x.Sort))
                .SingleOrDefaultAsync(t => t.Id == topicId && t.IsActive, ct)
            : null;
        if (topic is null)
            ModelState.AddModelError(nameof(TicketOpenForm.Topic), "errTopic");

        // Dynamic topic fields (B3): agent-required fields must be filled, and
        // filled values must pass the field's configured format check (the S7 form
        // designer's ⚙ "Doğrulama" — email/phone/number).
        List<FormField> topicFields = topic is null ? [] : AgentFields(topic);
        foreach (var field in topicFields)
        {
            form.Fields.TryGetValue(field.Id, out var raw);
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (field.RequiredForAgents)
                    ModelState.AddModelError($"Fields[{field.Id}]", $"errField:{field.Label}");
            }
            else if (!FormFieldConfig.Parse(field.Configuration).IsValidValue(raw.Trim()))
            {
                ModelState.AddModelError($"Fields[{field.Id}]", $"errFieldFormat:{field.Label}");
            }
        }

        if (string.IsNullOrWhiteSpace(form.Summary))
            ModelState.AddModelError(nameof(TicketOpenForm.Summary), "errSummary");
        if (string.IsNullOrWhiteSpace(form.Details))
            ModelState.AddModelError(nameof(TicketOpenForm.Details), "errDetails");

        // Attachment size cap (admin/settings-system Ekler, S7): attachments/max_size_mb
        // is enforced at every upload ingress.
        var attachLimits = await settings.GetAttachmentsAsync(ct);
        if (form.Files.Any(f => f.Length > attachLimits.MaxSizeBytes))
            ModelState.AddModelError(nameof(TicketOpenForm.Files), "errAttachTooBig");

        // ---- CC list: comma/semicolon separated addresses (mockup placeholder) ------
        var ccAddresses = (form.Cc ?? "")
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var address in ccAddresses.Where(a => !new EmailAddressAttribute().IsValid(a)))
            ModelState.AddModelError(nameof(TicketOpenForm.Cc), $"errCc:{address}");

        // Selects post real ids; only existing rows are accepted (unknown → null,
        // portal /open priority precedent).
        var priorityId = form.Priority is { } pid
            ? await db.TicketPriorities.Where(p => p.Id == pid).Select(p => (int?)p.Id).SingleOrDefaultAsync(ct)
            : null;
        var departmentId = form.DepartmentId is { } did
            ? await db.Departments.Where(d => d.Id == did).Select(d => (int?)d.Id).SingleOrDefaultAsync(ct)
            : null;
        var slaId = form.SlaId is { } sid
            ? await db.SlaPlans.Where(s => s.Id == sid).Select(s => (int?)s.Id).SingleOrDefaultAsync(ct)
            : null;

        if (!ModelState.IsValid)
            return View(await BuildVmAsync(staff, form, user, ct));

        // Pre-flight permission on the effective department (TicketService cascade:
        // topic override beats the select beats settings default) — checked BEFORE
        // the new guest user is created so a denial leaves no orphan row.
        var effectiveDeptId = topic!.DepartmentId
            ?? departmentId
            ?? ParseOrNull(await settings.GetAsync("core", "default_dept_id", ct));
        try
        {
            if (effectiveDeptId is { } deptId)
                await permissions.EnsureAsync(actor, PermissionKeys.TicketCreate, deptId, ct);
        }
        catch (PermissionDeniedException)
        {
            ModelState.AddModelError(nameof(TicketOpenForm.Topic), "errDenied");
            return View(await BuildVmAsync(staff, form, user, ct));
        }

        // Inline new user: guest domain User (+UserEmail, org auto-link by email
        // domain — portal AccountController.LinkDomainUserAsync pattern) WITHOUT an
        // Identity account: IdentityUserId stays null until they register themselves.
        user ??= await CreateGuestUserAsync(form.NewUserName!.Trim(), form.NewUserEmail!.Trim(),
            string.IsNullOrWhiteSpace(form.NewUserPhone) ? null : form.NewUserPhone.Trim(), actor, ct);

        // Plain-text composer input → HTML body (portal /open precedent; the thread
        // ingress sanitizes at post time as well).
        var encoder = HtmlEncoder.Default;
        var body = string.Join("<br>", form.Details.Trim().Split('\n')
            .Select(line => encoder.Encode(line.TrimEnd('\r'))));

        // Real creation through the domain service: sequence number, ticket
        // filters (admin/filters, S7 — a Reject action refuses the create),
        // help-topic routing cascade (dept/priority/SLA/status/assignee), audit +
        // events and the initial Message thread entry all happen inside CreateAsync.
        Ticket ticket;
        try
        {
            ticket = await tickets.CreateAsync(new TicketCreateRequest
            {
                UserId = user.Id,
                UserEmailId = user.DefaultEmailId,
                Subject = form.Summary.Trim(),
                Body = body,
                HelpTopicId = topic.Id,
                DepartmentId = departmentId,
                PriorityId = priorityId,
                SlaId = slaId,
                Source = ParseSource(form.Source),
                DueDate = form.Due is { } due
                    ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    : null,
            }, actor, ct);
        }
        catch (TicketRejectedByFilterException)
        {
            // NOTE: an inline-created guest user row survives the refusal (the
            // filter verdict needs the full create context) — harmless, flagged.
            ModelState.AddModelError(nameof(TicketOpenForm.Summary), "errFiltered");
            return View(await BuildVmAsync(staff, form, user, ct));
        }

        // B5: uploads → IFileStore content + StoredFile/Attachment rows on the
        // initial Message entry (portal OpenController precedent).
        var messageEntryId = await db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId && e.Type == ThreadEntryType.Message)
            .Select(e => e.Id)
            .SingleAsync(ct);
        foreach (var upload in form.Files.Where(f => f.Length > 0))
        {
            await using var content = upload.OpenReadStream();
            var stored = await files.SaveAsync(content, Path.GetFileName(upload.FileName),
                string.IsNullOrEmpty(upload.ContentType) ? "application/octet-stream" : upload.ContentType, ct);
            db.StoredFiles.Add(stored);
            db.Attachments.Add(new Attachment
            {
                ObjectType = AttachmentObjectType.ThreadEntry,
                ObjectId = messageEntryId,
                File = stored,
            });
        }

        // Dynamic field answers → FormEntry/FormEntryValue on the ticket
        // (osTicket form_entry parity; portal /open precedent).
        foreach (var topicForm in topic.Forms.OrderBy(f => f.Sort))
        {
            var entry = new FormEntry
            {
                FormDefinitionId = topicForm.FormDefinitionId,
                ObjectType = FormObjectType.Ticket,
                ObjectId = ticket.Id,
                Sort = topicForm.Sort,
            };
            foreach (var field in AgentFields(topicForm))
            {
                form.Fields.TryGetValue(field.Id, out var raw);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                int? valueId = null;
                var value = raw.Trim();
                if (field.Type == "choices")
                {
                    // List-backed choices post the item id; inline (designer
                    // textarea) choices post the option string itself.
                    var config = FormFieldConfig.Parse(field.Configuration);
                    if (config.ListId is { } listId)
                    {
                        if (!int.TryParse(value, out var itemId))
                            continue;
                        var item = await db.ListDefinitions
                            .Where(l => l.Id == listId)
                            .SelectMany(l => l.Items)
                            .SingleOrDefaultAsync(i => i.Id == itemId, ct);
                        if (item is null)
                            continue;
                        valueId = item.Id;
                        value = item.Value;
                    }
                    else if (!config.Choices.Contains(value, StringComparer.Ordinal))
                    {
                        continue; // not one of the designed options
                    }
                }
                entry.Values.Add(new FormEntryValue { FormFieldId = field.Id, Value = value, ValueId = valueId });
            }
            if (entry.Values.Count > 0)
                db.FormEntries.Add(entry);
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        // CC → ThreadCollaborator rows (osTicket collaborator parity: unknown
        // addresses become guest users, org auto-linked by domain, name = address).
        foreach (var address in ccAddresses)
        {
            var collaborator = await db.UserEmails
                    .Where(e => e.Address.ToLower() == address.ToLower())
                    .Select(e => e.User!)
                    .FirstOrDefaultAsync(ct)
                ?? await CreateGuestUserAsync(address, address, null, actor, ct);
            if (collaborator.Id != user.Id)
                await threads.AddCollaboratorAsync(ticket.ThreadId, collaborator.Id, CollaboratorRole.Cc, actor, ct);
        }

        // Ata select ("s:{id}" / "t:{id}", ticket-view dialog parity) — the ticket
        // exists either way; a refused assignment must not fail the creation.
        int? assignStaffId = null, assignTeamId = null;
        if (form.Assignee is { } assignee)
        {
            if (assignee.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var sId))
                assignStaffId = sId;
            else if (assignee.StartsWith("t:", StringComparison.Ordinal) && int.TryParse(assignee[2..], out var tId))
                assignTeamId = tId;
        }
        if (assignStaffId is not null || assignTeamId is not null)
        {
            try
            {
                await tickets.AssignAsync(ticket.Id, assignStaffId, assignTeamId, actor, ct);
            }
            catch (DomainException)
            {
                // Permission refusal on assign only — the created ticket stands.
            }
        }

        // Advanced section (B5): optional first response as a staff Response entry
        // (recipients snapshot, ticket-view Reply parity) + optional internal note.
        // NOTE(S8): the signature radio ("Sig") and the Notify choice apply at
        // outbound-mail render — nothing to persist until the mail subsystem lands.
        if (!string.IsNullOrWhiteSpace(form.Reply))
        {
            var toEmail = await db.UserEmails.Where(e => e.Id == user.DefaultEmailId)
                .Select(e => e.Address).FirstOrDefaultAsync(ct);
            var recipients = JsonSerializer.Serialize(new { to = new[] { $"{user.Name} <{toEmail}>" } });
            await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Response, form.Reply.Trim(), actor,
                new PostOptions { Format = "text", Recipients = recipients }, ct);
        }
        if (!string.IsNullOrWhiteSpace(form.Note))
        {
            await threads.PostAsync(ticket.ThreadId, ThreadEntryType.Note, form.Note.Trim(), actor,
                new PostOptions { Format = "text" }, ct);
        }

        return Redirect($"/agent/ticket-view?id={ticket.Id}");
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Mockup source rows Telefon/E-posta/Web/Diğer; Telefon is the default.</summary>
    private static TicketSource ParseSource(string? source) => source switch
    {
        "email" => TicketSource.Email,
        "web" => TicketSource.Web,
        "other" => TicketSource.Other,
        _ => TicketSource.Phone,
    };

    private static int? ParseOrNull(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>Agent-side field scope (portal twin uses VisibleToUsers); honors the
    /// per-topic disable set from the admin forms tab (HelpTopicForm.Extra).</summary>
    private static List<FormField> AgentFields(HelpTopic topic) =>
        topic.Forms.OrderBy(f => f.Sort)
            .SelectMany(AgentFields)
            .ToList();

    private static List<FormField> AgentFields(HelpTopicForm topicForm)
    {
        var disabled = topicForm.DisabledFieldIds();
        return topicForm.FormDefinition!.Fields
            .Where(f => f.VisibleToAgents && !f.IsDisabled && !disabled.Contains(f.Id))
            .OrderBy(f => f.Sort)
            .ToList();
    }

    /// <summary>Sanitized canned HTML → composer plain text (ticket-view parity).</summary>
    private static string HtmlToComposerText(string html)
    {
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>\\s*", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// Guest domain User + UserEmail, organization auto-linked by email domain
    /// (Organization.Domain, comma-separated — AccountController.LinkDomainUserAsync
    /// pattern). No Identity account: IdentityUserId stays null.
    /// </summary>
    private async Task<User> CreateGuestUserAsync(string name, string address, string? phone,
        ActorContext actor, CancellationToken ct)
    {
        var host = address[(address.IndexOf('@') + 1)..];
        var orgId = (await db.Organizations
                .Where(o => o.Domain != null)
                .Select(o => new { o.Id, o.Domain })
                .ToListAsync(ct))
            .FirstOrDefault(o => o.Domain!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(d => d.Equals(host, StringComparison.OrdinalIgnoreCase)))?.Id;

        var user = new User
        {
            Name = name,
            Phone = phone,
            OrganizationId = orgId,
            Emails = [new UserEmail { Address = address }],
        };
        db.Users.Add(user);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        user.DefaultEmailId = user.Emails[0].Id;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return user;
    }

    private async Task<TicketOpenVm> BuildVmAsync(Staff staff, TicketOpenForm form, User? selectedUser, CancellationToken ct)
    {
        // Topic select: active topics (public AND private — agent scope), mockup
        // order, "Parent / Child" labels; each option carries its routing overrides
        // for the client-side cascade (the server cascade stays authoritative).
        var topics = await db.HelpTopics
            .Where(t => t.IsActive)
            .OrderBy(t => t.Sort)
            .Include(t => t.Parent)
            .Include(t => t.Forms.OrderBy(f => f.Sort))
                .ThenInclude(f => f.FormDefinition)
                    .ThenInclude(d => d!.Fields.OrderBy(x => x.Sort))
            .ToListAsync(ct);

        // Field configurations (S7 form designer output): list-backed choices post
        // the item id, inline designer choices post the option string; defaults
        // pre-fill the control on first render (portal /open twin).
        var configs = topics.SelectMany(AgentFields)
            .DistinctBy(f => f.Id)
            .ToDictionary(f => f.Id, f => FormFieldConfig.Parse(f.Configuration));
        var listIds = configs.Values.Select(c => c.ListId).OfType<int>().Distinct().ToList();
        Dictionary<int, List<TicketOpenChoiceVm>> listChoices = listIds.Count == 0
            ? []
            : await db.ListDefinitions
                .Where(l => listIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    Items = l.Items.Where(i => i.IsEnabled).OrderBy(i => i.Sort)
                        .Select(i => new TicketOpenChoiceVm(i.Id.ToString(), i.Value)).ToList(),
                })
                .ToDictionaryAsync(l => l.Id, l => l.Items, ct);

        IReadOnlyList<TicketOpenChoiceVm> ChoicesOf(FormField f)
        {
            var config = configs[f.Id];
            if (config.ListId is { } listId)
                return listChoices.TryGetValue(listId, out var items) ? items : [];
            return [.. config.Choices.Select(c => new TicketOpenChoiceVm(c, c))];
        }

        var topicVms = topics.Select(t => new TicketOpenTopicVm(
            t.Id,
            t.Parent is null ? t.Name : $"{t.Parent.Name} / {t.Name}",
            t.DepartmentId, t.PriorityId, t.SlaId,
            AgentFields(t).Select(f => new TicketOpenFieldVm(
                f.Id, f.Type, f.Label, f.Hint, f.RequiredForAgents,
                configs[f.Id].Default,
                ChoicesOf(f)))
                .ToList()))
            .ToList();

        var departments = await db.Departments
            .OrderBy(d => d.Id)
            .Select(d => new TicketOpenDeptVm(d.Id, d.Name, d.Signature))
            .ToListAsync(ct);

        var slas = await db.SlaPlans
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => new TicketOpenSlaVm(s.Id, s.Name, s.GracePeriodHours))
            .ToListAsync(ct);

        // Mockup rows low/normal/high/emergency in that order (urgency descending).
        var priorities = await db.TicketPriorities
            .OrderByDescending(p => p.Urgency)
            .Select(p => new TicketOpenPriorityVm(p.Id, p.Key))
            .ToListAsync(ct);

        // Vacation guard (profile Tatil Modu): the open-form Ata select only offers available agents.
        var agents = await db.Staff
            .Where(s => s.IsActive && !s.OnVacation)
            .OrderBy(s => s.FirstName)
            .Select(s => new TicketOpenAssigneeVm($"s:{s.Id}", s.FirstName + " " + s.LastName))
            .ToListAsync(ct);
        var teams = await db.Teams
            .OrderBy(t => t.Id)
            .Select(t => new TicketOpenAssigneeVm($"t:{t.Id}", t.Name))
            .ToListAsync(ct);

        // Canned select: every enabled response — the department is being chosen on
        // this very page, so the dept scoping of ticket-view does not apply yet.
        // admin/settings-kb enable_canned off: the whole menu hides (ticket-view twin).
        var cannedEnabled = (await settings.GetKbAsync(ct)).EnableCanned;
        List<CannedOptionVm> canned = cannedEnabled
            ? await db.CannedResponses
                .Where(c => c.IsEnabled)
                .OrderBy(c => c.Title)
                .Select(c => new CannedOptionVm(c.Id, c.Title))
                .ToListAsync(ct)
            : [];

        // Cascade fallbacks when no topic routes: the settings defaults.
        var defaultDeptId = ParseOrNull(await settings.GetAsync("core", "default_dept_id", ct));
        var defaultSlaId = ParseOrNull(await settings.GetAsync("core", "default_sla_id", ct));

        // Re-render label for the search input after a validation round-trip.
        var userLabel = selectedUser is not null
            ? $"{selectedUser.Name} — {selectedUser.Emails.FirstOrDefault(e => e.Id == selectedUser.DefaultEmailId)?.Address ?? selectedUser.Emails.FirstOrDefault()?.Address}"
            : form.UserQuery;

        return new TicketOpenVm(
            topicVms, departments, slas, priorities, agents, teams, canned, cannedEnabled,
            staff.Signature, defaultDeptId, defaultSlaId, userLabel, form);
    }
}

/// <summary>Posted new-ticket form; validation codes map to i18n in the view (B3).</summary>
public class TicketOpenForm
{
    public int? UserId { get; set; }

    /// <summary>Search input text, kept for re-render after validation errors.</summary>
    public string? UserQuery { get; set; }

    public bool NewUser { get; set; }
    public string? NewUserName { get; set; }
    public string? NewUserEmail { get; set; }
    public string? NewUserPhone { get; set; }

    public string? Cc { get; set; }

    /// <summary>all | user | none. TODO(S8): consumed at outbound-mail send.</summary>
    public string? Notify { get; set; }

    public string? Source { get; set; }

    public int? Topic { get; set; }
    public int? DepartmentId { get; set; }
    public int? SlaId { get; set; }
    public DateOnly? Due { get; set; }

    /// <summary>"s:{staffId}" / "t:{teamId}" / empty (ticket-view assign parity).</summary>
    public string? Assignee { get; set; }

    public string Summary { get; set; } = "";
    public int? Priority { get; set; }
    public string Details { get; set; } = "";

    /// <summary>Dynamic topic-field answers keyed by FormField id.</summary>
    public Dictionary<int, string?> Fields { get; set; } = [];

    public List<IFormFile> Files { get; set; } = [];

    public string? Reply { get; set; }

    /// <summary>none | mine | dept. NOTE(S8): applied at outbound-mail render.</summary>
    public string? Sig { get; set; }

    public string? Note { get; set; }
}

public sealed record TicketOpenVm(
    IReadOnlyList<TicketOpenTopicVm> Topics,
    IReadOnlyList<TicketOpenDeptVm> Departments,
    IReadOnlyList<TicketOpenSlaVm> Slas,
    IReadOnlyList<TicketOpenPriorityVm> Priorities,
    IReadOnlyList<TicketOpenAssigneeVm> Agents,
    IReadOnlyList<TicketOpenAssigneeVm> Teams,
    IReadOnlyList<CannedOptionVm> Canned,
    bool CannedEnabled,
    string StaffSignature,
    int? DefaultDeptId,
    int? DefaultSlaId,
    string? UserLabel,
    TicketOpenForm Form);

public sealed record TicketOpenTopicVm(
    int Id,
    string Label,
    int? DeptId,
    int? PriorityId,
    int? SlaId,
    IReadOnlyList<TicketOpenFieldVm> Fields);

public sealed record TicketOpenFieldVm(
    int Id,
    string Type,
    string Label,
    string? Hint,
    bool Required,
    string? Default,
    IReadOnlyList<TicketOpenChoiceVm> Choices);

/// <summary>Key = posted option value (list item id, or the inline choice string).</summary>
public sealed record TicketOpenChoiceVm(string Key, string Label);

public sealed record TicketOpenDeptVm(int Id, string Name, string Signature);

public sealed record TicketOpenSlaVm(int Id, string Name, int GraceHours);

public sealed record TicketOpenPriorityVm(int Id, string Key);

public sealed record TicketOpenAssigneeVm(string Value, string Label);
