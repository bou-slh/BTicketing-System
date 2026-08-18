using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Web.Hubs;

namespace RapidsolDestek.Web.Services;

/// <summary>
/// B7 live-board plumbing (EffortEmailHandler precedent): maps the S4 domain events
/// onto LiveBoardHub broadcasts. Two client methods:
/// "BoardChanged" — column membership may have changed, clients refetch their own
/// permission-filtered state (the signal itself carries no ticket data beyond the id);
/// "Ticker" — one Canlı Akış feed line (kind + params, localized client-side).
/// Audience = the ticket's department group (QueueEngine visibility, see LiveBoardHub).
/// Runs post-SaveChanges in the mutation's scope; effort events dispatch inside the
/// proposal row-lock transaction, so this handler reads via the same scoped
/// AppDbContext (sees the pending state) and clients debounce their refetch briefly
/// to land after the commit.
/// </summary>
public sealed class LiveBoardHandler(
    AppDbContext db,
    IHubContext<LiveBoardHub> hub,
    ILogger<LiveBoardHandler> logger) :
    IDomainEventHandler<TicketCreated>,
    IDomainEventHandler<TicketAssigned>,
    IDomainEventHandler<TicketStatusChanged>,
    IDomainEventHandler<TicketTransferred>,
    IDomainEventHandler<ThreadEntryAdded>,
    IDomainEventHandler<EffortProposed>,
    IDomainEventHandler<EffortRevised>,
    IDomainEventHandler<EffortWithdrawn>,
    IDomainEventHandler<EffortApproved>,
    IDomainEventHandler<EffortRejected>,
    IDomainEventHandler<TicketOverdue>
{
    /// <summary>Card snapshot for composing broadcasts (ids + names, never entities).</summary>
    private sealed record Snapshot(int TicketId, string Number, int DepartmentId, string Org);

    public async Task HandleAsync(TicketCreated evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "created", ct);
        await TickerAsync(s, "created", evt.OccurredAt, ct: ct);
    }

    public async Task HandleAsync(TicketAssigned evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "assigned", ct);
        if (evt.StaffId is { } staffId)
        {
            var name = await db.Staff.Where(st => st.Id == staffId)
                .Select(st => st.FirstName + " " + st.LastName).SingleOrDefaultAsync(ct);
            await TickerAsync(s, "assigned", evt.OccurredAt, name: name, ct: ct);
        }
    }

    public async Task HandleAsync(TicketStatusChanged evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "status", ct);
        // Mockup feed only announces resolutions ("çözüldü olarak işaretlendi");
        // other transitions move cards silently via BoardChanged.
        var newKey = await db.TicketStatuses.Where(st => st.Id == evt.NewStatusId)
            .Select(st => st.Key).SingleOrDefaultAsync(ct);
        if (newKey == "solved")
            await TickerAsync(s, "solved", evt.OccurredAt, ct: ct);
    }

    public async Task HandleAsync(TicketTransferred evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is not { } s)
            return;
        // The old department loses the card, the new one gains it.
        await hub.Clients.Group(LiveBoardHub.DepartmentGroup(evt.OldDepartmentId))
            .SendAsync(LiveBoardHub.BoardChangedMethod, new { ticketId = s.TicketId, reason = "transferred" }, ct);
        await BoardChangedAsync(s, "transferred", ct);
    }

    public async Task HandleAsync(ThreadEntryAdded evt, CancellationToken ct = default)
    {
        // Notes never touch column state or the feed; replies flip IsAnswered and
        // can rescue an SLA-risk card, so both directions refetch.
        if (evt.TicketId is not { } ticketId || evt.Type == ThreadEntryType.Note)
            return;
        if (await LoadAsync(ticketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "replied", ct);
        if (evt.Type == ThreadEntryType.Message)
            await TickerAsync(s, "userReply", evt.OccurredAt, ct: ct);
    }

    public Task HandleAsync(EffortProposed evt, CancellationToken ct = default) =>
        EffortMovedAsync(evt.TicketId, "effortProposed", evt.Hours, evt.OccurredAt, ct);

    public Task HandleAsync(EffortRevised evt, CancellationToken ct = default) =>
        EffortMovedAsync(evt.TicketId, "effortProposed", evt.Hours, evt.OccurredAt, ct);

    public async Task HandleAsync(EffortWithdrawn evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is { } s)
            await BoardChangedAsync(s, "effort", ct);
    }

    public Task HandleAsync(EffortApproved evt, CancellationToken ct = default) =>
        EffortDecidedAsync(evt.TicketId, evt.ProposalId, "effortApproved", ct);

    public Task HandleAsync(EffortRejected evt, CancellationToken ct = default) =>
        EffortDecidedAsync(evt.TicketId, evt.ProposalId, "effortRejected", ct);

    public async Task HandleAsync(TicketOverdue evt, CancellationToken ct = default)
    {
        if (await LoadAsync(evt.TicketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "overdue", ct);
        await TickerAsync(s, "slaRisk", evt.OccurredAt, ct: ct);
    }

    private async Task EffortMovedAsync(int ticketId, string kind, decimal hours, DateTimeOffset at, CancellationToken ct)
    {
        if (await LoadAsync(ticketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "effort", ct);
        await TickerAsync(s, kind, at, hours: hours, ct: ct);
    }

    private async Task EffortDecidedAsync(int ticketId, int proposalId, string kind, CancellationToken ct)
    {
        if (await LoadAsync(ticketId, ct) is not { } s)
            return;
        await BoardChangedAsync(s, "effort", ct);
        // Decider per the mockup feed ("Bourla Salehi R716549 eforunu onayladı");
        // auto-approved proposals have no deciding user → the ticket owner stands in.
        var name = await db.EffortProposals.Where(p => p.Id == proposalId)
            .Select(p => p.DecidedByUserId != null
                ? db.Users.Where(u => u.Id == p.DecidedByUserId).Select(u => u.Name).FirstOrDefault()
                : p.Ticket!.User!.Name)
            .SingleOrDefaultAsync(ct);
        await TickerAsync(s, kind, DateTimeOffset.UtcNow, name: name, ct: ct);
    }

    private async Task<Snapshot?> LoadAsync(int ticketId, CancellationToken ct)
    {
        var snapshot = await db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => new Snapshot(
                t.Id,
                t.Number,
                t.DepartmentId,
                t.User!.Organization != null ? t.User.Organization.Name : t.User.Name))
            .SingleOrDefaultAsync(ct);
        if (snapshot is null)
            logger.LogWarning("Live board: ticket {TicketId} vanished before broadcast", ticketId);
        return snapshot;
    }

    private Task BoardChangedAsync(Snapshot s, string reason, CancellationToken ct) =>
        hub.Clients.Group(LiveBoardHub.DepartmentGroup(s.DepartmentId))
            .SendAsync(LiveBoardHub.BoardChangedMethod, new { ticketId = s.TicketId, reason }, ct);

    private Task TickerAsync(Snapshot s, string kind, DateTimeOffset at,
        string? name = null, decimal? hours = null, CancellationToken ct = default) =>
        hub.Clients.Group(LiveBoardHub.DepartmentGroup(s.DepartmentId))
            .SendAsync(LiveBoardHub.TickerMethod, new
            {
                kind,
                number = s.Number,
                org = s.Org,
                name,
                hours,
                ts = at.ToString("O"),
            }, ct);
}
