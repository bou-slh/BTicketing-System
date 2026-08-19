using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Forms;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin custom lists (mockups/admin/lists.html + list-edit.html, ROADMAP §6.3):
/// the B1 engine over ListDefinition rows feeding the form designer's choice
/// fields. System lists (Type != null — the seeded status/priority mirrors) are
/// protected everywhere: disabled checkboxes in the list, skipped by bulk actions,
/// read-only in the editor with their items mirrored live from the real
/// status/priority tables. Editor: item CRUD rows where the sort-mode select gates
/// the Sıra column (B4 — manual enables the order inputs, alphabetical disables
/// them and re-orders on save), a working import dialog (value[,abbrev] per line,
/// honest added/skipped report), and the item-properties designer persisted as the
/// osTicket-parity configuration JSON.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class ListsController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    /// <summary>Property designer types (mockup lse.tShort…tCheckbox — no phone/file).</summary>
    public static readonly string[] PropertyTypes = ["text", "memo", "choices", "date", "checkbox"];

    private static readonly string[] SortKeys = ["name", "created", "updated"];
    private static readonly string[] Validations = ["email", "phone", "number"];

    // ---- lists.html (B1) -----------------------------------------------------------------

    [HttpGet("/admin/lists")]
    [NavKey("lists")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = db.ListDefinitions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(l =>
                EF.Functions.ILike(l.Name, pattern)
                || (l.PluralName != null && EF.Functions.ILike(l.PluralName, pattern)));
        }

        var sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey == "updated";
        var projected = query.Select(l => new
        {
            l.Id,
            // The mockup's rows carry the plural spelling ("Modüller") — lists
            // display PluralName when set, the singular Name otherwise.
            Name = l.PluralName ?? l.Name,
            l.Type, l.IsActive,
            ItemCount = l.Items.Count,
            Created = l.CreatedAt,
            Updated = l.UpdatedAt ?? l.CreatedAt,
        });
        projected = (sortKey, desc) switch
        {
            ("name", true) => projected.OrderByDescending(l => l.Name).ThenBy(l => l.Id),
            ("name", false) => projected.OrderBy(l => l.Name).ThenBy(l => l.Id),
            ("created", true) => projected.OrderByDescending(l => l.Created).ThenBy(l => l.Id),
            ("created", false) => projected.OrderBy(l => l.Created).ThenBy(l => l.Id),
            (_, false) => projected.OrderBy(l => l.Updated).ThenBy(l => l.Id),
            _ => projected.OrderByDescending(l => l.Updated).ThenBy(l => l.Id),
        };
        var rows = await projected.ToListAsync(ct);

        // System lists mirror the real tables — their item counts come from there
        // (mockup canon: Talep Durumları 7, Öncelikler 4 with empty mirror rows).
        var statusCount = await db.TicketStatuses.CountAsync(ct);
        var priorityCount = await db.TicketPriorities.CountAsync(ct);
        var shaped = rows.Select(l => new ListRowVm(
                l.Id, l.Name, l.Type is not null, l.IsActive,
                l.Type switch
                {
                    "ticket-status" => statusCount,
                    "priority" => priorityCount,
                    _ => l.ItemCount,
                },
                l.Created, l.Updated))
            .ToList();

        var total = shaped.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);

        return View(new ListsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            shaped.Skip((page - 1) * PageSize).Take(PageSize).ToList()));
    }

    /// <summary>dlg-more bulk actions. System lists are skipped for every action
    /// (ls.bulkHelp canon); delete also skips lists a form field still points at
    /// (guard-skip, teams precedent).</summary>
    [HttpPost("/admin/lists/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("ls.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var lists = await db.ListDefinitions.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - lists.Count;
        var referenced = act == "delete" ? await ReferencedListIdsAsync(ct) : [];

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var list in lists)
            {
                if (list.Type is not null)
                {
                    skipped++;
                    continue;
                }
                switch (act)
                {
                    case "enable":
                        list.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        list.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        if (referenced.Contains(list.Id))
                        {
                            skipped++;
                        }
                        else
                        {
                            db.ListDefinitions.Remove(list); // items cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["ListsToastOk"] = ok;
        TempData["ListsToastSkipped"] = skipped;
        TempData["ListsToast"] = skipped > 0 ? "ls.bulkPartial" : "ls.bulkDone";
        if (skipped > 0)
            TempData["ListsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- list-edit.html --------------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Liste" create form.</summary>
    [HttpGet("/admin/list-edit")]
    [NavKey("lists")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        ListDefinition? list = null;
        if (id is not null)
        {
            list = await db.ListDefinitions
                .Include(l => l.Items.OrderBy(i => i.Sort).ThenBy(i => i.Id))
                .SingleOrDefaultAsync(l => l.Id == id, ct);
            if (list is null)
                return NotFound();
        }

        // System lists render their mirror table's rows read-only.
        List<ListItemRowVm> items = list?.Type switch
        {
            "ticket-status" => await db.TicketStatuses.OrderBy(s => s.Sort).ThenBy(s => s.Id)
                .Select(s => new ListItemRowVm(0, s.Name, s.Key, s.Sort, s.IsEnabled))
                .ToListAsync(ct),
            "priority" => await db.TicketPriorities.OrderBy(p => p.Urgency)
                .Select(p => new ListItemRowVm(0, p.Name, p.Key, p.Urgency, true))
                .ToListAsync(ct),
            _ => [.. (list?.Items ?? [])
                .Select(i => new ListItemRowVm(i.Id, i.Value, i.Abbrev ?? "", i.Sort, i.IsEnabled))],
        };

        return View(new ListEditVm(list, list?.Type is not null, items, ParseProperties(list?.Configuration)));
    }

    /// <summary>
    /// Whole-page save (B3/B4): definition + item rows + property designer in one
    /// post. itemMarks/propMarks are "row"+flag marker sequences (schedules
    /// precedent). Sort-mode gating is honored server-side: manual takes the posted
    /// Sıra numbers (the inputs are enabled), alphabetical ignores them and
    /// re-orders by Turkish collation.
    /// </summary>
    [HttpPost("/admin/list-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, string? plural, string? sortMode, string? notes,
        int[] itemIds, string[] itemValues, string[] itemAbbrevs, int[] itemSorts, string[] itemMarks,
        string[] propLabels, string[] propTypes, string[] propVars, string[] propMarks,
        string[] propHints, string[] propDefaults, string[] propValidations,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "lse.errName");

        var mode = sortMode switch
        {
            "alphaDesc" => ListSortMode.AlphaDescending,
            "manual" => ListSortMode.SortColumn,
            _ => ListSortMode.Alpha,
        };

        // ---- item rows --------------------------------------------------------------
        var enabledFlags = RowFlags(itemMarks, "on");
        var items = new List<(int Id, string Value, string? Abbrev, int Sort, bool Enabled)>();
        for (var i = 0; i < itemValues.Length; i++)
        {
            var value = (itemValues[i] ?? "").Trim();
            var itemId = i < itemIds.Length ? itemIds[i] : 0;
            if (value.Length == 0)
            {
                if (itemId > 0)
                    return EditToastBack(id, "lse.errItem"); // clearing an existing value
                continue; // blank new row — dropped
            }
            var abbrev = i < itemAbbrevs.Length ? (itemAbbrevs[i] ?? "").Trim() : "";
            items.Add((itemId, value.Length > 120 ? value[..120] : value,
                abbrev.Length == 0 ? null : abbrev,
                i < itemSorts.Length ? itemSorts[i] : i + 1,
                i < enabledFlags.Count && enabledFlags[i]));
        }
        var tr = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
        if (items.GroupBy(x => x.Value, tr).Any(g => g.Count() > 1))
            return EditToastBack(id, "lse.errDupItem");

        var propsJson = BuildPropertiesJson(
            propLabels, propTypes, propVars, propMarks, propHints, propDefaults, propValidations);

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            ListDefinition list;
            if (id is null)
            {
                list = new ListDefinition { Name = name };
                db.ListDefinitions.Add(list);
            }
            else
            {
                var found = await db.ListDefinitions
                    .Include(l => l.Items)
                    .SingleOrDefaultAsync(l => l.Id == id, ct);
                if (found is null)
                    return NotFound();
                if (found.Type is not null)
                    return EditToastBack(id, "lse.errSystem"); // system lists are protected
                list = found;
            }

            list.Name = name;
            list.PluralName = string.IsNullOrWhiteSpace(plural) ? null : plural.Trim();
            list.SortMode = mode;
            list.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            list.Configuration = propsJson;

            // ---- item reconciliation (B4): missing rows are removed (historic
            // answers keep their copied Value string — FormEntryValue.ValueId is a
            // soft reference by design). ----
            var postedIds = items.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
            foreach (var gone in list.Items.Where(i => !postedIds.Contains(i.Id)).ToList())
            {
                list.Items.Remove(gone);
                db.Remove(gone);
            }

            // Sort assignment: manual = posted numbers (stable-tied by position);
            // alpha modes ignore the (gated-off) inputs and re-order on save.
            var ordered = mode switch
            {
                ListSortMode.Alpha => items.OrderBy(x => x.Value, tr).ToList(),
                ListSortMode.AlphaDescending => items.OrderByDescending(x => x.Value, tr).ToList(),
                _ => items.OrderBy(x => x.Sort).ToList(),
            };
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var item = row.Id > 0 ? list.Items.FirstOrDefault(x => x.Id == row.Id) : null;
                if (row.Id > 0 && item is null)
                    continue; // stale row
                if (item is null)
                {
                    item = new ListItem { Value = row.Value };
                    list.Items.Add(item);
                }
                item.Value = row.Value;
                item.Abbrev = row.Abbrev;
                item.Sort = i + 1;
                item.IsEnabled = row.Enabled;
            }

            await db.SaveChangesAsync(ct);

            TempData["ListEditToast"] = id is null ? "lse.toastCreated" : "lse.toastSaved";
            return RedirectToAction(nameof(Edit), new { id = list.Id });
        }
    }

    /// <summary>
    /// dlg-import (B2/B5): one item per line, optional ", abbrev" after the first
    /// comma. Honest report: blank lines, duplicates of existing values and repeats
    /// within the paste are skipped and counted (lse.importResult toast).
    /// </summary>
    [HttpPost("/admin/list-edit/import")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(int id, string? text, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var list = await db.ListDefinitions
            .Include(l => l.Items)
            .SingleOrDefaultAsync(l => l.Id == id, ct);
        if (list is null)
            return NotFound();
        if (list.Type is not null)
            return EditToastBack(id, "lse.errSystem");

        var tr = StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), ignoreCase: true);
        var seen = new HashSet<string>(list.Items.Select(i => i.Value), tr);
        var nextSort = list.Items.Count == 0 ? 1 : list.Items.Max(i => i.Sort) + 1;
        int added = 0, skipped = 0;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var line in (text ?? "").Split('\n'))
            {
                var raw = line.Trim();
                if (raw.Length == 0)
                    continue; // pure blank lines are not "skipped rows"
                var comma = raw.IndexOf(',');
                var value = (comma < 0 ? raw : raw[..comma]).Trim();
                var abbrev = comma < 0 ? null : raw[(comma + 1)..].Trim();
                if (value.Length is 0 or > 120 || !seen.Add(value))
                {
                    skipped++;
                    continue;
                }
                list.Items.Add(new ListItem
                {
                    Value = value,
                    Abbrev = string.IsNullOrEmpty(abbrev) ? null : abbrev,
                    Sort = nextSort++,
                });
                added++;
            }

            // Alphabetical lists keep their configured order after an import too.
            if (list.SortMode != ListSortMode.SortColumn)
            {
                var ordered = list.SortMode == ListSortMode.Alpha
                    ? list.Items.OrderBy(i => i.Value, tr).ToList()
                    : list.Items.OrderByDescending(i => i.Value, tr).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    ordered[i].Sort = i + 1;
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["ListEditToast"] = "lse.importResult";
        TempData["ListEditToastOk"] = added;
        TempData["ListEditToastSkipped"] = skipped;
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Editor delete (dlg-delete confirm): system lists and lists still
    /// referenced by a form field's choices configuration are refused.</summary>
    [HttpPost("/admin/list-edit/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var list = await db.ListDefinitions.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (list is null)
            return NotFound();
        if (list.Type is not null)
            return EditToastBack(id, "lse.errSystem");
        if ((await ReferencedListIdsAsync(ct)).Contains(id))
            return EditToastBack(id, "lse.errInUse");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.ListDefinitions.Remove(list); // items cascade
            await db.SaveChangesAsync(ct);
        }

        TempData["ListsToast"] = "ls.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>Lists a form field's choices configuration points at (delete guard).</summary>
    private async Task<HashSet<int>> ReferencedListIdsAsync(CancellationToken ct)
    {
        var configs = await db.Set<FormField>()
            .Where(f => f.Configuration != null && f.Configuration.Contains("list_id"))
            .Select(f => f.Configuration!)
            .ToListAsync(ct);
        return [.. configs.Select(c => FormFieldConfig.Parse(c).ListId).OfType<int>()];
    }

    /// <summary>Marker sequence → per-row flag ("row" opens a row, flag value sets it).</summary>
    private static List<bool> RowFlags(string[] marks, string flag)
    {
        var flags = new List<bool>();
        foreach (var mark in marks)
        {
            if (mark == "row")
                flags.Add(false);
            else if (mark == flag && flags.Count > 0)
                flags[^1] = true;
        }
        return flags;
    }

    /// <summary>Property designer rows → ListDefinition.Configuration
    /// (osTicket list "properties form" parity, flat v1 JSON).</summary>
    public static string? BuildPropertiesJson(
        string[] labels, string[] types, string[] vars, string[] marks,
        string[] hints, string[] defaults, string[] validations)
    {
        var reqFlags = RowFlags(marks, "req");
        var intFlags = RowFlags(marks, "int");
        var rows = new List<Dictionary<string, object>>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labels.Length; i++)
        {
            string At(string[] arr) => i < arr.Length ? (arr[i] ?? "").Trim() : "";
            var label = At(labels);
            if (label.Length == 0)
                continue;
            var baseName = At(vars).Length > 0 ? FormsController.Slug(At(vars)) : FormsController.Slug(label);
            var propName = baseName;
            var n = 2;
            while (!usedNames.Add(propName))
                propName = $"{baseName}_{n++}";
            var row = new Dictionary<string, object>
            {
                ["label"] = label,
                ["type"] = i < types.Length && PropertyTypes.Contains(types[i]) ? types[i] : "text",
                ["name"] = propName,
                ["required"] = i < reqFlags.Count && reqFlags[i],
                ["internal"] = i < intFlags.Count && intFlags[i],
            };
            if (At(hints).Length > 0)
                row["help"] = At(hints);
            if (At(defaults).Length > 0)
                row["default"] = At(defaults);
            if (Validations.Contains(At(validations)))
                row["validation"] = At(validations);
            rows.Add(row);
        }
        return rows.Count == 0
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, object> { ["properties"] = rows });
    }

    /// <summary>Configuration JSON → property designer rows (inverse of
    /// <see cref="BuildPropertiesJson"/>).</summary>
    public static List<ListPropertyRowVm> ParseProperties(string? json)
    {
        var rows = new List<ListPropertyRowVm>();
        if (string.IsNullOrWhiteSpace(json))
            return rows;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var p in props.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                    continue;
                string S(string key) => p.TryGetProperty(key, out var el)
                    && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
                bool B(string key) => p.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.True;
                if (S("label").Length == 0)
                    continue;
                rows.Add(new ListPropertyRowVm(
                    S("label"),
                    PropertyTypes.Contains(S("type")) ? S("type") : "text",
                    S("name"), B("required"), B("internal"),
                    S("help"), S("default"), S("validation")));
            }
        }
        catch (JsonException)
        {
        }
        return rows;
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["ListsToast"] = key;
        if (error)
            TempData["ListsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation/guard PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["ListEditToast"] = key;
        TempData["ListEditToastError"] = true;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record ListsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<ListRowVm> Rows);

public sealed record ListRowVm(
    int Id,
    string Name,
    bool IsSystem,
    bool IsActive,
    int ItemCount,
    DateTimeOffset Created,
    DateTimeOffset Updated);

public sealed record ListEditVm(
    ListDefinition? List,
    bool IsSystem,
    IReadOnlyList<ListItemRowVm> Items,
    IReadOnlyList<ListPropertyRowVm> Properties);

public sealed record ListItemRowVm(int Id, string Value, string Abbrev, int Sort, bool Enabled);

public sealed record ListPropertyRowVm(
    string Label,
    string Type,
    string Var,
    bool Required,
    bool Internal,
    string Hint,
    string Default,
    string Validation);
