using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
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

    // ---- S6 agent/canned.html CRUD (create / edit / disable / delete) ----------------

    private static string UniqueTitle() => $"Test Yanıtı {Guid.NewGuid():N}";

    [Fact]
    public async Task Create_NormalizesPlainTextBody_AndPersists()
    {
        var title = UniqueTitle();
        int cannedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            // mcetin = Kıdemli Temsilci: holds canned.manage.
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var bordro = await s.Db.Departments.SingleAsync(d => d.Name == "Bordro");
            var canned = await s.Get<ICannedResponseService>().CreateAsync(new CannedUpsertRequest
            {
                Title = $"  {title}  ",
                DepartmentId = bordro.Id,
                Response = "Merhaba %{ticket.user.name},\n\nTalebiniz işlemde.",
            }, mcetin);
            cannedId = canned.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.CannedResponses.SingleAsync(c => c.Id == cannedId);
        Assert.Equal(title, saved.Title);
        Assert.True(saved.IsEnabled);
        // Plain textarea input is paragraph-wrapped to stored HTML; %{variables} survive.
        Assert.Equal("<p>Merhaba %{ticket.user.name},</p><p>Talebiniz işlemde.</p>", saved.Response);
    }

    [Fact]
    public async Task Create_DuplicateTitleRefused_AndPermissionGated()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var svc = s.Get<ICannedResponseService>();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => svc.CreateAsync(new CannedUpsertRequest
        {
            Title = "Ek Bilgi Talebi", // seeded canon title "Ek bilgi talebi", case-insensitive
            Response = "x",
        }, mcetin));
        Assert.Equal("title-in-use", ex.Code);

        // dkaya = Temsilci: no canned.manage anywhere.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => svc.CreateAsync(new CannedUpsertRequest
        {
            Title = UniqueTitle(),
            Response = "x",
        }, dkaya));
    }

    [Fact]
    public async Task Update_SavesEditDialogFields_AcrossScopes()
    {
        int cannedId, destekId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            destekId = (await s.Db.Departments.SingleAsync(d => d.Name == "Destek")).Id;
            var canned = await s.Get<ICannedResponseService>().CreateAsync(new CannedUpsertRequest
            {
                Title = UniqueTitle(),
                Response = "Eski içerik.",
            }, mcetin);
            cannedId = canned.Id;
        }

        var newTitle = UniqueTitle();
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            await s.Get<ICannedResponseService>().UpdateAsync(cannedId, new CannedUpsertRequest
            {
                Title = newTitle,
                DepartmentId = destekId,
                Response = "<p>Yeni içerik.</p>",
                IsEnabled = false,
            }, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.CannedResponses.SingleAsync(c => c.Id == cannedId);
        Assert.Equal(newTitle, saved.Title);
        Assert.Equal(destekId, saved.DepartmentId);
        Assert.Equal("<p>Yeni içerik.</p>", saved.Response);
        Assert.False(saved.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_False_DropsOutOfComposerList_ThenReEnables()
    {
        int cannedId, bordroId;
        string title = UniqueTitle();
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            bordroId = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
            var canned = await s.Get<ICannedResponseService>().CreateAsync(new CannedUpsertRequest
            {
                Title = title,
                DepartmentId = bordroId,
                Response = "x",
            }, mcetin);
            cannedId = canned.Id;

            await s.Get<ICannedResponseService>().SetEnabledAsync(cannedId, false, mcetin);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            // Disabled → gone from the ticket-view/ticket-open composer select source.
            var list = await fresh.Get<ICannedResponseService>().ListForAsync(bordroId);
            Assert.DoesNotContain(list, c => c.Id == cannedId);

            var mcetin = await TestActors.StaffAsync(fresh.Db, "mcetin");
            await fresh.Get<ICannedResponseService>().SetEnabledAsync(cannedId, true, mcetin);
        }

        using var last = new ServiceScopeBundle(fixture);
        var relisted = await last.Get<ICannedResponseService>().ListForAsync(bordroId);
        Assert.Contains(relisted, c => c.Id == cannedId);
    }

    [Fact]
    public async Task Delete_HardDeletes_AndIsPermissionGated()
    {
        int cannedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var canned = await s.Get<ICannedResponseService>().CreateAsync(new CannedUpsertRequest
            {
                Title = UniqueTitle(),
                Response = "x",
            }, mcetin);
            cannedId = canned.Id;

            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<ICannedResponseService>().DeleteAsync(cannedId, dkaya));

            await s.Get<ICannedResponseService>().DeleteAsync(cannedId, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.False(await fresh.Db.CannedResponses.AnyAsync(c => c.Id == cannedId));
    }
}
