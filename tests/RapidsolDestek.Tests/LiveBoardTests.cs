using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Agent.Controllers;
using RapidsolDestek.Web.Hubs;
using RapidsolDestek.Web.Services;
using System.Text.Json;

namespace RapidsolDestek.Tests;

/// <summary>
/// B7 live board: column-membership derivation (LiveBoardEngine over QueueEngine
/// visibility) and the domain-event → LiveBoardHub broadcast handler, asserted over
/// the seeded canon (mockups/agent/live.html columns).
/// </summary>
[Collection("Postgres")]
public class LiveBoardColumnTests(PostgresFixture fixture)
{
    private static async Task<IQueryable<Ticket>> VisibleAsync(ServiceScopeBundle s, string username)
    {
        var actor = await TestActors.StaffAsync(s.Db, username);
        return await s.Get<IQueueEngine>().BuildAsync(QueueCriteria.Empty, actor);
    }

    [Fact]
    public async Task NewColumn_IsTheNewStatusRow()
    {
        using var s = new ServiceScopeBundle(fixture);
        var numbers = await LiveBoardEngine.New(await VisibleAsync(s, "uakin"))
            .Select(t => t.Number).ToListAsync();

        // Seeded stNew tickets (live.html Yeni column family).
        Assert.Contains("R716552", numbers);
        Assert.Contains("R716562", numbers);
        Assert.DoesNotContain("R716555", numbers); // hero is "open"
        Assert.DoesNotContain("R716536", numbers); // open+unassigned belongs to Atanmamış
    }

    [Fact]
    public async Task UnassignedColumn_IsOpenStateUnassigned_MinusTheNewColumn()
    {
        using var s = new ServiceScopeBundle(fixture);
        var numbers = await LiveBoardEngine.Unassigned(await VisibleAsync(s, "uakin"))
            .Select(t => t.Number).ToListAsync();

        Assert.Contains("R716536", numbers); // open, no staff, no team
        Assert.Contains("R716592", numbers);
        Assert.DoesNotContain("R716552", numbers); // "new" has its own column
        Assert.DoesNotContain("R716555", numbers); // assigned to uakin
        Assert.DoesNotContain("R716532", numbers); // solved = closed state
    }

    [Fact]
    public async Task WaitingColumn_IsTheWaitStatusRow()
    {
        using var s = new ServiceScopeBundle(fixture);
        var numbers = await LiveBoardEngine.Waiting(await VisibleAsync(s, "uakin"))
            .Select(t => t.Number).ToListAsync();

        Assert.Contains("R716560", numbers);
        Assert.Contains("R716544", numbers);
        Assert.DoesNotContain("R716555", numbers);
    }

    [Fact]
    public async Task SlaRiskColumn_TakesTheOverdueFlag()
    {
        using var s = new ServiceScopeBundle(fixture);
        var numbers = await LiveBoardEngine.SlaRisk(await VisibleAsync(s, "uakin"), DateTimeOffset.UtcNow)
            .Select(t => t.Number).ToListAsync();

        Assert.Contains("R716539", numbers); // seeded IsOverdue
        Assert.DoesNotContain("R716561", numbers); // no due instant, no flag
    }

    [Fact]
    public async Task EffortColumn_IsTheActivePendingProposal()
    {
        using var s = new ServiceScopeBundle(fixture);
        var numbers = await LiveBoardEngine.EffortPending(await VisibleAsync(s, "uakin"))
            .Select(t => t.Number).ToListAsync();

        Assert.Contains("R716555", numbers); // hero pending 6h
        Assert.Contains("R716549", numbers); // pending 3h
        Assert.DoesNotContain("R716544", numbers); // latest revision rejected
        Assert.DoesNotContain("R716532", numbers); // approved + solved
    }

    [Fact]
    public async Task Columns_RespectDepartmentVisibility()
    {
        using var s = new ServiceScopeBundle(fixture);
        // mcetin's only department is Danışmanlık (plus tickets assigned to her).
        var numbers = await LiveBoardEngine.New(await VisibleAsync(s, "mcetin"))
            .Select(t => t.Number).ToListAsync();

        Assert.Contains("R716563", numbers); // Danışmanlık
        Assert.DoesNotContain("R716552", numbers); // Bordro — invisible to mcetin
    }
}

[Collection("Postgres")]
public class LiveBoardHandlerTests(PostgresFixture fixture)
{
    /// <summary>Captures group broadcasts; everything else is out of contract.</summary>
    private sealed class CapturingHub : IHubContext<LiveBoardHub>
    {
        public List<(string Group, string Method, string PayloadJson)> Sent { get; } = [];

        public IHubClients Clients => new GroupOnlyClients(this);
        public IGroupManager Groups { get; } = new NoopGroups();

        private sealed class GroupOnlyClients(CapturingHub owner) : IHubClients
        {
            public IClientProxy Group(string groupName) => new Proxy(owner, groupName);
            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Client(string connectionId) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class Proxy(CapturingHub owner, string group) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                owner.Sent.Add((group, method, JsonSerializer.Serialize(args[0])));
                return Task.CompletedTask;
            }
        }

        private sealed class NoopGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    private sealed record HandlerBundle(ServiceScopeBundle Scope, LiveBoardHandler Handler, CapturingHub Hub) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }

    private HandlerBundle Create()
    {
        var scope = new ServiceScopeBundle(fixture);
        var hub = new CapturingHub();
        return new HandlerBundle(scope,
            new LiveBoardHandler(scope.Db, hub, NullLogger<LiveBoardHandler>.Instance), hub);
    }

    private static async Task<(int Id, int DeptId)> TicketAsync(ServiceScopeBundle s, string number)
    {
        var row = await s.Db.Tickets.Where(t => t.Number == number)
            .Select(t => new { t.Id, t.DepartmentId }).SingleAsync();
        return (row.Id, row.DepartmentId);
    }

    [Fact]
    public async Task TicketCreated_BroadcastsBoardChangeAndTicker_ToTheDepartmentGroup()
    {
        using var b = Create();
        var (id, deptId) = await TicketAsync(b.Scope, "R716555");

        await b.Handler.HandleAsync(new TicketCreated(id, "R716555", 0, deptId));

        var group = LiveBoardHub.DepartmentGroup(deptId);
        var changed = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.BoardChangedMethod);
        Assert.Equal(group, changed.Group);
        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        Assert.Equal(group, ticker.Group);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("created", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("R716555", doc.RootElement.GetProperty("number").GetString());
        Assert.Equal("Ulaşım A.Ş.", doc.RootElement.GetProperty("org").GetString());
    }

    [Fact]
    public async Task TicketAssigned_TickerCarriesTheAssigneeName()
    {
        using var b = Create();
        var (id, _) = await TicketAsync(b.Scope, "R716555");
        var dkaya = await b.Scope.Db.Staff.SingleAsync(s => s.Username == "dkaya");

        await b.Handler.HandleAsync(new TicketAssigned(id, dkaya.Id, null, "Deniz Kaya"));

        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("assigned", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Deniz Kaya", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task TicketReleased_SignalsBoardChangeWithoutTicker()
    {
        using var b = Create();
        var (id, _) = await TicketAsync(b.Scope, "R716555");

        await b.Handler.HandleAsync(new TicketAssigned(id, null, null, "Deniz Kaya"));

        Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.BoardChangedMethod);
        Assert.DoesNotContain(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
    }

    [Fact]
    public async Task StatusChanged_TickersOnlyResolutions()
    {
        using var b = Create();
        var (id, _) = await TicketAsync(b.Scope, "R716555");
        var solvedId = await b.Scope.Db.TicketStatuses.Where(s => s.Key == "solved").Select(s => s.Id).SingleAsync();
        var waitId = await b.Scope.Db.TicketStatuses.Where(s => s.Key == "wait").Select(s => s.Id).SingleAsync();

        await b.Handler.HandleAsync(new TicketStatusChanged(id, 0, waitId, "x"));
        Assert.DoesNotContain(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.BoardChangedMethod);

        await b.Handler.HandleAsync(new TicketStatusChanged(id, 0, solvedId, "x"));
        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("solved", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ThreadEntry_CustomerMessageTickers_NoteStaysSilent()
    {
        using var b = Create();
        var ticket = await b.Scope.Db.Tickets.SingleAsync(t => t.Number == "R716555");

        await b.Handler.HandleAsync(new ThreadEntryAdded(ticket.ThreadId, 0, ThreadEntryType.Note, ticket.Id));
        Assert.Empty(b.Hub.Sent);

        await b.Handler.HandleAsync(new ThreadEntryAdded(ticket.ThreadId, 0, ThreadEntryType.Message, ticket.Id));
        Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.BoardChangedMethod);
        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("userReply", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Transferred_SignalsBothDepartments()
    {
        using var b = Create();
        var (id, deptId) = await TicketAsync(b.Scope, "R716555");
        var otherDept = await b.Scope.Db.Departments.Where(d => d.Id != deptId).Select(d => d.Id).FirstAsync();

        await b.Handler.HandleAsync(new TicketTransferred(id, otherDept, deptId));

        var groups = b.Hub.Sent.Where(x => x.Method == LiveBoardHub.BoardChangedMethod)
            .Select(x => x.Group).ToList();
        Assert.Contains(LiveBoardHub.DepartmentGroup(otherDept), groups);
        Assert.Contains(LiveBoardHub.DepartmentGroup(deptId), groups);
    }

    [Fact]
    public async Task EffortProposed_TickerCarriesHours()
    {
        using var b = Create();
        var (id, _) = await TicketAsync(b.Scope, "R716555");

        await b.Handler.HandleAsync(new EffortProposed(id, 0, 1, 6m));

        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("effortProposed", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(6m, doc.RootElement.GetProperty("hours").GetDecimal());
    }

    [Fact]
    public async Task EffortApproved_TickerNamesTheDecider()
    {
        using var b = Create();
        // Seeded approved proposal on R716532, decided by Mehmet Demir.
        var proposal = await b.Scope.Db.EffortProposals
            .SingleAsync(p => p.Ticket!.Number == "R716532" && p.State == EffortState.Approved);

        await b.Handler.HandleAsync(new EffortApproved(proposal.TicketId, proposal.Id, proposal.RevisionNo, false));

        var ticker = Assert.Single(b.Hub.Sent, x => x.Method == LiveBoardHub.TickerMethod);
        using var doc = JsonDocument.Parse(ticker.PayloadJson);
        Assert.Equal("effortApproved", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Mehmet Demir", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task VanishedTicket_IsLoggedNotThrown()
    {
        using var b = Create();
        await b.Handler.HandleAsync(new TicketCreated(int.MaxValue, "R000000", 0, 1));
        Assert.Empty(b.Hub.Sent);
    }

    [Fact]
    public void Handlers_AreRegisteredForAllBoardEvents()
    {
        using var scope = fixture.CreateScope();
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<TicketCreated>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<TicketAssigned>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<TicketStatusChanged>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<ThreadEntryAdded>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<EffortProposed>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<EffortApproved>>(),
            h => h is LiveBoardHandler);
        Assert.Contains(scope.ServiceProvider.GetServices<IDomainEventHandler<TicketOverdue>>(),
            h => h is LiveBoardHandler);
    }
}
