using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

// ---- view model ---------------------------------------------------------------------

public sealed record AuditRowVm(
    DateTimeOffset OccurredAt,
    string ActorName,
    string EventText,
    string ObjectPhrase,
    string? Link,
    string? Ip);

public sealed record AuditLogsIndexVm(
    string? From,
    string? To,
    string Type,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int FromRow,
    int ToRow,
    IReadOnlyList<AuditRowVm> Rows);

/// <summary>
/// Admin audit logs (mockups/admin/audit-logs.html, ROADMAP §6.3 + §4 B10): the B1
/// engine over the AuditEvent table the S3 interceptor has been filling on every
/// domain mutation — from/to + type filters, time sort, pagination, CSV export
/// (UTF-8 BOM precedent) and the B10 drill-down: each row's object cell links to the
/// object's real page where one exists (LinkFor map below; tested). The mockup's
/// narrative event sentences are sample data — real rows render the interceptor's
/// Action + object phrase (+ changed-column names from the Data diff) via the
/// al.ev* keys (INVENTED rendering, flagged). Type filter map: Talep→Ticket,
/// Kullanıcı→User, Temsilci→Staff, Ayar→Setting — Setting is INotAudited today, so
/// the Ayar option filters honestly to empty (canon flag: the mockup's setting rows
/// imply auditing settings saves; osTicket audits neither).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class AuditLogsController(AppDbContext db) : Controller
{
    public const int PageSize = 10; // the mockup's page: 10 rows, "1–10 / 1.206"

    private static readonly string[] Types = ["all", "ticket", "user", "agent", "setting"];

    [HttpGet("/admin/audit-logs")]
    [NavKey("audit-logs")]
    public async Task<IActionResult> Index(
        string? from, string? to, string? type, string? dir, int page = 1,
        [FromServices] IStringLocalizerFactory localizerFactory = null!,
        CancellationToken ct = default)
    {
        type = Types.Contains(type) ? type! : "all";
        var desc = dir != "asc"; // mockup indicator: time sorted-desc
        var query = BuildQuery(from, to, type);

        var total = await query.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);

        var ordered = desc
            ? query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id);
        var events = await ordered.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        var l = PageLocalizer(localizerFactory);
        var rows = events.Select(e => ToRow(l, e)).ToList();

        return View(new AuditLogsIndexVm(
            NormalizeDay(from), NormalizeDay(to), type, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows));
    }

    /// <summary>Toolbar "CSV Dışa Aktar": the FILTERED trail as CSV (UTF-8 BOM, orgs precedent).</summary>
    [HttpGet("/admin/audit-logs/export")]
    public async Task<IActionResult> Export(
        string? from, string? to, string? type, string? dir,
        [FromServices] IStringLocalizerFactory localizerFactory,
        CancellationToken ct = default)
    {
        type = Types.Contains(type) ? type! : "all";
        var desc = dir != "asc";
        var query = BuildQuery(from, to, type);
        var ordered = desc
            ? query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id);
        var events = await ordered.ToListAsync(ct);

        var l = PageLocalizer(localizerFactory);
        var dateFmt = l["al.dateFmt"].Value;

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=denetim-gunlukleri.csv";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string[] headers = [l["al.colTime"], l["al.colUser"], l["al.colEvent"], l["al.colObject"], l["al.colIp"]];
        await writer.WriteLineAsync(string.Join(",", headers.Select(DashboardEngine.Csv)));
        foreach (var e in events)
        {
            var row = ToRow(l, e);
            string?[] cells =
            [
                row.OccurredAt.ToLocalTime().ToString(dateFmt, CultureInfo.CurrentUICulture),
                row.ActorName, row.EventText, row.ObjectPhrase, row.Ip,
            ];
            await writer.WriteLineAsync(string.Join(",", cells.Select(DashboardEngine.Csv)));
        }
        return new EmptyResult();
    }

    // ---- drill-down map (B10; unit-tested) ------------------------------------------------

    /// <summary>
    /// The audited object's real page. Editors take ?id=; dialog-based admin lists
    /// (teams/SLAs/pages/templates/banlist/api keys) link to the list page that hosts
    /// their per-row dialog (partial drill-down, flagged). Types without any page
    /// (thread entities, notes, effort proposals, settings…) return null → the row
    /// renders plain.
    /// </summary>
    public static string? LinkFor(string objectType, string objectId)
    {
        var idRoute = objectType switch
        {
            "Ticket" => "/agent/ticket-view",
            "TaskItem" => "/agent/task-view",
            "User" => "/agent/user-view",
            "Organization" => "/agent/org-view",
            "Staff" => "/admin/staff-edit",
            "Department" => "/admin/department-edit",
            "HelpTopic" => "/admin/helptopic-edit",
            "Role" => "/admin/role-edit",
            "Schedule" => "/admin/schedule-edit",
            "Filter" => "/admin/filter-edit",
            "SavedQueue" => "/admin/queues",
            "FormDefinition" => "/admin/form-edit",
            "ListDefinition" => "/admin/list-edit",
            "EmailAccount" => "/admin/email-edit",
            "FaqArticle" => "/agent/kb-faq",
            _ => null,
        };
        if (idRoute is not null)
            return int.TryParse(objectId, out var id) ? $"{idRoute}?id={id}" : null;

        return objectType switch
        {
            "Team" => "/admin/teams",
            "SlaPlan" => "/admin/slas",
            "SitePage" => "/admin/pages",
            "EmailTemplateSet" => "/admin/templates",
            "BanlistEntry" => "/admin/banlist",
            "ApiKey" => "/admin/apikeys",
            "KbCategory" => "/agent/kb",
            "CannedResponse" => "/agent/canned",
            _ => null,
        };
    }

    /// <summary>Localized object-type name key; null falls back to the CLR name (flagged).</summary>
    public static string? ObjectKeyFor(string objectType) => objectType switch
    {
        "Ticket" => "al.typeTicket",
        "User" => "al.typeUser",
        "Staff" => "al.typeAgent",
        "Setting" => "al.typeSetting",
        "TaskItem" => "al.objTask",
        "Organization" => "al.objOrg",
        "Department" => "al.objDept",
        "HelpTopic" => "al.objTopic",
        "Role" => "al.objRole",
        "Team" => "al.objTeam",
        "SlaPlan" => "al.objSla",
        "Schedule" => "al.objSchedule",
        "Filter" => "al.objFilter",
        "SavedQueue" => "al.objQueue",
        "FormDefinition" => "al.objForm",
        "ListDefinition" => "al.objList",
        "SitePage" => "al.objSitePage",
        "EmailAccount" => "al.objEmail",
        "EmailTemplateSet" => "al.objTemplate",
        "FaqArticle" => "al.objFaq",
        "KbCategory" => "al.objKbCat",
        "CannedResponse" => "al.objCanned",
        "BanlistEntry" => "al.objBan",
        "ApiKey" => "al.objApiKey",
        "EffortProposal" => "al.objEffort",
        "Thread" => "al.objThread",
        "ThreadEntry" => "al.objThreadEntry",
        "ThreadEvent" => "al.objThreadEvent",
        "ThreadCollaborator" => "al.objCollab",
        _ => null,
    };

    // ---- helpers --------------------------------------------------------------------------

    private IQueryable<Domain.Entities.AuditEvent> BuildQuery(string? from, string? to, string type)
    {
        var query = db.AuditEvents.AsQueryable();
        query = type switch
        {
            "ticket" => query.Where(e => e.ObjectType == "Ticket"),
            "user" => query.Where(e => e.ObjectType == "User"),
            "agent" => query.Where(e => e.ObjectType == "Staff"),
            // Honest empty today: Setting is INotAudited (see class doc).
            "setting" => query.Where(e => e.ObjectType == "Setting"),
            _ => query,
        };
        if (ParseDay(from) is { } fromAt)
            query = query.Where(e => e.OccurredAt >= fromAt);
        if (ParseDay(to) is { } toAt)
            query = query.Where(e => e.OccurredAt < toAt.AddDays(1)); // inclusive end day
        return query;
    }

    private static AuditRowVm ToRow(IStringLocalizer l, Domain.Entities.AuditEvent e)
    {
        var typeName = ObjectKeyFor(e.ObjectType) is { } key ? l[key].Value : e.ObjectType;
        var label = e.ObjectLabel ?? (e.ObjectId.Length > 0 ? $"#{e.ObjectId}" : "");
        // Mockup object cells: "Talep R716555" (no colon) vs "Kullanıcı: Bourla Salehi".
        var phrase = e.ObjectType is "Ticket" or "TaskItem"
            ? $"{typeName} {label}".Trim()
            : label.Length > 0 ? $"{typeName}: {label}" : typeName;

        var fields = ChangedFields(e.Data);
        var eventText = e.Action switch
        {
            "Created" => l["al.evCreated", phrase].Value,
            "Deleted" => l["al.evDeleted", phrase].Value,
            _ => fields.Length > 0
                ? l["al.evUpdatedFields", phrase, string.Join(", ", fields)].Value
                : l["al.evUpdated", phrase].Value,
        };

        return new AuditRowVm(
            e.OccurredAt, e.ActorName, eventText, phrase, LinkFor(e.ObjectType, e.ObjectId), e.IpAddress);
    }

    /// <summary>Changed property names from the interceptor's { prop: { old, new } } diff.</summary>
    private static string[] ChangedFields(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(data);
            return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IStringLocalizer PageLocalizer(IStringLocalizerFactory factory) =>
        factory.Create("Areas.Admin.Views.AuditLogs.Index", typeof(Program).Assembly.GetName().Name!);

    private static DateTimeOffset? ParseDay(string? value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
            return null;
        var at = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
    }

    private static string? NormalizeDay(string? value) =>
        ParseDay(value) is null ? null : value;
}
