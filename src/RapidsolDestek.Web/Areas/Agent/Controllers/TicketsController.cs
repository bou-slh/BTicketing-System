using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent ticket list (mockups/agent/tickets.html, ROADMAP §6.2): queue tree with live
/// counts over the seeded SavedQueue tree, the B1 list engine (sort/search/filters/
/// pagination), the dlg-advsearch advanced search incl. save-as-queue, CSV export and
/// the bulk actions that map onto existing TicketService transitions.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class TicketsController(
    AppDbContext db,
    IQueueEngine queueEngine,
    ITicketService ticketService,
    ISettingsService settings,
    IMemoryCache cache) : Controller
{
    private static readonly TimeSpan CountsTtl = TimeSpan.FromSeconds(30);

    [HttpGet("/agent/tickets")]
    [NavKey("tickets")]
    public async Task<IActionResult> Index(
        int? queue, string? q, string[] st, string[] pr, string? sort, string? dir,
        string[] ff, string[] fo, string[] fv, string[] cols, int page = 1, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        var rules = AdvRule.Parse(ff, fo, fv);
        var isAdv = rules.Count > 0;

        // S7 admin/settings-tickets: tickets.default_queue_id picks the landing queue,
        // tickets.top_level_counts gates the queue-tree badges.
        var behavior = await settings.GetTicketBehaviorAsync(ct);

        var queues = await LoadQueuesAsync(staff.Id, ct);
        var activeQueue = isAdv ? null : ResolveActiveQueue(queues, queue, behavior.DefaultQueueId);

        // ---- Queue tree with live counts (cached briefly per staff — the tree runs
        // one COUNT per countable queue; 30s staleness is acceptable, ROADMAP note). ----
        var counts = behavior.TopLevelCounts
            ? await QueueCountsAsync(queues, staff.Id, actor, ct)
            : new Dictionary<int, int>();

        // ---- List query: queue criteria (∩ quick search via criteria.Search →
        // tsvector over number/subject/thread bodies) + adv rules + toolbar filters. ----
        var criteria = isAdv ? QueueCriteria.Empty : QueueCriteria.Parse(activeQueue?.Criteria);
        if (!string.IsNullOrWhiteSpace(q))
            criteria = WithSearch(criteria, q.Trim());

        var query = await queueEngine.BuildAsync(criteria, actor, ct);
        query = TicketListEngine.ApplyRules(query, rules);
        query = TicketListEngine.ApplyStatusFilter(query, st);
        query = TicketListEngine.ApplyPriorityFilter(query, pr);

        var total = await query.CountAsync(ct);

        var (sortKey, desc) = ResolveSort(sort, dir, activeQueue);
        query = TicketListEngine.ApplySort(query, sortKey, desc);

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)TicketListEngine.PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await ProjectRows(query)
            .Skip((page - 1) * TicketListEngine.PageSize)
            .Take(TicketListEngine.PageSize)
            .ToListAsync(ct);

        var statuses = await db.TicketStatuses
            .Where(s => s.IsEnabled && !s.IsInternal)
            .OrderBy(s => s.Sort)
            .Select(s => new StatusOptionVm(s.Id, s.Key))
            .ToListAsync(ct);

        return View(new TicketsIndexVm(
            activeQueue?.Id, isAdv, q, st, pr, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * TicketListEngine.PageSize + 1,
            Math.Min(page * TicketListEngine.PageSize, total),
            ResolveColumns(activeQueue, cols),
            BuildTree(queues, activeQueue, counts),
            rows, rules, cols, statuses));
    }

    /// <summary>Streamed CSV of the current list — visible columns, active filter, all pages.</summary>
    [HttpGet("/agent/tickets/export")]
    public async Task<IActionResult> Export(
        int? queue, string? q, string[] st, string[] pr, string? sort, string? dir,
        string[] ff, string[] fo, string[] fv, string[] cols,
        [FromServices] IStringLocalizerFactory localizerFactory,
        [FromServices] IStringLocalizer<SharedResources> sl,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        var rules = AdvRule.Parse(ff, fo, fv);
        var queues = await LoadQueuesAsync(staff.Id, ct);
        var activeQueue = rules.Count > 0 ? null : ResolveActiveQueue(queues, queue);

        var criteria = rules.Count > 0 ? QueueCriteria.Empty : QueueCriteria.Parse(activeQueue?.Criteria);
        if (!string.IsNullOrWhiteSpace(q))
            criteria = WithSearch(criteria, q.Trim());

        var query = await queueEngine.BuildAsync(criteria, actor, ct);
        query = TicketListEngine.ApplyRules(query, rules);
        query = TicketListEngine.ApplyStatusFilter(query, st);
        query = TicketListEngine.ApplyPriorityFilter(query, pr);
        var (sortKey, desc) = ResolveSort(sort, dir, activeQueue);
        query = TicketListEngine.ApplySort(query, sortKey, desc);

        // Exported field set: explicit cols override > the queue's configured
        // export column set (S7 admin/queues Dışa Aktarma tab, SavedQueueExportField
        // rows — heading override included) > the visible columns (S6 behavior).
        var exportSet = cols.Length == 0
            ? (activeQueue?.ExportFields ?? [])
                .OrderBy(f => f.Sort).ThenBy(f => f.Id)
                .Select(f => (Key: TicketListEngine.ExportKeyFromPath(f.FieldPath), f.Heading))
                .Where(f => f.Key is not null)
                .Select(f => (Key: f.Key!, f.Heading))
                .ToList()
            : [];
        var visible = exportSet.Count > 0
            ? exportSet.Select(f => f.Key).ToList()
            : (IReadOnlyList<string>)ResolveColumns(activeQueue, cols);

        // Page-resx headers (same keys the table renders with).
        var pageL = localizerFactory.Create("Areas.Agent.Views.Tickets.Index", typeof(Program).Assembly.GetName().Name!);
        string Header(string key) => key switch
        {
            "no" => pageL["tq.colNo"],
            "updated" => sl["common.updated"],
            "subject" => pageL["tq.colSubject"],
            "user" => pageL["tq.colUser"],
            "org" => pageL["tq.colOrg"],
            "priority" => pageL["tq.colPriority"],
            "effort" => pageL["tq.colEffort"],
            "status" => sl["common.status"],
            "assigned" => pageL["tq.colAssigned"],
            "sla" => pageL["tq.colSla"],
            "created" => sl["common.created"],
            _ => key,
        };
        string Cell(TicketRowVm r, string key) => key switch
        {
            "no" => r.Number,
            "updated" => r.Updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            "subject" => r.Subject,
            "user" => r.UserName,
            "org" => r.OrgName ?? "",
            "priority" => sl[$"priority.{r.PriorityKey}"],
            "effort" => r.EffortHours is { } h ? h.ToString("0.##") : "",
            "status" => sl[$"status.{r.StatusKey}"],
            "assigned" => r.AssigneeName ?? "",
            "sla" => r.Due?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
            "created" => r.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            "dept" => r.DeptName,
            _ => "",
        };
        var headers = exportSet.Count > 0
            ? exportSet.Select(f => f.Heading ?? Header(f.Key))
            : visible.Select(Header);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=talepler.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", headers.Select(TicketListEngine.Csv)));
        await foreach (var row in ProjectRows(query).AsAsyncEnumerable().WithCancellation(ct))
            await writer.WriteLineAsync(string.Join(",", visible.Select(k => TicketListEngine.Csv(Cell(row, k)))));
        return new EmptyResult();
    }

    /// <summary>
    /// dlg-advsearch "Aramayı Kaydet": persists the rule rows / column picker / sort as a
    /// personal SavedQueue (StaffId = current staff) — it appears under the mockup's
    /// "Kayıtlı Aramalarım" node. Rules that fit the flat v1 criteria schema map onto it;
    /// the rest are stored under descriptive extra keys the engine logs-and-ignores by
    /// design (QueueEngine forward-compat policy — same as the seeded SLA VIP queue).
    /// </summary>
    [HttpPost("/agent/tickets/save-search")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSearch(
        string? qname, string[] ff, string[] fo, string[] fv, string[] cols, string? sort,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var name = (qname ?? "").Trim();
        if (name.Length == 0)
            return BadRequest();

        var rules = AdvRule.Parse(ff, fo, fv);
        var criteria = new Dictionary<string, object?>();
        foreach (var rule in rules)
        {
            switch (rule.Field, rule.Op)
            {
                case ("status", "is"):
                    var statusKey = await db.TicketStatuses
                        .Where(s => EF.Functions.ILike(s.Name, rule.Value) || EF.Functions.ILike(s.Key, rule.Value))
                        .Select(s => s.Key).FirstOrDefaultAsync(ct);
                    if (statusKey is not null)
                        criteria["status"] = statusKey;
                    else
                        criteria[$"status_{rule.Op}"] = rule.Value;
                    break;
                case ("dept", "is"):
                    var deptId = await db.Departments
                        .Where(d => EF.Functions.ILike(d.Name, rule.Value))
                        .Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
                    if (deptId is not null)
                        criteria["dept"] = deptId;
                    else
                        criteria[$"dept_{rule.Op}"] = rule.Value;
                    break;
                case ("assigned", "is"):
                    var staffId = await db.Staff
                        .Where(s => EF.Functions.ILike(s.FullName, rule.Value) || EF.Functions.ILike(s.Username, rule.Value))
                        .Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
                    if (staffId is not null)
                        criteria["assignee"] = staffId.Value.ToString();
                    else
                        criteria[$"assigned_{rule.Op}"] = rule.Value;
                    break;
                case ("effort", "is") when TicketListEngine.ParseEffortValue(rule.Value) == EffortState.Pending:
                    criteria["effort"] = "pending";
                    break;
                case ("date", _):
                    criteria[$"created_{rule.Op}"] = rule.Value;
                    break;
                default:
                    criteria[$"{rule.Field}_{rule.Op}"] = rule.Value;
                    break;
            }
        }

        // Column picker → SavedQueueColumn rows over the shared QueueColumn defs
        // (created on demand for keys the seeder doesn't cover, e.g. "effort").
        var chosen = ResolveColumns(null, cols);
        var queueColumns = new List<SavedQueueColumn>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var path = TicketListEngine.PathFromColumnKey(chosen[i]);
            if (path is null)
                continue;
            var column = await db.QueueColumns.FirstOrDefaultAsync(c => c.PrimaryPath == path, ct)
                ?? db.QueueColumns.Add(new QueueColumn { Name = CanonColumnName(chosen[i]), PrimaryPath = path }).Entity;
            queueColumns.Add(new SavedQueueColumn { Column = column, Sort = i + 1 });
        }

        var (optionName, optionColumns) = sort switch
        {
            "created" => ("Oluşturulma Tarihi", """["-created_at"]"""),
            "priority" => ("Öncelik", """["priority__urgency"]"""),
            "due" => ("Son Tarih", """["due_date"]"""),
            _ => ("Son Güncellenen", """["-last_update_at"]"""),
        };
        var sortOption = await db.QueueSortOptions.FirstOrDefaultAsync(o => o.Name == optionName, ct)
            ?? db.QueueSortOptions.Add(new QueueSortOption { Name = optionName, Root = "Ticket", Columns = optionColumns }).Entity;

        var maxSort = await db.SavedQueues
            .Where(x => x.StaffId == staff.Id)
            .Select(x => (int?)x.Sort).MaxAsync(ct) ?? 0;

        var saved = new SavedQueue
        {
            Title = name,
            StaffId = staff.Id,
            Root = "Ticket",
            Sort = maxSort + 1,
            InheritColumns = queueColumns.Count == 0,
            Criteria = JsonSerializer.Serialize(criteria),
            Columns = queueColumns,
            Sorts = [new SavedQueueSort { SortOption = sortOption, Sort = 1, IsDefault = true }],
        };
        db.SavedQueues.Add(saved);
        var actor = ActorContext.ForStaff(staff);
        using (actor.BeginAuditScope())
        {
            await db.SaveChangesAsync(ct);
            saved.Path = $"/{saved.Id}/";
            await db.SaveChangesAsync(ct);
        }
        cache.Remove(CountsCacheKey(staff.Id));

        return Json(new { url = $"/agent/tickets?queue={saved.Id}" });
    }

    /// <summary>
    /// Bulk bar actions over the row selection. Only actions with an existing service
    /// run ("assign" = assign-to-me, "status" via TransitionStatusAsync); merge/transfer/
    /// delete render disabled in the view (TODO(S6) — no service/picker yet). Rows the
    /// actor may not touch are skipped and reported, never silently dropped.
    /// </summary>
    [HttpPost("/agent/tickets/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, int? statusId, string? returnUrl, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff);

        if (ids.Length == 0 || (act == "status" && statusId is null))
        {
            TempData["TicketsBulkNone"] = true;
            return LocalRedirectOrIndex(returnUrl);
        }

        int ok = 0, skipped = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (act)
                {
                    case "assign":
                        await ticketService.AssignAsync(id, staff.Id, null, actor, ct);
                        ok++;
                        break;
                    case "status":
                        await ticketService.TransitionStatusAsync(id, statusId!.Value, actor, ct);
                        ok++;
                        break;
                    default:
                        return LocalRedirectOrIndex(returnUrl);
                }
            }
            catch (DomainException)
            {
                // Permission denied / effort work-gate / unknown id — count and report.
                skipped++;
            }
        }

        cache.Remove(CountsCacheKey(staff.Id));
        TempData["TicketsBulkOk"] = ok;
        TempData["TicketsBulkSkipped"] = skipped;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- helpers ------------------------------------------------------------

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        returnUrl is not null && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Index));

    private Task<List<SavedQueue>> LoadQueuesAsync(int staffId, CancellationToken ct) =>
        db.SavedQueues
            .Where(x => x.Root == "Ticket" && x.IsEnabled && (x.StaffId == null || x.StaffId == staffId))
            .Include(x => x.Columns).ThenInclude(c => c.Column)
            .Include(x => x.Sorts).ThenInclude(s => s.SortOption)
            .Include(x => x.ExportFields)
            .OrderBy(x => x.Sort).ThenBy(x => x.Id)
            .AsSplitQuery()
            .ToListAsync(ct);

    /// <summary>?queue=id, else the mockup's default active node ("Bana Atanan" — the
    /// first child queue whose criteria is assignee:me), else the first leaf.</summary>
    private static SavedQueue? ResolveActiveQueue(List<SavedQueue> queues, int? requestedId, int defaultQueueId = 0)
    {
        var leaves = queues.Where(x => x.ParentId != null || x.StaffId != null).ToList();
        if (requestedId is { } id && queues.FirstOrDefault(x => x.Id == id) is { } requested)
            return requested;
        // tickets.default_queue_id (S7 admin/settings-tickets); 0/unresolvable falls
        // back to the built-in default (the assignee:me child, mockup "Bana Atanan").
        if (defaultQueueId != 0 && queues.FirstOrDefault(x => x.Id == defaultQueueId) is { } configured)
            return configured;
        return leaves.FirstOrDefault(x =>
                x.ParentId != null && QueueCriteria.Parse(x.Criteria).Assignee == "me")
            ?? leaves.FirstOrDefault();
    }

    private static string CountsCacheKey(int staffId) => $"agent-tickets:queue-counts:{staffId}";

    /// <summary>
    /// Live per-queue counts. The mockup shows counts on the open-state child queues
    /// only (closed windows and saved searches render without a badge) — that keeps the
    /// pass at one COUNT per badge; results are memory-cached for 30s per staff.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, int>> QueueCountsAsync(
        List<SavedQueue> queues, int staffId, ActorContext actor, CancellationToken ct)
    {
        if (cache.TryGetValue(CountsCacheKey(staffId), out Dictionary<int, int>? cached) && cached is not null)
            return cached;

        var counts = new Dictionary<int, int>();
        foreach (var q in queues.Where(x => x.ParentId != null && x.StaffId == null
                     && QueueCriteria.Parse(x.Criteria).State != "closed"))
        {
            var query = await queueEngine.BuildAsync(QueueCriteria.Parse(q.Criteria), actor, ct);
            counts[q.Id] = await query.CountAsync(ct);
        }
        cache.Set(CountsCacheKey(staffId), counts, CountsTtl);
        return counts;
    }

    private static IReadOnlyList<QueueGroupVm> BuildTree(
        List<SavedQueue> queues, SavedQueue? active, IReadOnlyDictionary<int, int> counts)
    {
        var groups = new List<QueueGroupVm>();
        foreach (var root in queues.Where(x => x.ParentId == null && x.StaffId == null))
        {
            var children = queues.Where(x => x.ParentId == root.Id)
                .Select(x => new QueueLinkVm(x.Id, x.Title,
                    counts.TryGetValue(x.Id, out var n) ? n : null, x.Id == active?.Id))
                .ToList();
            if (children.Count > 0)
                groups.Add(new QueueGroupVm(root.Title, children));
        }
        var personal = queues.Where(x => x.StaffId != null)
            .Select(x => new QueueLinkVm(x.Id, x.Title, null, x.Id == active?.Id))
            .ToList();
        if (personal.Count > 0)
            groups.Add(new QueueGroupVm(null, personal));
        return groups;
    }

    /// <summary>cols override > queue column config (InheritColumns=false) > mockup default.</summary>
    private static IReadOnlyList<string> ResolveColumns(SavedQueue? activeQueue, string[] cols)
    {
        var chosen = cols.Where(TicketListEngine.KnownColumns.Contains).ToArray();
        if (chosen.Length > 0)
        {
            // Status has no checkbox in the mockup's column picker — it always renders.
            return TicketListEngine.KnownColumns
                .Where(k => chosen.Contains(k) || k == "status").ToList();
        }
        if (activeQueue is { InheritColumns: false } && activeQueue.Columns.Count > 0)
        {
            var configured = activeQueue.Columns
                .OrderBy(c => c.Sort)
                .Select(c => c.Column?.PrimaryPath is { } path ? TicketListEngine.ColumnKeyFromPath(path) : null)
                .Where(k => k is not null)
                .Select(k => k!)
                .ToList();
            if (configured.Count > 0)
                return configured;
        }
        return TicketListEngine.DefaultColumns;
    }

    /// <summary>Explicit ?sort > active queue's default sort option > updated-desc.</summary>
    private static (string Sort, bool Desc) ResolveSort(string? sort, string? dir, SavedQueue? activeQueue)
    {
        if (sort is not null && (TicketListEngine.SortableColumns.Contains(sort) || sort is "created" or "due"))
            return (sort, dir != "asc" && (dir == "desc" || sort is "updated" or "created" or "effort" or "priority"));
        var def = activeQueue?.Sorts.FirstOrDefault(s => s.IsDefault)?.SortOption?.Columns;
        return TicketListEngine.SortFromOptionColumns(def) ?? ("updated", true);
    }

    private static QueueCriteria WithSearch(QueueCriteria c, string search) => new()
    {
        State = c.State, Status = c.Status, IsAnswered = c.IsAnswered, IsOverdue = c.IsOverdue,
        Effort = c.Effort, Assignee = c.Assignee, DepartmentId = c.DepartmentId,
        HelpTopicId = c.HelpTopicId, Priority = c.Priority, Closed = c.Closed,
        Search = search, UnknownKeys = c.UnknownKeys,
    };

    /// <summary>Canonical TR names for on-demand QueueColumn rows (seeder naming parity).</summary>
    private static string CanonColumnName(string key) => key switch
    {
        "no" => "Talep No",
        "updated" => "Son Güncelleme",
        "subject" => "Konu",
        "user" => "Kullanıcı",
        "org" => "Şirket",
        "priority" => "Öncelik",
        "effort" => "Efor",
        "status" => "Durum",
        "assigned" => "Atanan",
        "sla" => "SLA Kalan",
        _ => key,
    };

    private IQueryable<TicketRowVm> ProjectRows(IQueryable<Ticket> query) =>
        query.Select(t => new TicketRowVm(
            t.Id,
            t.Number,
            t.LastUpdateAt ?? t.CreatedAt,
            t.Subject,
            t.Thread!.Entries.Count,
            db.Attachments.Any(a => a.ObjectType == AttachmentObjectType.ThreadEntry && !a.Inline
                && t.Thread!.Entries.Any(e => e.Id == a.ObjectId)),
            t.UserId,
            t.User!.Name,
            t.User.OrganizationId,
            t.User.Organization != null ? t.User.Organization.Name : null,
            t.Priority != null ? t.Priority.Key : "normal",
            t.EffortProposals.OrderByDescending(p => p.RevisionNo)
                .Select(p => (EffortState?)p.State).FirstOrDefault(),
            t.EffortProposals.OrderByDescending(p => p.RevisionNo)
                .Select(p => (decimal?)p.Hours).FirstOrDefault(),
            // Derived status (§2 canon): overdue flag wins on open tickets; the
            // effort pseudo-statuses live in the Efor column on this page.
            t.IsOverdue && t.Status!.State == TicketState.Open ? "overdue" : t.Status!.Key,
            t.Staff != null ? t.Staff.FullName : null,
            t.DueDate ?? t.EstimatedDueDate,
            t.CreatedAt,
            t.Department!.Name));
}

public sealed record TicketsIndexVm(
    int? ActiveQueueId,
    bool IsAdvanced,
    string? Query,
    string[] StatusFilter,
    string[] PriorityFilter,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<string> Columns,
    IReadOnlyList<QueueGroupVm> Tree,
    IReadOnlyList<TicketRowVm> Rows,
    IReadOnlyList<AdvRule> Rules,
    string[] ColsParam,
    IReadOnlyList<StatusOptionVm> Statuses);

/// <summary>Tree group; Label null = the personal "Kayıtlı Aramalarım" node.</summary>
public sealed record QueueGroupVm(string? Label, IReadOnlyList<QueueLinkVm> Items);

public sealed record QueueLinkVm(int Id, string Title, int? Count, bool Active);

public sealed record StatusOptionVm(int Id, string Key);

public sealed record TicketRowVm(
    int Id,
    string Number,
    DateTimeOffset Updated,
    string Subject,
    int MessageCount,
    bool HasAttachment,
    int UserId,
    string UserName,
    int? OrgId,
    string? OrgName,
    string PriorityKey,
    EffortState? EffortState,
    decimal? EffortHours,
    string StatusKey,
    string? AssigneeName,
    DateTimeOffset? Due,
    DateTimeOffset Created,
    string DeptName);
