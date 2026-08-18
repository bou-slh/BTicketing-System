using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin dashboard (mockups/admin/dashboard.html, ROADMAP §6.3 + §4 B10): date-range
/// driven stat tiles, the data-generated activity chart (same SVG structure as the
/// mockup's hardcoded one) and the three sortable stats tables, plus CSV export.
/// All aggregate definitions live in <see cref="DashboardEngine"/>.
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class DashboardController(AppDbContext db) : Controller
{
    [HttpGet("/admin")]
    [HttpGet("/admin/dashboard")]
    [NavKey("dashboard")]
    public async Task<IActionResult> Index(
        string? start, string? range, string? sort, string? dir, string? tab,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var r = DashboardEngine.Resolve(start, range, now);
        var (sortKey, desc) = DashboardEngine.ResolveSort(sort, dir);

        var facts = await DashboardEngine.FactsAsync(db, r.Start, r.End, ct);
        var assigns = await DashboardEngine.AssignedFactsAsync(db, r.Start, r.End, ct);
        var prev = await DashboardEngine.PrevCountsAsync(db, r.PrevStart, r.Start, ct);

        var buckets = DashboardEngine.Buckets(r, now);
        var (opened, closed) = DashboardEngine.Series(buckets, facts);

        return View(new AdminDashboardVm(
            r,
            DashboardEngine.Tiles(facts, r),
            prev.Opened,
            prev.Closed,
            buckets,
            opened,
            closed,
            DashboardEngine.NiceMax(Math.Max(opened.DefaultIfEmpty(0).Max(), closed.DefaultIfEmpty(0).Max())),
            DashboardEngine.LabelEvery(buckets),
            DashboardEngine.Rows(facts, assigns, f => f.DeptName, a => a.DeptName, r, sortKey, desc),
            DashboardEngine.Rows(facts, assigns, f => f.TopicName, a => a.TopicName, r, sortKey, desc),
            DashboardEngine.Rows(facts, assigns, f => f.AgentName, a => a.AgentName, r, sortKey, desc),
            sortKey,
            desc,
            tab is "topic" or "agent" ? tab : "dept"));
    }

    /// <summary>
    /// "Dışa Aktar" on the İstatistikler surface: one CSV of all three stats tables for
    /// the current range + sort, section-tagged by a Grup column (UTF-8 BOM — agent
    /// tickets export precedent).
    /// </summary>
    [HttpGet("/admin/dashboard/export")]
    public async Task<IActionResult> Export(
        string? start, string? range, string? sort, string? dir,
        [FromServices] IStringLocalizerFactory localizerFactory,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var r = DashboardEngine.Resolve(start, range, now);
        var (sortKey, desc) = DashboardEngine.ResolveSort(sort, dir);

        var facts = await DashboardEngine.FactsAsync(db, r.Start, r.End, ct);
        var assigns = await DashboardEngine.AssignedFactsAsync(db, r.Start, r.End, ct);

        var pageL = localizerFactory.Create(
            "Areas.Admin.Views.Dashboard.Index", typeof(Program).Assembly.GetName().Name!);

        (string SectionKey, List<DashboardEngine.StatRow> Rows)[] sections =
        [
            ("db.tabDept", DashboardEngine.Rows(facts, assigns, f => f.DeptName, a => a.DeptName, r, sortKey, desc)),
            ("db.tabTopic", DashboardEngine.Rows(facts, assigns, f => f.TopicName, a => a.TopicName, r, sortKey, desc)),
            ("db.tabAgent", DashboardEngine.Rows(facts, assigns, f => f.AgentName, a => a.AgentName, r, sortKey, desc)),
        ];

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=istatistikler.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string[] headers =
        [
            pageL["db.colGroup"], pageL["db.stats"], pageL["db.colOpened"], pageL["db.colAssigned"],
            pageL["db.colOverdue"], pageL["db.colClosed"], pageL["db.colReopened"],
            pageL["db.colService"], pageL["db.colResponse"],
        ];
        await writer.WriteLineAsync(string.Join(",", headers.Select(DashboardEngine.Csv)));
        foreach (var (sectionKey, rows) in sections)
        {
            foreach (var row in rows)
            {
                string?[] cells =
                [
                    pageL[sectionKey],
                    row.Name,
                    row.Opened.ToString(),
                    row.Assigned.ToString(),
                    row.Overdue.ToString(),
                    row.Closed.ToString(),
                    row.Reopened.ToString(),
                    row.AvgServiceHours?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    row.AvgResponseHours?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ];
                await writer.WriteLineAsync(string.Join(",", cells.Select(DashboardEngine.Csv)));
            }
        }
        return new EmptyResult();
    }
}

public sealed record AdminDashboardVm(
    DashboardEngine.DateRange Range,
    DashboardEngine.TileStats Tiles,
    int PrevOpened,
    int PrevClosed,
    IReadOnlyList<DashboardEngine.Bucket> Buckets,
    int[] OpenedSeries,
    int[] ClosedSeries,
    int YMax,
    int LabelEvery,
    List<DashboardEngine.StatRow> DeptRows,
    List<DashboardEngine.StatRow> TopicRows,
    List<DashboardEngine.StatRow> AgentRows,
    string Sort,
    bool Desc,
    string ActiveTab);
