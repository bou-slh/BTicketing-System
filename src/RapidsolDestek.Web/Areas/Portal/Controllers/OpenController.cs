using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal new-ticket page (mockups/portal/open.html): help topic → dynamic form
/// fields (seeded HelpTopicForm output), attach with client file list, validation,
/// and a real ticket through ITicketService (ROADMAP §6.1 open.html row; B3/B5).
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class OpenController(AppDbContext db, ITicketService tickets, IFileStore files, ISettingsService settings) : Controller
{
    [HttpGet("/open")]
    [NavKey("new")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        return View(await BuildVmAsync(user, new OpenForm(), ct));
    }

    [HttpPost("/open")]
    [NavKey("new")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(OpenForm form, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null)
            return Forbid(); // portal login not linked to a domain User

        // Topic must be a real public+active topic (same scope the select offers).
        var topic = form.Topic is { } topicId
            ? await db.HelpTopics
                .Include(t => t.Forms.OrderBy(f => f.Sort))
                    .ThenInclude(f => f.FormDefinition)
                        .ThenInclude(d => d!.Fields.OrderBy(x => x.Sort))
                .SingleOrDefaultAsync(t => t.Id == topicId && t.IsPublic && t.IsActive, ct)
            : null;
        if (form.Topic is not null && topic is null) // null Topic already carries errTopic via [Required]
            ModelState.AddModelError(nameof(OpenForm.Topic), "errTopic");

        // Dynamic topic fields (B3): required-for-users fields must be filled.
        List<FormField> topicFields = topic is null ? [] : PortalFields(topic);
        foreach (var field in topicFields.Where(f => f.RequiredForUsers))
        {
            form.Fields.TryGetValue(field.Id, out var raw);
            if (string.IsNullOrWhiteSpace(raw))
                ModelState.AddModelError($"Fields[{field.Id}]", $"errField:{field.Label}");
        }

        // Priority select posts a real priority id; only the offered ones are accepted.
        var priorityId = form.Priority is { } pid
            ? await db.TicketPriorities
                .Where(p => p.Id == pid && PortalPriorityKeys.Contains(p.Key))
                .Select(p => (int?)p.Id)
                .SingleOrDefaultAsync(ct)
            : null;

        // Attachment size cap (admin/settings-system Ekler, S7): attachments/max_size_mb
        // is enforced at every upload ingress — refuse before anything is created.
        var attachLimits = await settings.GetAttachmentsAsync(ct);
        if (form.Files.Any(f => f.Length > attachLimits.MaxSizeBytes))
            ModelState.AddModelError(nameof(OpenForm.Files), "errAttachTooBig");

        if (!ModelState.IsValid)
            return View(await BuildVmAsync(user, form, ct));

        // Plain-text portal input → HTML body: encode, keep line breaks; the thread
        // ingress (ThreadService) sanitizes at post time as well.
        var encoder = HtmlEncoder.Default;
        var body = string.Join("<br>", form.Details.Trim().Split('\n')
            .Select(line => encoder.Encode(line.TrimEnd('\r'))));

        // TODO(S7): the reference field belongs to the built-in ticket form (form
        // designer output, ROADMAP §6.3 forms row). Until the designer exists it is
        // preserved as a labelled line inside the first message.
        if (!string.IsNullOrWhiteSpace(form.Reference))
            body += $"<br><br>{encoder.Encode(ReferenceLabel)}: {encoder.Encode(form.Reference.Trim())}";

        var actor = ActorContext.ForUser(user, HttpContext.Connection.RemoteIpAddress?.ToString());

        // Real creation through the domain service: sequence number, help-topic
        // routing cascade (dept/priority/SLA/status/assignee) and the initial
        // Message thread entry all happen inside CreateAsync.
        Ticket ticket;
        try
        {
            ticket = await tickets.CreateAsync(new TicketCreateRequest
            {
                UserId = user.Id,
                Subject = form.Summary.Trim(),
                Body = body,
                HelpTopicId = topic!.Id,
                PriorityId = priorityId,
                Source = TicketSource.Web,
            }, actor, ct);
        }
        catch (DomainRuleException ex) when (ex.Code == "max-open-exceeded")
        {
            // tickets.max_open_per_user (S7 admin/settings-tickets); the mockup's
            // maxOpenHelp promises rejection + notice. TODO(S8): overlimit email.
            ModelState.AddModelError(nameof(OpenForm.Summary), "errMaxOpen");
            return View(await BuildVmAsync(user, form, ct));
        }

        // B5: uploaded files → IFileStore content + StoredFile/Attachment rows on
        // the initial Message entry (DomainSeeder hero-attachment wiring parity).
        var messageEntryId = await db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId && e.Type == ThreadEntryType.Message)
            .Select(e => e.Id)
            .SingleAsync(ct);
        foreach (var upload in form.Files.Where(f => f.Length > 0))
        {
            await using var content = upload.OpenReadStream();
            var stored = await files.SaveAsync(content, upload.FileName,
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
        // (osTicket form_entry parity; the S7 designer will edit these forms).
        foreach (var topicForm in topic.Forms.OrderBy(f => f.Sort))
        {
            var entry = new FormEntry
            {
                FormDefinitionId = topicForm.FormDefinitionId,
                ObjectType = FormObjectType.Ticket,
                ObjectId = ticket.Id,
                Sort = topicForm.Sort,
            };
            foreach (var field in PortalFields(topicForm.FormDefinition!))
            {
                form.Fields.TryGetValue(field.Id, out var raw);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                int? valueId = null;
                var value = raw.Trim();
                if (field.Type == "choices" && int.TryParse(value, out var itemId)
                    && ListId(field) is { } listId)
                {
                    var item = await db.ListDefinitions
                        .Where(l => l.Id == listId)
                        .SelectMany(l => l.Items)
                        .SingleOrDefaultAsync(i => i.Id == itemId, ct);
                    if (item is null)
                        continue;
                    valueId = item.Id;
                    value = item.Value;
                }
                entry.Values.Add(new FormEntryValue { FormFieldId = field.Id, Value = value, ValueId = valueId });
            }
            if (entry.Values.Count > 0)
                db.FormEntries.Add(entry);
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        return Redirect("/tickets");
    }

    // Mockup priority options (Normal / Yüksek / Kritik) over real priority rows.
    private static readonly string[] PortalPriorityKeys = ["normal", "high", "emergency"];

    // Sample-data language is Turkish by convention (mockups/CONVENTIONS.md): the
    // fallback reference line stored in the thread body keeps the canonical label.
    private const string ReferenceLabel = "İlgili kayıt / referans";

    /// <summary>Principal → domain User via User.IdentityUserId (Home/Tickets parity).</summary>
    private async Task<User?> CurrentUserAsync(CancellationToken ct) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var identityId)
            ? await db.Users
                .Include(u => u.Organization)
                .SingleOrDefaultAsync(u => u.IdentityUserId == identityId, ct)
            : null;

    private static List<FormField> PortalFields(HelpTopic topic) =>
        topic.Forms.OrderBy(f => f.Sort)
            .SelectMany(f => PortalFields(f.FormDefinition!))
            .ToList();

    private static List<FormField> PortalFields(FormDefinition definition) =>
        definition.Fields
            .Where(f => f.VisibleToUsers && !f.IsDisabled)
            .OrderBy(f => f.Sort)
            .ToList();

    private static int? ListId(FormField field)
    {
        if (string.IsNullOrWhiteSpace(field.Configuration))
            return null;
        try
        {
            return JsonDocument.Parse(field.Configuration).RootElement
                .TryGetProperty("list_id", out var id) && id.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<OpenVm> BuildVmAsync(User? user, OpenForm form, CancellationToken ct)
    {
        // Topic select: public+active topics in mockup order; each carries its
        // designed extra fields (choices resolved to enabled list items).
        var topics = await db.HelpTopics
            .Where(t => t.IsPublic && t.IsActive)
            .OrderBy(t => t.Sort)
            .Include(t => t.Forms.OrderBy(f => f.Sort))
                .ThenInclude(f => f.FormDefinition)
                    .ThenInclude(d => d!.Fields.OrderBy(x => x.Sort))
            .ToListAsync(ct);

        var listIds = topics.SelectMany(PortalFields).Select(ListId)
            .OfType<int>().Distinct().ToList();
        Dictionary<int, List<OpenFieldChoiceVm>> choices = listIds.Count == 0
            ? []
            : await db.ListDefinitions
                .Where(l => listIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    Items = l.Items.Where(i => i.IsEnabled).OrderBy(i => i.Sort)
                        .Select(i => new OpenFieldChoiceVm(i.Id, i.Value)).ToList(),
                })
                .ToDictionaryAsync(l => l.Id, l => l.Items, ct);

        var topicVms = topics.Select(t => new OpenTopicVm(
            t.Id,
            t.Name,
            PortalFields(t).Select(f => new OpenTopicFieldVm(
                f.Id,
                f.Type,
                f.Label,
                f.Hint,
                f.RequiredForUsers,
                ListId(f) is { } listId && choices.TryGetValue(listId, out var items) ? items : []))
                .ToList()))
            .ToList();

        // Mockup priority rows (Normal/Yüksek/Kritik) mapped onto real priority ids.
        var priorities = await db.TicketPriorities
            .Where(p => PortalPriorityKeys.Contains(p.Key))
            .OrderByDescending(p => p.Urgency)
            .Select(p => new OpenPriorityVm(p.Id, p.Key))
            .ToListAsync(ct);

        return new OpenVm(user?.Organization?.Name ?? user?.Name ?? "", topicVms, priorities, form);
    }
}

/// <summary>Posted new-ticket form; validation codes are mapped to i18n in the view (B3).</summary>
public class OpenForm
{
    [Required(ErrorMessage = "errTopic")]
    public int? Topic { get; set; }

    [Required(ErrorMessage = "errSummary")]
    public string Summary { get; set; } = "";

    public int? Priority { get; set; }

    public string? Reference { get; set; }

    [Required(ErrorMessage = "errDetails")]
    public string Details { get; set; } = "";

    /// <summary>Dynamic topic-field answers keyed by FormField id.</summary>
    public Dictionary<int, string?> Fields { get; set; } = [];

    public List<IFormFile> Files { get; set; } = [];
}

public sealed record OpenVm(
    string CustomerName,
    IReadOnlyList<OpenTopicVm> Topics,
    IReadOnlyList<OpenPriorityVm> Priorities,
    OpenForm Form);

public sealed record OpenTopicVm(int Id, string Name, IReadOnlyList<OpenTopicFieldVm> Fields);

public sealed record OpenTopicFieldVm(
    int Id,
    string Type,
    string Label,
    string? Hint,
    bool RequiredForUsers,
    IReadOnlyList<OpenFieldChoiceVm> Choices);

public sealed record OpenFieldChoiceVm(int Id, string Value);

public sealed record OpenPriorityVm(int Id, string Key);
