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
/// Admin form designer (mockups/admin/forms.html + form-edit.html, ROADMAP §6.3):
/// an EDITOR over the existing FormDefinition/FormField model the S5/S6 open pages
/// already consume through HelpTopicForm — no parallel model. List: built-in
/// (IsSystem, undeletable) vs custom tables, search/sort/pagination, dlg-more bulk
/// enable/disable/delete. Editor: field CRUD rows (B4) with the ⚙ config dialog
/// writing back per-row hidden inputs (B2 — hint/default value/validation), the
/// type select revealing the choices options editor (custom list or inline options),
/// drag-reorder via the shared rd.js data-drag-rows contract, and a live preview
/// POSTing the CURRENT unsaved field set (queues/filters preview precedent).
/// Fields with existing answers are soft-disabled instead of deleted (osTicket
/// parity — FormEntryValue keeps its FK); whole-form delete removes the entered
/// data too, per the mockup's fme.deleteWarn canon.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class FormsController(AppDbContext db) : Controller
{
    public const int PageSize = 8;

    /// <summary>The mockup's type select, in its option order (fme.tShort…tFile).</summary>
    public static readonly string[] FieldTypes =
        ["text", "memo", "choices", "date", "checkbox", "phone", "file"];

    private static readonly string[] SortKeys = ["name", "updated"];
    private static readonly string[] Validations = ["email", "phone", "number"];

    // ---- forms.html (B1 over both tables) --------------------------------------------

    [HttpGet("/admin/forms")]
    [NavKey("forms")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = db.FormDefinitions.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.Title, pattern));
        }

        var sortKey = SortKeys.Contains(sort) ? sort! : "updated";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey == "updated";
        var projected = query.Select(f => new
        {
            f.Id, f.Title, f.IsSystem, f.IsActive,
            Updated = f.UpdatedAt ?? f.CreatedAt,
        });
        projected = (sortKey, desc) switch
        {
            ("name", true) => projected.OrderByDescending(f => f.Title).ThenBy(f => f.Id),
            ("name", false) => projected.OrderBy(f => f.Title).ThenBy(f => f.Id),
            (_, false) => projected.OrderBy(f => f.Updated).ThenBy(f => f.Id),
            _ => projected.OrderByDescending(f => f.Updated).ThenBy(f => f.Id),
        };
        var all = await projected
            .Select(f => new FormRowVm(f.Id, f.Title, f.IsSystem, f.IsActive, f.Updated))
            .ToListAsync(ct);

        // Built-in table has no pagination in the mockup; the custom table pages.
        var builtin = all.Where(f => f.IsSystem).ToList();
        var custom = all.Where(f => !f.IsSystem).ToList();
        var total = custom.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);

        return View(new FormsIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total),
            builtin,
            custom.Skip((page - 1) * PageSize).Take(PageSize).ToList()));
    }

    /// <summary>dlg-more bulk actions over the custom selection. System forms are
    /// skipped (fm.builtinHelp canon: built-ins cannot be deleted); delete removes
    /// the entered data too (fme.deleteWarn canon).</summary>
    [HttpPost("/admin/forms/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("fm.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var forms = await db.FormDefinitions.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - forms.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var form in forms)
            {
                if (form.IsSystem)
                {
                    skipped++;
                    continue;
                }
                switch (act)
                {
                    case "enable":
                        form.IsActive = true;
                        ok++;
                        break;
                    case "disable":
                        form.IsActive = false;
                        ok++;
                        break;
                    case "delete":
                        await DeleteFormWithDataAsync(form, ct);
                        ok++;
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["FormsToastOk"] = ok;
        TempData["FormsToastSkipped"] = skipped;
        TempData["FormsToast"] = skipped > 0 ? "fm.bulkPartial" : "fm.bulkDone";
        if (skipped > 0)
            TempData["FormsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- form-edit.html ------------------------------------------------------------------

    /// <summary>Designer page; without ?id= it is the "＋ Yeni Form" create form
    /// (the mockup's new-button links straight to form-edit.html).</summary>
    [HttpGet("/admin/form-edit")]
    [NavKey("forms")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        FormDefinition? form = null;
        if (id is not null)
        {
            form = await db.FormDefinitions
                .Include(f => f.Fields.OrderBy(x => x.Sort))
                .SingleOrDefaultAsync(f => f.Id == id, ct);
            if (form is null)
                return NotFound();
        }

        return View(await BuildEditVmAsync(form, ct));
    }

    /// <summary>
    /// Whole-page save (B3/B4): title/instructions/notes plus the field rows as
    /// parallel arrays in DOM order (drag result = Sort). fieldMarks is a
    /// "row"/"req"/"int" marker sequence (unchecked checkboxes post nothing —
    /// schedules entryDayMarks precedent). A field missing from the post is deleted,
    /// unless answers exist — then it is soft-disabled (IsDisabled, osTicket parity).
    /// </summary>
    [HttpPost("/admin/form-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? title, string? instructions, string? notes,
        int[] fieldIds, string[] fieldLabels, string[] fieldTypes, string[] fieldVars,
        string[] fieldMarks, int[] fieldLists, string[] fieldChoices,
        string[] cfgHints, string[] cfgDefaults, string[] cfgValidations,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        title = (title ?? "").Trim();
        if (title.Length is 0 or > 120)
            return EditToastBack(id, "fme.errTitle");

        var rows = ParseFieldRows(fieldIds, fieldLabels, fieldTypes, fieldVars,
            fieldMarks, fieldLists, fieldChoices, cfgHints, cfgDefaults, cfgValidations);
        // Existing rows must keep a label; blank NEW rows are silently dropped.
        if (rows.Any(r => r.Id > 0 && r.Label.Length == 0))
            return EditToastBack(id, "fme.errLabel");
        rows = [.. rows.Where(r => r.Id > 0 || r.Label.Length > 0)];

        // Choice lists must exist (row deleted since render → silently unbind).
        var wantedLists = rows.Where(r => r.ListId > 0).Select(r => r.ListId).Distinct().ToList();
        var knownLists = wantedLists.Count == 0
            ? []
            : (await db.ListDefinitions.Where(l => wantedLists.Contains(l.Id))
                .Select(l => l.Id).ToListAsync(ct)).ToHashSet();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            FormDefinition form;
            if (id is null)
            {
                form = new FormDefinition { Title = title };
                db.FormDefinitions.Add(form);
            }
            else
            {
                var found = await db.FormDefinitions
                    .Include(f => f.Fields)
                    .SingleOrDefaultAsync(f => f.Id == id, ct);
                if (found is null)
                    return NotFound();
                form = found;
            }

            form.Title = title;
            form.Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();
            form.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            // ---- Field reconciliation (B4) ------------------------------------------
            // Missing active fields: soft-disable when answers exist, delete otherwise.
            // Already-disabled fields stay untouched (they are not rendered).
            var postedIds = rows.Where(r => r.Id > 0).Select(r => r.Id).ToHashSet();
            var disabledInstead = 0;
            foreach (var field in form.Fields.Where(f => !f.IsDisabled && !postedIds.Contains(f.Id)).ToList())
            {
                if (await db.Set<FormEntryValue>().AnyAsync(v => v.FormFieldId == field.Id, ct))
                {
                    field.IsDisabled = true; // answers survive (osTicket flag parity)
                    disabledInstead++;
                }
                else
                {
                    form.Fields.Remove(field);
                    db.Remove(field);
                }
            }

            // Variable names: auto-slug from the label when blank, unique in-form
            // (soft-disabled fields keep holding their name).
            var usedNames = new HashSet<string>(
                form.Fields.Where(f => f.IsDisabled).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var field = row.Id > 0 ? form.Fields.FirstOrDefault(f => f.Id == row.Id && !f.IsDisabled) : null;
                if (row.Id > 0 && field is null)
                    continue; // stale row (deleted concurrently)

                var name = UniqueName(row.Var.Length > 0 ? Slug(row.Var) : Slug(row.Label), usedNames);

                var listId = row.Type == "choices" && row.ListId > 0 && knownLists.Contains(row.ListId)
                    ? (int?)row.ListId
                    : null;
                var config = new FormFieldConfig(
                    listId,
                    row.Type == "choices" && listId is null ? row.Choices : [],
                    row.Default.Length > 0 ? row.Default : null,
                    Validations.Contains(row.Validation) ? row.Validation : null);

                if (field is null)
                {
                    field = new FormField { Type = row.Type, Label = row.Label, Name = name };
                    form.Fields.Add(field);
                }

                field.Label = row.Label;
                field.Type = row.Type;
                field.Name = name;
                field.Sort = i + 1;
                field.Hint = row.Hint.Length > 0 ? row.Hint : null;
                field.Configuration = config.ToJson();
                // The mockup's single "Zorunlu" checkbox drives both audiences;
                // "Dahili" hides the field from end users (agents always see it).
                field.RequiredForUsers = row.Required && !row.Internal;
                field.RequiredForAgents = row.Required;
                field.VisibleToUsers = !row.Internal;
                field.VisibleToAgents = true;
            }

            await db.SaveChangesAsync(ct);

            if (disabledInstead > 0)
            {
                TempData["FormEditToast"] = "fme.toastSavedDisabled";
                TempData["FormEditToastCount"] = disabledInstead;
            }
            else
            {
                TempData["FormEditToast"] = id is null ? "fme.toastCreated" : "fme.toastSaved";
            }
            return RedirectToAction(nameof(Edit), new { id = form.Id });
        }
    }

    /// <summary>dlg-deletedata confirm: deletes the form AND every entry made into it
    /// (fme.deleteWarn canon). Built-in forms are refused.</summary>
    [HttpPost("/admin/form-edit/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var form = await db.FormDefinitions.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (form is null)
            return NotFound();
        if (form.IsSystem)
            return EditToastBack(id, "fme.errSystem");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            await DeleteFormWithDataAsync(form, ct);
            await db.SaveChangesAsync(ct);
        }

        TempData["FormsToast"] = "fm.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Live preview (B4, INVENTED control — queues/filters precedent): renders the
    /// POSTED, possibly unsaved field set exactly as the portal open page would show
    /// it (user-visible fields only; choices resolved against real list items).
    /// </summary>
    [HttpPost("/admin/form-edit/preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        int[] fieldIds, string[] fieldLabels, string[] fieldTypes, string[] fieldVars,
        string[] fieldMarks, int[] fieldLists, string[] fieldChoices,
        string[] cfgHints, string[] cfgDefaults, string[] cfgValidations,
        CancellationToken ct = default)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var rows = ParseFieldRows(fieldIds, fieldLabels, fieldTypes, fieldVars,
            fieldMarks, fieldLists, fieldChoices, cfgHints, cfgDefaults, cfgValidations);
        rows = [.. rows.Where(r => r.Label.Length > 0 && !r.Internal)];

        var listIds = rows.Where(r => r.Type == "choices" && r.ListId > 0)
            .Select(r => r.ListId).Distinct().ToList();
        var listItems = listIds.Count == 0
            ? []
            : await db.ListDefinitions
                .Where(l => listIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    Items = l.Items.Where(i => i.IsEnabled).OrderBy(i => i.Sort)
                        .Select(i => i.Value).ToList(),
                })
                .ToDictionaryAsync(l => l.Id, l => l.Items, ct);

        var fields = rows.Select(r => new FormPreviewFieldVm(
                r.Label, r.Type, r.Hint.Length > 0 ? r.Hint : null, r.Required,
                r.Default.Length > 0 ? r.Default : null,
                r.Type != "choices" ? []
                    : r.ListId > 0
                        ? (listItems.TryGetValue(r.ListId, out var items) ? items : [])
                        : r.Choices))
            .ToList();

        var pageL = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizerFactory>()
            .Create("Areas.Admin.Views.Forms.Edit", typeof(Program).Assembly.GetName().Name!);
        return PartialView("_Preview", new FormPreviewVm(fields, pageL["fme.previewEmpty"]));
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>Form + fields cascade; the entered data (FormEntry/Values) is removed
    /// explicitly — FormEntryValue→FormField is Restrict by design.</summary>
    private async Task DeleteFormWithDataAsync(FormDefinition form, CancellationToken ct)
    {
        var entries = await db.FormEntries.Where(e => e.FormDefinitionId == form.Id).ToListAsync(ct);
        db.FormEntries.RemoveRange(entries); // values cascade
        db.FormDefinitions.Remove(form);     // fields + HelpTopicForm rows cascade
    }

    /// <summary>Parallel-array field rows in posted (drag) order. fieldMarks walks
    /// as: "row" opens a row, then optional "req"/"int" flags for that row.</summary>
    public static List<FieldRowInput> ParseFieldRows(
        int[] fieldIds, string[] fieldLabels, string[] fieldTypes, string[] fieldVars,
        string[] fieldMarks, int[] fieldLists, string[] fieldChoices,
        string[] cfgHints, string[] cfgDefaults, string[] cfgValidations)
    {
        // Flags per row from the marker sequence.
        var flags = new List<(bool Req, bool Int)>();
        foreach (var mark in fieldMarks)
        {
            switch (mark)
            {
                case "row":
                    flags.Add((false, false));
                    break;
                case "req" when flags.Count > 0:
                    flags[^1] = (true, flags[^1].Int);
                    break;
                case "int" when flags.Count > 0:
                    flags[^1] = (flags[^1].Req, true);
                    break;
            }
        }

        var rows = new List<FieldRowInput>();
        for (var i = 0; i < fieldLabels.Length; i++)
        {
            string At(string[] arr) => i < arr.Length ? (arr[i] ?? "").Trim() : "";
            var type = i < fieldTypes.Length && FieldTypes.Contains(fieldTypes[i]) ? fieldTypes[i] : "text";
            var choices = At(fieldChoices)
                .Split('\n')
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            rows.Add(new FieldRowInput(
                i < fieldIds.Length ? fieldIds[i] : 0,
                At(fieldLabels).Length > 120 ? At(fieldLabels)[..120] : At(fieldLabels),
                type,
                At(fieldVars),
                i < flags.Count && flags[i].Req,
                i < flags.Count && flags[i].Int,
                i < fieldLists.Length ? fieldLists[i] : 0,
                choices,
                At(cfgHints),
                At(cfgDefaults),
                At(cfgValidations)));
        }
        return rows;
    }

    /// <summary>Variable-name slug (TR-aware): "Personel Numarası" → personel_numarasi.</summary>
    public static string Slug(string raw)
    {
        var map = new Dictionary<char, char>
        {
            ['ç'] = 'c', ['Ç'] = 'c', ['ğ'] = 'g', ['Ğ'] = 'g', ['ı'] = 'i', ['I'] = 'i',
            ['İ'] = 'i', ['ö'] = 'o', ['Ö'] = 'o', ['ş'] = 's', ['Ş'] = 's', ['ü'] = 'u', ['Ü'] = 'u',
        };
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw.Trim())
        {
            var mapped = map.TryGetValue(c, out var m) ? m : char.ToLowerInvariant(c);
            if (char.IsAsciiLetterOrDigit(mapped))
                sb.Append(mapped);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "alan" : slug.Length > 64 ? slug[..64] : slug;
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        var n = 2;
        while (!used.Add(name))
            name = $"{baseName}_{n++}";
        return name;
    }

    private async Task<FormEditVm> BuildEditVmAsync(FormDefinition? form, CancellationToken ct)
    {
        var fields = (form?.Fields ?? [])
            .Where(f => !f.IsDisabled)
            .OrderBy(f => f.Sort)
            .Select(f =>
            {
                var config = FormFieldConfig.Parse(f.Configuration);
                return new FormFieldRowVm(
                    f.Id, f.Label, f.Type, f.Name,
                    f.RequiredForAgents || f.RequiredForUsers,
                    !f.VisibleToUsers,
                    config.ListId ?? 0,
                    string.Join("\n", config.Choices),
                    f.Hint ?? "",
                    config.Default ?? "",
                    config.Validation ?? "");
            })
            .ToList();

        // Choice-list options: active lists plus any list a field already points at
        // (schedules inactive-selection precedent).
        var currentListIds = fields.Where(f => f.ListId > 0).Select(f => f.ListId).ToList();
        var lists = await db.ListDefinitions
            .Where(l => l.IsActive || currentListIds.Contains(l.Id))
            .OrderBy(l => l.Name)
            .Select(l => new OptionVm(l.Id, l.Name))
            .ToListAsync(ct);

        var disabledCount = form?.Fields.Count(f => f.IsDisabled) ?? 0;
        return new FormEditVm(form, fields, lists, disabledCount);
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["FormsToast"] = key;
        if (error)
            TempData["FormsToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the designer (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["FormEditToast"] = key;
        TempData["FormEditToastError"] = true;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

/// <summary>One posted designer row (Save + Preview share the parser).</summary>
public sealed record FieldRowInput(
    int Id,
    string Label,
    string Type,
    string Var,
    bool Required,
    bool Internal,
    int ListId,
    List<string> Choices,
    string Hint,
    string Default,
    string Validation);

public sealed record FormsIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<FormRowVm> Builtin,
    IReadOnlyList<FormRowVm> Custom);

public sealed record FormRowVm(int Id, string Title, bool IsSystem, bool IsActive, DateTimeOffset Updated);

public sealed record FormEditVm(
    FormDefinition? Form,
    IReadOnlyList<FormFieldRowVm> Fields,
    IReadOnlyList<OptionVm> Lists,
    int DisabledFieldCount);

/// <summary>One designer row as rendered (config JSON unpacked for the ⚙ dialog).</summary>
public sealed record FormFieldRowVm(
    int Id,
    string Label,
    string Type,
    string Var,
    bool Required,
    bool Internal,
    int ListId,
    string ChoicesText,
    string Hint,
    string Default,
    string Validation);

public sealed record FormPreviewVm(IReadOnlyList<FormPreviewFieldVm> Fields, string EmptyText);

public sealed record FormPreviewFieldVm(
    string Label,
    string Type,
    string? Hint,
    bool Required,
    string? Default,
    IReadOnlyList<string> Choices);
