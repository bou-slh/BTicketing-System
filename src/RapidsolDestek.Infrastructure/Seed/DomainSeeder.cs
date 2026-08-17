using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure.Seed;

/// <summary>
/// Idempotent dev/demo seed of the S3 domain model with the mockup canon
/// (mockups/ROADMAP.md §2 + the authority pages). Illustrative sample data only —
/// real business data replaces it at launch; nothing in the product may hardcode
/// these values. Runs once on an empty database (guarded by TicketStatuses).
/// Relative dates ("Bugün 10:18") are seeded relative to the current day so the
/// demo keeps reading like the mockups.
/// </summary>
public static class DomainSeeder
{
    private static readonly TimeSpan Tr = TimeSpan.FromHours(3); // Europe/Istanbul

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var files = services.GetRequiredService<IFileStore>();

        if (await db.TicketStatuses.AnyAsync())
            return;

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Tr).DateTime);
        var yesterday = today.AddDays(-1);
        DateTimeOffset At(DateOnly d, int h, int m) => new(d.Year, d.Month, d.Day, h, m, 0, Tr);
        DateTimeOffset On(int y, int mo, int d, int h = 12, int m = 0) => new(y, mo, d, h, m, 0, Tr);

        // ----- Statuses (canon: builtin list "Talep Durumları", 7 items) --------------
        // "overdue" is display-derived from Ticket.IsOverdue (osTicket parity: a flag,
        // not a state) but gets an internal row so the admin list matches the canon 7.
        var stNew = new TicketStatus { Key = "new", Name = "Yeni", State = TicketState.Open, Sort = 1 };
        var stOpen = new TicketStatus { Key = "open", Name = "Açık", State = TicketState.Open, Sort = 2 };
        var stWait = new TicketStatus { Key = "wait", Name = "Yanıt bekliyor", State = TicketState.Open, Sort = 3 };
        var stTest = new TicketStatus { Key = "test", Name = "Testte", State = TicketState.Open, Sort = 4 };
        var stSolved = new TicketStatus { Key = "solved", Name = "Çözüldü", State = TicketState.Closed, Sort = 5, AllowReopen = true };
        var stClosed = new TicketStatus { Key = "closed", Name = "Kapalı", State = TicketState.Closed, Sort = 6, AllowReopen = true };
        var stOverdue = new TicketStatus { Key = "overdue", Name = "Gecikmiş", State = TicketState.Open, Sort = 7, IsInternal = true };
        db.TicketStatuses.AddRange(stNew, stOpen, stWait, stTest, stSolved, stClosed, stOverdue);

        // ----- Priorities (canon i18n-tr.js: exactly four; osTicket urgency/colors) ---
        var prLow = new TicketPriority { Key = "low", Name = "Düşük", Color = "#DDFFDD", Urgency = 4 };
        var prNormal = new TicketPriority { Key = "normal", Name = "Normal", Color = "#FFFFF0", Urgency = 3 };
        var prHigh = new TicketPriority { Key = "high", Name = "Yüksek", Color = "#FEE7E7", Urgency = 2 };
        var prEmergency = new TicketPriority { Key = "emergency", Name = "Acil", Color = "#FEE7E7", Urgency = 1 };
        db.TicketPriorities.AddRange(prLow, prNormal, prHigh, prEmergency);

        // ----- Thread event types (osTicket event.yaml + effort-loop events) ---------
        var events = new[]
        {
            "created", "closed", "reopened", "assigned", "released", "transferred",
            "referred", "overdue", "edited", "merged", "linked", "collab-added",
            "effort-proposed", "effort-revised", "effort-withdrawn", "effort-approved",
            "effort-rejected",
        }.ToDictionary(n => n, n => new ThreadEventType { Name = n });
        db.ThreadEventTypes.AddRange(events.Values);

        // ----- Sequences (next safely above the highest seeded number, R716592) -------
        var seqTickets = new Sequence { Name = "Genel Talepler", Next = 716600, IsInternal = true };
        var seqTasks = new Sequence { Name = "Görev Sırası", Next = 2042, IsInternal = true };
        db.Sequences.AddRange(seqTickets, seqTasks);

        // ----- Schedules (canon admin/schedules.html) ---------------------------------
        var schWeek = new Schedule
        {
            Name = "Hafta içi 09:00–18:00", Timezone = "Europe/Istanbul",
            Description = "Standart mesai saatleri",
            Entries =
            [
                // Day: osTicket-style day-of-week bitmask (Mon..Fri = 2+4+8+16+32).
                new ScheduleEntry { Name = "Hafta içi", Repeats = ScheduleRepeat.Weekly, StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(18, 0), Day = 62, Sort = 1 },
            ],
        };
        var schAllDay = new Schedule
        {
            Name = "7/24", Timezone = "Europe/Istanbul", Description = "Kesintisiz çalışma",
            Entries = [new ScheduleEntry { Name = "Her gün", Repeats = ScheduleRepeat.Daily, StartsAt = new TimeOnly(0, 0), EndsAt = new TimeOnly(23, 59), Sort = 1 }],
        };
        var schHolidays = new Schedule
        {
            Name = "Resmi Tatiller 2026", Kind = ScheduleKind.Holidays,
            Timezone = "Europe/Istanbul", Description = "2026 resmi tatil günleri",
            Entries =
            [
                new ScheduleEntry { Name = "29 Ekim Cumhuriyet Bayramı", StartsOn = new DateOnly(2026, 10, 29), Sort = 1 },
                new ScheduleEntry { Name = "1 Ocak Yılbaşı", StartsOn = new DateOnly(2027, 1, 1), Sort = 2 },
            ],
        };
        var schSaturday = new Schedule
        {
            Name = "Cumartesi Yarım Gün", Timezone = "Europe/Istanbul",
            Description = "Cumartesi 09:00–13:00",
            Entries = [new ScheduleEntry { Name = "Cumartesi", Repeats = ScheduleRepeat.Weekly, StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(13, 0), Day = 64, Sort = 1 }],
        };
        db.Schedules.AddRange(schWeek, schAllDay, schHolidays, schSaturday);

        // ----- SLA plans (canon admin/slas.html) --------------------------------------
        var slaStandart = new SlaPlan { Name = "Standart", GracePeriodHours = 48, Schedule = schWeek };
        var slaVip = new SlaPlan { Name = "VIP", GracePeriodHours = 8, Schedule = schWeek, Notes = "Sözleşmeli VIP kullanıcılar için hızlandırılmış plan." };
        var slaKritik = new SlaPlan { Name = "Kritik", GracePeriodHours = 4, Schedule = schAllDay };
        var slaDahili = new SlaPlan { Name = "Dahili Talepler", GracePeriodHours = 72, Schedule = schWeek, IsActive = false };
        db.SlaPlans.AddRange(slaStandart, slaVip, slaKritik, slaDahili);

        // ----- Roles (canon admin/roles.html; keys follow osTicket dotted naming) -----
        string[] allPerms =
        [
            "ticket.create", "ticket.edit", "ticket.assign", "ticket.release", "ticket.transfer",
            "ticket.refer", "ticket.merge", "ticket.link", "ticket.reply", "ticket.markanswered",
            "ticket.close", "ticket.delete", "effort.propose",
            "task.create", "task.edit", "task.assign", "task.transfer", "task.reply", "task.close", "task.delete",
            "user.edit", "user.manage", "user.dir", "org.edit",
            "faq.manage", "canned.manage", "thread.edit",
            "dept.manage", "staff.manage", "banlist.manage", "stats.view", "search.advanced",
        ];
        var roleYonetici = new Role { Name = "Yönetici", Permissions = [.. allPerms] };
        var roleKidemli = new Role
        {
            Name = "Kıdemli Temsilci",
            Permissions = [.. allPerms.Where(p => p is not ("ticket.delete" or "task.delete" or "dept.manage" or "staff.manage" or "banlist.manage"))],
            Notes = "Silme dışında tüm talep yetkileri; kullanıcı ve şirket kayıtlarını düzenleyebilir.",
        };
        var roleTemsilci = new Role
        {
            Name = "Temsilci",
            Permissions =
            [
                "ticket.create", "ticket.edit", "ticket.reply", "ticket.markanswered", "ticket.close",
                "effort.propose", "task.create", "task.edit", "task.reply", "task.close", "user.dir",
            ],
        };
        var roleSaltOkunur = new Role { Name = "Salt Okunur", IsEnabled = false, Permissions = [] };
        db.Roles.AddRange(roleYonetici, roleKidemli, roleTemsilci, roleSaltOkunur);

        await db.SaveChangesAsync();

        // ----- Departments (canon: Destek, Bordro (child), Danışmanlık) ---------------
        var depDestek = new Department { Name = "Destek", TicketAutoResponse = true };
        var depBordro = new Department
        {
            Name = "Bordro", Parent = depDestek, Sla = slaStandart, Schedule = schWeek,
            Signature = "RapidSol Bordro Ekibi\nbordro@rapidsol.com.tr · 0212 555 24 80",
        };
        var depDanismanlik = new Department { Name = "Danışmanlık", IsPublic = false };
        db.Departments.AddRange(depDestek, depBordro, depDanismanlik);
        await db.SaveChangesAsync();
        depDestek.Path = $"/{depDestek.Id}/";
        depBordro.Path = $"/{depDestek.Id}/{depBordro.Id}/";
        depDanismanlik.Path = $"/{depDanismanlik.Id}/";

        // ----- Email accounts (canon admin/emails.html) -------------------------------
        var emDestek = new EmailAccount { Address = "destek@rapidsol.com.tr", DisplayName = "RapidSol Destek", Department = depDestek, PriorityId = prNormal.Id };
        var emBordro = new EmailAccount { Address = "bordro@rapidsol.com.tr", DisplayName = "RapidSol Bordro", Department = depBordro, PriorityId = prHigh.Id };
        var emBilgi = new EmailAccount { Address = "bilgi@rapidsol.com.tr", DisplayName = "RapidSol Bilgi", Department = depDanismanlik, PriorityId = prLow.Id };
        db.EmailAccounts.AddRange(emDestek, emBordro, emBilgi);
        await db.SaveChangesAsync();
        depDestek.EmailAccountId = emDestek.Id;
        depBordro.EmailAccountId = emBordro.Id;
        depDanismanlik.EmailAccountId = emBilgi.Id;

        // ----- Staff (canon agent/directory.html roster; §2 dept mapping wins) --------
        var identityByUsername = await db.StaffUsers
            .ToDictionaryAsync(u => u.UserName!, u => u.Id);
        Guid? IdentityOf(string username) =>
            identityByUsername.TryGetValue(username, out var id) ? id : null;

        // Directory canon email pattern: first-name-first-token.lastname (umit.akin@…).
        Staff MakeStaff(string username, string first, string last, Department dept, Role role,
            string phoneSuffix, bool admin = false, bool vacation = false) => new()
        {
            Username = username, FirstName = first, LastName = last,
            Email = $"{Fold(first.Split(' ')[0])}.{Fold(last)}@rapidsol.com.tr",
            Phone = $"+90 212 555 0{phoneSuffix}", PhoneExt = phoneSuffix,
            Department = dept, Role = role, IsAdmin = admin, OnVacation = vacation,
            IdentityUserId = IdentityOf(username),
        };

        var uakin = MakeStaff("uakin", "Ümit Yaşar", "Akın", depBordro, roleYonetici, "101", admin: true);
        var mcetin = MakeStaff("mcetin", "Merve", "Çetin", depDanismanlik, roleKidemli, "102");
        var dkaya = MakeStaff("dkaya", "Deniz", "Kaya", depDestek, roleTemsilci, "103");
        var saydin = MakeStaff("saydin", "Selin", "Aydın", depBordro, roleTemsilci, "104", vacation: true);
        var kyilmaz = MakeStaff("kyilmaz", "Kerem", "Yılmaz", depDestek, roleTemsilci, "105");
        var adogan = MakeStaff("adogan", "Aslı", "Doğan", depDestek, roleTemsilci, "106");
        db.Staff.AddRange(uakin, mcetin, dkaya, saydin, kyilmaz, adogan);
        await db.SaveChangesAsync();

        depDestek.ManagerStaffId = mcetin.Id;
        depBordro.ManagerStaffId = uakin.Id;

        // ----- Teams (canon admin/teams.html + "Destek Ekibi" from assign dialogs) ----
        var teamBordro = new Team
        {
            Name = "Bordro Ekibi", LeadStaffId = uakin.Id,
            Notes = "Bordro dönem kapanışlarında öncelikli müdahale ekibi.",
            Members =
            [
                new TeamMember { Staff = uakin },
                new TeamMember { Staff = mcetin },
                new TeamMember { Staff = dkaya, AlertsEnabled = false },
                new TeamMember { Staff = saydin },
            ],
        };
        var teamVip = new Team
        {
            Name = "VIP Masası", LeadStaffId = mcetin.Id,
            Members = [new TeamMember { Staff = mcetin }, new TeamMember { Staff = saydin }, new TeamMember { Staff = kyilmaz }],
        };
        var teamEntegrasyon = new Team
        {
            Name = "Entegrasyon", LeadStaffId = dkaya.Id,
            Members = [new TeamMember { Staff = dkaya }, new TeamMember { Staff = adogan }],
        };
        var teamDestek = new Team
        {
            Name = "Destek Ekibi", LeadStaffId = dkaya.Id,
            Members = [new TeamMember { Staff = dkaya }, new TeamMember { Staff = kyilmaz }, new TeamMember { Staff = adogan }],
        };
        db.Teams.AddRange(teamBordro, teamVip, teamEntegrasyon, teamDestek);

        // ----- Site pages (canon admin/pages.html; before topics for thank-you ref) ---
        var pgWelcome = new SitePage { Name = "Hoş Geldiniz", Type = SitePageType.Landing, IsActive = true, Body = "<h1>RapidsolDestek'e hoş geldiniz</h1><p>Taleplerinizi buradan iletebilirsiniz.</p>" };
        var pgOffline = new SitePage { Name = "Bakım Modu", Type = SitePageType.Offline, IsActive = false, Body = "<h1>Bakımdayız</h1><p>Kısa süre içinde tekrar hizmetinizdeyiz.</p>" };
        var pgThanks = new SitePage { Name = "Talep Alındı", Type = SitePageType.ThankYou, IsActive = true, Body = "<h1>Talebiniz alındı</h1><p>%{ticket.number} numarası ile kaydedildi.</p>" };
        var pgKvkk = new SitePage { Name = "KVKK Aydınlatma", Type = SitePageType.Other, IsActive = true, Body = "<h1>KVKK Aydınlatma Metni</h1><p>Kişisel verileriniz 6698 sayılı KVKK kapsamında işlenir.</p>" };
        db.SitePages.AddRange(pgWelcome, pgOffline, pgThanks, pgKvkk);
        await db.SaveChangesAsync();

        // ----- Forms & lists (canon admin/forms.html, lists.html, list-edit.html) -----
        var listStatuses = new ListDefinition { Name = "Talep Durumları", Type = "ticket-status", Notes = "Sistem listesi — talep durumu tablosunu yansıtır." };
        var listPriorities = new ListDefinition { Name = "Öncelikler", Type = "priority", Notes = "Sistem listesi — öncelik tablosunu yansıtır." };
        var listSectors = new ListDefinition
        {
            Name = "Sektörler",
            Items =
            [
                new ListItem { Value = "Toplu taşıma", Sort = 1 }, new ListItem { Value = "Çelik üretim", Sort = 2 },
                new ListItem { Value = "Çağrı merkezi", Sort = 3 }, new ListItem { Value = "İK danışmanlık", Sort = 4 },
                new ListItem { Value = "Perakende", Sort = 5 }, new ListItem { Value = "Lojistik", Sort = 6 },
                new ListItem { Value = "Enerji", Sort = 7 }, new ListItem { Value = "Finans", Sort = 8 },
                new ListItem { Value = "Sağlık", Sort = 9 }, new ListItem { Value = "Eğitim", Sort = 10 },
                new ListItem { Value = "Üretim", Sort = 11 }, new ListItem { Value = "Teknoloji", Sort = 12 },
            ],
        };
        var listModuller = new ListDefinition
        {
            Name = "Modül", PluralName = "Modüller", SortMode = ListSortMode.SortColumn,
            Notes = "Bordro Ek Bilgileri formundaki \"Modül\" seçim alanını besler.",
            Configuration = """{"properties":[{"name":"aciklama","label":"Açıklama","type":"text"},{"name":"sorumlu_ekip","label":"Sorumlu Ekip","type":"choice"}]}""",
            Items =
            [
                new ListItem { Value = "Bordro", Abbrev = "BRD", Sort = 1 },
                new ListItem { Value = "İzin", Abbrev = "IZN", Sort = 2 },
                new ListItem { Value = "Vardiya", Abbrev = "VRD", Sort = 3 },
                new ListItem { Value = "Raporlama", Abbrev = "RPR", Sort = 4, IsEnabled = false },
            ],
        };
        db.ListDefinitions.AddRange(listStatuses, listPriorities, listSectors, listModuller);
        await db.SaveChangesAsync();

        var formUser = new FormDefinition { Title = "Kullanıcı Bilgileri", Name = "user", Kind = FormKind.User, IsSystem = true };
        var formTicket = new FormDefinition { Title = "Talep Detayları", Name = "ticket", Kind = FormKind.Ticket, IsSystem = true };
        var formTask = new FormDefinition { Title = "Görev Detayları", Name = "task", Kind = FormKind.Task, IsSystem = true };
        var formOrg = new FormDefinition { Title = "Şirket Bilgileri", Name = "org", Kind = FormKind.Organization, IsSystem = true };
        var formCompany = new FormDefinition { Title = "Şirket Profili", Name = "company", IsSystem = true, Notes = "Yardım masasının kendi şirket bilgileri." };
        var formBordroEk = new FormDefinition
        {
            Title = "Bordro Ek Bilgileri", Name = "bordro_ek",
            Fields =
            [
                new FormField { Type = "choices", Label = "Modül", Name = "modul", Sort = 1, Configuration = $$"""{"list_id":{{listModuller.Id}}}""" },
                new FormField { Type = "text", Label = "Personel No", Name = "personel_no", Sort = 2 },
                new FormField { Type = "date", Label = "Dönem", Name = "donem", Sort = 3 },
            ],
        };
        var formProje = new FormDefinition
        {
            Title = "Proje Talebi", Name = "proje",
            Fields =
            [
                new FormField { Type = "text", Label = "Proje Adı", Name = "proje_adi", Sort = 1 },
                new FormField { Type = "text", Label = "Tahmini Süre", Name = "tahmini_sure", Sort = 2 },
            ],
        };
        db.FormDefinitions.AddRange(formUser, formTicket, formTask, formOrg, formCompany, formBordroEk, formProje);

        // ----- Help topics (canon admin/helptopics.html + helptopic-edit.html) --------
        var htBordro = new HelpTopic { Name = "Bordro", Department = depBordro, PriorityId = prNormal.Id, Sort = 1 };
        var htYolUcreti = new HelpTopic
        {
            Name = "Yol Ücreti", Parent = htBordro, Department = depBordro,
            PriorityId = prHigh.Id, SlaId = slaVip.Id, StatusId = stOpen.Id, Sort = 2,
            SitePageId = pgThanks.Id, NumberFormat = "BRD-######",
            Notes = "Yol ücreti düzeltmeleri için ayrı alt konu — İK ekibinin isteğiyle açıldı.",
        };
        var htIzin = new HelpTopic { Name = "İzin", Department = depDanismanlik, PriorityId = prNormal.Id, Sort = 3 };
        var htVardiya = new HelpTopic { Name = "Vardiya", Department = depDanismanlik, PriorityId = prLow.Id, IsActive = false, Sort = 4 };
        var htDanismanlik = new HelpTopic { Name = "Danışmanlık", Department = depDanismanlik, PriorityId = prNormal.Id, IsPublic = false, Sort = 5 };
        db.HelpTopics.AddRange(htBordro, htYolUcreti, htIzin, htVardiya, htDanismanlik);
        await db.SaveChangesAsync();
        htYolUcreti.StaffId = uakin.Id; // auto-assign canon: Ümit Yaşar Akın
        htYolUcreti.Forms.Add(new HelpTopicForm { FormDefinitionId = formBordroEk.Id, Sort = 1 });

        // ----- Organizations & users (canon agent/orgs.html, users.html, org-view) ----
        // Sector moved from the Notes blob into the Sector column (agent/orgs.html column);
        // Ulaşım's note text lives as the canon OrgNote below (agent/org-view.html Notlar tab).
        // CreatedAt = the orgs.html Updated column canon (Bugün 09:12 / 8 Ağu / 6 Ağu / Dün 17:05).
        var orgUlasim = new Organization
        {
            Name = "Ulaşım A.Ş.", Domain = "ulasim.com.tr", Sector = "Toplu taşıma", ManagerStaffId = uakin.Id,
            Phone = "+90 212 555 0142",
            Address = "Esentepe Mah. Kore Şehitleri Cad. No: 34, Şişli / İstanbul",
            ShareTicketsWithMembers = true, CcPrimaryContacts = true,
            CreatedAt = At(today, 9, 12),
        };
        var orgTosyali = new Organization { Name = "Tosyalı Holding", Domain = "tosyali.com.tr", Sector = "Çelik üretim", ManagerStaffId = mcetin.Id, CreatedAt = On(2026, 8, 8) };
        var orgKonecta = new Organization { Name = "Konecta", Domain = "konecta.com", Sector = "Çağrı merkezi", ManagerStaffId = dkaya.Id, CreatedAt = On(2026, 8, 6) };
        var orgRapidsol = new Organization { Name = "RapidSol", Domain = "rapidsol.com.tr", Sector = "İK danışmanlık", ManagerStaffId = uakin.Id, CreatedAt = At(yesterday, 17, 5) };
        db.Organizations.AddRange(orgUlasim, orgTosyali, orgKonecta, orgRapidsol);

        var bourlaIdentity = await db.CustomerUsers
            .Where(c => c.NormalizedEmail == "BOURLA.SALEHI@ULASIM.COM.TR")
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();

        User MakeUser(string name, string email, Organization? org, DateTimeOffset registered,
            string? phone = null, Guid? identity = null, bool blocked = false) => new()
        {
            Name = name, Organization = org, Phone = phone, IdentityUserId = identity,
            IsBlocked = blocked, CreatedAt = registered,
            Emails = [new UserEmail { Address = email }],
        };

        var bourla = MakeUser("Bourla Salehi", "bourla.salehi@ulasim.com.tr", orgUlasim, On(2025, 1, 12), "+90 212 555 0180", bourlaIdentity);
        var ayse = MakeUser("Ayşe Yıldırım", "ayse.yildirim@ulasim.com.tr", orgUlasim, On(2025, 3, 3), "+90 212 555 0142");
        var mehmet = MakeUser("Mehmet Demir", "mehmet.demir@tosyali.com.tr", orgTosyali, On(2025, 4, 18));
        var elif = MakeUser("Elif Şahin", "elif.sahin@konecta.com", orgKonecta, On(2026, 5, 22)); // Misafir: no portal account
        var burak = MakeUser("Burak Öztürk", "burak.ozturk@ulasim.com.tr", orgUlasim, On(2025, 6, 9), "+90 532 555 0217");
        var zeynep = MakeUser("Zeynep Arslan", "zeynep.arslan@tosyali.com.tr", orgTosyali, On(2025, 10, 30), blocked: true); // Kilitli
        var can = MakeUser("Can Koç", "can.koc@rapidsol.com.tr", orgRapidsol, On(2026, 2, 14));
        var hakan = MakeUser("Hakan Sarı", "hakan.sari@ulasim.com.tr", orgUlasim, On(2025, 8, 1));
        var pelin = MakeUser("Pelin Ak", "pelin.ak@ulasim.com.tr", orgUlasim, On(2026, 3, 10)); // Misafir
        var murat = MakeUser("Murat Eren", "murat.eren@ulasim.com.tr", orgUlasim, On(2025, 9, 5), "+90 533 555 0468");
        User[] users = [bourla, ayse, mehmet, elif, burak, zeynep, can, hakan, pelin, murat];
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        foreach (var u in users)
            u.DefaultEmailId = u.Emails[0].Id;

        // Canon internal note on the hero org (agent/org-view.html Notlar tab).
        db.OrgNotes.Add(new OrgNote
        {
            Organization = orgUlasim, StaffId = uakin.Id, AuthorName = "Ümit Yaşar Akın",
            CreatedAt = On(2026, 8, 1, 10, 30),
            Body = "Sözleşme yenilemesi Eylül başında. İK direktörü Ayşe Yıldırım tüm bordro "
                + "taleplerinin tek onay noktası; kritik değişikliklerde önce onu bilgilendirin.",
        });

        // Canon internal note on the hero user (agent/user-view.html Notlar tab).
        db.UserNotes.Add(new UserNote
        {
            User = bourla, StaffId = mcetin.Id, AuthorName = "Merve Çetin",
            CreatedAt = On(2026, 8, 4, 11, 5),
            Body = "Kullanıcı telefonla arandı; bordro entegrasyonu projesi için tek yetkili iletişim "
                + "kişisi olduğu teyit edildi. Acil konularda önce e-posta, sonra telefon tercih ediyor.",
        });

        // ----- Knowledge base (canon agent/kb.html + portal/kb.html) ------------------
        var kbBordro = new KbCategory { Name = "Bordro", IsPublic = true, Description = "Bordro hesaplama ve kesinti soruları" };
        var kbIzin = new KbCategory { Name = "İzin", IsPublic = true, Description = "İzin türleri, devir ve bakiye işlemleri" };
        var kbVardiya = new KbCategory { Name = "Vardiya", IsPublic = true, Description = "Vardiya planlama ve puantaj" };
        var kbSistem = new KbCategory { Name = "Sistem", IsPublic = true, Description = "Sistem kullanımı ve yönetimi" };
        db.KbCategories.AddRange(kbBordro, kbIzin, kbVardiya, kbSistem);

        FaqArticle Faq(KbCategory cat, string q, bool published, string answer = "") => new()
        {
            Category = cat, Question = q, IsPublished = published,
            Answer = answer == "" ? $"<p>{q}</p>" : answer,
        };
        var faqYolUcreti = Faq(kbBordro, "Yol ücreti nasıl hesaplanır?", true,
            "<p>Yol ücreti, fiili çalışılan gün sayısı üzerinden hesaplanır; vergi istisnası ve kesinti kuralları uygulanır.</p>" +
            "<ol><li>Fiili çalışılan gün sayısını belirleyin.</li><li>Günlük yol ücreti tutarıyla çarpın.</li><li>Vergi istisnası sınırını kontrol edin.</li></ol>" +
            "<p>Örnek: 21 fiili gün × 88,00 TL = 1.848,00 TL.</p>");
        var kbArticles = new[]
        {
            faqYolUcreti,
            Faq(kbBordro, "Ek mesai bordroya nasıl yansır?", true),
            Faq(kbBordro, "Bordro kesinti kalemleri rehberi", false),
            Faq(kbBordro, "Asgari ücret güncellemesi sonrası kontroller", true),
            Faq(kbIzin, "Yıllık izin devri nasıl yapılır?", true),
            Faq(kbIzin, "Mazeret izni girişi", true),
            Faq(kbIzin, "İzin bakiyesi raporu alma", false),
            Faq(kbIzin, "Rapor (istirahat) girişi nasıl yapılır?", true),
            Faq(kbVardiya, "Vardiya planı yükleme adımları", true),
            Faq(kbVardiya, "Gece vardiyası katsayısı ayarı", false),
            Faq(kbVardiya, "Vardiya değişim talepleri", true),
            Faq(kbSistem, "Parola sıfırlama işlemi", true),
            Faq(kbSistem, "Toplu personel içe aktarma", false),
            Faq(kbSistem, "API erişim anahtarları", false),
            Faq(kbSistem, "Yeni kullanıcı nasıl eklenir?", true),
            Faq(kbSistem, "Bildirim tercihleri nasıl değiştirilir?", true),
        };
        db.FaqArticles.AddRange(kbArticles);
        await db.SaveChangesAsync();
        faqYolUcreti.HelpTopics.Add(new FaqArticleTopic { HelpTopicId = htBordro.Id });
        faqYolUcreti.HelpTopics.Add(new FaqArticleTopic { HelpTopicId = htYolUcreti.Id });

        // ----- Canned responses (canon agent/canned.html + ticket-view composer) ------
        db.CannedResponses.AddRange(
            new CannedResponse { Title = "Efor onayı isteği", Department = depBordro, Response = "<p>Merhaba %{ticket.user.name},</p><p>Talebiniz için öngörülen efor: %{effort.hours} saat. Onayınızı rica ederiz.</p>" },
            new CannedResponse { Title = "Ek bilgi talebi", Department = depDestek, Response = "<p>Merhaba, talebinizle ilgili ek bilgiye ihtiyacımız var.</p>" },
            new CannedResponse { Title = "Uzaktan bağlantı yönergesi", Department = depDestek, Response = "<p>Uzaktan bağlantı için aşağıdaki adımları izleyin.</p>" },
            new CannedResponse { Title = "Bordro dönemi kapanış bilgilendirmesi", Department = depBordro, IsEnabled = false, Response = "<p>Bordro dönemi kapanış çalışması başlamıştır.</p>" },
            new CannedResponse { Title = "Talep kapanış teşekkürü", Department = depDestek, Response = "<p>Talebiniz çözümlenmiştir; bize ulaştığınız için teşekkür ederiz.</p>" },
            new CannedResponse { Title = "Bordro düzeltme bilgilendirmesi", Department = depBordro, Response = "<p>Bordro düzeltmesi tamamlanmıştır.</p>" },
            new CannedResponse { Title = "Efor onayı hatırlatması", Department = depBordro, Response = "<p>Bekleyen efor onayınızı hatırlatırız.</p>" });

        // ----- Email template sets (canon admin/templates.html + template-edit) -------
        (string Code, string Name)[] templateNames =
        [
            ("ticket.autoresp", "Yeni Talep Otomatik Yanıtı"),
            ("ticket.autoreply", "Yeni Talep Oto-cevap"),
            ("message.autoresp", "Yeni Mesaj Onayı"),
            ("ticket.notice", "Yeni Talep Bildirimi"),
            ("ticket.overlimit", "Limit Aşımı Bildirimi"),
            ("ticket.reply", "Yanıt Şablonu"),
            ("effort.request", "Efor Onayı İsteği"),
            ("effort.response", "Efor Yanıtı Uyarısı"),
            ("ticket.activity.notice", "Yeni Aktivite Bildirimi"),
            ("ticket.alert", "Yeni Talep Uyarısı"),
            ("message.alert", "Yeni Mesaj Uyarısı"),
            ("note.alert", "İç Aktivite Uyarısı"),
            ("assigned.alert", "Atama Uyarısı"),
            ("transfer.alert", "Aktarım Uyarısı"),
            ("ticket.overdue", "Gecikme Uyarısı"),
            ("task.alert", "Yeni Görev Uyarısı"),
            ("task.activity.alert", "Yeni Aktivite Uyarısı"),
            ("task.activity.notice", "Yeni Aktivite Bildirimi (katılımcı)"),
            ("task.assigned.alert", "Görev Atama Uyarısı"),
            ("task.transfer.alert", "Görev Aktarım Uyarısı"),
            ("task.overdue.alert", "Görev Gecikme Uyarısı"),
        ];
        EmailTemplateSet MakeSet(string name, string lang, bool active) => new()
        {
            Name = name, Language = lang, IsActive = active,
            Templates = [.. templateNames.Select(t => new EmailTemplate
            {
                CodeName = t.Code, Subject = t.Name,
                Body = $"<p>{t.Name} — %{{ticket.number}}</p>",
            })],
        };
        db.EmailTemplateSets.AddRange(MakeSet("Varsayılan (TR)", "tr", true), MakeSet("English Set (EN)", "en", true));

        // ----- Filters, banlist, API keys (canon admin pages) -------------------------
        var fVip = new Filter
        {
            Name = "VIP kullanıcı önceliklendirme", ExecOrder = 1, Target = FilterTarget.Email,
            Rules =
            [
                new FilterRule { What = "email", How = FilterMatchHow.EndsWith, Value = "@ulasim.com.tr" },
                new FilterRule { What = "email", How = FilterMatchHow.EndsWith, Value = "@tosyali.com.tr" },
                new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = "acil" },
            ],
            Actions = [new FilterAction { Type = "priority", Sort = 1, Configuration = $$"""{"priority_id":{{prHigh.Id}}}""" }],
        };
        var fSpam = new Filter
        {
            Name = "Spam engelleme", ExecOrder = 2, Target = FilterTarget.Email, MatchAllRules = false, StopOnMatch = true,
            Rules =
            [
                new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = "kampanya" },
                new FilterRule { What = "email", How = FilterMatchHow.EndsWith, Value = "@kampanyamail.net" },
            ],
            Actions = [new FilterAction { Type = "reject", Sort = 1 }],
        };
        var fWeb = new Filter
        {
            Name = "Web formu yönlendirme", ExecOrder = 3, Target = FilterTarget.Web,
            Rules = [new FilterRule { What = "topic", How = FilterMatchHow.Equal, Value = "Bordro" }],
            Actions = [new FilterAction { Type = "dept", Sort = 1, Configuration = $$"""{"dept_id":{{depBordro.Id}}}""" }],
        };
        var fApi = new Filter
        {
            Name = "API talepleri etiketleme", ExecOrder = 4, Target = FilterTarget.Api, IsActive = false,
            Rules =
            [
                new FilterRule { What = "source", How = FilterMatchHow.Equal, Value = "API" },
                new FilterRule { What = "subject", How = FilterMatchHow.StartsWith, Value = "[oto]" },
            ],
            Actions = [new FilterAction { Type = "note", Sort = 1, Configuration = """{"note":"API üzerinden oluşturuldu"}""" }],
        };
        db.Filters.AddRange(fVip, fSpam, fWeb, fApi);

        db.BanlistEntries.AddRange(
            new BanlistEntry { Address = "spam@ornek.com" },
            new BanlistEntry { Address = "toplu-reklam@kampanyamail.net" },
            new BanlistEntry { Address = "bildirim@sahte-banka.xyz", IsActive = false });

        db.ApiKeys.AddRange(
            new ApiKey { Key = "7A3F09B2E65C4A1893D0F7B2A94E6D41", IpAddress = "185.34.12.10", CanCreateTickets = true, CanTriggerJobs = true },
            new ApiKey { Key = "C9E1F274A08B4D63917E5A20FB36C8D1", IpAddress = "10.0.4.22", CanCreateTickets = false, CanTriggerJobs = true },
            new ApiKey { Key = "4E88AD10C2F94B7E8A31D6905C74F2A7", IpAddress = "78.186.44.9", IsActive = false, CanCreateTickets = true },
            new ApiKey { Key = "B05C77E952A44F0D8C6E2B19A83413AD", IpAddress = "213.14.77.3", CanCreateTickets = true });

        // ----- Queue tree (canon agent/tickets.html + admin/queues.html) --------------
        var colNo = new QueueColumn { Name = "Talep No", PrimaryPath = "number", Decorator = "link" };
        var colUpdated = new QueueColumn { Name = "Son Güncelleme", PrimaryPath = "last_update_at" };
        var colSubject = new QueueColumn { Name = "Konu", PrimaryPath = "subject", SecondaryPath = "user__name", Decorator = "link" };
        var colUser = new QueueColumn { Name = "Kullanıcı", PrimaryPath = "user__name", SecondaryPath = "user__organization__name" };
        var colSlaLeft = new QueueColumn { Name = "SLA Kalan", PrimaryPath = "estimated_due_date", Decorator = "countdown" };
        var colAssignee = new QueueColumn { Name = "Atanan", PrimaryPath = "staff__full_name" };
        var colStatus = new QueueColumn { Name = "Durum", PrimaryPath = "status__key", Decorator = "status-chip" };
        var colPriority = new QueueColumn { Name = "Öncelik", PrimaryPath = "priority__key", Decorator = "priority-chip" };
        db.QueueColumns.AddRange(colNo, colUpdated, colSubject, colUser, colSlaLeft, colAssignee, colStatus, colPriority);

        var sortRecent = new QueueSortOption { Name = "Son Güncellenen", Root = "Ticket", Columns = """["-last_update_at"]""" };
        var sortSla = new QueueSortOption { Name = "SLA Kalan Süre", Root = "Ticket", Columns = """["estimated_due_date"]""" };
        db.QueueSortOptions.AddRange(sortRecent, sortSla);
        await db.SaveChangesAsync();

        SavedQueue Q(string title, SavedQueue? parent, string criteria, int sort) => new()
        {
            Title = title, Parent = parent, Criteria = criteria, Sort = sort,
        };
        var qOpen = Q("Açık", null, """{"state":"open"}""", 1);
        var qUnanswered = Q("Yanıtlanmamış", qOpen, """{"state":"open","isanswered":false}""", 1);
        var qAnswered = Q("Yanıtlanmış", qOpen, """{"state":"open","isanswered":true}""", 2);
        var qOverdue = Q("Gecikmiş", qOpen, """{"state":"open","isoverdue":true}""", 3);
        var qEffort = Q("Efor Onayında", qOpen, """{"state":"open","effort":"pending"}""", 4);
        var qMine = Q("Taleplerim", null, """{"assignee":"me"}""", 2);
        var qAssignedMe = Q("Bana Atanan", qMine, """{"assignee":"me"}""", 1);
        var qAssignedTeam = Q("Takımıma Atanan", qMine, """{"assignee":"my-teams"}""", 2);
        var qClosed = Q("Kapalı", null, """{"state":"closed"}""", 3);
        var qClosedToday = Q("Bugün", qClosed, """{"state":"closed","closed":"today"}""", 1);
        var qClosedWeek = Q("Bu Hafta", qClosed, """{"state":"closed","closed":"week"}""", 2);
        var qClosedMonth = Q("Bu Ay", qClosed, """{"state":"closed","closed":"month"}""", 3);
        // Personal saved search (owner uakin → renders under "Kayıtlı Aramalarım").
        var qSlaVip = new SavedQueue
        {
            Title = "SLA Riskli VIP", StaffId = uakin.Id, InheritColumns = false, Sort = 1,
            Criteria = """{"state":"open","sla_remaining_lt":"2h","org_tag":"VIP"}""",
            Columns =
            [
                new SavedQueueColumn { Column = colNo, Sort = 1, Width = 100 },
                new SavedQueueColumn { Column = colUpdated, Sort = 2, Width = 130 },
                new SavedQueueColumn { Column = colSubject, Sort = 3 },
                new SavedQueueColumn { Column = colUser, Sort = 4, Width = 160 },
                new SavedQueueColumn { Column = colSlaLeft, Sort = 5, Width = 110 },
                new SavedQueueColumn { Column = colAssignee, Sort = 6, Width = 140 },
            ],
            Sorts = [new SavedQueueSort { SortOption = sortSla, Sort = 1, IsDefault = true }],
        };
        db.SavedQueues.AddRange(qOpen, qUnanswered, qAnswered, qOverdue, qEffort,
            qMine, qAssignedMe, qAssignedTeam, qClosed, qClosedToday, qClosedWeek, qClosedMonth, qSlaVip);
        await db.SaveChangesAsync();
        foreach (var q in new[] { qOpen, qMine, qClosed, qSlaVip })
            q.Path = $"/{q.Id}/";
        foreach (var q in new[] { qUnanswered, qAnswered, qOverdue, qEffort })
            q.Path = $"/{qOpen.Id}/{q.Id}/";
        foreach (var q in new[] { qAssignedMe, qAssignedTeam })
            q.Path = $"/{qMine.Id}/{q.Id}/";
        foreach (var q in new[] { qClosedToday, qClosedWeek, qClosedMonth })
            q.Path = $"/{qClosed.Id}/{q.Id}/";

        // ----- Tickets (canon §10; the R7165xx family) --------------------------------
        Ticket T(string number, string subject, User user, Department dept, TicketStatus status,
            TicketPriority priority, Staff? assignee, DateTimeOffset created, DateTimeOffset updated,
            HelpTopic? topic = null, SlaPlan? sla = null, TicketSource source = TicketSource.Web,
            bool overdue = false, bool answered = false, DateTimeOffset? closed = null)
        {
            var t = new Ticket
            {
                Number = number, Subject = subject, UserId = user.Id,
                UserEmailId = user.DefaultEmailId, DepartmentId = dept.Id,
                StatusId = status.Id, PriorityId = priority.Id, StaffId = assignee?.Id,
                HelpTopicId = (topic ?? htBordro).Id, SlaId = (sla ?? slaStandart).Id,
                Source = source, IsOverdue = overdue, IsAnswered = answered,
                CreatedAt = created, LastUpdateAt = updated, ClosedAt = closed,
                Thread = new Thread { CreatedAt = created, LastMessageAt = created },
            };
            db.Tickets.Add(t);
            return t;
        }

        // Hero ticket R716555 — golden-path story (agent/ticket-view.html authority).
        var heroCreated = At(yesterday, 16, 29);
        var hero = T("R716555", "Yol ücreti hatası hakkında", bourla, depBordro, stOpen, prNormal,
            uakin, heroCreated, At(today, 10, 18), topic: htBordro, sla: slaStandart);
        hero.DueDate = At(today, 17, 0);
        hero.EstimatedDueDate = At(today, 17, 0);

        // Placeholder bytes written through IFileStore so downloads actually stream;
        // padded to the canon byte sizes. Placeholders are not openable documents —
        // they exist so download endpoints have real content to serve.
        async Task<StoredFile> SeedFile(string name, string mimeType, int size, DateTimeOffset createdAt)
        {
            var bytes = new byte[size];
            var filler = System.Text.Encoding.UTF8.GetBytes($"RapidsolDestek seed placeholder — {name}\n");
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = filler[i % filler.Length];
            var file = await files.SaveAsync(new MemoryStream(bytes), name, mimeType);
            file.CreatedAt = createdAt;
            return file;
        }

        const string xlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var fXlsx = await SeedFile("yol-ucreti-kontrol.xlsx", xlsxMime, 48_512, heroCreated);
        var fPng = await SeedFile("ekran-goruntusu.png", "image/png", 212_480, heroCreated);
        var fPdf = await SeedFile("efor-detayi.pdf", "application/pdf", 96_256, At(today, 10, 18));
        // KB attachments of the hero article (canon portal/kb-article.html).
        var fKbXlsx = await SeedFile("yol-ucreti-hesaplama-ornegi.xlsx", xlsxMime, 27_648, At(yesterday, 11, 0));
        var fKbPdf = await SeedFile("2026-vergi-istisna-tutarlari.pdf", "application/pdf", 84_992, At(yesterday, 11, 0));
        db.StoredFiles.AddRange(fXlsx, fPng, fPdf, fKbXlsx, fKbPdf);

        var heroMsg = new ThreadEntry
        {
            Thread = hero.Thread!, Type = ThreadEntryType.Message, UserId = bourla.Id,
            Poster = "Bourla Salehi", Source = "Web", CreatedAt = heroCreated,
            Body = "Temmuz bordrosunda yol ücreti kesintisi beklenenden yüksek görünüyor. " +
                   "İlgili personel numaralarını ve ekran görüntüsünü ekledim. Kontrol edip dönüş yapabilir misiniz?",
        };
        var heroNote = new ThreadEntry
        {
            Thread = hero.Thread!, Type = ThreadEntryType.Note, StaffId = mcetin.Id,
            Poster = "Merve Çetin", Source = "Web", CreatedAt = At(today, 10, 5),
            Body = "Kesinti parametresi Temmuz güncellemesinde iki kez uygulanmış görünüyor. " +
                   "Ümit, bordro modülündeki 4512 numaralı kayda bakabilir misin?",
        };
        var heroReply = new ThreadEntry
        {
            Thread = hero.Thread!, Type = ThreadEntryType.Response, StaffId = uakin.Id,
            Poster = "Ümit Y. Akın", Source = "Web", CreatedAt = At(today, 10, 18),
            Body = "Merhaba, inceleme tamamlandı. Yol ücreti kesinti parametresi hatalı çoğaltılmış; " +
                   "düzeltme ve test çalışması için toplam 6 saat efor öngörüyoruz. Onayınızdan sonra çalışmaya başlayacağız.",
        };
        db.ThreadEntries.AddRange(heroMsg, heroNote, heroReply);

        hero.Thread!.Events.Add(new ThreadEvent
        {
            EventType = events["assigned"], StaffId = uakin.Id, ActorType = ActorType.Staff,
            ActorId = uakin.Id, Username = "Ümit Y. Akın", OccurredAt = At(today, 9, 54),
            Data = """{"staff":"Ümit Y. Akın"}""",
        });
        hero.Thread!.Events.Add(new ThreadEvent
        {
            EventType = events["effort-proposed"], StaffId = uakin.Id, ActorType = ActorType.Staff,
            ActorId = uakin.Id, Username = "Ümit Y. Akın", OccurredAt = At(today, 10, 18),
            Data = """{"hours":6}""",
        });

        hero.EffortProposals.Add(new EffortProposal
        {
            RevisionNo = 1, Hours = 6, Note = "Düzenleme + test dahil",
            ProposedByStaffId = uakin.Id, CreatedAt = At(today, 10, 18),
        });

        // Remaining canonical sample tickets.
        T("R716561", "AGİ tutarı güncellemesi", can, depBordro, stOpen, prLow, uakin, At(today, 8, 30), At(today, 9, 47));
        T("R716560", "Temmuz bordrosunda SGK kesintisi farkı", elif, depBordro, stWait, prHigh, uakin, At(yesterday, 14, 10), At(today, 9, 12), answered: true);
        T("R716548", "Yıllık izin devri hesaplama sorusu", mehmet, depDanismanlik, stOpen, prNormal, uakin, At(yesterday, 9, 20), At(yesterday, 17, 40), topic: htIzin);
        var t44 = T("R716544", "Fazla mesai onay akışı çalışmıyor", bourla, depBordro, stWait, prHigh, uakin, On(2026, 8, 7, 10, 12), At(yesterday, 15, 3), answered: true);
        t44.EffortProposals.Add(new EffortProposal
        {
            RevisionNo = 1, Hours = 8, State = EffortState.Rejected, ProposedByStaffId = uakin.Id,
            DecidedByUserId = bourla.Id, DecidedAt = At(yesterday, 15, 3),
            DecisionNote = "Kapsam netleşmeden onay veremiyoruz; alternatif çözüm önerisi rica ederiz.",
            CreatedAt = On(2026, 8, 8, 11, 0),
        });
        T("R716539", "Vardiya planı içe aktarma hatası", burak, depDestek, stOpen, prEmergency, uakin, On(2026, 8, 6, 9, 40), At(yesterday, 11, 26), topic: htVardiya, overdue: true);
        var t32 = T("R716532", "Kıdem tazminatı raporu talebi", mehmet, depBordro, stSolved, prNormal, uakin, On(2026, 8, 5, 13, 25), On(2026, 8, 8, 16, 55), closed: On(2026, 8, 8, 16, 55));
        t32.EffortProposals.Add(new EffortProposal
        {
            RevisionNo = 1, Hours = 5, State = EffortState.Approved, ProposedByStaffId = uakin.Id,
            DecidedByUserId = mehmet.Id, DecidedAt = On(2026, 8, 6, 10, 30), CreatedAt = On(2026, 8, 5, 15, 0),
        });
        T("R716528", "Personel işe giriş bildirgesi gecikmesi", elif, depDestek, stOpen, prHigh, uakin, On(2026, 8, 7, 9, 5), On(2026, 8, 8, 10, 31));
        T("R716551", "Vardiya ekleme talebi", bourla, depDestek, stTest, prNormal, mcetin, On(2026, 8, 4, 10, 45), On(2026, 8, 5, 16, 5), topic: htVardiya);
        T("R716545", "Fazla mesai raporu düzenlemesi", bourla, depBordro, stOpen, prNormal, dkaya, On(2026, 8, 4, 11, 20), On(2026, 8, 4, 11, 20));
        T("R716502", "Kıdem tazminatı tavanı güncellemesi", bourla, depBordro, stSolved, prNormal, uakin, On(2026, 7, 26, 9, 30), On(2026, 7, 28, 15, 0), closed: On(2026, 7, 28, 15, 0));
        T("R716489", "SGK bildirge formatı sorusu", bourla, depBordro, stClosed, prNormal, mcetin, On(2026, 7, 18, 14, 5), On(2026, 7, 21, 10, 40), closed: On(2026, 7, 21, 10, 40));
        T("R716476", "Yıllık izin devri hesaplaması", bourla, depDanismanlik, stSolved, prNormal, dkaya, On(2026, 7, 13, 8, 50), On(2026, 7, 15, 12, 20), topic: htIzin, closed: On(2026, 7, 15, 12, 20));
        T("R716540", "SGK bildirge hatası", ayse, depBordro, stOpen, prHigh, uakin, On(2026, 8, 6, 8, 15), At(today, 8, 55));
        T("R716512", "Vardiya şablonu içe aktarma sorunu", burak, depDestek, stWait, prNormal, uakin, On(2026, 8, 1, 10, 30), At(yesterday, 15, 32), topic: htVardiya, answered: true);
        T("R716455", "İzin devri düzeltme talebi", murat, depDanismanlik, stOpen, prNormal, uakin, On(2026, 7, 30, 9, 10), On(2026, 8, 7, 14, 25), topic: htIzin);
        T("R716301", "Aylık bordro raporu gecikmesi", hakan, depBordro, stClosed, prNormal, mcetin, On(2026, 7, 22, 11, 40), On(2026, 7, 30, 9, 15), closed: On(2026, 7, 30, 9, 15));
        T("R716536", "Maaş ödeme dosyası bankaya gitmedi", elif, depBordro, stOpen, prEmergency, null, At(yesterday, 8, 5), At(today, 7, 48), sla: slaKritik, source: TicketSource.Email);
        var t49 = T("R716549", "Ek mesai parametre düzenlemesi", mehmet, depBordro, stOpen, prNormal, mcetin, At(yesterday, 13, 15), At(today, 9, 30));
        t49.EffortProposals.Add(new EffortProposal
        {
            RevisionNo = 1, Hours = 3, ProposedByStaffId = mcetin.Id, CreatedAt = At(today, 9, 30),
        });
        T("R716550", "Kıdem tazminatı raporu talebi", zeynep, depBordro, stWait, prNormal, mcetin, At(yesterday, 14, 0), At(today, 8, 40), answered: true);
        T("R716552", "BES kesinti listesi talebi", mehmet, depBordro, stNew, prNormal, null, At(today, 7, 55), At(today, 7, 55), source: TicketSource.Email);
        T("R716557", "Puantaj raporu dışa aktarma", burak, depDestek, stNew, prNormal, null, At(today, 8, 20), At(today, 8, 20), topic: htVardiya);
        T("R716562", "Ağustos avans bordrosu hatası", elif, depBordro, stNew, prNormal, null, At(today, 9, 5), At(today, 9, 5), source: TicketSource.Email);
        T("R716563", "İzin bakiyesi görünmüyor", mehmet, depDanismanlik, stNew, prNormal, null, At(today, 9, 40), At(today, 9, 40), topic: htIzin);
        T("R716564", "Yeni personel kaydı açılmıyor", can, depDanismanlik, stNew, prNormal, null, At(today, 10, 0), At(today, 10, 0), topic: htDanismanlik);
        T("R716580", "Bordro fark hesaplama talebi", mehmet, depBordro, stOpen, prHigh, dkaya, At(today, 8, 10), At(today, 9, 50), sla: slaVip);
        T("R716592", "İzin devri kural sorusu", elif, depDanismanlik, stOpen, prNormal, null, At(today, 9, 15), At(today, 9, 55), topic: htIzin, sla: slaVip);

        await db.SaveChangesAsync();

        // Hero attachments (polymorphic soft links, need entry ids).
        db.Attachments.AddRange(
            new Attachment { ObjectType = AttachmentObjectType.ThreadEntry, ObjectId = heroMsg.Id, FileId = fXlsx.Id },
            new Attachment { ObjectType = AttachmentObjectType.ThreadEntry, ObjectId = heroMsg.Id, FileId = fPng.Id },
            new Attachment { ObjectType = AttachmentObjectType.ThreadEntry, ObjectId = heroReply.Id, FileId = fPdf.Id },
            new Attachment { ObjectType = AttachmentObjectType.FaqArticle, ObjectId = faqYolUcreti.Id, FileId = fKbXlsx.Id },
            new Attachment { ObjectType = AttachmentObjectType.FaqArticle, ObjectId = faqYolUcreti.Id, FileId = fKbPdf.Id });

        // ----- Tasks (canon agent/tasks.html + task-view.html) ------------------------
        var ticketsByNumber = await db.Tickets.ToDictionaryAsync(t => t.Number, t => t.Id);
        TaskItem MakeTask(string number, string title, string? ticketNumber, Department dept,
            Staff assignee, DateTimeOffset created, DateOnly? due, bool closed = false)
        {
            var task = new TaskItem
            {
                Number = number, Title = title,
                TicketId = ticketNumber is not null ? ticketsByNumber[ticketNumber] : null,
                DepartmentId = dept.Id, StaffId = assignee.Id, CreatedAt = created,
                DueDate = due is { } d ? At(d, 18, 0) : null,
                ClosedAt = closed ? created.AddDays(2) : null,
                Thread = new Thread { CreatedAt = created },
            };
            db.TaskItems.Add(task);
            return task;
        }

        var t2041 = MakeTask("T-2041", "Bordro parametre güncellemesi", "R716555", depBordro, uakin, On(2026, 8, 7, 14, 20), new DateOnly(2026, 8, 15));
        MakeTask("T-2040", "SGK bildirge kontrolü", "R716540", depBordro, uakin, On(2026, 8, 10, 9, 0), new DateOnly(2026, 8, 17));
        MakeTask("T-2039", "Vardiya şablonu içe aktarma", "R716512", depDestek, uakin, On(2026, 8, 9, 10, 30), new DateOnly(2026, 8, 16));
        MakeTask("T-2038", "İşe giriş bildirge takibi", "R716528", depDestek, mcetin, On(2026, 8, 12, 9, 15), new DateOnly(2026, 8, 19));
        MakeTask("T-2036", "Test ortamı yenileme", "R716528", depDestek, uakin, On(2026, 8, 8, 11, 0), new DateOnly(2026, 8, 11));
        MakeTask("T-2035", "İzin devri düzeltmesi", "R716455", depDanismanlik, uakin, On(2026, 8, 7, 9, 30), new DateOnly(2026, 8, 10));
        MakeTask("T-2033", "Yıl sonu rapor taslağı", null, depBordro, uakin, On(2026, 8, 5, 15, 0), new DateOnly(2026, 8, 20));
        MakeTask("T-2032", "Ağustos bordro ön kontrolü", null, depBordro, uakin, On(2026, 8, 4, 10, 0), new DateOnly(2026, 8, 8), closed: true);
        MakeTask("T-2031", "Konecta SLA raporu", null, depDanismanlik, mcetin, On(2026, 8, 3, 13, 30), new DateOnly(2026, 8, 6), closed: true);

        db.ThreadEntries.AddRange(
            new ThreadEntry
            {
                Thread = t2041.Thread!, Type = ThreadEntryType.Response, StaffId = uakin.Id,
                Poster = "Ümit Yaşar Akın", CreatedAt = On(2026, 8, 7, 14, 22),
                Body = "Parametre yedeği alındı; düzeltme test ortamında doğrulanacak, ardından üretime taşınacak.",
            },
            new ThreadEntry
            {
                Thread = t2041.Thread!, Type = ThreadEntryType.Note, StaffId = mcetin.Id,
                Poster = "Merve Çetin", CreatedAt = On(2026, 8, 10, 9, 47),
                Body = "Kullanıcı tarafı efor onayını verdi (6 saat); üretim planını buna göre güncelleyelim.",
            });

        // ----- Settings (consumed by the engine from S4/S7 on) ------------------------
        (string Ns, string Key, string Value)[] settings =
        [
            ("core", "helpdesk_title", "RapidsolDestek"),
            ("core", "default_dept_id", depDestek.Id.ToString()),
            ("core", "default_priority", "normal"),
            ("core", "default_sla_id", slaStandart.Id.ToString()),
            ("tickets", "number_format", "R######"),
            ("tickets", "sequence_id", seqTickets.Id.ToString()),
            ("tickets", "default_status", "open"),
            ("tasks", "number_format", "T-####"),
            ("tasks", "sequence_id", seqTasks.Id.ToString()),
            ("effort", "enabled", "true"),
            ("effort", "block_work_until_approved", "false"),
            ("effort", "mandatory_reject_note", "true"),
            ("effort", "reminder_days", "3"),
            ("effort", "auto_approve_threshold_hours", "0"),
            ("effort", "revision_limit", "3"),
            // Maintenance mode (portal/offline.html); default off.
            // TODO(S7): edited by admin/settings-system.html "maintenance mode".
            ("system", "offline", "false"),
        ];
        db.Settings.AddRange(settings.Select(s => new Setting
        {
            Namespace = s.Ns, Key = s.Key, Value = s.Value, UpdatedAt = DateTimeOffset.UtcNow,
        }));

        await db.SaveChangesAsync();
    }

    private static string Fold(string s) => s.ToLowerInvariant()
        .Replace("ç", "c").Replace("ğ", "g").Replace("ı", "i")
        .Replace("ö", "o").Replace("ş", "s").Replace("ü", "u")
        .Replace(" ", ".");
}
