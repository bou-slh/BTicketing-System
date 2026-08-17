using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 OrgService (agent/orgs.html + org-view.html): create with the duplicate-name
/// guard and domain normalization, the persisted sync flags, notes, and the delete
/// guards (members / member tickets). Assertions read through fresh scopes
/// (EF identity-map staleness across scopes).
/// </summary>
[Collection("Postgres")]
public class OrgServiceTests(PostgresFixture fixture)
{
    private static string UniqueName(string prefix = "Test Şirketi") =>
        $"{prefix} {Guid.NewGuid():N}";

    [Fact]
    public async Task Create_NormalizesDomains_AndPersists()
    {
        var name = UniqueName();
        int orgId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            // mcetin = Kıdemli Temsilci: holds org.edit.
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var org = await s.Get<IOrgService>().CreateAsync(new OrgCreateRequest
            {
                Name = name,
                Domain = "@testsirketi.com.tr, testsirketi.com",
                Sector = "  Deneme  ",
            }, mcetin);
            orgId = org.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.Organizations.SingleAsync(o => o.Id == orgId);
        // Stored "@"-less and comma-joined — the shape UserService's auto-link matcher reads.
        Assert.Equal("testsirketi.com.tr,testsirketi.com", saved.Domain);
        Assert.Equal("Deneme", saved.Sector);
    }

    [Fact]
    public async Task Create_DuplicateNameRefused_CaseInsensitive()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IOrgService>().CreateAsync(new OrgCreateRequest
            {
                Name = "KONECTA", // seeded canon org, case-insensitive
            }, mcetin));
        Assert.Equal("name-in-use", ex.Code);
    }

    [Fact]
    public async Task Create_RequiresOrgEditPermission()
    {
        using var s = new ServiceScopeBundle(fixture);
        // dkaya = Temsilci: no org.edit anywhere.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            s.Get<IOrgService>().CreateAsync(new OrgCreateRequest { Name = UniqueName() }, dkaya));
    }

    [Fact]
    public async Task SetSyncFlags_PersistAcrossScopes()
    {
        int orgId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var org = await s.Get<IOrgService>().CreateAsync(new OrgCreateRequest { Name = UniqueName() }, mcetin);
            orgId = org.Id;

            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<IOrgService>().SetSyncFlagsAsync(orgId, true, false, true, dkaya));

            await s.Get<IOrgService>().SetSyncFlagsAsync(orgId, true, false, true, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.Organizations.SingleAsync(o => o.Id == orgId);
        Assert.True(saved.ShareTicketsWithMembers);
        Assert.False(saved.CcPrimaryContacts);
        Assert.True(saved.AssignToManager);
    }

    [Fact]
    public async Task AddNote_AppendsWithActorAsAuthor()
    {
        int orgId, noteId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var org = await s.Get<IOrgService>().CreateAsync(new OrgCreateRequest { Name = UniqueName() }, mcetin);
            orgId = org.Id;
            var note = await s.Get<IOrgService>().AddNoteAsync(orgId, "  Deneme notu.  ", mcetin);
            noteId = note.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.OrgNotes.SingleAsync(n => n.Id == noteId);
        Assert.Equal(orgId, saved.OrganizationId);
        Assert.Equal("Merve Çetin", saved.AuthorName);
        Assert.Equal("Deneme notu.", saved.Body);
    }

    [Fact]
    public async Task Delete_RefusedWhileMembersExist_ThenSucceedsEmpty()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var svc = s.Get<IOrgService>();

        // Fresh org with one member (no tickets) → "has-members".
        var org = await svc.CreateAsync(new OrgCreateRequest { Name = UniqueName() }, mcetin);
        var user = await s.Get<IUserService>().CreateAsync(new UserCreateRequest
        {
            Name = "Üye Testi",
            Email = $"test.{Guid.NewGuid():N}@example.com",
            OrganizationId = org.Id,
        }, mcetin);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => svc.DeleteAsync(org.Id, mcetin));
        Assert.Equal("has-members", ex.Code);

        // Detach the member → the delete goes through (notes cascade at the DB level).
        await s.Get<IUserService>().SetOrganizationAsync(user.Id, null, mcetin);
        await svc.AddNoteAsync(org.Id, "Silinecek not.", mcetin);
        await svc.DeleteAsync(org.Id, mcetin);

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.False(await fresh.Db.Organizations.AnyAsync(o => o.Id == org.Id));
        Assert.False(await fresh.Db.OrgNotes.AnyAsync(n => n.OrganizationId == org.Id));
    }

    [Fact]
    public async Task Delete_RefusedWhileMemberTicketsExist()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        // Ulaşım A.Ş.: seeded canon org with member tickets (hero R716555 et al.).
        var ulasim = await s.Db.Organizations.SingleAsync(o => o.Name == "Ulaşım A.Ş.");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IOrgService>().DeleteAsync(ulasim.Id, mcetin));
        Assert.Equal("has-tickets", ex.Code);
    }
}
