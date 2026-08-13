using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Queues;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

[Collection("Postgres")]
public class QueueEngineTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SeededQueueCriteria_FindTheCanonTickets()
    {
        using var s = new ServiceScopeBundle(fixture);
        var engine = s.Get<IQueueEngine>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");

        // "Bana Atanan" — hero R716555 is assigned to uakin.
        var mine = await (await engine.BuildAsync(QueueCriteria.Parse("""{"assignee":"me"}"""), uakin))
            .Select(t => t.Number).ToListAsync();
        Assert.Contains("R716555", mine);

        // "Gecikmiş" — R716539 is the seeded overdue emergency.
        var overdue = await (await engine.BuildAsync(QueueCriteria.Parse("""{"state":"open","isoverdue":true}"""), uakin))
            .Select(t => t.Number).ToListAsync();
        Assert.Contains("R716539", overdue);

        // "Efor Onayında" — R716555 (6h) and R716549 (3h) hold pending proposals.
        var effortPending = await (await engine.BuildAsync(QueueCriteria.Parse("""{"state":"open","effort":"pending"}"""), uakin))
            .Select(t => t.Number).ToListAsync();
        Assert.Contains("R716555", effortPending);
        Assert.Contains("R716549", effortPending);
        // R716544's latest revision is Rejected — not pending.
        Assert.DoesNotContain("R716544", effortPending);

        // Closed state never leaks into open queues.
        var open = await (await engine.BuildAsync(QueueCriteria.Parse("""{"state":"open"}"""), uakin))
            .Select(t => t.Number).ToListAsync();
        Assert.DoesNotContain("R716489", open);
    }

    [Fact]
    public async Task Visibility_ScopesToActorDepartmentsAndOwnership()
    {
        using var s = new ServiceScopeBundle(fixture);
        var engine = s.Get<IQueueEngine>();

        // mcetin sees only Danışmanlık (plus anything assigned to her personally).
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var visible = await (await engine.BuildAsync(QueueCriteria.Empty, mcetin))
            .Include(t => t.Department).ToListAsync();
        Assert.All(visible, t => Assert.True(
            t.Department!.Name == "Danışmanlık" || t.StaffId == mcetin.Id,
            $"{t.Number} leaked into mcetin's scope"));

        // Portal users see exactly their own tickets.
        var bourla = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var own = await (await engine.BuildAsync(QueueCriteria.Empty, bourla)).ToListAsync();
        Assert.NotEmpty(own);
        Assert.All(own, t => Assert.Equal(bourla.Id, t.UserId));
    }

    [Fact]
    public async Task Search_HitsSubjectAndThreadBody()
    {
        using var s = new ServiceScopeBundle(fixture);
        var engine = s.Get<IQueueEngine>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");

        // Subject term of the hero ticket.
        var bySubject = await (await engine.SearchAsync("ücreti", uakin)).Select(t => t.Number).ToListAsync();
        Assert.Contains("R716555", bySubject);

        // Body-only term (Merve's internal note mentions "parametresi").
        var byBody = await (await engine.SearchAsync("parametresi", uakin)).Select(t => t.Number).ToListAsync();
        Assert.Contains("R716555", byBody);

        var miss = await (await engine.SearchAsync("xyzzy-yok-böyle-kelime", uakin)).ToListAsync();
        Assert.Empty(miss);
    }
}

[Collection("Postgres")]
public class PermissionServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ResolvesCanonRolesPerDepartment()
    {
        using var s = new ServiceScopeBundle(fixture);
        var perms = s.Get<IPermissionService>();
        var db = s.Db;

        var uakin = await db.Staff.SingleAsync(st => st.Username == "uakin");
        var mcetin = await db.Staff.SingleAsync(st => st.Username == "mcetin");
        var dkaya = await db.Staff.SingleAsync(st => st.Username == "dkaya");
        var bordro = await db.Departments.SingleAsync(d => d.Name == "Bordro");
        var danismanlik = await db.Departments.SingleAsync(d => d.Name == "Danışmanlık");
        var destek = await db.Departments.SingleAsync(d => d.Name == "Destek");

        // Admin bypasses everything, everywhere.
        var admin = await perms.ResolveAsync(uakin.Id);
        Assert.True(admin.IsAdmin);
        Assert.True(admin.Can(PermissionKeys.TicketDelete, danismanlik.Id));

        // Kıdemli Temsilci in her own department, nothing elsewhere.
        var senior = await perms.ResolveAsync(mcetin.Id);
        Assert.True(senior.Can(PermissionKeys.TicketReply, danismanlik.Id));
        Assert.False(senior.Can(PermissionKeys.TicketDelete, danismanlik.Id)); // canon: silme dışında
        Assert.False(senior.Can(PermissionKeys.TicketReply, bordro.Id));

        // Temsilci: no assign right, but user directory yes.
        var agent = await perms.ResolveAsync(dkaya.Id);
        Assert.False(agent.Can(PermissionKeys.TicketAssign, destek.Id));
        Assert.True(agent.Can(PermissionKeys.UserDirectory, destek.Id));
        Assert.True(agent.CanAnywhere(PermissionKeys.EffortPropose));
    }
}

[Collection("Postgres")]
public class CannedResponseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListFor_FiltersByDepartmentAndEnabled()
    {
        using var s = new ServiceScopeBundle(fixture);
        var db = s.Db;
        var bordro = await db.Departments.SingleAsync(d => d.Name == "Bordro");

        var list = await s.Get<ICannedResponseService>().ListForAsync(bordro.Id);

        Assert.Contains(list, c => c.Title == "Efor onayı isteği");
        Assert.DoesNotContain(list, c => c.Title == "Bordro dönemi kapanış bilgilendirmesi"); // disabled
        Assert.DoesNotContain(list, c => c.Title == "Ek bilgi talebi"); // Destek-scoped
    }

    [Fact]
    public async Task Expand_FillsTicketAndEffortVariables()
    {
        using var s = new ServiceScopeBundle(fixture);
        var db = s.Db;
        var hero = await db.Tickets.SingleAsync(t => t.Number == "R716555");
        var canned = await db.CannedResponses.SingleAsync(c => c.Title == "Efor onayı isteği");

        var expanded = await s.Get<ICannedResponseService>().ExpandAsync(canned.Id, hero.Id);

        Assert.Contains("Bourla Salehi", expanded);
        Assert.Contains("6", expanded); // %{effort.hours}
        Assert.DoesNotContain("%{", expanded); // nothing left unexpanded
    }
}
