using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin ticket filters list + editor (mockups/admin/filters.html + filter-edit.html,
/// ROADMAP §6.3). List: B1 engine over Filter rows, dlg-more bulk enable/disable/
/// delete (filters are referenced by nothing — no delete guard needed). Editor:
/// whole-page save of the info fields plus the two B4 row builders — rule rows
/// (what/how/value) and action rows (type + one param control whose value travels
/// in a hidden actValues input so the parallel arrays stay aligned) — reconciled
/// by row id. The match-count preview (ROADMAP B4 "N tickets would match" — the
/// mockup defines no control, INVENTED) posts the CURRENT unsaved rules to
/// <see cref="Preview"/> and answers through <see cref="IFilterEngine"/>.
/// The filters themselves run inside TicketService.CreateAsync (FilterEngine).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class FiltersController(AppDbContext db, IFilterEngine engine) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "order", "updated"];

    /// <summary>The mockup's rule-field select (osTicket filter_rule.what keys).
    /// A rule holding another key (seed canon "source") renders as an appended
    /// verbatim option — schedules-timezone precedent.</summary>
    private static readonly string[] RuleWhats = ["name", "email", "replyto", "subject", "topic", "org"];

    /// <summary>Editor operator tokens ↔ FilterMatchHow. The mockup lists four;
    /// the remaining osTicket operators (ends, notequal…) stay expressible as
    /// verbatim appended options so seed rows round-trip honestly.</summary>
    private static readonly (string Token, FilterMatchHow How)[] HowTokens =
    [
        ("equal", FilterMatchHow.Equal),
        ("contains", FilterMatchHow.Contains),
        ("starts", FilterMatchHow.StartsWith),
        ("regex", FilterMatchHow.Matches),
        ("notequal", FilterMatchHow.NotEqual),
        ("notcontains", FilterMatchHow.NotContains),
        ("ends", FilterMatchHow.EndsWith),
        ("notmatches", FilterMatchHow.NotMatches),
    ];

    /// <summary>Action type → (config JSON key, param kind). Kinds: "id" = one of
    /// the option selects, "text" = free-text input, "" = no parameter.</summary>
    private static readonly (string Type, string ConfigKey, string Kind)[] ActionTypes =
    [
        ("reject", "", ""),
        ("dept", "dept_id", "id"),
        ("priority", "priority_id", "id"),
        ("sla", "sla_id", "id"),
        ("team", "team_id", "id"),
        ("agent", "staff_id", "id"),
        ("topic", "topic_id", "id"),
        ("status", "status_id", "id"),
        ("noautoresp", "", ""),
        ("canned", "canned_id", "id"),
        ("email", "to", "text"),
        // Seed canon type outside the mockup's list (API filter's internal note).
        ("note", "note", "text"),
    ];

    // ---- filters.html (B1 list) -----------------------------------------------------------

    [HttpGet("/admin/filters")]
    [NavKey("filters")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var query = db.Filters.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.Name, pattern));
        }

        var projected = query.Select(f => new
        {
            f.Id, f.Name, f.IsActive, f.ExecOrder, f.Target,
            RuleCount = f.Rules.Count,
            EmailAddress = f.EmailAccountId != null
                ? db.EmailAccounts.Where(e => e.Id == f.EmailAccountId).Select(e => e.Address).FirstOrDefault()
                : null,
            Updated = f.UpdatedAt ?? f.CreatedAt,
        });

        // DEFAULT = execution order ascending (the mockup's exact row order 1→4;
        // its static sorted-desc indicator contradicts its own rows — the
        // indicator follows the ACTIVE sort, helptopics deviation precedent).
        var sortKey = SortKeys.Contains(sort) ? sort! : "order";
        var desc = dir == "asc" ? false : dir == "desc" || (dir is null && sortKey == "updated");
        projected = (sortKey, desc) switch
        {
            ("name", true) => projected.OrderByDescending(f => f.Name).ThenBy(f => f.Id),
            ("name", false) => projected.OrderBy(f => f.Name).ThenBy(f => f.Id),
            ("updated", true) => projected.OrderByDescending(f => f.Updated).ThenBy(f => f.Id),
            ("updated", false) => projected.OrderBy(f => f.Updated).ThenBy(f => f.Id),
            (_, true) => projected.OrderByDescending(f => f.ExecOrder).ThenBy(f => f.Id),
            _ => projected.OrderBy(f => f.ExecOrder).ThenBy(f => f.Id),
        };

        var total = await projected.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await projected.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(f => new FilterRowVm(
                f.Id, f.Name, f.IsActive, f.ExecOrder, f.RuleCount, f.Target, f.EmailAddress, f.Updated))
            .ToListAsync(ct);

        return View(new FiltersIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>dlg-more bulk actions over the selection (schedules precedent;
    /// nothing references filters, so delete needs no in-use guard).</summary>
    [HttpPost("/admin/filters/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(
        string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("fl.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var filters = await db.Filters.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        int ok = filters.Count, skipped = ids.Length - filters.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var filter in filters)
            {
                switch (act)
                {
                    case "enable": filter.IsActive = true; break;
                    case "disable": filter.IsActive = false; break;
                    case "delete": db.Filters.Remove(filter); break; // rules/actions cascade
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["FiltersToastOk"] = ok;
        TempData["FiltersToastSkipped"] = skipped;
        TempData["FiltersToast"] = skipped > 0 ? "fl.bulkPartial" : "fl.bulkDone";
        if (skipped > 0)
            TempData["FiltersToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- filter-edit.html -------------------------------------------------------------------

    /// <summary>Editor page; without ?id= it is the "＋ Yeni Filtre" create form
    /// (the mockup's new-button links straight to filter-edit.html).</summary>
    [HttpGet("/admin/filter-edit")]
    [NavKey("filters")]
    public async Task<IActionResult> Edit(int? id, CancellationToken ct = default)
    {
        Filter? filter = null;
        if (id is not null)
        {
            filter = await db.Filters
                .Include(f => f.Rules.OrderBy(r => r.Id))
                .Include(f => f.Actions.OrderBy(a => a.Sort).ThenBy(a => a.Id))
                .SingleOrDefaultAsync(f => f.Id == id, ct);
            if (filter is null)
                return NotFound();
        }

        return View(await BuildEditVmAsync(filter, ct));
    }

    /// <summary>
    /// Whole-page save: info fields + both B4 builders in one post. Rule rows are
    /// the parallel ruleIds/ruleWhats/ruleHows/ruleVals arrays; action rows are
    /// actIds/actTypes/actValues (the page script mirrors the visible param
    /// control into the hidden actValues input). Validation writes NOTHING on
    /// failure (B3).
    /// </summary>
    [HttpPost("/admin/filter-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, string? name, int? execOrder, string? active, string? target,
        string? stopOnMatch, string? match, string? notes,
        int[] ruleIds, string[] ruleWhats, string[] ruleHows, string[] ruleVals,
        int[] actIds, string[] actTypes, string[] actValues,
        CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return EditToastBack(id, "fle.errName");
        if (execOrder is not (>= 1 and <= 999))
            return EditToastBack(id, "fle.errOrder");
        var (targetKind, emailAccountId, targetOk) = await ParseTargetAsync(target, ct);
        if (!targetOk)
            return EditToastBack(id, "fle.errValues");

        var (rules, ruleError) = ParseRuleRows(ruleIds, ruleWhats, ruleHows, ruleVals, lenient: false);
        if (ruleError is not null)
            return EditToastBack(id, ruleError);
        if (rules.Count == 0)
            return EditToastBack(id, "fle.errRules"); // osTicket refuses rule-less filters

        var (actions, actionError) = ParseActionRows(actIds, actTypes, actValues);
        if (actionError is not null)
            return EditToastBack(id, actionError);

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            Filter filter;
            if (id is null)
            {
                filter = new Filter { Name = name };
                db.Filters.Add(filter);
            }
            else
            {
                var found = await db.Filters
                    .Include(f => f.Rules).Include(f => f.Actions)
                    .SingleOrDefaultAsync(f => f.Id == id, ct);
                if (found is null)
                    return NotFound();
                filter = found;
            }

            filter.Name = name;
            filter.ExecOrder = execOrder.Value;
            filter.IsActive = active != "0";
            filter.Target = targetKind;
            filter.EmailAccountId = emailAccountId;
            filter.StopOnMatch = stopOnMatch is not null;
            filter.MatchAllRules = match != "any";
            filter.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            // ---- Row reconciliation (B4): update by id, remove missing, add new. ----
            var keepRuleIds = rules.Where(r => r.Id > 0).Select(r => r.Id).ToHashSet();
            filter.Rules.RemoveAll(r => !keepRuleIds.Contains(r.Id));
            foreach (var (rowId, proto) in rules)
            {
                var row = rowId > 0 ? filter.Rules.FirstOrDefault(r => r.Id == rowId) : null;
                if (row is null)
                {
                    filter.Rules.Add(proto);
                }
                else
                {
                    row.What = proto.What;
                    row.How = proto.How;
                    row.Value = proto.Value;
                }
            }

            var keepActionIds = actions.Where(a => a.Id > 0).Select(a => a.Id).ToHashSet();
            filter.Actions.RemoveAll(a => !keepActionIds.Contains(a.Id));
            foreach (var (rowId, proto) in actions)
            {
                var row = rowId > 0 ? filter.Actions.FirstOrDefault(a => a.Id == rowId) : null;
                if (row is null)
                {
                    filter.Actions.Add(proto);
                }
                else
                {
                    row.Type = proto.Type;
                    row.Configuration = proto.Configuration;
                    row.Sort = proto.Sort;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["FiltersToast"] = id is null ? "fl.toastCreated" : "fl.toastSaved";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Editor delete (dlg-delete confirm); rules/actions cascade.</summary>
    [HttpPost("/admin/filter-edit/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var filter = await db.Filters.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (filter is null)
            return NotFound();

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            db.Filters.Remove(filter);
            await db.SaveChangesAsync(ct);
        }

        TempData["FiltersToast"] = "fl.toastDeleted";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Match-count preview (ROADMAP B4): evaluates the POSTED editor state — the
    /// CURRENT, possibly unsaved rules under the chosen AND/OR mode — against the
    /// existing ticket store and answers how many tickets match. Rules are
    /// evaluated regardless of the filter's target channel (the preview tests the
    /// RULES; the channel gate only applies to live traffic). Mail-only fields
    /// (reply-to) cannot match stored tickets — such rules are skipped and
    /// reported in the response. Lenient parse: rows Save would refuse are skipped.
    /// </summary>
    [HttpPost("/admin/filter-edit/preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        string? match,
        int[] ruleIds, string[] ruleWhats, string[] ruleHows, string[] ruleVals,
        CancellationToken ct)
    {
        var (rules, _) = ParseRuleRows(ruleIds, ruleWhats, ruleHows, ruleVals, lenient: true);
        var result = await engine.PreviewAsync(
            [.. rules.Select(r => r.Row)], matchAll: match != "any", ct);
        return Json(new PreviewResultVm(result.Matches, result.Total, result.MailOnlyRules));
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Target select value → (Target, EmailAccountId): "any"/"web"/"api"/
    /// "email" or "em:{id}" for one system mailbox (osTicket email_id).</summary>
    private async Task<(FilterTarget Target, int? EmailAccountId, bool Ok)> ParseTargetAsync(
        string? value, CancellationToken ct)
    {
        switch (value)
        {
            case null or "" or "any":
                return (FilterTarget.Any, null, true);
            case "web":
                return (FilterTarget.Web, null, true);
            case "api":
                return (FilterTarget.Api, null, true);
            case "email":
                return (FilterTarget.Email, null, true);
            default:
                if (value.StartsWith("em:", StringComparison.Ordinal)
                    && int.TryParse(value[3..], out var emailId)
                    && await db.EmailAccounts.AnyAsync(e => e.Id == emailId, ct))
                    return (FilterTarget.Email, emailId, true);
                return (FilterTarget.Any, null, false);
        }
    }

    /// <summary>Rule rows: what token (mockup's six + verbatim extras like the
    /// seed's "source"), operator token, non-empty value; regex operators must
    /// hold a compilable pattern. Duplicate (what,how,value) rows collide with
    /// the unique index — refused with fle.errDupRule.</summary>
    private static (List<(int Id, FilterRule Row)> Rows, string? Error) ParseRuleRows(
        int[] ids, string[] whats, string[] hows, string[] vals, bool lenient)
    {
        var rows = new List<(int, FilterRule)>();
        var count = whats.Length;
        if (ids.Length != count || hows.Length != count || vals.Length != count)
            return (rows, lenient ? null : "fle.errRule");

        var seen = new HashSet<(string, FilterMatchHow, string)>();
        for (var i = 0; i < count; i++)
        {
            var what = (whats[i] ?? "").Trim().ToLowerInvariant();
            var value = (vals[i] ?? "").Trim();
            var how = HowTokens.Where(h => h.Token == hows[i])
                .Select(h => (FilterMatchHow?)h.How).FirstOrDefault();

            string? error =
                what.Length is 0 or > 32 || how is null || value.Length is 0 or > 255 ? "fle.errRule"
                : how is FilterMatchHow.Matches or FilterMatchHow.NotMatches && !IsValidRegex(value) ? "fle.errRegex"
                : !seen.Add((what, how.Value, value)) ? "fle.errDupRule"
                : null;
            if (error is not null)
            {
                if (lenient)
                    continue;
                return (rows, error);
            }

            rows.Add((ids[i], new FilterRule { What = what, How = how!.Value, Value = value }));
        }
        return (rows, null);
    }

    /// <summary>Action rows: known type + its parameter (option id or text per
    /// kind; reject/noautoresp carry none). Sort = the posted row order — actions
    /// apply top-to-bottom (fle.actionsHelp).</summary>
    private static (List<(int Id, FilterAction Row)> Rows, string? Error) ParseActionRows(
        int[] ids, string[] types, string[] values)
    {
        var rows = new List<(int, FilterAction)>();
        var count = types.Length;
        if (ids.Length != count || values.Length != count)
            return (rows, "fle.errAction");

        for (var i = 0; i < count; i++)
        {
            var spec = ActionTypes.FirstOrDefault(t => t.Type == types[i]);
            if (spec.Type is null)
                return (rows, "fle.errAction");
            var value = (values[i] ?? "").Trim();

            string? configuration;
            switch (spec.Kind)
            {
                case "id" when int.TryParse(value, out var refId) && refId > 0:
                    configuration = $$"""{"{{spec.ConfigKey}}":{{refId}}}""";
                    break;
                case "text" when value.Length is > 0 and <= 255:
                    configuration = JsonSerializer.Serialize(
                        new Dictionary<string, string> { [spec.ConfigKey] = value });
                    break;
                case "":
                    configuration = null;
                    break;
                default:
                    return (rows, "fle.errAction");
            }

            rows.Add((ids[i], new FilterAction
            {
                Type = spec.Type,
                Configuration = configuration,
                Sort = rows.Count + 1,
            }));
        }
        return (rows, null);
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<FilterEditVm> BuildEditVmAsync(Filter? filter, CancellationToken ct)
    {
        var targetValue = filter is null ? "any" : filter.Target switch
        {
            FilterTarget.Web => "web",
            FilterTarget.Api => "api",
            FilterTarget.Email => filter.EmailAccountId is { } emailId ? $"em:{emailId}" : "email",
            _ => "any",
        };

        var rules = (filter?.Rules ?? [])
            .OrderBy(r => r.Id)
            .Select(r => new FilterRuleRowVm(r.Id, r.What,
                HowTokens.Where(h => h.How == r.How).Select(h => h.Token).First(), r.Value))
            .ToList();

        var actions = (filter?.Actions ?? [])
            .OrderBy(a => a.Sort).ThenBy(a => a.Id)
            .Select(a =>
            {
                var spec = ActionTypes.FirstOrDefault(t => t.Type == a.Type);
                var value = spec.Kind switch
                {
                    "id" => ConfigInt(a.Configuration, spec.ConfigKey)?.ToString() ?? "",
                    "text" => ConfigText(a.Configuration, spec.ConfigKey) ?? "",
                    _ => "",
                };
                return new FilterActionRowVm(a.Id, a.Type, value);
            })
            .ToList();

        return new FilterEditVm(
            filter, targetValue, rules, actions,
            await db.Filters.MaxAsync(f => (int?)f.ExecOrder, ct) is { } maxOrder
                ? Math.Min(maxOrder + 1, 999) : 1,
            await db.EmailAccounts.OrderBy(e => e.Id)
                .Select(e => new OptionVm(e.Id, e.Address)).ToListAsync(ct),
            await db.Departments.Where(d => d.IsActive).OrderBy(d => d.Name)
                .Select(d => new OptionVm(d.Id, d.Name)).ToListAsync(ct),
            await db.TicketPriorities.OrderBy(p => p.Urgency)
                .Select(p => new StatusOptionVm(p.Id, p.Key, p.Name)).ToListAsync(ct),
            await db.SlaPlans.Where(s => s.IsActive).OrderBy(s => s.Name)
                .Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct),
            await db.Teams.OrderBy(t => t.Name)
                .Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct),
            await db.Staff.Where(s => s.IsActive).OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
                .Select(s => new OptionVm(s.Id, s.FirstName + " " + s.LastName)).ToListAsync(ct),
            await db.HelpTopics.Where(t => t.IsActive && !t.IsArchived).OrderBy(t => t.Sort).ThenBy(t => t.Id)
                .Select(t => new OptionVm(t.Id, t.Name)).ToListAsync(ct),
            await db.TicketStatuses.Where(s => s.IsEnabled).OrderBy(s => s.Sort).ThenBy(s => s.Id)
                .Select(s => new StatusOptionVm(s.Id, s.Key, s.Name)).ToListAsync(ct),
            await db.CannedResponses.Where(c => c.IsEnabled).OrderBy(c => c.Title)
                .Select(c => new OptionVm(c.Id, c.Title)).ToListAsync(ct));
    }

    private static int? ConfigInt(string? json, string key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)
                ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ConfigText(string? json, string key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["FiltersToast"] = key;
        if (error)
            TempData["FiltersToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    /// <summary>Validation failure PRG back to the editor (B3 — nothing was written).</summary>
    private IActionResult EditToastBack(int? id, string key)
    {
        TempData["FilterEditToast"] = key;
        TempData["FilterEditToastError"] = true;
        return id is null
            ? RedirectToAction(nameof(Edit))
            : RedirectToAction(nameof(Edit), new { id });
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record FiltersIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<FilterRowVm> Rows);

public sealed record FilterRowVm(
    int Id,
    string Name,
    bool IsActive,
    int ExecOrder,
    int RuleCount,
    FilterTarget Target,
    string? EmailAddress,
    DateTimeOffset Updated);

public sealed record FilterEditVm(
    Filter? Filter,
    string TargetValue,
    IReadOnlyList<FilterRuleRowVm> Rules,
    IReadOnlyList<FilterActionRowVm> Actions,
    int NextExecOrder,
    IReadOnlyList<OptionVm> EmailAccounts,
    IReadOnlyList<OptionVm> Departments,
    IReadOnlyList<StatusOptionVm> Priorities,
    IReadOnlyList<OptionVm> Slas,
    IReadOnlyList<OptionVm> Teams,
    IReadOnlyList<OptionVm> Agents,
    IReadOnlyList<OptionVm> Topics,
    IReadOnlyList<StatusOptionVm> Statuses,
    IReadOnlyList<OptionVm> Canned);

public sealed record FilterRuleRowVm(int Id, string What, string How, string Value = "");

/// <summary>Action row prefill: Value = the option id (as string) or the text
/// parameter, extracted from the configuration JSON.</summary>
public sealed record FilterActionRowVm(int Id, string Type, string Value);

/// <summary>Preview JSON payload; the editor composes the localized line from it.</summary>
public sealed record PreviewResultVm(int Matches, int Total, int MailOnly);
