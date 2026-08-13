using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

public interface IQueueEngine
{
    /// <summary>
    /// Composes a queue's criteria into a filtered ticket query, always intersected
    /// with the actor's department visibility (and AssignedOnly narrowing).
    /// </summary>
    Task<IQueryable<Ticket>> BuildAsync(QueueCriteria criteria, ActorContext actor, CancellationToken ct = default);

    Task<QueueCriteria> LoadAsync(int savedQueueId, CancellationToken ct = default);

    /// <summary>Topbar full-text search over ticket number/subject and thread bodies (tsvector).</summary>
    Task<IQueryable<Ticket>> SearchAsync(string query, ActorContext actor, CancellationToken ct = default);
}

/// <summary>
/// osTicket's queue engine replaced with LINQ composition over the flat criteria JSON
/// (v1 schema seeded on the queue tree; the S7 builder emits the same shape). Unknown
/// criteria keys are logged and ignored — forward compatible, never wrong results
/// silently: a queue that uses unsupported keys still lists, just wider.
/// </summary>
public sealed class QueueEngine(
    AppDbContext db,
    IPermissionService permissions,
    ILogger<QueueEngine> logger) : IQueueEngine
{
    public async Task<IQueryable<Ticket>> BuildAsync(QueueCriteria criteria, ActorContext actor, CancellationToken ct = default)
    {
        var query = await VisibleTicketsAsync(actor, ct);

        if (criteria.UnknownKeys.Count > 0)
            logger.LogWarning("Queue criteria contains unsupported keys: {Keys}", string.Join(", ", criteria.UnknownKeys));

        if (criteria.State is { } state)
        {
            var parsed = Enum.Parse<TicketState>(state, ignoreCase: true);
            query = query.Where(t => t.Status!.State == parsed);
        }

        if (criteria.Status is { } statusKey)
            query = query.Where(t => t.Status!.Key == statusKey);

        if (criteria.IsAnswered is { } answered)
            query = query.Where(t => t.IsAnswered == answered);

        if (criteria.IsOverdue is { } overdue)
            query = query.Where(t => t.IsOverdue == overdue);

        if (criteria.Effort == "pending")
        {
            query = query.Where(t => t.EffortProposals
                .OrderByDescending(p => p.RevisionNo)
                .Take(1)
                .Any(p => p.State == EffortState.Pending));
        }

        query = criteria.Assignee switch
        {
            "me" when actor.IsStaff => query.Where(t => t.StaffId == actor.Id),
            "my-teams" when actor.IsStaff => query.Where(t =>
                t.TeamId != null && db.Teams.Any(team =>
                    team.Id == t.TeamId && team.Members.Any(m => m.StaffId == actor.Id))),
            "none" => query.Where(t => t.StaffId == null && t.TeamId == null),
            _ when criteria.AssigneeStaffId is { } staffId => query.Where(t => t.StaffId == staffId),
            _ => query,
        };

        if (criteria.DepartmentId is { } dept)
            query = query.Where(t => t.DepartmentId == dept);

        if (criteria.HelpTopicId is { } topic)
            query = query.Where(t => t.HelpTopicId == topic);

        if (criteria.Priority is { } priority)
            query = query.Where(t => t.Priority!.Key == priority);

        if (criteria.Closed is { } window)
        {
            var since = window switch
            {
                "today" => DateTimeOffset.UtcNow.Date,
                "week" => DateTimeOffset.UtcNow.Date.AddDays(-7),
                "month" => DateTimeOffset.UtcNow.Date.AddMonths(-1),
                _ => DateTime.MinValue,
            };
            if (since != DateTime.MinValue)
                query = query.Where(t => t.ClosedAt != null && t.ClosedAt >= new DateTimeOffset(since, TimeSpan.Zero));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
            query = ApplySearch(query, criteria.Search);

        return query;
    }

    public async Task<QueueCriteria> LoadAsync(int savedQueueId, CancellationToken ct = default)
    {
        var json = await db.SavedQueues.Where(q => q.Id == savedQueueId)
            .Select(q => q.Criteria).SingleOrDefaultAsync(ct)
            ?? throw new DomainNotFoundException("SavedQueue", savedQueueId);
        return QueueCriteria.Parse(json);
    }

    public async Task<IQueryable<Ticket>> SearchAsync(string query, ActorContext actor, CancellationToken ct = default)
    {
        var tickets = await VisibleTicketsAsync(actor, ct);
        return ApplySearch(tickets, query);
    }

    private IQueryable<Ticket> ApplySearch(IQueryable<Ticket> tickets, string text)
    {
        return tickets.Where(t =>
            EF.Property<NpgsqlTsVector>(t, "SearchVector").Matches(EF.Functions.PlainToTsQuery("simple", text))
            || t.Thread!.Entries.Any(e =>
                EF.Property<NpgsqlTsVector>(e, "SearchVector").Matches(EF.Functions.PlainToTsQuery("simple", text))));
    }

    /// <summary>Base scope: staff see their departments (admin: all); portal users their own tickets.</summary>
    private async Task<IQueryable<Ticket>> VisibleTicketsAsync(ActorContext actor, CancellationToken ct)
    {
        IQueryable<Ticket> query = db.Tickets;

        if (actor.IsUser)
            return query.Where(t => t.UserId == actor.Id);

        if (actor.IsStaff)
        {
            var set = await permissions.ResolveAsync(actor.Id!.Value, ct);
            if (!set.IsAdmin)
            {
                var depts = set.DepartmentIds.ToArray();
                query = query.Where(t => depts.Contains(t.DepartmentId) || t.StaffId == actor.Id);
            }
            if (set.AssignedOnly)
                query = query.Where(t => t.StaffId == actor.Id);
        }

        return query;
    }
}
