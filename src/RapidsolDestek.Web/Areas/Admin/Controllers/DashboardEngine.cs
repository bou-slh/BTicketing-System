using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Aggregate queries + pure compute behind the admin dashboard
/// (mockups/admin/dashboard.html, ROADMAP §4 B10 / §6.3). Split TicketListEngine-style
/// so the range/bucket/aggregation logic is unit-testable without a controller.
///
/// Metric definitions (each mockup label mapped to real data — B10 "define honestly"):
///  - Açılan (opened)        = tickets with CreatedAt in the range
///  - Çözülen/Kapatılan      = tickets with ClosedAt in the range (solved + closed states
///                             both stamp ClosedAt; the mockup uses both labels for the
///                             same fact — tile/legend "Çözülen", table column "Kapatılan")
///  - Atanan (assigned)      = "assigned" thread events in the range (TicketService writes
///                             one on create-with-assignee and on AssignAsync; the seeder
///                             only writes the hero's, so seeded counts are sparse)
///  - Geciken (overdue)      = tickets opened in the range currently flagged IsOverdue
///                             (the flag is the §2 canon overdue mechanism; the S8 SLA
///                             sweep will feed it)
///  - Yeniden Açılan         = tickets with ReopenedAt in the range
///  - Servis Süresi          = avg(ClosedAt − CreatedAt) over tickets closed in the range
///  - Yanıt Süresi           = avg(first staff Response − CreatedAt) over tickets opened
///                             in the range that have a staff response
///  - SLA Uyumu              = of tickets closed in the range: % closed on/before their
///                             due instant (DueDate ?? EstimatedDueDate); tickets without
///                             a due instant count compliant unless flagged overdue
///  - stat notes "+%12"      = delta vs the previous period of equal length
/// Grouping: department = ticket's department, topic = ticket's help topic
/// ("Parent / Child" label, ticket-open precedent), agent = ticket's CURRENT assignee
/// (thread events don't snapshot dept/topic/assignee — deviation from osTicket's
/// per-event snapshot, noted in ROADMAP).
/// </summary>
public static class DashboardEngine
{
    public enum BucketKind { Day, Week, Month }

    /// <summary>Resolved reporting window; PrevStart..Start is the comparison period.</summary>
    public sealed record DateRange(
        DateTimeOffset Start, DateTimeOffset End, DateTimeOffset PrevStart,
        DateOnly StartDate, string RangeKey);

    /// <summary>One chart bucket. Month/Day feed the localized x-label (db.mo* keys).</summary>
    public sealed record Bucket(DateTimeOffset Start, DateTimeOffset End, int Month, int Day, BucketKind Kind);

    /// <summary>Flat per-ticket projection for tickets touching the range.</summary>
    public sealed record TicketFact(
        DateTimeOffset CreatedAt,
        DateTimeOffset? ClosedAt,
        DateTimeOffset? ReopenedAt,
        bool IsOverdue,
        DateTimeOffset? Due,
        DateTimeOffset? FirstResponseAt,
        string DeptName,
        string? TopicName,
        string? AgentName);

    /// <summary>An "assigned" thread event in range, carrying the ticket's group names.</summary>
    public sealed record AssignFact(string DeptName, string? TopicName, string? AgentName);

    public sealed record StatRow(
        string Name, int Opened, int Assigned, int Overdue, int Closed, int Reopened,
        double? AvgServiceHours, double? AvgResponseHours);

    public sealed record TileStats(
        int Opened, int Closed, double? AvgFirstResponseHours, int? SlaCompliancePct);

    public static readonly string[] SortableColumns = ["opened", "assigned", "overdue", "closed", "reopened"];

    // ---- range & buckets (pure) ---------------------------------------------

    /// <summary>
    /// Mockup range control semantics: a start date + a period select.
    /// "Son 30 gün" = 30 days from the start date; "Bu çeyrek" = the calendar quarter
    /// containing it; "Bu yıl" = its calendar year (chart buckets cap at 'now', which
    /// reproduces the mockup's Oca..Ağu year-to-date labels). Default = the 30-day
    /// window ending today.
    /// </summary>
    public static DateRange Resolve(string? start, string? range, DateTimeOffset now)
    {
        var rangeKey = range is "quarter" or "year" ? range : "30";
        var today = DateOnly.FromDateTime(now.Date);
        var startDate = DateOnly.TryParseExact(start, "yyyy-MM-dd", out var parsed)
            ? parsed
            : today.AddDays(-29);

        DateTimeOffset At(DateOnly d) => new(d.Year, d.Month, d.Day, 0, 0, 0, now.Offset);

        DateTimeOffset s, e;
        switch (rangeKey)
        {
            case "quarter":
                var qStartMonth = ((startDate.Month - 1) / 3) * 3 + 1;
                s = At(new DateOnly(startDate.Year, qStartMonth, 1));
                e = s.AddMonths(3);
                break;
            case "year":
                s = At(new DateOnly(startDate.Year, 1, 1));
                e = s.AddYears(1);
                break;
            default:
                s = At(startDate);
                e = s.AddDays(30);
                break;
        }
        return new DateRange(s, e, s - (e - s), startDate, rangeKey);
    }

    /// <summary>
    /// Buckets across the range, capped at 'now' (a "Bu yıl" range renders year-to-date,
    /// like the mockup's Oca..Ağu axis). Granularity: ≤31 days daily, ≤120 days weekly,
    /// else monthly.
    /// </summary>
    public static IReadOnlyList<Bucket> Buckets(DateRange range, DateTimeOffset now)
    {
        var cap = range.End < now ? range.End : now;
        var days = (range.End - range.Start).TotalDays;
        var kind = days <= 31 ? BucketKind.Day : days <= 120 ? BucketKind.Week : BucketKind.Month;

        var buckets = new List<Bucket>();
        var cursor = range.Start;
        while (cursor < cap || buckets.Count == 0)
        {
            var next = kind switch
            {
                BucketKind.Day => cursor.AddDays(1),
                BucketKind.Week => cursor.AddDays(7),
                _ => cursor.AddMonths(1),
            };
            if (next > range.End)
                next = range.End;
            buckets.Add(new Bucket(cursor, next, cursor.Month, cursor.Day, kind));
            cursor = next;
            if (cursor >= range.End)
                break;
        }
        return buckets;
    }

    /// <summary>Show every k-th x label (the mockup axis carries 8 labels).</summary>
    public static int LabelEvery(IReadOnlyList<Bucket> buckets) =>
        buckets.Count == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(buckets.Count / 8.0));

    /// <summary>
    /// Y-axis max as 3 equal "nice" steps covering the series max (the mockup grid is
    /// 0/50/100/150 — i.e. step 50 for a max value of 128).
    /// </summary>
    public static int NiceMax(int maxValue)
    {
        if (maxValue <= 0)
            return 3;
        int[] nice = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];
        foreach (var step in nice)
        {
            if (3 * step >= maxValue)
                return 3 * step;
        }
        var big = (int)Math.Ceiling(maxValue / 3.0);
        return 3 * big;
    }

    // ---- queries ------------------------------------------------------------

    /// <summary>
    /// Tickets whose created/closed/reopened instant falls in the range, projected flat.
    /// Aggregation happens in memory (the window is bounded and the per-column ranges
    /// differ; avoids untranslatable DateTimeOffset arithmetic in SQL).
    /// </summary>
    public static Task<List<TicketFact>> FactsAsync(
        AppDbContext db, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        db.Tickets
            .Where(t => (t.CreatedAt >= start && t.CreatedAt < end)
                || (t.ClosedAt >= start && t.ClosedAt < end)
                || (t.ReopenedAt >= start && t.ReopenedAt < end))
            .Select(t => new TicketFact(
                t.CreatedAt,
                t.ClosedAt,
                t.ReopenedAt,
                t.IsOverdue,
                t.DueDate ?? t.EstimatedDueDate,
                t.Thread!.Entries
                    .Where(e => e.Type == ThreadEntryType.Response && e.StaffId != null)
                    .Min(e => (DateTimeOffset?)e.CreatedAt),
                t.Department!.Name,
                t.HelpTopic == null
                    ? null
                    : t.HelpTopic.Parent != null
                        ? t.HelpTopic.Parent.Name + " / " + t.HelpTopic.Name
                        : t.HelpTopic.Name,
                t.Staff != null ? t.Staff.FullName : null))
            .ToListAsync(ct);

    /// <summary>"assigned" thread events in range, joined back to their ticket's groups.</summary>
    public static Task<List<AssignFact>> AssignedFactsAsync(
        AppDbContext db, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        db.ThreadEvents
            .Where(ev => ev.EventType!.Name == "assigned" && !ev.Annulled
                && ev.OccurredAt >= start && ev.OccurredAt < end)
            .Join(db.Tickets, ev => ev.ThreadId, t => t.ThreadId, (ev, t) => new AssignFact(
                t.Department!.Name,
                t.HelpTopic == null
                    ? null
                    : t.HelpTopic.Parent != null
                        ? t.HelpTopic.Parent.Name + " / " + t.HelpTopic.Name
                        : t.HelpTopic.Name,
                t.Staff != null ? t.Staff.FullName : null))
            .ToListAsync(ct);

    /// <summary>Opened/closed counts of the previous period (tile delta notes).</summary>
    public static async Task<(int Opened, int Closed)> PrevCountsAsync(
        AppDbContext db, DateTimeOffset prevStart, DateTimeOffset prevEnd, CancellationToken ct = default)
    {
        var opened = await db.Tickets.CountAsync(
            t => t.CreatedAt >= prevStart && t.CreatedAt < prevEnd, ct);
        var closed = await db.Tickets.CountAsync(
            t => t.ClosedAt >= prevStart && t.ClosedAt < prevEnd, ct);
        return (opened, closed);
    }

    // ---- pure aggregation ----------------------------------------------------

    /// <summary>Chart series: opened / resolved counts per bucket.</summary>
    public static (int[] Opened, int[] Closed) Series(
        IReadOnlyList<Bucket> buckets, IReadOnlyList<TicketFact> facts)
    {
        var opened = new int[buckets.Count];
        var closed = new int[buckets.Count];
        for (var i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            opened[i] = facts.Count(f => f.CreatedAt >= b.Start && f.CreatedAt < b.End);
            closed[i] = facts.Count(f => f.ClosedAt >= b.Start && f.ClosedAt < b.End);
        }
        return (opened, closed);
    }

    public static TileStats Tiles(IReadOnlyList<TicketFact> facts, DateRange r)
    {
        var opened = facts.Count(f => InRange(f.CreatedAt, r));
        var closedFacts = facts.Where(f => f.ClosedAt is { } c && InRange(c, r)).ToList();

        var responded = facts
            .Where(f => InRange(f.CreatedAt, r) && f.FirstResponseAt is not null)
            .Select(f => (f.FirstResponseAt!.Value - f.CreatedAt).TotalHours)
            .ToList();

        int? slaPct = null;
        if (closedFacts.Count > 0)
        {
            var compliant = closedFacts.Count(f => f.Due is { } due
                ? f.ClosedAt!.Value <= due
                : !f.IsOverdue);
            slaPct = (int)Math.Round(100.0 * compliant / closedFacts.Count);
        }

        return new TileStats(
            opened, closedFacts.Count,
            responded.Count > 0 ? responded.Average() : null,
            slaPct);
    }

    /// <summary>
    /// One stats table (dept/topic/agent) — group selector returns the row name or null
    /// to exclude the fact (e.g. unassigned tickets have no agent row).
    /// </summary>
    public static List<StatRow> Rows(
        IReadOnlyList<TicketFact> facts,
        IReadOnlyList<AssignFact> assigns,
        Func<TicketFact, string?> dim,
        Func<AssignFact, string?> assignDim,
        DateRange r,
        string sort,
        bool desc)
    {
        var assigned = assigns
            .Select(assignDim)
            .Where(n => n is not null)
            .GroupBy(n => n!)
            .ToDictionary(g => g.Key, g => g.Count());

        var names = facts.Select(dim).Where(n => n is not null).Select(n => n!)
            .Concat(assigned.Keys)
            .Distinct()
            .ToList();

        var rows = new List<StatRow>();
        foreach (var name in names)
        {
            var group = facts.Where(f => dim(f) == name).ToList();
            var openedFacts = group.Where(f => InRange(f.CreatedAt, r)).ToList();
            var closedFacts = group.Where(f => f.ClosedAt is { } c && InRange(c, r)).ToList();
            var service = closedFacts
                .Select(f => (f.ClosedAt!.Value - f.CreatedAt).TotalHours).ToList();
            var response = openedFacts
                .Where(f => f.FirstResponseAt is not null)
                .Select(f => (f.FirstResponseAt!.Value - f.CreatedAt).TotalHours).ToList();

            var row = new StatRow(
                name,
                openedFacts.Count,
                assigned.TryGetValue(name, out var a) ? a : 0,
                openedFacts.Count(f => f.IsOverdue),
                closedFacts.Count,
                group.Count(f => f.ReopenedAt is { } ro && InRange(ro, r)),
                service.Count > 0 ? service.Average() : null,
                response.Count > 0 ? response.Average() : null);
            if (row.Opened + row.Assigned + row.Overdue + row.Closed + row.Reopened > 0)
                rows.Add(row);
        }

        Func<StatRow, int> key = sort switch
        {
            "assigned" => x => x.Assigned,
            "overdue" => x => x.Overdue,
            "closed" => x => x.Closed,
            "reopened" => x => x.Reopened,
            _ => x => x.Opened,
        };
        var ordered = desc ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>?sort/?dir over the five sortable columns; default opened-desc (mockup).</summary>
    public static (string Sort, bool Desc) ResolveSort(string? sort, string? dir)
    {
        var key = sort is not null && SortableColumns.Contains(sort) ? sort : "opened";
        return (key, dir != "asc");
    }

    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static bool InRange(DateTimeOffset instant, DateRange r) =>
        instant >= r.Start && instant < r.End;
}
