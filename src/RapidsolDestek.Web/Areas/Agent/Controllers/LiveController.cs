using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// Live board "Canlı İzleme" (mockups/agent/live.html, B7): server-rendered board +
/// a JSON state twin the page JS refetches on LiveBoardHub signals, plus the two
/// mutation endpoints (Üstlen claim, drag-move) that run through TicketService so
/// permission checks, audit and domain events all apply. Column membership derives
/// via LiveBoardEngine over IQueueEngine department visibility.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class LiveController(AppDbContext db, IQueueEngine queue, ITicketService tickets) : Controller
{
    [HttpGet("/agent/live")]
    [NavKey("live")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        ViewData["MainStyle"] = "max-width:1400px";
        return View(await BuildStateAsync(staff, ct));
    }

    /// <summary>Board state twin for the page JS (refetched on BoardChanged).</summary>
    [HttpGet("/agent/live/state")]
    public async Task<IActionResult> State(CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        return Json(await BuildStateAsync(staff, ct));
    }

    /// <summary>
    /// Üstlen: self-assign via the osTicket-parity claim path. A claimed "Yeni" card
    /// additionally transitions new→open so the card leaves the column (B7 "claims
    /// and moves the card") — best-effort, since claim itself needs no ticket.edit.
    /// </summary>
    [HttpPost("/agent/live/claim")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        var current = await db.Tickets.Where(t => t.Id == id)
            .Select(t => new { t.StaffId, StatusKey = t.Status!.Key })
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return NotFound(new { code = "notfound" });
        if (current.StaffId is not null)
            return Conflict(new { code = "taken" });

        try
        {
            await tickets.ClaimAsync(id, actor, ct);
        }
        catch (Exception ex) when (MapError(ex) is { } error)
        {
            return error;
        }

        if (current.StatusKey == "new")
        {
            try
            {
                var openId = await db.TicketStatuses.Where(s => s.Key == "open")
                    .Select(s => s.Id).SingleAsync(ct);
                await tickets.TransitionStatusAsync(id, openId, actor, ct);
            }
            catch (PermissionDeniedException)
            {
                // Claim succeeded; without ticket.edit the card stays in Yeni with
                // its new assignee — honest state, no rollback.
            }
        }

        return Json(new { ok = true });
    }

    /// <summary>
    /// Drag-drop between columns = real mutation: Yeni/Yanıt Bekleyen are status
    /// transitions, Atanmamış is a release. SLA Riskli and Efor Onayında are
    /// derived-membership columns with no equivalent mutation — dropping there is
    /// refused ("derived") and the client snaps the card back with a toast.
    /// </summary>
    [HttpPost("/agent/live/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(int id, string col, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            switch (col)
            {
                case "new":
                case "waiting":
                    var key = col == "new" ? "new" : "wait";
                    var statusId = await db.TicketStatuses.Where(s => s.Key == key)
                        .Select(s => s.Id).SingleAsync(ct);
                    await tickets.TransitionStatusAsync(id, statusId, actor, ct);
                    break;
                case "unassigned":
                    await tickets.AssignAsync(id, null, null, actor, ct: ct);
                    break;
                default:
                    return BadRequest(new { code = "derived" });
            }
        }
        catch (Exception ex) when (MapError(ex) is { } error)
        {
            return error;
        }

        return Json(new { ok = true });
    }

    /// <summary>Service exceptions → stable toast codes (DomainRuleException.Code precedent).</summary>
    private IActionResult? MapError(Exception ex) => ex switch
    {
        PermissionDeniedException => StatusCode(StatusCodes.Status403Forbidden, new { code = "denied" }),
        DomainRuleException { Code: StaffAvailability.OnVacationRuleCode } => Conflict(new { code = "vacation" }),
        WorkBlockedByEffortException => Conflict(new { code = "blocked" }),
        DomainNotFoundException => NotFound(new { code = "notfound" }),
        _ => null,
    };

    // ----- State assembly -----------------------------------------------------

    private async Task<LiveBoardState> BuildStateAsync(Staff staff, CancellationToken ct)
    {
        var actor = ActorContext.ForStaff(staff);
        var visible = await queue.BuildAsync(QueueCriteria.Empty, actor, ct);
        var now = DateTimeOffset.UtcNow;

        var newCards = await Project(LiveBoardEngine.New(visible)
            .OrderByDescending(t => t.CreatedAt)).ToListAsync(ct);
        var unassigned = await Project(LiveBoardEngine.Unassigned(visible)
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)).ToListAsync(ct);
        var waiting = await Project(LiveBoardEngine.Waiting(visible)
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)).ToListAsync(ct);
        var slaRisk = await Project(LiveBoardEngine.SlaRisk(visible, now)
            .OrderByDescending(t => t.IsOverdue).ThenBy(t => t.DueDate ?? t.EstimatedDueDate)).ToListAsync(ct);
        var effort = await Project(LiveBoardEngine.EffortPending(visible)
            .OrderByDescending(t => t.LastUpdateAt ?? t.CreatedAt)).ToListAsync(ct);

        return new LiveBoardState(
            newCards, unassigned, waiting, slaRisk, effort,
            await BuildFeedAsync(visible, ct),
            await BuildTeamAsync(ct));
    }

    private static IQueryable<LiveCardVm> Project(IQueryable<Ticket> query) =>
        query.Select(t => new LiveCardVm(
            t.Id,
            t.Number,
            t.Subject,
            t.User!.Organization != null ? t.User.Organization.Name : t.User.Name,
            t.HelpTopic != null ? t.HelpTopic.Name : t.Department!.Name,
            t.Staff != null ? t.Staff.FirstName + " " + t.Staff.LastName : null,
            t.CreatedAt,
            t.DueDate ?? t.EstimatedDueDate,
            t.IsOverdue,
            t.EffortProposals.OrderByDescending(p => p.RevisionNo).Take(1)
                .Where(p => p.State == EffortState.Pending)
                .Select(p => (decimal?)p.Hours).FirstOrDefault()));

    /// <summary>
    /// Canlı Akış backfill: the most recent feed-worthy moments reconstructed from
    /// persisted thread events + customer messages (domain events are not stored).
    /// Ascending order — the ticker renders column-reverse, newest on top.
    /// </summary>
    private async Task<IReadOnlyList<LiveFeedVm>> BuildFeedAsync(IQueryable<Ticket> visible, CancellationToken ct)
    {
        string[] kinds = ["created", "assigned", "closed", "effort-proposed", "effort-approved", "effort-rejected"];
        var events = await db.ThreadEvents
            .Where(e => !e.Annulled && kinds.Contains(e.EventType!.Name))
            .Join(visible, e => e.ThreadId, t => t.ThreadId, (e, t) => new
            {
                e.OccurredAt,
                Kind = e.EventType!.Name,
                e.Data,
                e.Username,
                t.Number,
                Org = t.User!.Organization != null ? t.User.Organization.Name : t.User.Name,
            })
            .OrderByDescending(x => x.OccurredAt)
            .Take(12)
            .ToListAsync(ct);

        var replies = await db.ThreadEntries
            .Where(e => e.Type == ThreadEntryType.Message && e.UserId != null)
            .Join(visible, e => e.ThreadId, t => t.ThreadId, (e, t) => new
            {
                e.CreatedAt,
                TicketCreatedAt = t.CreatedAt,
                t.Number,
                Org = t.User!.Organization != null ? t.User.Organization.Name : t.User.Name,
            })
            .OrderByDescending(x => x.CreatedAt)
            .Take(12)
            .ToListAsync(ct);

        var staffNames = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.FullName, ct);

        var items = new List<LiveFeedVm>();
        foreach (var e in events)
        {
            var data = ParseData(e.Data);
            switch (e.Kind)
            {
                case "created":
                    items.Add(new LiveFeedVm("created", e.Number, e.Org, null, null, e.OccurredAt));
                    break;
                case "assigned":
                    // Assignee from the event payload ({"staffId":N} via ThreadService,
                    // {"staff":"Ad Soyad"} in seed data); a release carries neither.
                    var name = data.TryGetValue("staffId", out var sid) && sid.ValueKind == JsonValueKind.Number
                            && staffNames.TryGetValue(sid.GetInt32(), out var resolved)
                        ? resolved
                        : data.TryGetValue("staff", out var sname) && sname.ValueKind == JsonValueKind.String
                            ? sname.GetString()
                            : null;
                    if (name is not null)
                        items.Add(new LiveFeedVm("assigned", e.Number, e.Org, name, null, e.OccurredAt));
                    break;
                case "closed" when data.TryGetValue("status", out var st) && st.GetString() == "solved":
                    items.Add(new LiveFeedVm("solved", e.Number, e.Org, null, null, e.OccurredAt));
                    break;
                case "effort-proposed" when data.TryGetValue("hours", out var h) && h.ValueKind == JsonValueKind.Number:
                    items.Add(new LiveFeedVm("effortProposed", e.Number, e.Org, null, h.GetDecimal(), e.OccurredAt));
                    break;
                case "effort-approved":
                    items.Add(new LiveFeedVm("effortApproved", e.Number, e.Org, e.Username, null, e.OccurredAt));
                    break;
                case "effort-rejected":
                    items.Add(new LiveFeedVm("effortRejected", e.Number, e.Org, e.Username, null, e.OccurredAt));
                    break;
            }
        }

        // Customer replies; the ticket's opening message is "created", not a reply.
        items.AddRange(replies
            .Where(r => r.CreatedAt > r.TicketCreatedAt.AddMinutes(1))
            .Select(r => new LiveFeedVm("userReply", r.Number, r.Org, null, null, r.CreatedAt)));

        return [.. items.OrderByDescending(i => i.Ts).Take(6).OrderBy(i => i.Ts)];
    }

    /// <summary>
    /// Ekip Durumu: the active visible roster with live open-ticket counts.
    /// "away" = vacation mode (StaffAvailability's Tatil Modu) — the mockup defines
    /// no presence source, flagged on the ROADMAP row.
    /// </summary>
    private async Task<IReadOnlyList<LiveTeamVm>> BuildTeamAsync(CancellationToken ct)
    {
        var rows = await db.Staff
            .Where(s => s.IsActive && s.IsVisible)
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName)
            .Select(s => new
            {
                s.FirstName,
                s.LastName,
                s.OnVacation,
                OpenCount = db.Tickets.Count(t => t.StaffId == s.Id && t.Status!.State == TicketState.Open),
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => new LiveTeamVm(
            $"{r.FirstName} {r.LastName}",
            $"{r.FirstName[..1]}{r.LastName[..1]}".ToUpperInvariant(),
            r.OnVacation,
            r.OpenCount))];
    }

    private static Dictionary<string, JsonElement> ParseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record LiveBoardState(
    IReadOnlyList<LiveCardVm> New,
    IReadOnlyList<LiveCardVm> Unassigned,
    IReadOnlyList<LiveCardVm> Waiting,
    IReadOnlyList<LiveCardVm> SlaRisk,
    IReadOnlyList<LiveCardVm> Effort,
    IReadOnlyList<LiveFeedVm> Feed,
    IReadOnlyList<LiveTeamVm> Team);

public sealed record LiveCardVm(
    int Id,
    string Number,
    string Subject,
    string Org,
    string Topic,
    string? Assignee,
    DateTimeOffset Created,
    DateTimeOffset? Due,
    bool Overdue,
    decimal? EffortHours);

public sealed record LiveFeedVm(
    string Kind,
    string Number,
    string Org,
    string? Name,
    decimal? Hours,
    DateTimeOffset Ts);

public sealed record LiveTeamVm(
    string Name,
    string Initials,
    bool Away,
    int OpenCount);
