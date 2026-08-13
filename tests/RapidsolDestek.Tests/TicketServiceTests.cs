using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

[Collection("Postgres")]
public class TicketServiceTests(PostgresFixture fixture)
{
    private static async Task<int> UserIdAsync(AppDbContext db, string name) =>
        await db.Users.Where(u => u.Name == name).Select(u => u.Id).SingleAsync();

    [Fact]
    public async Task Create_ViaHelpTopic_AppliesTheFullRoutingCascade()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var topic = await s.Db.HelpTopics.SingleAsync(t => t.Name == "Yol Ücreti");

        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Cascade testi {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            HelpTopicId = topic.Id,
        }, owner);

        // Topic canon: dept Bordro, priority high, SLA VIP, auto-assign uakin, BRD-###### numbers.
        Assert.Equal(topic.DepartmentId, ticket.DepartmentId);
        Assert.Equal(topic.PriorityId, ticket.PriorityId);
        Assert.Equal(topic.SlaId, ticket.SlaId);
        Assert.Equal(topic.StaffId, ticket.StaffId);
        Assert.StartsWith("BRD-", ticket.Number);

        var threadEventNames = await s.Db.ThreadEvents.Where(e => e.ThreadId == ticket.ThreadId)
            .Join(s.Db.ThreadEventTypes, e => e.EventTypeId, t => t.Id, (e, t) => t.Name).ToListAsync();
        Assert.Contains("created", threadEventNames);
        Assert.Contains("assigned", threadEventNames);
    }

    [Fact]
    public async Task Create_WithoutTopic_UsesSettingsDefaults()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Can Koç");

        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Varsayılan rota {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
        }, owner);

        var defaultDept = await s.Db.Departments.SingleAsync(d => d.Name == "Destek");
        Assert.Equal(defaultDept.Id, ticket.DepartmentId);
        Assert.Matches("^R[0-9]{6,}$", ticket.Number);
        Assert.Equal("open", (await s.Db.TicketStatuses.FindAsync(ticket.StatusId))!.Key);

        // First message became the thread's opening entry, sanitized html.
        var entry = await s.Db.ThreadEntries.SingleAsync(e => e.ThreadId == ticket.ThreadId);
        Assert.Equal(ThreadEntryType.Message, entry.Type);
    }

    [Fact]
    public async Task Numbering_IsUniqueUnderConcurrency()
    {
        var numbers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            using var scope = fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await TestActors.UserAsync(db, "Bourla Salehi");
            var ticket = await scope.ServiceProvider.GetRequiredService<ITicketService>()
                .CreateAsync(new TicketCreateRequest
                {
                    UserId = owner.Id!.Value,
                    Subject = $"Eşzamanlılık {i} {Guid.NewGuid():N}",
                    Body = "<p>x</p>",
                }, owner);
            return ticket.Number;
        }));

        Assert.Equal(numbers.Length, numbers.Distinct().Count());
    }

    [Fact]
    public async Task Transition_RequiresClosePermissionInTheDepartment()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Kapatma yetkisi {Guid.NewGuid():N}",
            Body = "<p>x</p>",
        }, owner); // routes to Destek

        var solvedId = await s.Db.TicketStatuses.Where(st => st.Key == "solved").Select(st => st.Id).SingleAsync();

        // mcetin's only department is Danışmanlık → denied on a Destek ticket.
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        await Assert.ThrowsAsync<PermissionDeniedException>(
            () => s.Get<ITicketService>().TransitionStatusAsync(ticket.Id, solvedId, mcetin));

        // dkaya (Temsilci, Destek) holds ticket.close → allowed; ClosedAt stamped.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        await s.Get<ITicketService>().TransitionStatusAsync(ticket.Id, solvedId, dkaya);
        var closed = await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id);
        Assert.NotNull(closed.ClosedAt);

        // Reopen clears ClosedAt and stamps ReopenedAt.
        var openId = await s.Db.TicketStatuses.Where(st => st.Key == "open").Select(st => st.Id).SingleAsync();
        await s.Get<ITicketService>().TransitionStatusAsync(ticket.Id, openId, dkaya);
        var reopened = await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id);
        Assert.Null(reopened.ClosedAt);
        Assert.NotNull(reopened.ReopenedAt);
    }

    [Fact]
    public async Task Claim_SelfAssignsUnassignedVisibleTickets_Once()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Elif Şahin");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Üstlen testi {Guid.NewGuid():N}",
            Body = "<p>x</p>",
        }, owner); // Destek, unassigned

        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        await s.Get<ITicketService>().ClaimAsync(ticket.Id, dkaya);
        Assert.Equal(dkaya.Id, (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id)).StaffId);

        // Already assigned → claim denied (even for another agent).
        var kyilmaz = await TestActors.StaffAsync(s.Db, "kyilmaz");
        await Assert.ThrowsAsync<PermissionDeniedException>(
            () => s.Get<ITicketService>().ClaimAsync(ticket.Id, kyilmaz));
    }

    [Fact]
    public async Task Transfer_MovesDepartmentAndWritesEvent()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Aktarım testi {Guid.NewGuid():N}",
            Body = "<p>x</p>",
        }, owner);

        var bordro = await s.Db.Departments.SingleAsync(d => d.Name == "Bordro");
        var uakin = await TestActors.StaffAsync(s.Db, "uakin"); // admin bypass
        await s.Get<ITicketService>().TransferAsync(ticket.Id, bordro.Id, uakin);

        Assert.Equal(bordro.Id, (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id)).DepartmentId);
        var names = await s.Db.ThreadEvents.Where(e => e.ThreadId == ticket.ThreadId)
            .Join(s.Db.ThreadEventTypes, e => e.EventTypeId, t => t.Id, (e, t) => t.Name).ToListAsync();
        Assert.Contains("transferred", names);
    }
}
