using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 UserService (agent/users.html + user-view.html): create with org auto-link,
/// duplicate guard, block/unblock, delete guard against the Restrict FKs, the
/// "Geçersiz kıl" override semantics, notes and the strict CSV import parser.
/// Assertions read through fresh scopes (EF identity-map staleness across scopes).
/// </summary>
[Collection("Postgres")]
public class UserServiceTests(PostgresFixture fixture)
{
    private static string UniqueEmail(string domain = "example.com") =>
        $"test.{Guid.NewGuid():N}@{domain}";

    [Fact]
    public async Task Create_AutoLinksOrganizationByEmailDomain_AndSetsDefaultEmail()
    {
        var email = UniqueEmail("ulasim.com.tr");
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            // mcetin = Kıdemli Temsilci: holds user.edit + user.manage.
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var user = await s.Get<IUserService>().CreateAsync(new UserCreateRequest
            {
                Name = "Servis Testi",
                Email = email,
            }, mcetin);
            userId = user.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.Users.Include(u => u.Emails).Include(u => u.Organization)
            .SingleAsync(u => u.Id == userId);
        Assert.Equal("Ulaşım A.Ş.", saved.Organization!.Name); // Organization.Domain auto-link
        Assert.Equal(email, Assert.Single(saved.Emails).Address);
        Assert.Equal(saved.Emails[0].Id, saved.DefaultEmailId);
    }

    [Fact]
    public async Task Create_ExplicitOrganizationWinsOverDomain()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var konecta = await s.Db.Organizations.SingleAsync(o => o.Name == "Konecta");

        var user = await s.Get<IUserService>().CreateAsync(new UserCreateRequest
        {
            Name = "Servis Testi",
            Email = UniqueEmail("ulasim.com.tr"),
            OrganizationId = konecta.Id,
        }, mcetin);

        Assert.Equal(konecta.Id, user.OrganizationId);
    }

    [Fact]
    public async Task Create_DuplicateEmailRefused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IUserService>().CreateAsync(new UserCreateRequest
            {
                Name = "Mükerrer",
                Email = "BOURLA.SALEHI@ulasim.com.tr", // seeded hero, case-insensitive
            }, mcetin));
        Assert.Equal("email-in-use", ex.Code);
    }

    [Fact]
    public async Task Create_RequiresUserEditPermission()
    {
        using var s = new ServiceScopeBundle(fixture);
        // dkaya = Temsilci: only user.dir — no user.edit anywhere.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            s.Get<IUserService>().CreateAsync(new UserCreateRequest
            {
                Name = "Yetkisiz",
                Email = UniqueEmail(),
            }, dkaya));
    }

    [Fact]
    public async Task SetBlocked_TogglesFlag_AndRequiresUserManage()
    {
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var user = await s.Get<IUserService>().CreateAsync(new UserCreateRequest
            {
                Name = "Kilit Testi",
                Email = UniqueEmail(),
            }, mcetin);
            userId = user.Id;

            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<IUserService>().SetBlockedAsync(userId, true, dkaya));

            await s.Get<IUserService>().SetBlockedAsync(userId, true, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.True((await fresh.Db.Users.SingleAsync(u => u.Id == userId)).IsBlocked);
    }

    [Fact]
    public async Task Delete_RefusedWhileUserHasTickets()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        // Seeded hero owns R716555 — the tickets FK is Restrict, guard must refuse.
        var bourla = await s.Db.Users.SingleAsync(u => u.Name == "Bourla Salehi");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IUserService>().DeleteAsync(bourla.Id, mcetin));
        Assert.Equal("has-tickets", ex.Code);
    }

    [Fact]
    public async Task Delete_RemovesUserWithEmailsAndNotes()
    {
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var service = s.Get<IUserService>();
            var user = await service.CreateAsync(new UserCreateRequest
            {
                Name = "Silme Testi",
                Email = UniqueEmail(),
            }, mcetin);
            userId = user.Id;
            await service.AddNoteAsync(userId, "Silinecek not.", mcetin);
            await service.DeleteAsync(userId, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.False(await fresh.Db.Users.AnyAsync(u => u.Id == userId));
        Assert.False(await fresh.Db.UserEmails.AnyAsync(e => e.UserId == userId));
        Assert.False(await fresh.Db.UserNotes.AnyAsync(n => n.UserId == userId));
    }

    [Fact]
    public async Task Override_StoresPerUserValue_AndBlankRevertsToInherited()
    {
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var service = s.Get<IUserService>();
            var user = await service.CreateAsync(new UserCreateRequest
            {
                Name = "Devralma Testi",
                Email = UniqueEmail("ulasim.com.tr"), // inherits Ulaşım phone/address
            }, mcetin);
            userId = user.Id;
            Assert.Null(user.Phone); // inherited until overridden

            await service.OverrideAsync(userId, "phone", "+90 212 555 0999", mcetin);
        }

        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.Equal("+90 212 555 0999",
                (await s.Db.Users.SingleAsync(u => u.Id == userId)).Phone);

            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            await s.Get<IUserService>().OverrideAsync(userId, "address", "  ", mcetin); // blank = inherit
            await Assert.ThrowsAsync<DomainRuleException>(() =>
                s.Get<IUserService>().OverrideAsync(userId, "shoe-size", "45", mcetin));
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.Null((await fresh.Db.Users.SingleAsync(u => u.Id == userId)).Address);
    }

    [Fact]
    public async Task AddNote_AppendsWithActorAsAuthor()
    {
        int userId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var service = s.Get<IUserService>();
            var user = await service.CreateAsync(new UserCreateRequest
            {
                Name = "Not Testi",
                Email = UniqueEmail(),
            }, mcetin);
            userId = user.Id;
            await service.AddNoteAsync(userId, "İlk iç not.", mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var note = await fresh.Db.UserNotes.SingleAsync(n => n.UserId == userId);
        Assert.Equal("Merve Çetin", note.AuthorName);
        Assert.Equal("İlk iç not.", note.Body);
        Assert.NotNull(note.StaffId);
    }

    [Fact]
    public async Task ImportCsv_CreatesValidRows_SkipsBadOnes_ReportsCounts()
    {
        var okUlasim = UniqueEmail("ulasim.com.tr");
        var okByOrgName = UniqueEmail("gmail.com");
        var okNoOrg = UniqueEmail("hotmail.com");
        var csv = string.Join('\n',
        [
            "name,email,org",                                     // header → skipped silently
            $"İçe Aktarım Bir,{okUlasim}",                        // org auto-link by domain
            $"\"Aktarım, Virgüllü\",{okByOrgName},Konecta",       // quoted name + org by name
            $"İçe Aktarım Üç,{okNoOrg},Bilinmeyen Şirket",        // unknown org → no link
            "Geçersiz Satır,not-an-email",                        // invalid email → skipped
            "bourla.salehi@ulasim.com.tr",                        // single field → skipped
            $"Mükerrer,{okUlasim}",                               // duplicate within file → skipped
            "Mükerrer Kayıt,bourla.salehi@ulasim.com.tr",         // duplicate in db → skipped
            "",                                                   // blank line → ignored
        ]);

        CsvImportResult result;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            using var reader = new StringReader(csv);
            result = await s.Get<IUserService>().ImportCsvAsync(reader, mcetin);
        }

        Assert.Equal(3, result.Created);
        Assert.Equal(4, result.Skipped);

        using var fresh = new ServiceScopeBundle(fixture);
        var one = await fresh.Db.Users.Include(u => u.Organization)
            .SingleAsync(u => u.Emails.Any(e => e.Address == okUlasim));
        Assert.Equal("Ulaşım A.Ş.", one.Organization!.Name);
        Assert.NotNull(one.DefaultEmailId);

        var two = await fresh.Db.Users.Include(u => u.Organization)
            .SingleAsync(u => u.Emails.Any(e => e.Address == okByOrgName));
        Assert.Equal("Aktarım, Virgüllü", two.Name);
        Assert.Equal("Konecta", two.Organization!.Name);

        var three = await fresh.Db.Users
            .SingleAsync(u => u.Emails.Any(e => e.Address == okNoOrg));
        Assert.Null(three.OrganizationId);
    }
}
