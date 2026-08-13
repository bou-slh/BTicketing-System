using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

[Collection("Postgres")]
public class ThreadServiceTests(PostgresFixture fixture)
{
    private async Task<(int TicketId, int ThreadId, ActorContext Agent, ActorContext Owner)> CreateAsync(ServiceScopeBundle s)
    {
        var agent = await TestActors.StaffAsync(s.Db, "dkaya");
        var owner = await TestActors.UserAsync(s.Db, "Burak Öztürk");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Thread testi {Guid.NewGuid():N}",
            Body = "<p>ilk mesaj</p>",
        }, owner);
        return (ticket.Id, ticket.ThreadId, agent, owner);
    }

    [Fact]
    public async Task Post_SanitizesHostileHtmlAtIngress()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (_, threadId, agent, _) = await CreateAsync(s);

        var entry = await s.Get<IThreadService>().PostAsync(threadId, ThreadEntryType.Note,
            """
            <p onclick="alert(1)">merhaba <b>dünya</b></p>
            <script>steal()</script>
            <img src="x" onerror="alert(2)">
            <a href="javascript:alert(3)">tıkla</a>
            <a href="https://rapidsol.com.tr">güvenli</a>
            """, agent);

        Assert.DoesNotContain("script", entry.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", entry.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", entry.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", entry.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<b>dünya</b>", entry.Body);
        Assert.Contains("https://rapidsol.com.tr", entry.Body);
    }

    [Fact]
    public async Task Post_MaintainsAnsweredFlagAndTimestamps()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, threadId, agent, owner) = await CreateAsync(s);
        var threads = s.Get<IThreadService>();

        await threads.PostAsync(threadId, ThreadEntryType.Response, "<p>yanıt</p>", agent);
        var afterResponse = await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.True(afterResponse.IsAnswered);

        await threads.PostAsync(threadId, ThreadEntryType.Message, "<p>yeni soru</p>", owner);
        var afterMessage = await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        Assert.False(afterMessage.IsAnswered);

        var thread = await s.Db.Threads.AsNoTracking().SingleAsync(t => t.Id == threadId);
        Assert.NotNull(thread.LastResponseAt);
        Assert.NotNull(thread.LastMessageAt);
        Assert.True(thread.LastMessageAt >= thread.LastResponseAt);
    }

    [Fact]
    public async Task Locks_ExcludeOthers_RenewByCode_ReleaseAndSteal()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, _, _, _) = await CreateAsync(s);
        var threads = s.Get<IThreadService>();
        var dkayaId = (await TestActors.StaffAsync(s.Db, "dkaya")).Id!.Value;
        var uakinId = (await TestActors.StaffAsync(s.Db, "uakin")).Id!.Value;
        var ttl = TimeSpan.FromMinutes(5);

        var mine = await threads.AcquireTicketLockAsync(ticketId, dkayaId, ttl);
        Assert.NotNull(mine);

        // Someone else can't take it; the holder can re-enter.
        Assert.Null(await threads.AcquireTicketLockAsync(ticketId, uakinId, ttl));
        Assert.NotNull(await threads.AcquireTicketLockAsync(ticketId, dkayaId, ttl));

        Assert.True(await threads.RenewLockAsync(mine!.Id, mine.Code!, ttl));
        Assert.False(await threads.RenewLockAsync(mine.Id, "yanlış-kod", ttl));

        await threads.ReleaseTicketLockAsync(ticketId, dkayaId);
        var released = await threads.AcquireTicketLockAsync(ticketId, uakinId, ttl);
        Assert.NotNull(released);

        // Expired locks are stolen.
        await threads.ReleaseTicketLockAsync(ticketId, uakinId);
        var expiring = await threads.AcquireTicketLockAsync(ticketId, uakinId, TimeSpan.FromMilliseconds(-1));
        Assert.NotNull(expiring);
        var stolen = await threads.AcquireTicketLockAsync(ticketId, dkayaId, ttl);
        Assert.NotNull(stolen);
    }

    [Fact]
    public async Task Drafts_RoundTripPerNamespace()
    {
        using var s = new ServiceScopeBundle(fixture);
        var threads = s.Get<IThreadService>();
        var staffId = (await TestActors.StaffAsync(s.Db, "kyilmaz")).Id!.Value;
        var ns = $"ticket.reply.{Guid.NewGuid():N}";

        Assert.Null(await threads.GetDraftAsync(staffId, ns));
        await threads.SaveDraftAsync(staffId, ns, "taslak 1");
        await threads.SaveDraftAsync(staffId, ns, "taslak 2"); // overwrite, single row
        var draft = await threads.GetDraftAsync(staffId, ns);
        Assert.Equal("taslak 2", draft!.Body);

        await threads.DeleteDraftAsync(staffId, ns);
        Assert.Null(await threads.GetDraftAsync(staffId, ns));
    }

    [Fact]
    public async Task Collaborators_AddOnce_WriteTimelineEvent()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (_, threadId, agent, _) = await CreateAsync(s);
        var threads = s.Get<IThreadService>();
        var ayse = await s.Db.Users.SingleAsync(u => u.Name == "Ayşe Yıldırım");

        await threads.AddCollaboratorAsync(threadId, ayse.Id, CollaboratorRole.Cc, agent);
        await threads.AddCollaboratorAsync(threadId, ayse.Id, CollaboratorRole.ThirdParty, agent); // upsert

        var collab = await s.Db.ThreadCollaborators.AsNoTracking()
            .SingleAsync(c => c.ThreadId == threadId && c.UserId == ayse.Id);
        Assert.Equal(CollaboratorRole.ThirdParty, collab.Role);

        var names = await s.Db.ThreadEvents.Where(e => e.ThreadId == threadId)
            .Join(s.Db.ThreadEventTypes, e => e.EventTypeId, t => t.Id, (e, t) => t.Name).ToListAsync();
        Assert.Contains("collab-added", names);
    }
}
