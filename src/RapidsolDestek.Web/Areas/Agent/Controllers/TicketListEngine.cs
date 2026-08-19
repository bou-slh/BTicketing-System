using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>One advanced-search rule row (dlg-advsearch): field / operator / free-text value.</summary>
public sealed record AdvRule(string Field, string Op, string Value)
{
    public static readonly string[] Fields = ["status", "effort", "dept", "assigned", "org", "date"];
    public static readonly string[] Ops = ["is", "not", "contains", "after"];

    /// <summary>Zips the parallel ff/fo/fv querystring arrays into validated rules.</summary>
    public static IReadOnlyList<AdvRule> Parse(string[] fields, string[] ops, string[] values)
    {
        var rules = new List<AdvRule>();
        for (var i = 0; i < fields.Length; i++)
        {
            var op = i < ops.Length ? ops[i] : "is";
            var value = (i < values.Length ? values[i] : "").Trim();
            if (value.Length == 0 || !Fields.Contains(fields[i]) || !Ops.Contains(op))
                continue;
            rules.Add(new AdvRule(fields[i], op, value));
        }
        return rules;
    }
}

/// <summary>
/// B1 list-engine composition for the agent ticket list (mockups/agent/tickets.html):
/// pure IQueryable transforms layered on top of IQueueEngine.BuildAsync output, plus
/// the column-key vocabulary shared by the list, the advanced-search column picker,
/// the CSV export and save-as-queue.
/// </summary>
public static class TicketListEngine
{
    /// <summary>Mockup pagination shows "1–8" per page.</summary>
    public const int PageSize = 8;

    /// <summary>The mockup table's column order (col-check excluded — it is structural).</summary>
    public static readonly string[] DefaultColumns =
        ["no", "updated", "subject", "user", "org", "priority", "effort", "status", "assigned"];

    /// <summary>All renderable column keys ("sla" comes from queue column configs only).</summary>
    public static readonly string[] KnownColumns =
        ["no", "updated", "subject", "user", "org", "priority", "effort", "status", "assigned", "sla"];

    /// <summary>Columns with a sortable header in the mockup.</summary>
    public static readonly string[] SortableColumns = ["updated", "subject", "priority", "effort"];

    /// <summary>QueueColumn.PrimaryPath (osTicket data paths, see DomainSeeder) → column key.</summary>
    public static string? ColumnKeyFromPath(string primaryPath) => primaryPath switch
    {
        "number" => "no",
        "last_update_at" => "updated",
        "subject" => "subject",
        "user__name" => "user",
        "user__organization__name" => "org",
        "priority__key" => "priority",
        "effort__hours" => "effort",
        "status__key" => "status",
        "staff__full_name" => "assigned",
        "estimated_due_date" => "sla",
        _ => null,
    };

    /// <summary>Column key → QueueColumn.PrimaryPath (inverse of <see cref="ColumnKeyFromPath"/>).</summary>
    public static string? PathFromColumnKey(string key) => key switch
    {
        "no" => "number",
        "updated" => "last_update_at",
        "subject" => "subject",
        "user" => "user__name",
        "org" => "user__organization__name",
        "priority" => "priority__key",
        "effort" => "effort__hours",
        "status" => "status__key",
        "assigned" => "staff__full_name",
        "sla" => "estimated_due_date",
        _ => null,
    };

    /// <summary>
    /// Applies the dlg-advsearch rule rows. Values are free text (mockup inputs), so string
    /// fields match via ILIKE ("is" = exact case-insensitive, "contains" = substring);
    /// status matches name or key, effort parses to the active proposal's state, date
    /// applies to CreatedAt. Unparseable/irrelevant combinations are skipped (never
    /// silently wrong — the rule simply doesn't narrow, mirroring QueueEngine's
    /// unknown-key policy).
    /// </summary>
    public static IQueryable<Ticket> ApplyRules(IQueryable<Ticket> query, IEnumerable<AdvRule> rules)
    {
        foreach (var rule in rules)
        {
            var v = rule.Value;
            var contains = $"%{v}%";
            query = (rule.Field, rule.Op) switch
            {
                ("status", "is") => query.Where(t =>
                    EF.Functions.ILike(t.Status!.Name, v) || EF.Functions.ILike(t.Status!.Key, v)),
                ("status", "not") => query.Where(t =>
                    !EF.Functions.ILike(t.Status!.Name, v) && !EF.Functions.ILike(t.Status!.Key, v)),
                ("status", "contains") => query.Where(t => EF.Functions.ILike(t.Status!.Name, contains)),

                ("effort", "is" or "contains") when ParseEffortValue(v) is { } state =>
                    query.Where(t => t.EffortProposals
                        .OrderByDescending(p => p.RevisionNo).Take(1).Any(p => p.State == state)),
                ("effort", "not") when ParseEffortValue(v) is { } state =>
                    query.Where(t => !t.EffortProposals
                        .OrderByDescending(p => p.RevisionNo).Take(1).Any(p => p.State == state)),

                ("dept", "is") => query.Where(t => EF.Functions.ILike(t.Department!.Name, v)),
                ("dept", "not") => query.Where(t => !EF.Functions.ILike(t.Department!.Name, v)),
                ("dept", "contains") => query.Where(t => EF.Functions.ILike(t.Department!.Name, contains)),

                ("assigned", "is") => query.Where(t =>
                    t.Staff != null && EF.Functions.ILike(t.Staff.FullName, v)),
                ("assigned", "not") => query.Where(t =>
                    t.Staff == null || !EF.Functions.ILike(t.Staff.FullName, v)),
                ("assigned", "contains") => query.Where(t =>
                    t.Staff != null && EF.Functions.ILike(t.Staff.FullName, contains)),

                ("org", "is") => query.Where(t =>
                    t.User!.Organization != null && EF.Functions.ILike(t.User.Organization.Name, v)),
                ("org", "not") => query.Where(t =>
                    t.User!.Organization == null || !EF.Functions.ILike(t.User.Organization.Name, v)),
                ("org", "contains") => query.Where(t =>
                    t.User!.Organization != null && EF.Functions.ILike(t.User.Organization.Name, contains)),

                ("date", "after") when ParseDate(v) is { } after =>
                    query.Where(t => t.CreatedAt > after),
                ("date", "is") when ParseDate(v) is { } day =>
                    query.Where(t => t.CreatedAt >= day && t.CreatedAt < day.AddDays(1)),
                ("date", "not") when ParseDate(v) is { } day =>
                    query.Where(t => t.CreatedAt < day || t.CreatedAt >= day.AddDays(1)),

                _ => query,
            };
        }
        return query;
    }

    /// <summary>
    /// Free-text effort value → EffortState; accepts the state keys plus the TR/EN
    /// display words the agent would naturally type.
    /// </summary>
    public static EffortState? ParseEffortValue(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.Contains("pending") || v.Contains("bekl") || v.Contains("onayda") || v.Contains("onayında"))
            return EffortState.Pending;
        if (v.Contains("approved") || v.Contains("onayland") || v.Contains("kabul"))
            return EffortState.Approved;
        if (v.Contains("rejected") || v.Contains("redded") || v.Contains("ret"))
            return EffortState.Rejected;
        return null;
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy"];
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var exact))
            return exact;
        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentUICulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>
    /// Toolbar status multi-filter over the mockup's checkbox keys. The derived keys
    /// select via their source flags (effortWait = active pending proposal, overdue =
    /// IsOverdue) and OR with the plain status keys.
    /// </summary>
    public static IQueryable<Ticket> ApplyStatusFilter(IQueryable<Ticket> query, string[] keys)
    {
        if (keys.Length == 0)
            return query;
        var plain = keys.Where(k => k is not ("effortWait" or "overdue")).ToArray();
        var wantEffort = keys.Contains("effortWait");
        var wantOverdue = keys.Contains("overdue");
        return query.Where(t =>
            plain.Contains(t.Status!.Key)
            || (wantEffort && t.EffortProposals
                .OrderByDescending(p => p.RevisionNo).Take(1).Any(p => p.State == EffortState.Pending))
            || (wantOverdue && t.IsOverdue));
    }

    public static IQueryable<Ticket> ApplyPriorityFilter(IQueryable<Ticket> query, string[] keys) =>
        keys.Length == 0 ? query : query.Where(t => t.Priority != null && keys.Contains(t.Priority.Key));

    /// <summary>
    /// Server-driven sort (B1). "effort" orders by the active proposal's hours via a
    /// correlated subquery; ties always fall back to most-recently-updated.
    /// </summary>
    public static IQueryable<Ticket> ApplySort(IQueryable<Ticket> query, string sort, bool desc)
    {
        var ordered = (sort, desc) switch
        {
            ("created", false) => query.OrderBy(t => t.CreatedAt),
            ("created", true) => query.OrderByDescending(t => t.CreatedAt),
            // Urgency: lower = more urgent; "desc" = most urgent first (mockup intent).
            ("priority", true) => query.OrderBy(t => t.Priority!.Urgency),
            ("priority", false) => query.OrderByDescending(t => t.Priority!.Urgency),
            ("due", false) => query.OrderBy(t => t.DueDate ?? t.EstimatedDueDate),
            ("due", true) => query.OrderByDescending(t => t.DueDate ?? t.EstimatedDueDate),
            ("subject", false) => query.OrderBy(t => t.Subject),
            ("subject", true) => query.OrderByDescending(t => t.Subject),
            ("effort", false) => query.OrderBy(t => t.EffortProposals
                .OrderByDescending(p => p.RevisionNo).Select(p => (decimal?)p.Hours).FirstOrDefault()),
            ("effort", true) => query.OrderByDescending(t => t.EffortProposals
                .OrderByDescending(p => p.RevisionNo).Select(p => (decimal?)p.Hours).FirstOrDefault()),
            (_, false) => query.OrderBy(t => t.LastUpdateAt ?? t.CreatedAt),
            _ => query.OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt),
        };
        return ordered.ThenByDescending(t => t.LastUpdateAt ?? t.CreatedAt).ThenBy(t => t.Id);
    }

    /// <summary>
    /// Maps a QueueSortOption.Columns JSON (array of "field" / "-field" entries —
    /// seeded canon AND the S7 queue builder's output) to this page's sort keys.
    /// The first mappable entry wins (ApplySort's built-in updated tiebreak covers
    /// the rest); "-" flips direction. Bare "priority__urgency" = ascending urgency
    /// = most urgent first = this page's ("priority", desc:true) semantics.
    /// </summary>
    public static (string Sort, bool Desc)? SortFromOptionColumns(string? columnsJson)
    {
        if (string.IsNullOrWhiteSpace(columnsJson))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(columnsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var path = entry.GetString()!;
                var neg = path.StartsWith('-');
                if (neg)
                    path = path[1..];
                (string Sort, bool Desc)? mapped = path switch
                {
                    "last_update_at" => ("updated", neg),
                    "created_at" => ("created", neg),
                    "subject" => ("subject", neg),
                    "priority__urgency" => ("priority", !neg),
                    "estimated_due_date" or "due_date" => ("due", neg),
                    _ => null,
                };
                if (mapped is not null)
                    return mapped;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed sort config never breaks the list — default sort applies.
        }
        return null;
    }

    /// <summary>
    /// SavedQueueExportField.FieldPath → CSV column key. Superset of
    /// <see cref="ColumnKeyFromPath"/>: the export tab offers dept/created,
    /// which the list itself never renders.
    /// </summary>
    public static string? ExportKeyFromPath(string fieldPath) => fieldPath switch
    {
        "created_at" => "created",
        "dept__name" => "dept",
        _ => ColumnKeyFromPath(fieldPath),
    };

    /// <summary>RFC-4180 CSV field escaping.</summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
