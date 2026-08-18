using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Admin.Controllers;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Tests;

/// <summary>
/// Admin dashboard aggregates (ROADMAP §6.3 dashboard row, B10): range resolution,
/// chart bucketing, and the FactsAsync/Rows/Tiles aggregation incl. range boundaries
/// and the empty range. Synthetic tickets live in far-past windows (2003/2004) so the
/// shared seeded canon never leaks into the asserted counts.
/// </summary>
[Collection("Postgres")]
public class AdminDashboardTests(PostgresFixture fixture)
{
    private static readonly TimeSpan Tz = TimeSpan.FromHours(3);

    private static DashboardEngine.DateRange Window(int year, int month, int day, int days) =>
        Resolved(new DateTimeOffset(year, month, day, 0, 0, 0, Tz), days);

    private static DashboardEngine.DateRange Resolved(DateTimeOffset start, int days) =>
        new(start, start.AddDays(days), start.AddDays(-days),
            DateOnly.FromDateTime(start.Date), "30");

    // ---- range resolution (pure) --------------------------------------------

    [Fact]
    public void Resolve_DefaultsToThirtyDayWindowEndingToday()
    {
        var now = new DateTimeOffset(2026, 8, 18, 14, 30, 0, Tz);
        var r = DashboardEngine.Resolve(null, null, now);

        Assert.Equal("30", r.RangeKey);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, Tz), r.Start);
        Assert.Equal(r.Start.AddDays(30), r.End);
        // Window ends today (exclusive end = tomorrow midnight).
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 0, 0, 0, Tz), r.End);
        // Comparison period sits immediately before with equal length.
        Assert.Equal(r.Start - (r.End - r.Start), r.PrevStart);
    }

    [Fact]
    public void Resolve_QuarterAndYearUseTheCalendarPeriodOfTheStartDate()
    {
        var now = new DateTimeOffset(2026, 8, 18, 14, 30, 0, Tz);

        var q = DashboardEngine.Resolve("2026-08-05", "quarter", now);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, Tz), q.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, Tz), q.End);

        var y = DashboardEngine.Resolve("2026-07-12", "year", now);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, Tz), y.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, Tz), y.End);
        Assert.Equal(y.Start.AddYears(-1), y.PrevStart);

        // Garbage input falls back to the default window.
        var bad = DashboardEngine.Resolve("not-a-date", "bogus", now);
        Assert.Equal("30", bad.RangeKey);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, Tz), bad.Start);
    }

    [Fact]
    public void Buckets_YearRangeIsMonthlyAndCapsAtNow()
    {
        var now = new DateTimeOffset(2026, 8, 18, 14, 30, 0, Tz);
        var r = DashboardEngine.Resolve("2026-07-12", "year", now);
        var buckets = DashboardEngine.Buckets(r, now);

        // Oca..Ağu — exactly the mockup's year-to-date axis.
        Assert.Equal(8, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(DashboardEngine.BucketKind.Month, b.Kind));
        Assert.Equal(1, buckets[0].Month);
        Assert.Equal(8, buckets[^1].Month);
        Assert.Equal(1, DashboardEngine.LabelEvery(buckets));
    }

    [Fact]
    public void Buckets_ThirtyDayRangeIsDaily_AndSparseLabels()
    {
        var now = new DateTimeOffset(2026, 8, 18, 14, 30, 0, Tz);
        var r = DashboardEngine.Resolve(null, null, now);
        var buckets = DashboardEngine.Buckets(r, now);

        Assert.Equal(30, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(DashboardEngine.BucketKind.Day, b.Kind));
        Assert.Equal(r.Start, buckets[0].Start);
        // Buckets tile the range without gaps.
        for (var i = 1; i < buckets.Count; i++)
            Assert.Equal(buckets[i - 1].End, buckets[i].Start);
        // ~8 labels like the mockup axis.
        Assert.Equal(4, DashboardEngine.LabelEvery(buckets));
    }

    [Fact]
    public void NiceMax_ProducesThreeNiceSteps()
    {
        Assert.Equal(3, DashboardEngine.NiceMax(0));     // flat/empty series still draws a grid
        Assert.Equal(3, DashboardEngine.NiceMax(2));
        Assert.Equal(15, DashboardEngine.NiceMax(11));
        Assert.Equal(150, DashboardEngine.NiceMax(128)); // the mockup's 0/50/100/150 grid
        Assert.Equal(300, DashboardEngine.NiceMax(151));
    }

    // ---- aggregate queries ---------------------------------------------------

    [Fact]
    public async Task Facts_RangeBoundaries_IncludeStartExcludeEnd()
    {
        var r = Window(2003, 5, 10, 30);

        using (var seedScope = new ServiceScopeBundle(fixture))
        {
            var db = seedScope.Db;
            var dept = await db.Departments.FirstAsync(d => d.Name == "Destek");
            var user = await db.Users.FirstAsync(u => u.Name == "Bourla Salehi");
            var status = await db.TicketStatuses.FirstAsync(s => s.Key == "open");

            Ticket T(string number, DateTimeOffset created, DateTimeOffset? closed = null) =>
                new()
                {
                    Number = number, Subject = "Sınır testi", UserId = user.Id,
                    DepartmentId = dept.Id, StatusId = status.Id, CreatedAt = created,
                    LastUpdateAt = created, ClosedAt = closed,
                    Thread = new Thread { CreatedAt = created },
                };

            db.Tickets.AddRange(
                T("DBB-0001", r.Start),                    // first instant — inside
                T("DBB-0002", r.End.AddSeconds(-1)),       // last second — inside
                T("DBB-0003", r.End),                      // exclusive end — outside
                T("DBB-0004", r.Start.AddSeconds(-1)),     // previous period
                // Created before the range, closed exactly at Start — closed-in-range.
                T("DBB-0005", r.Start.AddDays(-5), closed: r.Start));
            await db.SaveChangesAsync();
        }

        // Fresh scope for the asserts (suite convention).
        using var s = new ServiceScopeBundle(fixture);
        var facts = (await DashboardEngine.FactsAsync(s.Db, r.Start, r.End))
            .Where(f => f.DeptName == "Destek").ToList();

        var tiles = DashboardEngine.Tiles(facts, r);
        Assert.Equal(2, tiles.Opened);   // DBB-0001 + DBB-0002
        Assert.Equal(1, tiles.Closed);   // DBB-0005

        var prev = await DashboardEngine.PrevCountsAsync(s.Db, r.PrevStart, r.Start);
        Assert.Equal(2, prev.Opened);    // DBB-0004 + DBB-0005 (created 5 days before Start)
    }

    [Fact]
    public async Task EmptyRange_YieldsZeroEverything()
    {
        var r = Window(1999, 2, 1, 30);

        using var s = new ServiceScopeBundle(fixture);
        var facts = await DashboardEngine.FactsAsync(s.Db, r.Start, r.End);
        var assigns = await DashboardEngine.AssignedFactsAsync(s.Db, r.Start, r.End);
        Assert.Empty(facts);
        Assert.Empty(assigns);

        var tiles = DashboardEngine.Tiles(facts, r);
        Assert.Equal(0, tiles.Opened);
        Assert.Equal(0, tiles.Closed);
        Assert.Null(tiles.AvgFirstResponseHours);
        Assert.Null(tiles.SlaCompliancePct);

        var buckets = DashboardEngine.Buckets(r, DateTimeOffset.Now);
        var (opened, closed) = DashboardEngine.Series(buckets, facts);
        Assert.All(opened, v => Assert.Equal(0, v));
        Assert.All(closed, v => Assert.Equal(0, v));

        Assert.Empty(DashboardEngine.Rows(facts, assigns, f => f.DeptName, a => a.DeptName, r, "opened", true));
    }

    [Fact]
    public async Task Rows_GroupSortAndDeriveEveryColumn()
    {
        var r = Window(2004, 3, 1, 30);
        string agentName;

        using (var seedScope = new ServiceScopeBundle(fixture))
        {
            var db = seedScope.Db;
            var bordro = await db.Departments.FirstAsync(d => d.Name == "Bordro");
            var destek = await db.Departments.FirstAsync(d => d.Name == "Destek");
            var user = await db.Users.FirstAsync(u => u.Name == "Bourla Salehi");
            var open = await db.TicketStatuses.FirstAsync(x => x.Key == "open");
            var solved = await db.TicketStatuses.FirstAsync(x => x.Key == "solved");
            var uakin = await db.Staff.FirstAsync(x => x.Username == "uakin");
            agentName = uakin.FullName;
            var assignedType = await db.ThreadEventTypes.FirstAsync(x => x.Name == "assigned");

            Ticket T(string number, Department dept, DateTimeOffset created,
                DateTimeOffset? closed = null, int? staffId = null, bool overdue = false) =>
                new()
                {
                    Number = number, Subject = "Aggregate testi", UserId = user.Id,
                    DepartmentId = dept.Id, StatusId = closed is null ? open.Id : solved.Id,
                    CreatedAt = created, LastUpdateAt = created, ClosedAt = closed,
                    StaffId = staffId, IsOverdue = overdue,
                    Thread = new Thread { CreatedAt = created },
                };

            var b1 = T("DBR-0001", bordro, r.Start.AddDays(1), staffId: uakin.Id, overdue: true);
            // Closed 5h after creation → Bordro service avg 5.0.
            var b2 = T("DBR-0002", bordro, r.Start.AddDays(2), closed: r.Start.AddDays(2).AddHours(5));
            b2.ReopenedAt = r.Start.AddDays(3);
            var d1 = T("DBR-0003", destek, r.Start.AddDays(4));

            // First staff response 2h after creation → Bordro response avg 2.0.
            b1.Thread!.Entries.Add(new ThreadEntry
            {
                Type = ThreadEntryType.Response, StaffId = uakin.Id, Poster = "Ümit Y. Akın",
                Body = "Yanıt", CreatedAt = b1.CreatedAt.AddHours(2),
            });
            // An in-range "assigned" event on the Destek ticket.
            d1.Thread!.Events.Add(new ThreadEvent
            {
                EventTypeId = assignedType.Id, OccurredAt = r.Start.AddDays(4).AddMinutes(5),
                Username = "test", ActorType = ActorType.Staff,
            });

            db.Tickets.AddRange(b1, b2, d1);
            await db.SaveChangesAsync();
        }

        using var s = new ServiceScopeBundle(fixture);
        var facts = await DashboardEngine.FactsAsync(s.Db, r.Start, r.End);
        var assigns = await DashboardEngine.AssignedFactsAsync(s.Db, r.Start, r.End);

        var rows = DashboardEngine.Rows(facts, assigns, f => f.DeptName, a => a.DeptName, r, "opened", true);
        Assert.Equal(2, rows.Count);
        // Default sort: opened desc — Bordro (2) before Destek (1).
        Assert.Equal(["Bordro", "Destek"], rows.Select(x => x.Name).ToArray());

        var bordroRow = rows[0];
        Assert.Equal(2, bordroRow.Opened);
        Assert.Equal(1, bordroRow.Overdue);
        Assert.Equal(1, bordroRow.Closed);
        Assert.Equal(1, bordroRow.Reopened);
        Assert.Equal(0, bordroRow.Assigned);
        Assert.NotNull(bordroRow.AvgServiceHours);
        Assert.Equal(5.0, bordroRow.AvgServiceHours!.Value, 3);
        Assert.NotNull(bordroRow.AvgResponseHours);
        Assert.Equal(2.0, bordroRow.AvgResponseHours!.Value, 3);

        var destekRow = rows[1];
        Assert.Equal(1, destekRow.Opened);
        Assert.Equal(1, destekRow.Assigned); // from the thread event
        Assert.Null(destekRow.AvgServiceHours);

        // Ascending flips the order.
        var asc = DashboardEngine.Rows(facts, assigns, f => f.DeptName, a => a.DeptName, r, "opened", false);
        Assert.Equal(["Destek", "Bordro"], asc.Select(x => x.Name).ToArray());

        // Agent table: only the assigned ticket surfaces, under the CURRENT assignee.
        var agentRows = DashboardEngine.Rows(facts, assigns, f => f.AgentName, a => a.AgentName, r, "opened", true);
        var mine = Assert.Single(agentRows, x => x.Name == agentName);
        Assert.Equal(1, mine.Opened);

        // Chart series bucket the same facts by day.
        var buckets = DashboardEngine.Buckets(r, DateTimeOffset.Now);
        var (opened, _) = DashboardEngine.Series(buckets, facts);
        Assert.Equal(3, opened.Sum());
        Assert.Equal(1, opened[1]); // b1 on day 2 of the window
        Assert.Equal(1, opened[2]);
        Assert.Equal(1, opened[4]);
    }

    [Fact]
    public async Task SlaCompliance_ComparesClosedAtAgainstDueInstant()
    {
        var r = Window(2005, 6, 1, 30);

        using (var seedScope = new ServiceScopeBundle(fixture))
        {
            var db = seedScope.Db;
            var dept = await db.Departments.FirstAsync(d => d.Name == "Danışmanlık");
            var user = await db.Users.FirstAsync(u => u.Name == "Bourla Salehi");
            var solved = await db.TicketStatuses.FirstAsync(x => x.Key == "solved");

            Ticket T(string number, DateTimeOffset created, DateTimeOffset closed, DateTimeOffset? due) =>
                new()
                {
                    Number = number, Subject = "SLA testi", UserId = user.Id,
                    DepartmentId = dept.Id, StatusId = solved.Id, CreatedAt = created,
                    LastUpdateAt = closed, ClosedAt = closed, DueDate = due,
                    Thread = new Thread { CreatedAt = created },
                };

            var c1 = r.Start.AddDays(1);
            db.Tickets.AddRange(
                T("DBS-0001", c1, c1.AddHours(4), c1.AddHours(8)),   // on time
                T("DBS-0002", c1, c1.AddHours(12), c1.AddHours(8)),  // breached
                T("DBS-0003", c1, c1.AddHours(4), null),             // no due → compliant
                T("DBS-0004", c1, c1.AddHours(4), null));
            await db.SaveChangesAsync();
        }

        using var s = new ServiceScopeBundle(fixture);
        var facts = (await DashboardEngine.FactsAsync(s.Db, r.Start, r.End))
            .Where(f => f.DeptName == "Danışmanlık").ToList();
        var tiles = DashboardEngine.Tiles(facts, r);

        Assert.Equal(4, tiles.Closed);
        Assert.Equal(75, tiles.SlaCompliancePct); // 3 of 4
    }
}
