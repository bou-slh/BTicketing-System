using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 2: autoresponses + alert fan-out through the REAL pipeline (domain
/// events → Ticket/TaskMailHandler → renderer → queue → inline job → capturing
/// transport/fallback). Seeded canon roster: dept Destek (manager mcetin,
/// members dkaya/kyilmaz/adogan, account destek@), dept Bordro (manager uakin),
/// owner Bourla Salehi (org Ulaşım → account manager uakin).
/// </summary>
[Collection("Postgres")]
public class AlertFanoutTests(PostgresFixture fixture)
{
    private async Task<(int Id, string Number, int ThreadId, int UserId, int DeptId)> CreateTicketAsync(
        ServiceScopeBundle s, ActorContext? actor = null)
    {
        var owner = actor ?? await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = (await TestActors.UserAsync(s.Db, "Bourla Salehi")).Id!.Value,
            Subject = $"Fanout testi {Guid.NewGuid():N}",
            Body = "<p>İçerik</p>",
        }, owner);
        return (ticket.Id, ticket.Number, ticket.ThreadId, ticket.UserId, ticket.DepartmentId);
    }

    private IReadOnlyList<CapturedEmail> Sent => fixture.Factory.Emails.Sent;

    private void Clear()
    {
        fixture.Factory.Emails.Clear();
        fixture.Factory.Transport.Clear();
    }

    /// <summary>
    /// Pins the masters/checkboxes a test depends on (restored on dispose). The
    /// settings-tickets/-tasks page tests legitimately persist unposted checkboxes
    /// as False (form semantics), so full-suite runs cannot rely on virgin defaults.
    /// </summary>
    private async Task<IAsyncDisposable> PinAsync(params (string Ns, string Key, string Value)[] pins)
    {
        var overrides = new List<SettingOverride>();
        foreach (var (ns, key, value) in pins)
            overrides.Add(await SettingOverride.SetAsync(fixture, ns, key, value));
        return new PinSet(overrides);
    }

    private sealed class PinSet(List<SettingOverride> overrides) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            for (var i = overrides.Count - 1; i >= 0; i--)
                await overrides[i].DisposeAsync();
        }
    }

    // ---- new ticket -------------------------------------------------------------------

    [Fact]
    public async Task UserCreate_SendsAutoresponse_AndAlertFanout()
    {
        await using var pins = await PinAsync(
            ("email", "admin_email", "admin@rapidsol.com.tr"),
            ("autoresp", "new_ticket", "true"),
            ("alerts", "new_ticket", "true"), ("alerts", "new_ticket_admin", "true"),
            ("alerts", "new_ticket_dept_manager", "true"), ("alerts", "new_ticket_account_manager", "true"),
            ("alerts", "new_ticket_dept_members", "false"));
        using var s = new ServiceScopeBundle(fixture);
        Clear();
        var t = await CreateTicketAsync(s);

        // Owner confirmation (ticket.autoresp) from the department account.
        var autoresp = Sent.Single(m => m.Subject == "Yeni Talep Otomatik Yanıtı");
        Assert.Equal("bourla.salehi@ulasim.com.tr", autoresp.To);
        Assert.Contains(t.Number, autoresp.HtmlBody);
        Assert.Equal("destek@rapidsol.com.tr",
            fixture.Factory.Transport.Sent.Single(x => x.Subject == "Yeni Talep Otomatik Yanıtı").FromAddress);

        // Alerts (ticket.alert): dept manager + account manager + admin_email;
        // dept members box is OFF by default → no member copies.
        var alerts = Sent.Where(m => m.Subject == "Yeni Talep Uyarısı").Select(m => m.To).ToList();
        Assert.Contains("merve.cetin@rapidsol.com.tr", alerts);   // Destek manager
        Assert.Contains("umit.akin@rapidsol.com.tr", alerts);     // Ulaşım account manager
        Assert.Contains("admin@rapidsol.com.tr", alerts);
        Assert.DoesNotContain("deniz.kaya@rapidsol.com.tr", alerts);
        Assert.Equal(alerts.Count, alerts.Distinct().Count());    // deduped
    }

    [Fact]
    public async Task UserCreate_SuppressionLayers_MasterOff_DeptFlagOff_TicketFlag()
    {
        await using var pins = await PinAsync(
            ("autoresp", "new_ticket", "true"),
            ("alerts", "new_ticket", "true"), ("alerts", "new_ticket_dept_manager", "true"));

        // Global master off → no confirmation, alerts unaffected.
        await using (await SettingOverride.SetAsync(fixture, "autoresp", "new_ticket", "false"))
        {
            using var s = new ServiceScopeBundle(fixture);
            Clear();
            await CreateTicketAsync(s);
            Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Talep Otomatik Yanıtı");
            Assert.Contains(Sent, m => m.Subject == "Yeni Talep Uyarısı");
        }

        // Department flag off (department-edit "kapat" switch) → no confirmation.
        await using (var db = fixture.CreateContext())
        {
            var dept = await db.Departments.SingleAsync(d => d.Name == "Destek");
            dept.TicketAutoResponse = false;
            await db.SaveChangesAsync();
        }
        try
        {
            using var s = new ServiceScopeBundle(fixture);
            Clear();
            await CreateTicketAsync(s);
            Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Talep Otomatik Yanıtı");
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var dept = await db.Departments.SingleAsync(d => d.Name == "Destek");
            dept.TicketAutoResponse = true;
            await db.SaveChangesAsync();
        }

        // Ticket.AutoResponseDisabled (filter noautoresp action) → no confirmation.
        {
            using var s = new ServiceScopeBundle(fixture);
            var t = await CreateTicketAsync(s);
            var ticket = await s.Db.Tickets.SingleAsync(x => x.Id == t.Id);
            ticket.AutoResponseDisabled = true;
            await s.Db.SaveChangesAsync();
            Clear();
            await s.Get<TicketMailHandler>().HandleAsync(new TicketCreated(t.Id, t.Number, t.UserId, t.DeptId));
            Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Talep Otomatik Yanıtı");
            Assert.Contains(Sent, m => m.Subject == "Yeni Talep Uyarısı");
        }
    }

    [Fact]
    public async Task AgentCreate_NotifyChoice_All_User_None()
    {
        await using var pins = await PinAsync(
            ("autoresp", "agent_new_ticket", "true"),
            ("alerts", "new_ticket", "true"), ("alerts", "new_ticket_dept_manager", "true"),
            ("alerts", "new_ticket_account_manager", "true"));
        using var s = new ServiceScopeBundle(fixture);
        var t = await CreateTicketAsync(s);
        var uakin = await s.Db.Staff.SingleAsync(x => x.Username == "uakin");
        var handler = s.Get<TicketMailHandler>();

        // all (default): agent-opened notice to the owner + staff alerts — and the
        // acting agent never alerts themselves (uakin is the account manager here).
        Clear();
        await handler.HandleAsync(new TicketCreated(t.Id, t.Number, t.UserId, t.DeptId, ActorStaffId: uakin.Id));
        Assert.Equal("bourla.salehi@ulasim.com.tr", Sent.Single(m => m.Subject == "Yeni Talep Bildirimi").To);
        var alerts = Sent.Where(m => m.Subject == "Yeni Talep Uyarısı").Select(m => m.To).ToList();
        Assert.Contains("merve.cetin@rapidsol.com.tr", alerts);
        Assert.DoesNotContain("umit.akin@rapidsol.com.tr", alerts); // skip-actor

        // user: notice only.
        Clear();
        await handler.HandleAsync(new TicketCreated(t.Id, t.Number, t.UserId, t.DeptId, uakin.Id, "user"));
        Assert.Single(Sent, m => m.Subject == "Yeni Talep Bildirimi");
        Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Talep Uyarısı");

        // none: fully silent.
        Clear();
        await handler.HandleAsync(new TicketCreated(t.Id, t.Number, t.UserId, t.DeptId, uakin.Id, "none"));
        Assert.Empty(Sent);

        // Master off silences the agent notice too.
        await using (await SettingOverride.SetAsync(fixture, "autoresp", "agent_new_ticket", "false"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            Clear();
            await s2.Get<TicketMailHandler>().HandleAsync(new TicketCreated(t.Id, t.Number, t.UserId, t.DeptId, uakin.Id, "user"));
            Assert.Empty(Sent);
        }
    }

    // ---- new message ------------------------------------------------------------------

    [Fact]
    public async Task NewMessage_Confirmation_CollabCopies_And_StaffAlert()
    {
        await using var pins = await PinAsync(
            ("autoresp", "new_message", "true"), ("autoresp", "new_message_collab", "true"),
            ("alerts", "new_message", "true"), ("alerts", "new_message_last_respondent", "true"),
            ("alerts", "new_message_assigned", "true"),
            ("alerts", "new_message_dept_manager", "false"), ("alerts", "new_message_account_manager", "false"),
            ("tickets", "claim_on_response", "true"));
        await using (var db = fixture.CreateContext())
        {
            var dept = await db.Departments.SingleAsync(d => d.Name == "Destek");
            dept.MessageAutoResponse = true;
            await db.SaveChangesAsync();
        }
        try
        {
            using var s = new ServiceScopeBundle(fixture);
            var t = await CreateTicketAsync(s);
            var threads = s.Get<IThreadService>();
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            var ayse = await s.Db.Users.SingleAsync(u => u.Name == "Ayşe Yıldırım");

            // Staff response (auto-claims → dkaya assigned + last respondent) + a CC.
            await threads.PostAsync(t.ThreadId, ThreadEntryType.Response, "Kontrol ediyoruz", dkaya);
            await threads.AddCollaboratorAsync(t.ThreadId, ayse.Id, CollaboratorRole.Cc, dkaya);
            Clear();

            // Owner posts a new message.
            await threads.PostAsync(t.ThreadId, ThreadEntryType.Message, "Ek bilgi: personel no 4471", owner);

            // Receipt confirmation to the poster (message.autoresp).
            Assert.Equal("bourla.salehi@ulasim.com.tr", Sent.Single(m => m.Subject == "Yeni Mesaj Onayı").To);
            // Participant copy to the collaborator, never to the poster.
            Assert.Equal("ayse.yildirim@ulasim.com.tr", Sent.Single(m => m.Subject == "Yeni Aktivite Bildirimi").To);
            // Staff alert: last respondent == assigned == dkaya → ONE deduped copy;
            // manager/account-manager boxes are off by default.
            Assert.Equal("deniz.kaya@rapidsol.com.tr", Sent.Single(m => m.Subject == "Yeni Mesaj Uyarısı").To);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            var dept = await db.Departments.SingleAsync(d => d.Name == "Destek");
            dept.MessageAutoResponse = false;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FirstMessage_OfCreate_RaisesNoNewMessageTraffic()
    {
        await using var pins = await PinAsync(
            ("autoresp", "new_message", "true"), ("alerts", "new_message", "true"));
        using var s = new ServiceScopeBundle(fixture);
        Clear();
        await CreateTicketAsync(s);
        Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Mesaj Onayı");
        Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Mesaj Uyarısı");
    }

    // ---- overlimit --------------------------------------------------------------------

    [Fact]
    public async Task Overlimit_MailsTheRefusedUser_MasterGates()
    {
        await using var pins = await PinAsync(("autoresp", "overlimit", "true"));
        using var s = new ServiceScopeBundle(fixture);
        var bourla = await s.Db.Users.SingleAsync(u => u.Name == "Bourla Salehi");
        Clear();
        await s.Get<TicketMailHandler>().SendOverlimitNoticeAsync(bourla.Id, null);
        Assert.Equal("bourla.salehi@ulasim.com.tr", Sent.Single(m => m.Subject == "Limit Aşımı Bildirimi").To);

        await using (await SettingOverride.SetAsync(fixture, "autoresp", "overlimit", "false"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            Clear();
            await s2.Get<TicketMailHandler>().SendOverlimitNoticeAsync(bourla.Id, null);
            Assert.Empty(Sent);
        }
    }

    // ---- assignment / transfer / overdue ----------------------------------------------

    [Fact]
    public async Task Assignment_AlertsAssignee_SelfClaimSilent_SuppressFlagHonored()
    {
        await using var pins = await PinAsync(
            ("alerts", "assignment", "true"), ("alerts", "assignment_assigned", "true"));
        using var s = new ServiceScopeBundle(fixture);
        var tickets = s.Get<ITicketService>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var kyilmaz = await TestActors.StaffAsync(s.Db, "kyilmaz");
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;

        var t = await CreateTicketAsync(s);
        Clear();
        await tickets.AssignAsync(t.Id, dkayaId, null, uakin);
        Assert.Equal("deniz.kaya@rapidsol.com.tr", Sent.Single(m => m.Subject == "Atama Uyarısı").To);

        // Self-claim: the actor is the assignee → nobody is alerted.
        var t2 = await CreateTicketAsync(s);
        Clear();
        await tickets.ClaimAsync(t2.Id, kyilmaz);
        Assert.DoesNotContain(Sent, m => m.Subject == "Atama Uyarısı");

        // suppressAlert (ticket-open notify != all) mutes the assignment alert.
        var t3 = await CreateTicketAsync(s);
        Clear();
        await tickets.AssignAsync(t3.Id, dkayaId, null, uakin, suppressAlert: true);
        Assert.DoesNotContain(Sent, m => m.Subject == "Atama Uyarısı");
    }

    [Fact]
    public async Task TeamAssignment_LeadByDefault_MembersWhenBoxOn_FlagsRespected()
    {
        await using var pins = await PinAsync(
            ("alerts", "assignment", "true"), ("alerts", "assignment_team_lead", "true"),
            ("alerts", "assignment_team_members", "false"));
        using var s = new ServiceScopeBundle(fixture);
        var tickets = s.Get<ITicketService>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var teamDestek = await s.Db.Teams.SingleAsync(x => x.Name == "Destek Ekibi");
        var teamBordro = await s.Db.Teams.SingleAsync(x => x.Name == "Bordro Ekibi");

        // Default boxes: team_members OFF, team_lead ON → only the lead (dkaya).
        var t = await CreateTicketAsync(s);
        Clear();
        await tickets.AssignAsync(t.Id, null, teamDestek.Id, uakin);
        Assert.Equal("deniz.kaya@rapidsol.com.tr", Sent.Single(m => m.Subject == "Atama Uyarısı").To);

        // Members box on → alert-enabled, available members only: skip the actor
        // (uakin is a member), the alerts-off row (dkaya) and the vacationing
        // member (saydin) → only mcetin remains.
        await using (await SettingOverride.SetAsync(fixture, "alerts", "assignment_team_members", "true"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            var t2 = await CreateTicketAsync(s2);
            Clear();
            await s2.Get<ITicketService>().AssignAsync(t2.Id, null, teamBordro.Id, uakin);
            var alerts = Sent.Where(m => m.Subject == "Atama Uyarısı").Select(m => m.To).ToList();
            Assert.Equal(["merve.cetin@rapidsol.com.tr"], alerts);
        }
    }

    [Fact]
    public async Task Transfer_AlertsAssignee_And_NewDeptRecipients()
    {
        await using var pins = await PinAsync(
            ("alerts", "transfer", "true"), ("alerts", "transfer_assigned", "true"),
            ("alerts", "transfer_dept_manager", "true"), ("alerts", "transfer_dept_members", "false"),
            ("alerts", "assignment", "false"));
        using var s = new ServiceScopeBundle(fixture);
        var tickets = s.Get<ITicketService>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var bordroId = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
        var destekId = (await s.Db.Departments.SingleAsync(d => d.Name == "Destek")).Id;
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;

        // Assigned ticket: the assignee is alerted; the new dept's manager is the
        // actor (uakin manages Bordro) → skip-actor leaves exactly one mail.
        var t = await CreateTicketAsync(s);
        await tickets.AssignAsync(t.Id, dkayaId, null, uakin);
        Clear();
        await tickets.TransferAsync(t.Id, bordroId, uakin);
        Assert.Equal(["deniz.kaya@rapidsol.com.tr"],
            Sent.Where(m => m.Subject == "Aktarım Uyarısı").Select(m => m.To).ToList());

        // Unassigned ticket + members box on: the RECEIVING department expands
        // (AlertGroup All = primary members; manager rides his own checkbox).
        await using (await SettingOverride.SetAsync(fixture, "alerts", "transfer_dept_members", "true"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            var t2 = await CreateTicketAsync(s2);
            await s2.Get<ITicketService>().TransferAsync(t2.Id, bordroId, uakin);
            Clear();
            await s2.Get<ITicketService>().TransferAsync(t2.Id, destekId, uakin);
            // Containment, not equality: other suites legitimately add Destek staff.
            var alerts = Sent.Where(m => m.Subject == "Aktarım Uyarısı").Select(m => m.To).ToList();
            Assert.Contains("asli.dogan@rapidsol.com.tr", alerts);   // primary member
            Assert.Contains("deniz.kaya@rapidsol.com.tr", alerts);   // primary member
            Assert.Contains("kerem.yilmaz@rapidsol.com.tr", alerts); // primary member
            Assert.Contains("merve.cetin@rapidsol.com.tr", alerts);  // manager checkbox
            Assert.Equal(alerts.Count, alerts.Distinct().Count());   // deduped
        }
    }

    [Fact]
    public async Task Overdue_HandlerIsLive_SlaCanSilence()
    {
        await using var pins = await PinAsync(
            ("alerts", "overdue", "true"), ("alerts", "overdue_assigned", "true"),
            ("alerts", "overdue_dept_manager", "true"), ("alerts", "overdue_dept_members", "false"));
        using var s = new ServiceScopeBundle(fixture);
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;
        var handler = s.Get<TicketMailHandler>();

        var t = await CreateTicketAsync(s);
        await s.Get<ITicketService>().AssignAsync(t.Id, dkayaId, null, uakin);

        // Assigned + dept-manager box (default on) → assignee + Destek manager.
        Clear();
        await handler.HandleAsync(new TicketOverdue(t.Id));
        var alerts = Sent.Where(m => m.Subject == "Gecikme Uyarısı").Select(m => m.To).OrderBy(x => x).ToList();
        Assert.Equal(["deniz.kaya@rapidsol.com.tr", "merve.cetin@rapidsol.com.tr"], alerts);

        // The SLA plan's "no overdue alerts" flag silences the whole alert.
        var slaId = await s.Db.Tickets.Where(x => x.Id == t.Id).Select(x => x.SlaId).SingleAsync();
        Assert.NotNull(slaId); // seeded default SLA cascades on create
        await using (var db = fixture.CreateContext())
        {
            (await db.SlaPlans.SingleAsync(p => p.Id == slaId)).DisableOverdueAlerts = true;
            await db.SaveChangesAsync();
        }
        try
        {
            using var s2 = new ServiceScopeBundle(fixture);
            Clear();
            await s2.Get<TicketMailHandler>().HandleAsync(new TicketOverdue(t.Id));
            Assert.Empty(Sent);
        }
        finally
        {
            await using var db = fixture.CreateContext();
            (await db.SlaPlans.SingleAsync(p => p.Id == slaId)).DisableOverdueAlerts = false;
            await db.SaveChangesAsync();
        }

        // Master off → silent.
        await using (await SettingOverride.SetAsync(fixture, "alerts", "overdue", "false"))
        {
            using var s3 = new ServiceScopeBundle(fixture);
            Clear();
            await s3.Get<TicketMailHandler>().HandleAsync(new TicketOverdue(t.Id));
            Assert.Empty(Sent);
        }
    }

    // ---- task alerts ------------------------------------------------------------------

    [Fact]
    public async Task TaskCreate_AlertsManagerAndAdmin_InitialAssigneeAlerted()
    {
        await using var pins = await PinAsync(
            ("email", "admin_email", "admin@rapidsol.com.tr"),
            ("alerts", "task_new", "true"), ("alerts", "task_new_admin", "true"),
            ("alerts", "task_new_dept_manager", "true"), ("alerts", "task_new_dept_members", "false"),
            ("alerts", "task_assignment", "true"), ("alerts", "task_assignment_assigned", "true"),
            ("alerts", "task_activity", "true"));
        using var s = new ServiceScopeBundle(fixture);
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var destekId = (await s.Db.Departments.SingleAsync(d => d.Name == "Destek")).Id;
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;

        // Unassigned task → task.alert to dept manager + admin (members box off).
        Clear();
        var task = await s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
        {
            Title = $"Fanout görevi {Guid.NewGuid():N}"[..30],
            DepartmentId = destekId,
            Description = "İlk açıklama",
        }, uakin);
        var alerts = Sent.Where(m => m.Subject == "Yeni Görev Uyarısı").ToList();
        Assert.Equal(["admin@rapidsol.com.tr", "merve.cetin@rapidsol.com.tr"],
            alerts.Select(m => m.To).OrderBy(x => x).ToList());
        // The catalog's %{ticket.number} pill fills from the TASK number.
        Assert.All(alerts, m => Assert.Contains(task.Number, m.HtmlBody));
        // The description is the thread's first entry — no activity alert for it.
        Assert.DoesNotContain(Sent, m => m.Subject == "Yeni Aktivite Uyarısı");

        // Task created WITH an assignee → additional task.assigned.alert.
        Clear();
        await s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
        {
            Title = $"Atanmış görev {Guid.NewGuid():N}"[..30],
            DepartmentId = destekId,
            StaffId = dkayaId,
        }, uakin);
        Assert.Equal("deniz.kaya@rapidsol.com.tr", Sent.Single(m => m.Subject == "Görev Atama Uyarısı").To);
    }

    [Fact]
    public async Task TaskActivity_Transfer_Overdue_Alerts()
    {
        await using var pins = await PinAsync(
            ("alerts", "task_activity", "true"), ("alerts", "task_activity_assigned", "true"),
            ("alerts", "task_activity_dept_manager", "false"),
            ("alerts", "task_transfer", "true"), ("alerts", "task_transfer_assigned", "true"),
            ("alerts", "task_transfer_dept_manager", "true"),
            ("alerts", "task_overdue", "true"), ("alerts", "task_overdue_assigned", "true"),
            ("alerts", "task_overdue_dept_manager", "true"), ("alerts", "task_overdue_dept_members", "false"),
            ("alerts", "task_new", "false"), ("alerts", "task_assignment", "false"));
        using var s = new ServiceScopeBundle(fixture);
        var tasks = s.Get<ITaskService>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var destekId = (await s.Db.Departments.SingleAsync(d => d.Name == "Destek")).Id;
        var bordroId = (await s.Db.Departments.SingleAsync(d => d.Name == "Bordro")).Id;
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;

        var task = await tasks.CreateAsync(new TaskCreateRequest
        {
            Title = $"Aktivite görevi {Guid.NewGuid():N}"[..30],
            DepartmentId = destekId,
            StaffId = dkayaId,
            Description = "Açıklama",
        }, uakin);

        // New activity (a note by uakin) → assigned agent; actor himself never.
        Clear();
        await s.Get<IThreadService>().PostAsync(task.ThreadId, ThreadEntryType.Note, "Ara not", uakin);
        Assert.Equal("deniz.kaya@rapidsol.com.tr", Sent.Single(m => m.Subject == "Yeni Aktivite Uyarısı").To);

        // Transfer Destek → Bordro by uakin: assignee alerted; the new dept's
        // manager IS the actor → skipped.
        Clear();
        await tasks.TransferAsync(task.Id, bordroId, uakin);
        Assert.Equal(["deniz.kaya@rapidsol.com.tr"],
            Sent.Where(m => m.Subject == "Görev Aktarım Uyarısı").Select(m => m.To).ToList());

        // Overdue (raised directly — the sweep is the next slice): assignee +
        // dept manager (now Bordro → uakin; no actor to skip).
        Clear();
        await s.Get<TaskMailHandler>().HandleAsync(new TaskOverdue(task.Id));
        Assert.Equal(["deniz.kaya@rapidsol.com.tr", "umit.akin@rapidsol.com.tr"],
            Sent.Where(m => m.Subject == "Görev Gecikme Uyarısı").Select(m => m.To).OrderBy(x => x).ToList());

        // Task master off → silent (independent of the ticket overdue master).
        await using (await SettingOverride.SetAsync(fixture, "alerts", "task_overdue", "false"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            Clear();
            await s2.Get<TaskMailHandler>().HandleAsync(new TaskOverdue(task.Id));
            Assert.Empty(Sent);
        }
    }

    // ---- effort response recipient checkboxes -----------------------------------------

    [Fact]
    public async Task EffortResponse_RecipientCheckboxes_JoinTheFanout()
    {
        await using var pins = await PinAsync(
            ("alerts", "effort_response", "true"), ("alerts", "effort_response_assigned", "true"),
            ("alerts", "effort_response_dept_manager", "false"),
            ("autoresp", "effort_proposal", "true"), ("alerts", "assignment", "false"));
        using var s = new ServiceScopeBundle(fixture);
        var tickets = s.Get<ITicketService>();
        var efforts = s.Get<IEffortProposalService>();
        var uakin = await TestActors.StaffAsync(s.Db, "uakin");
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var dkayaId = (await s.Db.Staff.SingleAsync(x => x.Username == "dkaya")).Id;

        // Proposer uakin, assignee dkaya: approve → response to the proposer PLUS
        // the assigned agent (box default on), deduped.
        var t = await CreateTicketAsync(s);
        await tickets.AssignAsync(t.Id, dkayaId, null, uakin);
        await efforts.ProposeAsync(t.Id, 4, "Kapsam", uakin);
        Clear();
        await efforts.ApproveAsync(t.Id, owner);
        Assert.Equal(["deniz.kaya@rapidsol.com.tr", "umit.akin@rapidsol.com.tr"],
            Sent.Where(m => m.Subject == "Efor Yanıtı Uyarısı").Select(m => m.To).OrderBy(x => x).ToList());

        // Dept-manager box on + assigned box off: proposer + Destek manager.
        await using (await SettingOverride.SetAsync(fixture, "alerts", "effort_response_assigned", "false"))
        await using (await SettingOverride.SetAsync(fixture, "alerts", "effort_response_dept_manager", "true"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            var t2 = await CreateTicketAsync(s2);
            await s2.Get<IEffortProposalService>().ProposeAsync(t2.Id, 2, null, uakin);
            Clear();
            await s2.Get<IEffortProposalService>().ApproveAsync(t2.Id, owner);
            Assert.Equal(["merve.cetin@rapidsol.com.tr", "umit.akin@rapidsol.com.tr"],
                Sent.Where(m => m.Subject == "Efor Yanıtı Uyarısı").Select(m => m.To).OrderBy(x => x).ToList());
        }
    }
}
