using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Areas.Agent.Controllers;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin queue builder (mockups/admin/queues.html, ROADMAP §6.3): CRUD over the
/// SHARED SavedQueue tree the agent panel renders (Areas/Agent TicketsController).
/// The builder is an EDITOR over the existing engine — it emits exactly the rows
/// QueueEngine/TicketListEngine already execute: the flat criteria JSON (v1 schema,
/// incl. the seeded descriptive keys sla_remaining_lt / org_tag the engine
/// logs-and-ignores), SavedQueueColumn rows (heading/width/drag order),
/// one default QueueSortOption (JSON "field"/"-field" list), SavedQueueExportField
/// rows (honored by the agent CSV export) and the osTicket-parity row-styling
/// Conditions JSON (persisted, consumer TODO). The Preview tab posts the CURRENT
/// UNSAVED builder state through the real QueueEngine (filters preview precedent).
/// Seeded canon queues are IsSystem — editable, never deletable.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class QueuesController(AppDbContext db, IQueueEngine queueEngine) : Controller
{
    /// <summary>Preview row cap (the mockup shows a 3-row sample).</summary>
    public const int PreviewSize = 5;

    /// <summary>The mockup's criteria/condition field tokens.</summary>
    private static readonly string[] Fields =
        ["status", "priority", "dept", "slaRemaining", "orgTag", "assignee"];

    /// <summary>The mockup's operator tokens (is / is not / less / greater / contains).</summary>
    private static readonly string[] Ops = ["is", "not", "lt", "gt", "contains"];

    /// <summary>Builder field ↔ the flat criteria JSON keys (QueueCriteria/seed vocabulary).</summary>
    private static readonly (string Field, string Key)[] FieldKeys =
    [
        ("status", "status"),
        ("priority", "priority"),
        ("dept", "dept"),
        ("assignee", "assignee"),
        ("orgTag", "org_tag"),
        ("slaRemaining", "sla_remaining"),
    ];

    /// <summary>Sort field ↔ QueueSortOption column path; TR label = canon option naming
    /// (DomainSeeder "SLA Kalan Süre"/"Son Güncellenen" precedent — names are DB data).</summary>
    private static readonly (string Field, string Path, string TrName)[] SortFields =
    [
        ("slaRemaining", "estimated_due_date", "SLA Kalan Süre"),
        ("priority", "priority__urgency", "Öncelik"),
        ("status", "status__key", "Durum"),
        ("updated", "last_update_at", "Son Güncellenen"),
    ];

    /// <summary>The mockup's condition actions (row styling; persisted osTicket-parity).</summary>
    private static readonly string[] CondActions = ["highlight-red", "highlight-amber", "bold"];

    /// <summary>Export tab fields in mockup order: path, canonical TR heading (CSV data),
    /// and whether the mockup ships the box checked (create default).</summary>
    public static readonly (string Path, string Heading, bool DefaultOn)[] ExportCatalog =
    [
        ("number", "Talep No", true),
        ("subject", "Konu", true),
        ("status__key", "Durum", true),
        ("priority__key", "Öncelik", true),
        ("dept__name", "Departman", false),
        ("user__name", "Kullanıcı", true),
        ("user__organization__name", "Şirket", true),
        ("staff__full_name", "Atanan", true),
        ("created_at", "Oluşturulma", false),
        ("last_update_at", "Son Güncelleme", true),
        ("estimated_due_date", "SLA Kalan", true),
    ];

    // ---- GET /admin/queues --------------------------------------------------------------

    /// <summary>Builder page. ?id=N edits the shared queue N; ?id=0 is create mode;
    /// no id lands on the first shared root (the mockup renders a filled builder).</summary>
    [HttpGet("/admin/queues")]
    [NavKey("queues")]
    public async Task<IActionResult> Index(int? id, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        SavedQueue? queue = null;
        if (id is > 0)
        {
            queue = await LoadQueueAsync(id.Value, ct);
            if (queue is null)
                return NotFound();
        }
        else if (id is null)
        {
            var firstRootId = await SharedQueues()
                .Where(q => q.ParentId == null)
                .OrderBy(q => q.Sort).ThenBy(q => q.Id)
                .Select(q => (int?)q.Id).FirstOrDefaultAsync(ct);
            if (firstRootId is { } rootId)
                queue = await LoadQueueAsync(rootId, ct);
        }

        return View(await BuildVmAsync(queue, staff, ct));
    }

    // ---- POST /admin/queues (whole-page save, filter-edit precedent) ---------------------

    [HttpPost("/admin/queues")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, int? parentId, string? quickFilter,
        string[] cfF, string[] cfO, string[] cfV,
        int[] colIds, string[] colHeads, string[] colWidths,
        string? inheritSort, string[] sortFields, string[] sortDirs,
        string[] condFields, string[] condOps, string[] condVals, string[] condActs,
        string[] exportFields,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return ToastBack(id, "qb.errName");

        // Column rows: known defs only, no duplicates (composite PK queue+column).
        var knownColIds = (await db.QueueColumns.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var colRows = new List<(int ColumnId, string? Heading, int Width)>();
        for (var i = 0; i < colIds.Length; i++)
        {
            if (!knownColIds.Contains(colIds[i]))
                continue;
            if (colRows.Any(r => r.ColumnId == colIds[i]))
                return ToastBack(id, "qb.errDupCol");
            var heading = (i < colHeads.Length ? colHeads[i] : "").Trim();
            colRows.Add((colIds[i], heading.Length is 0 or > 120 ? null : heading,
                ParseWidth(i < colWidths.Length ? colWidths[i] : "")));
        }

        var criteriaJson = BuildCriteriaJson(cfF, cfO, cfV);
        var conditionsJson = BuildConditionsJson(condFields, condOps, condVals, condActs);
        var sortColumnsJson = inheritSort is null ? BuildSortColumnsJson(sortFields, sortDirs) : null;
        var quick = quickFilter is "status" or "priority" or "dept" or "assignee" ? quickFilter : null;
        var exportSet = ExportCatalog.Where(f => exportFields.Contains(f.Path)).ToList();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            SavedQueue queue;
            if (id is > 0)
            {
                var found = await LoadQueueAsync(id.Value, ct);
                if (found is null)
                    return NotFound();
                queue = found;
            }
            else
            {
                queue = new SavedQueue { Title = name, Root = "Ticket" };
                db.SavedQueues.Add(queue);
            }

            // Parent placement: shared queues only, never self or a descendant
            // (materialized-path prefix check — cycle guard).
            SavedQueue? parent = null;
            if (parentId is > 0)
            {
                parent = await SharedQueues().SingleOrDefaultAsync(q => q.Id == parentId, ct);
                if (parent is null || (queue.Id != 0 && parent.Path.StartsWith(queue.Path, StringComparison.Ordinal)))
                    return ToastBack(id, "qb.errParent");
            }

            queue.Title = name;
            queue.Criteria = criteriaJson;
            queue.QuickFilter = quick;
            queue.Conditions = conditionsJson;
            queue.InheritColumns = colRows.Count == 0;

            if (queue.Id == 0 || queue.ParentId != parent?.Id)
            {
                queue.ParentId = parent?.Id;
                var parentIdValue = parent?.Id;
                queue.Sort = (await db.SavedQueues
                    .Where(q => q.StaffId == null && q.ParentId == parentIdValue)
                    .Select(q => (int?)q.Sort).MaxAsync(ct) ?? 0) + 1;
            }

            // ---- Column rows (B4): reconcile on the composite key; the posted
            // array order (drag result) becomes Sort. ----
            queue.Columns.RemoveAll(c => colRows.All(r => r.ColumnId != c.ColumnId));
            for (var i = 0; i < colRows.Count; i++)
            {
                var (columnId, heading, width) = colRows[i];
                var row = queue.Columns.FirstOrDefault(c => c.ColumnId == columnId);
                if (row is null)
                    queue.Columns.Add(row = new SavedQueueColumn { ColumnId = columnId });
                row.Sort = i + 1;
                row.Heading = heading;
                row.Width = width;
            }

            // ---- Sort (B4): the rule rows compose ONE default QueueSortOption
            // (osTicket queue_sort columns = ordered JSON list); inherit = no own
            // rows. Reconciled in place — clearing and re-adding the same join key
            // in one unit would conflict on the composite PK. ----
            if (sortColumnsJson is null)
            {
                queue.Sorts.Clear();
            }
            else
            {
                var option = await db.QueueSortOptions
                        .FirstOrDefaultAsync(o => o.Root == "Ticket" && o.Columns == sortColumnsJson, ct)
                    ?? db.QueueSortOptions.Add(new QueueSortOption
                    {
                        Name = SortOptionName(sortColumnsJson),
                        Root = "Ticket",
                        Columns = sortColumnsJson,
                    }).Entity;
                var existing = queue.Sorts.FirstOrDefault(s =>
                    ReferenceEquals(s.SortOption, option) || (option.Id != 0 && s.SortOptionId == option.Id));
                queue.Sorts.RemoveAll(s => s != existing);
                if (existing is null)
                {
                    queue.Sorts.Add(new SavedQueueSort { SortOption = option, Sort = 1, IsDefault = true });
                }
                else
                {
                    existing.Sort = 1;
                    existing.IsDefault = true;
                }
            }

            // ---- Export column set: full replace in catalog order. ----
            queue.ExportFields.Clear();
            for (var i = 0; i < exportSet.Count; i++)
            {
                queue.ExportFields.Add(new SavedQueueExportField
                {
                    FieldPath = exportSet[i].Path,
                    Heading = exportSet[i].Heading,
                    Sort = i + 1,
                });
            }

            await db.SaveChangesAsync(ct);

            // Materialized path after the id exists; reparenting rewrites the subtree.
            var newPath = $"{parent?.Path ?? "/"}{queue.Id}/";
            if (queue.Path != newPath)
            {
                var oldPath = queue.Path;
                queue.Path = newPath;
                if (oldPath != "/")
                {
                    var descendants = await db.SavedQueues
                        .Where(q => q.Id != queue.Id && q.Path.StartsWith(oldPath)).ToListAsync(ct);
                    foreach (var d in descendants)
                        d.Path = newPath + d.Path[oldPath.Length..];
                }
                await db.SaveChangesAsync(ct);
            }

            TempData["QueuesToast"] = id is > 0 ? "qb.toastSaved" : "qb.toastCreated";
            return RedirectToAction(nameof(Index), new { id = queue.Id });
        }
    }

    // ---- POST /admin/queues/delete --------------------------------------------------------

    /// <summary>Delete guards: seeded canon queues (IsSystem) and queues with
    /// children are refused; columns/sorts/export rows cascade.</summary>
    [HttpPost("/admin/queues/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var queue = await SharedQueues().SingleOrDefaultAsync(q => q.Id == id, ct);
        if (queue is null)
            return NotFound();
        if (queue.IsSystem)
            return ToastBack(id, "qb.errSystem");
        if (await db.SavedQueues.AnyAsync(q => q.ParentId == id, ct))
            return ToastBack(id, "qb.errChildren");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.SavedQueues.Remove(queue);
            await db.SaveChangesAsync(ct);
        }

        TempData["QueuesToast"] = "qb.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    // ---- POST /admin/queues/preview ---------------------------------------------------------

    /// <summary>
    /// Live preview (B4): executes the POSTED, possibly unsaved builder state —
    /// criteria rows through the real QueueEngine (admin actor = full visibility),
    /// sort rows through TicketListEngine.ApplySort — and renders the sample table
    /// with the posted column rows (default preview columns when none). Lenient:
    /// rows Save would refuse simply don't narrow.
    /// </summary>
    [HttpPost("/admin/queues/preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        string[] cfF, string[] cfO, string[] cfV,
        int[] colIds, string[] colHeads,
        string? inheritSort, string[] sortFields, string[] sortDirs,
        [FromServices] IStringLocalizerFactory localizerFactory,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var vm = await BuildPreviewAsync(
            cfF, cfO, cfV, colIds, colHeads,
            inheritSort is null ? BuildSortColumnsJson(sortFields, sortDirs) : null,
            staff, localizerFactory, ct);
        return PartialView("_Preview", vm);
    }

    // ---- helpers: criteria mapping ----------------------------------------------------------

    /// <summary>
    /// Builder rows → the flat criteria JSON. "is" on a canonical field writes the
    /// bare engine key (status/priority/dept/assignee — executed; org_tag — the
    /// seeded descriptive key); other operators suffix the key (org_tag_contains,
    /// status_not…) which QueueEngine logs-and-ignores by design (forward-compat
    /// policy, same as the seeded SLA VIP). Fields outside the mockup's six are
    /// VERBATIM passthrough (state/isanswered/closed/effort… — the seeded tree
    /// round-trips loss-free) with typed values (bool/int inference).
    /// </summary>
    public static string BuildCriteriaJson(string[] fields, string[] ops, string[] values)
    {
        var criteria = new Dictionary<string, object?>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i] ?? "";
            var op = i < ops.Length && Ops.Contains(ops[i]) ? ops[i] : "is";
            var value = (i < values.Length ? values[i] : "").Trim();
            if (field.Length == 0 || value.Length == 0)
                continue;

            if (field == "slaRemaining")
            {
                // Seed canon spells the operator into the key (sla_remaining_lt).
                criteria[$"sla_remaining_{op}"] = value;
                continue;
            }

            var key = FieldKeys.FirstOrDefault(f => f.Field == field).Key;
            if (key is not null)
            {
                criteria[op == "is" ? key : $"{key}_{op}"] = field == "dept" && int.TryParse(value, out var deptId)
                    ? deptId : value;
                continue;
            }

            // Verbatim passthrough (loaded from a JSON key the mockup's field
            // select doesn't cover) — preserve the original typed value.
            criteria[op == "is" ? field : $"{field}_{op}"] = value switch
            {
                "true" => true,
                "false" => false,
                _ when long.TryParse(value, out var n) => n,
                _ => value,
            };
        }
        return JsonSerializer.Serialize(criteria);
    }

    /// <summary>Criteria JSON → builder rows (inverse of <see cref="BuildCriteriaJson"/>).
    /// Unknown keys render as verbatim rows so nothing is silently dropped.</summary>
    public static List<CritRowVm> ParseCriteriaRows(string? json)
    {
        var rows = new List<CritRowVm>();
        if (string.IsNullOrWhiteSpace(json))
            return rows;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return rows;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return rows;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var (key, op) = SplitOpSuffix(prop.Name);
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();

                if (key.StartsWith("sla_remaining", StringComparison.Ordinal))
                {
                    rows.Add(new CritRowVm("slaRemaining", op == "is" ? "lt" : op, value));
                    continue;
                }
                var field = FieldKeys.FirstOrDefault(f => f.Key == key).Field;
                rows.Add(field is not null
                    ? new CritRowVm(field, op, value)
                    : new CritRowVm(key, op, value)); // verbatim
            }
        }
        return rows;
    }

    private static (string Key, string Op) SplitOpSuffix(string propName)
    {
        foreach (var op in new[] { "not", "lt", "gt", "contains" })
        {
            if (propName.EndsWith($"_{op}", StringComparison.Ordinal))
            {
                var baseKey = propName[..^(op.Length + 1)];
                // sla_remaining carries its operator in the key by seed canon.
                if (baseKey == "sla_remaining" || FieldKeys.Any(f => f.Key == baseKey))
                    return (baseKey, op);
            }
        }
        return (propName, "is");
    }

    // ---- helpers: sort mapping ----------------------------------------------------------------

    /// <summary>Sort rows → QueueSortOption.Columns JSON ("field"/"-field" list).
    /// Bare "priority__urgency" = most urgent first = the builder's "Azalan"
    /// (TicketListEngine.SortFromOptionColumns convention).</summary>
    public static string? BuildSortColumnsJson(string[] fields, string[] dirs)
    {
        var entries = new List<string>();
        for (var i = 0; i < fields.Length; i++)
        {
            var spec = SortFields.FirstOrDefault(s => s.Field == fields[i]);
            if (spec.Path is null || entries.Any(e => e.TrimStart('-') == spec.Path))
                continue;
            var desc = (i < dirs.Length ? dirs[i] : "asc") == "desc";
            if (spec.Field == "priority")
                desc = !desc; // urgency scale inversion (1 = most urgent)
            entries.Add(desc ? $"-{spec.Path}" : spec.Path);
        }
        return entries.Count == 0 ? null : JsonSerializer.Serialize(entries);
    }

    /// <summary>QueueSortOption.Columns JSON → builder rows.</summary>
    public static List<SortRowVm> ParseSortRows(string? columnsJson)
    {
        var rows = new List<SortRowVm>();
        if (string.IsNullOrWhiteSpace(columnsJson))
            return rows;
        try
        {
            using var doc = JsonDocument.Parse(columnsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var path = entry.GetString()!;
                var neg = path.StartsWith('-');
                if (neg)
                    path = path[1..];
                var spec = SortFields.FirstOrDefault(s => s.Path == path);
                if (spec.Field is null)
                    continue; // paths outside the mockup's vocabulary don't render
                rows.Add(new SortRowVm(spec.Field, spec.Field == "priority" ? !neg : neg));
            }
        }
        catch (JsonException)
        {
        }
        return rows;
    }

    /// <summary>Canonical TR option name from the composed columns ("SLA Kalan Süre + Öncelik").</summary>
    private static string SortOptionName(string columnsJson) =>
        string.Join(" + ", ParseSortRows(columnsJson)
            .Select(r => SortFields.First(s => s.Field == r.Field).TrName)
            .DefaultIfEmpty("Özel"));

    // ---- helpers: conditions ------------------------------------------------------------------

    /// <summary>Condition rows → the persisted JSON (osTicket queue conditions parity;
    /// TODO consumer: the agent list renderer ignores it today, ROADMAP-flagged).</summary>
    public static string? BuildConditionsJson(string[] fields, string[] ops, string[] vals, string[] acts)
    {
        var rows = new List<Dictionary<string, string>>();
        for (var i = 0; i < fields.Length; i++)
        {
            var value = (i < vals.Length ? vals[i] : "").Trim();
            if (string.IsNullOrEmpty(fields[i]) || value.Length == 0)
                continue;
            rows.Add(new Dictionary<string, string>
            {
                ["field"] = fields[i],
                ["op"] = i < ops.Length && Ops.Contains(ops[i]) ? ops[i] : "is",
                ["value"] = value,
                ["action"] = i < acts.Length && CondActions.Contains(acts[i]) ? acts[i] : CondActions[0],
            });
        }
        return rows.Count == 0 ? null : JsonSerializer.Serialize(rows);
    }

    public static List<CondRowVm> ParseConditionRows(string? json)
    {
        var rows = new List<CondRowVm>();
        if (string.IsNullOrWhiteSpace(json))
            return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                string S(string k) => e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "" : "";
                rows.Add(new CondRowVm(S("field"), S("op"), S("value"), S("action")));
            }
        }
        catch (JsonException)
        {
        }
        return rows;
    }

    // ---- helpers: vm building -------------------------------------------------------------------

    private static int ParseWidth(string raw) =>
        int.TryParse(raw.Trim(), out var w) && w is > 0 and <= 2000 ? w : 0; // 0 = auto

    private IQueryable<SavedQueue> SharedQueues() =>
        db.SavedQueues.Where(q => q.Root == "Ticket" && q.StaffId == null);

    private Task<SavedQueue?> LoadQueueAsync(int id, CancellationToken ct) =>
        SharedQueues()
            .Include(q => q.Columns).ThenInclude(c => c.Column)
            .Include(q => q.Sorts).ThenInclude(s => s.SortOption)
            .Include(q => q.ExportFields)
            .AsSplitQuery()
            .SingleOrDefaultAsync(q => q.Id == id, ct);

    /// <summary>Shared tree flattened depth-first (picker + parent select options).</summary>
    private async Task<List<QueuePickVm>> FlattenedTreeAsync(CancellationToken ct)
    {
        var all = await SharedQueues()
            .OrderBy(q => q.Sort).ThenBy(q => q.Id)
            .Select(q => new { q.Id, q.Title, q.ParentId, q.Path })
            .ToListAsync(ct);
        var result = new List<QueuePickVm>();
        void Walk(int? parentId, int depth)
        {
            foreach (var q in all.Where(x => x.ParentId == parentId))
            {
                result.Add(new QueuePickVm(q.Id, q.Title, depth, q.Path));
                Walk(q.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return result;
    }

    private async Task<QueueBuilderVm> BuildVmAsync(SavedQueue? queue, Staff staff, CancellationToken ct)
    {
        var tree = await FlattenedTreeAsync(ct);
        var parentOptions = queue is null
            ? tree
            : tree.Where(t => !t.Path.StartsWith(queue.Path, StringComparison.Ordinal)).ToList();

        var columns = (queue?.Columns ?? [])
            .OrderBy(c => c.Sort)
            .Select(c => new ColRowVm(c.ColumnId,
                c.Heading ?? c.Column?.Name ?? "",
                c.Width == 0 ? "auto" : c.Width.ToString()))
            .ToList();

        var defaultSort = queue?.Sorts.FirstOrDefault(s => s.IsDefault) ?? queue?.Sorts.FirstOrDefault();
        var sortRows = ParseSortRows(defaultSort?.SortOption?.Columns);

        var exportChecked = queue is null
            ? ExportCatalog.Where(f => f.DefaultOn).Select(f => f.Path).ToList()
            : queue.ExportFields.Select(f => f.FieldPath).ToList();

        var localizerFactory = HttpContext.RequestServices.GetRequiredService<IStringLocalizerFactory>();
        var criteriaRows = ParseCriteriaRows(queue?.Criteria);
        var preview = await BuildPreviewAsync(
            [.. criteriaRows.Select(r => r.Field)],
            [.. criteriaRows.Select(r => r.Op)],
            [.. criteriaRows.Select(r => r.Value)],
            [.. columns.Select(c => c.ColumnId)],
            [.. columns.Select(c => c.Heading)],
            defaultSort?.SortOption?.Columns,
            staff, localizerFactory, ct);

        return new QueueBuilderVm(
            queue, tree, parentOptions, queue?.ParentId, queue?.QuickFilter,
            criteriaRows, columns,
            await db.QueueColumns.OrderBy(c => c.Id)
                .Select(c => new OptionVm(c.Id, c.Name)).ToListAsync(ct),
            queue is not null && sortRows.Count == 0,
            sortRows,
            ParseConditionRows(queue?.Conditions),
            exportChecked,
            await db.TicketStatuses.Where(s => s.IsEnabled && !s.IsInternal)
                .OrderBy(s => s.Sort).ThenBy(s => s.Id)
                .Select(s => new StatusOptionVm(s.Id, s.Key, s.Name)).ToListAsync(ct),
            await db.TicketPriorities.OrderBy(p => p.Urgency)
                .Select(p => new StatusOptionVm(p.Id, p.Key, p.Name)).ToListAsync(ct),
            await db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name)
                .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct),
            await db.Staff.Where(s => s.IsActive).OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new OptionVm(s.Id, s.FirstName + " " + s.LastName)).ToListAsync(ct),
            preview);
    }

    /// <summary>Runs the builder state through the real engine and shapes the preview table.</summary>
    private async Task<QueuePreviewVm> BuildPreviewAsync(
        string[] cfF, string[] cfO, string[] cfV, int[] colIds, string[] colHeads,
        string? sortColumnsJson, Staff staff, IStringLocalizerFactory localizerFactory,
        CancellationToken ct)
    {
        var pageL = localizerFactory.Create(
            "Areas.Admin.Views.Queues.Index", typeof(Program).Assembly.GetName().Name!);

        // Posted column rows → renderable keys; none → the mockup's five preview columns.
        var previewCols = new List<PreviewColVm>();
        if (colIds.Length > 0)
        {
            var defs = await db.QueueColumns
                .Where(c => colIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);
            for (var i = 0; i < colIds.Length; i++)
            {
                if (!defs.TryGetValue(colIds[i], out var def)
                    || TicketListEngine.ColumnKeyFromPath(def.PrimaryPath) is not { } key)
                    continue;
                var heading = (i < colHeads.Length ? colHeads[i] : "").Trim();
                previewCols.Add(new PreviewColVm(key, heading.Length > 0 ? heading : def.Name));
            }
        }
        if (previewCols.Count == 0)
        {
            previewCols =
            [
                new PreviewColVm("no", pageL["qb.pvTicket"]),
                new PreviewColVm("subject", pageL["qb.pvSubject"]),
                new PreviewColVm("user", pageL["qb.pvUser"]),
                new PreviewColVm("sla", pageL["qb.pvSla"]),
                new PreviewColVm("assigned", pageL["qb.pvAssignee"]),
            ];
        }

        var criteria = QueueCriteria.Parse(BuildCriteriaJson(cfF, cfO, cfV));
        var query = await queueEngine.BuildAsync(criteria, ActorContext.ForStaff(staff), ct);
        var (sortKey, desc) = TicketListEngine.SortFromOptionColumns(sortColumnsJson) ?? ("updated", true);
        query = TicketListEngine.ApplySort(query, sortKey, desc);

        var rows = await query.Take(PreviewSize).Select(t => new PreviewRowVm(
                t.Number,
                t.LastUpdateAt ?? t.CreatedAt,
                t.Subject,
                t.User!.Name,
                t.User.Organization != null ? t.User.Organization.Name : null,
                t.Priority != null ? t.Priority.Key : "normal",
                t.IsOverdue && t.Status!.State == TicketState.Open ? "overdue" : t.Status!.Key,
                t.Staff != null ? t.Staff.FullName : null,
                t.DueDate ?? t.EstimatedDueDate))
            .ToListAsync(ct);

        return new QueuePreviewVm(previewCols, rows,
            pageL["qb.previewEmpty"], pageL["qb.slaFmt"]);
    }

    /// <summary>Validation/guard PRG back to the builder (B3 — nothing was written).</summary>
    private IActionResult ToastBack(int? id, string key)
    {
        TempData["QueuesToast"] = key;
        TempData["QueuesToastError"] = true;
        return id is > 0
            ? RedirectToAction(nameof(Index), new { id })
            : RedirectToAction(nameof(Index), new { id = 0 });
    }
}

public sealed record QueueBuilderVm(
    SavedQueue? Queue,
    IReadOnlyList<QueuePickVm> Picker,
    IReadOnlyList<QueuePickVm> ParentOptions,
    int? ParentId,
    string? QuickFilter,
    IReadOnlyList<CritRowVm> Criteria,
    IReadOnlyList<ColRowVm> Columns,
    IReadOnlyList<OptionVm> AvailableColumns,
    bool InheritSort,
    IReadOnlyList<SortRowVm> Sorts,
    IReadOnlyList<CondRowVm> Conditions,
    IReadOnlyList<string> ExportChecked,
    IReadOnlyList<StatusOptionVm> Statuses,
    IReadOnlyList<StatusOptionVm> Priorities,
    IReadOnlyList<OptionVm> Departments,
    IReadOnlyList<OptionVm> Agents,
    QueuePreviewVm Preview);

public sealed record QueuePickVm(int Id, string Title, int Depth, string Path);

/// <summary>One criteria row; Field outside the mockup's six = verbatim JSON key.</summary>
public sealed record CritRowVm(string Field, string Op, string Value);

public sealed record ColRowVm(int ColumnId, string Heading, string Width);

public sealed record SortRowVm(string Field, bool Desc);

public sealed record CondRowVm(string Field, string Op, string Value, string Action);

public sealed record QueuePreviewVm(
    IReadOnlyList<PreviewColVm> Columns,
    IReadOnlyList<PreviewRowVm> Rows,
    string EmptyText,
    string SlaFmt);

public sealed record PreviewColVm(string Key, string Heading);

public sealed record PreviewRowVm(
    string Number,
    DateTimeOffset Updated,
    string Subject,
    string UserName,
    string? OrgName,
    string PriorityKey,
    string StatusKey,
    string? AssigneeName,
    DateTimeOffset? Due);
