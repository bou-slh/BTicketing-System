using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin help topics list + editor (mockups/admin/helptopics.html +
/// helptopic-edit.html, ROADMAP §6.3). List: the B1 engine over the REAL HelpTopic
/// hierarchy (children indented under their parent, departments precedent; search
/// flattens), the toolbar sort-mode select persists tickets/topic_sort_mode
/// (osTicket help_topic_sort_mode) which owns the default row order, bulk
/// enable/disable/delete behind the mockup's dlg-more with a reference guard
/// (child topics / tickets block deletion — teams precedent). Editor: every mockup
/// control persists to a real HelpTopic column — the routing cascade the S4
/// TicketService consumes (dept/status/priority/SLA/thank-you page/staff-or-team
/// assignment/numbering) plus the B3 number-format radio gating and the forms tab
/// (B4): attached HelpTopicForm rows with per-field enable checkboxes persisted as
/// the osTicket-parity Extra disable set, consumed by both open pages.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class HelpTopicsController(AppDbContext db, ISettingsService settings) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["topic", "updated"];

    // ---- helptopics.html (B1 list over the tree) --------------------------------------

    [HttpGet("/admin/helptopics")]
    [NavKey("helptopics")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        // Topics are few — materialize once, then order as a tree in memory
        // (departments precedent). Without an explicit header sort the persisted
        // sort mode owns the sibling order: manual = the Sort column, alphabetical
        // = Turkish collation.
        var mode = await settings.GetAsync("tickets", "topic_sort_mode", ct) ?? "manual";
        var all = await db.HelpTopics
            .Select(t => new TopicRowVm(
                t.Id, t.Name, t.ParentId, t.Sort,
                t.IsArchived ? TopicStatus.Archived : t.IsActive ? TopicStatus.Active : TopicStatus.Disabled,
                t.IsPublic,
                db.TicketPriorities.Where(p => p.Id == t.PriorityId).Select(p => p.Key).FirstOrDefault(),
                db.TicketPriorities.Where(p => p.Id == t.PriorityId).Select(p => p.Name).FirstOrDefault(),
                db.Departments.Where(d => d.Id == t.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                t.UpdatedAt ?? t.CreatedAt,
                "", 0))
            .ToListAsync(ct);

        // Display names are the full "Parent / Child" chain (mockup child rows show
        // "Bordro / Yol Ücreti"); build them before any flattening.
        var byId = all.ToDictionary(t => t.Id);
        all = [.. all.Select(t => t with { FullName = FullNameOf(t, byId) })];
        byId = all.ToDictionary(t => t.Id);

        var sortKey = SortKeys.Contains(sort) ? sort! : "";
        var desc = dir == "desc";

        List<TopicRowVm> ordered;
        var searching = !string.IsNullOrWhiteSpace(q);
        if (searching)
        {
            var needle = q!.Trim();
            ordered = [.. SortSiblings(all.Where(t =>
                t.FullName.Contains(needle, StringComparison.CurrentCultureIgnoreCase)), sortKey, desc, mode)];
        }
        else
        {
            ordered = [];
            void Walk(int? parentId, int depth)
            {
                foreach (var t in SortSiblings(all.Where(x => x.ParentId == parentId), sortKey, desc, mode))
                {
                    ordered.Add(t with { Depth = depth });
                    Walk(t.Id, depth + 1);
                }
            }
            Walk(null, 0);
            // Orphan safety: rows whose parent chain is broken still render (flat).
            ordered.AddRange(all.Where(t => !ordered.Any(o => o.Id == t.Id)));
        }

        var total = ordered.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = ordered.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return View(new TopicsIndexVm(
            q, sortKey, desc, mode, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows, Flat: searching));
    }

    /// <summary>Toolbar sort-mode "Kaydet": persists tickets/topic_sort_mode (osTicket
    /// help_topic_sort_mode) — the list's default order follows it.</summary>
    [HttpPost("/admin/helptopics/sort-mode")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SortMode(string? mode, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
            await settings.SetAsync("tickets", "topic_sort_mode", mode == "alpha" ? "alpha" : "manual", ct);
        return ToastBack("ht.toastMode", returnUrl: returnUrl);
    }

    /// <summary>
    /// dlg-more bulk actions over the selection. Delete guard: topics referenced by
    /// child topics or tickets are skipped and reported in the partial toast (the
    /// mockup has no reassign dialog — guard-skip per the teams precedent, flagged).
    /// </summary>
    [HttpPost("/admin/helptopics/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("ht.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var topics = await db.HelpTopics.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - topics.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var topic in topics)
            {
                switch (act)
                {
                    case "enable":
                        topic.IsActive = true;
                        topic.IsArchived = false;
                        ok++;
                        break;
                    case "disable":
                        topic.IsActive = false;
                        topic.IsArchived = false;
                        ok++;
                        break;
                    case "delete":
                        if (await IsTopicInUseAsync(topic.Id, ct))
                        {
                            skipped++;
                        }
                        else
                        {
                            db.HelpTopics.Remove(topic); // forms + FAQ links cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["TopicsToastOk"] = ok;
        TempData["TopicsToastSkipped"] = skipped;
        TempData["TopicsToast"] = skipped > 0 ? "ht.bulkPartial" : "ht.bulkDone";
        if (skipped > 0)
            TempData["TopicsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helptopic-edit.html ------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Konu" create form
    /// (the mockup's new-button links straight to helptopic-edit.html).</summary>
    [HttpGet("/admin/helptopic-edit")]
    [NavKey("helptopics")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        HelpTopic? topic = null;
        if (id is not null)
        {
            topic = await db.HelpTopics
                .Include(t => t.Forms.OrderBy(f => f.Sort))
                    .ThenInclude(f => f.FormDefinition)
                        .ThenInclude(d => d!.Fields.OrderBy(x => x.Sort))
                .SingleOrDefaultAsync(t => t.Id == id, ct);
            if (topic is null)
                return NotFound();
        }

        return View(await BuildEditVmAsync(topic, ct));
    }

    /// <summary>
    /// Whole-page save (B3): info + routing defaults + numbering + the forms tab in
    /// one post. formIds carry the attached forms in DOM order; enabledFields the
    /// checked field ids across all attached forms (unchecked = per-topic disabled,
    /// persisted into HelpTopicForm.Extra — osTicket extra.disable parity).
    /// </summary>
    [HttpPost("/admin/helptopic-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, string? status, bool isPublic, int? parentId, string? notes,
        int? deptId, int? statusId, int? priorityId, int? slaId, int? pageId, string? assign,
        string? numMode, string? numberFormat, string? sequence, bool noAutoresp,
        int[] formIds, int[] enabledFields, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "hte.errName");
        if (await db.HelpTopics.AnyAsync(t => t.Name == name && t.ParentId == parentId && t.Id != id, ct))
            return EditToastBack(id, "hte.errNameInUse");

        // Parent guard: must exist; never self or a descendant (walk the candidate's
        // ancestor chain — topics carry no materialized path).
        if (parentId is not null)
        {
            var parents = await db.HelpTopics.Select(t => new { t.Id, t.ParentId }).ToListAsync(ct);
            if (!parents.Any(t => t.Id == parentId))
                return EditToastBack(id, "hte.errParent");
            if (id is not null)
            {
                var probe = parentId;
                var hops = 0;
                while (probe is not null && hops++ < 100)
                {
                    if (probe == id)
                        return EditToastBack(id, "hte.errParentCycle");
                    probe = parents.FirstOrDefault(t => t.Id == probe)?.ParentId;
                }
            }
        }

        // B3 gating server side: the custom radio owns the format input; the format
        // needs at least one '#' digit slot (settings-tickets errFormat precedent).
        var customFormat = numMode == "custom";
        numberFormat = (numberFormat ?? "").Trim();
        if (customFormat && (numberFormat.Length == 0 || numberFormat.Length > 32 || !numberFormat.Contains('#')))
            return EditToastBack(id, "hte.errFormat");

        // Soft-ref selects: silently drop ids that no longer exist (row deleted since render).
        if (deptId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep, ct)) deptId = null;
        if (statusId is { } st && !await db.TicketStatuses.AnyAsync(s => s.Id == st, ct)) statusId = null;
        if (priorityId is { } pr && !await db.TicketPriorities.AnyAsync(p => p.Id == pr, ct)) priorityId = null;
        if (slaId is { } sla && !await db.SlaPlans.AnyAsync(s => s.Id == sla, ct)) slaId = null;
        if (pageId is { } pg && !await db.SitePages.AnyAsync(p => p.Id == pg, ct)) pageId = null;

        // Auto-assignment select: "s:{staffId}" / "t:{teamId}" (ticket-open Ata parity).
        int? assignStaffId = null, assignTeamId = null;
        if (assign is { Length: > 2 })
        {
            if (assign.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(assign[2..], out var sId)
                && await db.Staff.AnyAsync(s => s.Id == sId, ct))
                assignStaffId = sId;
            else if (assign.StartsWith("t:", StringComparison.Ordinal) && int.TryParse(assign[2..], out var tId)
                && await db.Teams.AnyAsync(t => t.Id == tId, ct))
                assignTeamId = tId;
        }

        // Sequence select: "" = the global numbering settings, "random" = per-topic
        // random digits (osTicket sequence_id 0), otherwise a real Sequence row.
        var useRandom = sequence == "random";
        int? sequenceId = null;
        if (!useRandom && int.TryParse(sequence, out var seqId)
            && await db.Sequences.AnyAsync(s => s.Id == seqId, ct))
            sequenceId = seqId;

        // Forms tab: only existing attachable (General) definitions count; posted
        // order is the Sort order.
        var wantedForms = formIds.Distinct().ToArray();
        var validForms = await db.FormDefinitions
            .Where(f => wantedForms.Contains(f.Id) && f.Kind == FormKind.General)
            .Include(f => f.Fields)
            .ToListAsync(ct);
        var enabled = enabledFields.ToHashSet();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            HelpTopic topic;
            if (id is null)
            {
                topic = new HelpTopic
                {
                    Name = name,
                    Sort = await db.HelpTopics.MaxAsync(t => (int?)t.Sort, ct) is { } max ? max + 1 : 1,
                };
                db.HelpTopics.Add(topic);
            }
            else
            {
                var found = await db.HelpTopics.Include(t => t.Forms)
                    .SingleOrDefaultAsync(t => t.Id == id, ct);
                if (found is null)
                    return NotFound();
                topic = found;
            }

            topic.Name = name;
            (topic.IsActive, topic.IsArchived) = status switch
            {
                "disabled" => (false, false),
                "archived" => (false, true),
                _ => (true, false),
            };
            topic.IsPublic = isPublic;
            topic.ParentId = parentId;
            topic.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            topic.DepartmentId = deptId;
            topic.StatusId = statusId;
            topic.PriorityId = priorityId;
            topic.SlaId = slaId;
            topic.SitePageId = pageId;
            topic.StaffId = assignStaffId;
            topic.TeamId = assignTeamId;
            topic.NumberFormat = customFormat ? numberFormat : null;
            topic.SequenceId = sequenceId;
            topic.UseRandomNumbers = useRandom;
            topic.NoAutoResponse = noAutoresp;

            // ---- Forms reconciliation (B4) --------------------------------------------
            var keep = wantedForms.Where(fid => validForms.Any(f => f.Id == fid)).ToList();
            topic.Forms.RemoveAll(f => !keep.Contains(f.FormDefinitionId));
            foreach (var (formId, index) in keep.Select((fid, i) => (fid, i)))
            {
                var definition = validForms.Single(f => f.Id == formId);
                var disabled = definition.Fields
                    .Where(f => !enabled.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToList();
                var row = topic.Forms.FirstOrDefault(f => f.FormDefinitionId == formId);
                if (row is null)
                {
                    topic.Forms.Add(new HelpTopicForm
                    {
                        FormDefinitionId = formId,
                        Sort = index + 1,
                        Extra = HelpTopicForm.BuildExtra(disabled),
                    });
                }
                else
                {
                    row.Sort = index + 1;
                    row.Extra = HelpTopicForm.BuildExtra(disabled);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["TopicsToast"] = id is null ? "ht.toastCreated" : "ht.toastSaved";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Editor delete button (dlg-delete confirm): a topic referenced by tickets or
    /// child topics is undeletable — refused with an error toast (the mockup defines
    /// no reassign flow; disable/archive instead, flagged for canon).
    /// </summary>
    [HttpPost("/admin/helptopic-edit/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var topic = await db.HelpTopics.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (topic is null)
            return NotFound();
        if (await IsTopicInUseAsync(id, ct))
            return EditToastBack(id, "hte.errInUse");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.HelpTopics.Remove(topic); // forms + FAQ links cascade
            await db.SaveChangesAsync(ct);
        }

        TempData["TopicsToast"] = "ht.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Referenced topics are undeletable: child topics and tickets point here
    /// (FAQ links and topic forms cascade; thread events are snapshots).</summary>
    private async Task<bool> IsTopicInUseAsync(int topicId, CancellationToken ct) =>
        await db.HelpTopics.AnyAsync(t => t.ParentId == topicId, ct)
        || await db.Tickets.AnyAsync(t => t.HelpTopicId == topicId, ct);

    private async Task<TopicEditVm> BuildEditVmAsync(HelpTopic? topic, CancellationToken ct)
    {
        // Parent select: every topic except self and its descendants (cycle guard,
        // walked in memory — topics carry no materialized path).
        var all = await db.HelpTopics
            .Select(t => new { t.Id, t.Name, t.ParentId, t.Sort })
            .ToListAsync(ct);
        var blocked = new HashSet<int>();
        if (topic is not null)
        {
            blocked.Add(topic.Id);
            bool grew;
            do
            {
                grew = false;
                foreach (var t in all.Where(x => x.ParentId is { } p && blocked.Contains(p) && !blocked.Contains(x.Id)))
                {
                    blocked.Add(t.Id);
                    grew = true;
                }
            }
            while (grew);
        }

        var names = all.ToDictionary(t => t.Id, t => (t.Name, t.ParentId));
        string FullName(int tid)
        {
            var parts = new List<string>();
            int? probe = tid;
            var hops = 0;
            while (probe is { } pid && names.TryGetValue(pid, out var entry) && hops++ < 100)
            {
                parts.Insert(0, entry.Name);
                probe = entry.ParentId;
            }
            return string.Join(" / ", parts);
        }

        var parents = all.Where(t => !blocked.Contains(t.Id))
            .OrderBy(t => t.Sort)
            .Select(t => new OptionVm(t.Id, FullName(t.Id)))
            .ToList();

        // Forms tab data: attached rows (fields + per-topic disable set) and every
        // attachable General definition (client templates for "Form ekle").
        // Bulk-disabled forms (admin/forms dlg-more, S7) drop out of the attach
        // list unless this topic already carries them (schedules IsActive precedent).
        var attachedIds = topic?.Forms.Select(f => f.FormDefinitionId).ToList() ?? [];
        var generalForms = await db.FormDefinitions
            .Where(f => f.Kind == FormKind.General && (f.IsActive || attachedIds.Contains(f.Id)))
            .Include(f => f.Fields.OrderBy(x => x.Sort))
            .OrderBy(f => f.Title)
            .ToListAsync(ct);
        TopicFormVm ToVm(FormDefinition definition, IReadOnlySet<int> disabled) => new(
            definition.Id, definition.Title,
            [.. definition.Fields.OrderBy(f => f.Sort).Select(f => new TopicFormFieldVm(
                f.Id, f.Label, f.Type, f.Name, f.VisibleToUsers, !disabled.Contains(f.Id)))]);
        var attached = (topic?.Forms.OrderBy(f => f.Sort) ?? Enumerable.Empty<HelpTopicForm>())
            .Where(f => f.FormDefinition is not null)
            .Select(f => ToVm(f.FormDefinition!, f.DisabledFieldIds()))
            .ToList();
        var noneDisabled = new HashSet<int>();
        var available = generalForms.Select(f => ToVm(f, noneDisabled)).ToList();

        return new TopicEditVm(
            topic,
            topic is null ? null : FullName(topic.Id),
            parents,
            await db.Departments.OrderBy(d => d.Path)
                .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct),
            // Initial-status select: agent-selectable statuses (osTicket lists them all).
            await db.TicketStatuses.Where(s => s.IsEnabled && !s.IsInternal).OrderBy(s => s.Sort)
                .Select(s => new StatusOptionVm(s.Id, s.Key, s.Name)).ToListAsync(ct),
            // Mockup order Düşük → Kritik = descending urgency value (lower = more urgent).
            await db.TicketPriorities.OrderByDescending(p => p.Urgency)
                .Select(p => new StatusOptionVm(p.Id, p.Key, p.Name)).ToListAsync(ct),
            await db.SlaPlans.OrderBy(s => s.Name).Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            // Thank-you select: real ThankYou-type SitePages (settings-company per-type
            // precedent; the mockup's "Hoş Geldiniz" option is sample state, flagged).
            await db.SitePages.Where(p => p.Type == SitePageType.ThankYou).OrderBy(p => p.Name)
                .Select(p => new OptionVm(p.Id, p.Name)).ToListAsync(ct),
            await db.Staff.Where(s => s.IsActive).OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new OptionVm(s.Id, s.FirstName + " " + s.LastName)).ToListAsync(ct),
            await db.Teams.OrderBy(t => t.Name).Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct),
            await db.Sequences.OrderBy(s => s.Id).Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            attached,
            available);
    }

    private static string FullNameOf(TopicRowVm row, Dictionary<int, TopicRowVm> byId)
    {
        var parts = new List<string> { row.Name };
        var probe = row.ParentId;
        var hops = 0;
        while (probe is { } pid && byId.TryGetValue(pid, out var parent) && hops++ < 100)
        {
            parts.Insert(0, parent.Name);
            probe = parent.ParentId;
        }
        return string.Join(" / ", parts);
    }

    /// <summary>Sibling order: explicit header sort wins; otherwise the persisted
    /// sort mode (manual = Sort column, alpha = Turkish collation).</summary>
    private static IEnumerable<TopicRowVm> SortSiblings(
        IEnumerable<TopicRowVm> rows, string sortKey, bool desc, string mode)
    {
        var tr = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
        IOrderedEnumerable<TopicRowVm> sorted = sortKey switch
        {
            "topic" => desc ? rows.OrderByDescending(t => t.Name, tr) : rows.OrderBy(t => t.Name, tr),
            "updated" => desc ? rows.OrderByDescending(t => t.Updated) : rows.OrderBy(t => t.Updated),
            _ when mode == "alpha" => rows.OrderBy(t => t.Name, tr),
            _ => rows.OrderBy(t => t.Sort),
        };
        return sorted.ThenBy(t => t.Id);
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["TopicsToast"] = key;
        if (error)
            TempData["TopicsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["TopicEditToast"] = key;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public enum TopicStatus
{
    Active,
    Disabled,
    Archived,
}

public sealed record TopicsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    string Mode,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<TopicRowVm> Rows,
    bool Flat);

public sealed record TopicRowVm(
    int Id,
    string Name,
    int? ParentId,
    int Sort,
    TopicStatus Status,
    bool IsPublic,
    string? PriorityKey,
    string? PriorityName,
    string? Department,
    DateTimeOffset Updated,
    string FullName,
    int Depth);

public sealed record TopicEditVm(
    HelpTopic? Topic,
    string? Crumb,
    IReadOnlyList<OptionVm> Parents,
    IReadOnlyList<OptionVm> Departments,
    IReadOnlyList<StatusOptionVm> Statuses,
    IReadOnlyList<StatusOptionVm> Priorities,
    IReadOnlyList<OptionVm> Slas,
    IReadOnlyList<OptionVm> ThankYouPages,
    IReadOnlyList<OptionVm> Agents,
    IReadOnlyList<OptionVm> Teams,
    IReadOnlyList<OptionVm> Sequences,
    IReadOnlyList<TopicFormVm> AttachedForms,
    IReadOnlyList<TopicFormVm> AvailableForms);

/// <summary>Select option that localizes through a stable key (status.*/priority.*).</summary>
public sealed record StatusOptionVm(int Id, string Key, string Name);

public sealed record TopicFormVm(int Id, string Title, IReadOnlyList<TopicFormFieldVm> Fields);

public sealed record TopicFormFieldVm(
    int Id, string Label, string Type, string Variable, bool VisibleToUsers, bool Enabled);
