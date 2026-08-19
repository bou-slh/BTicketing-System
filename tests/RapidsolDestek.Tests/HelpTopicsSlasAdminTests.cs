using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/helptopics.html + helptopic-edit.html + slas.html: the B1 tree list over
/// real HelpTopic rows, the editor round-tripping the FULL routing cascade the
/// TicketService consumes (incl. the B3 number-format radio honored at ticket
/// creation and the per-topic random sequence), the forms tab attach/detach with
/// per-field disable consumed by the portal open page (B4), and the SLA page's B2
/// per-row dialogs whose grace/transient switches feed the engine (EstimatedDueDate
/// at create, transient replacement on transfer).
/// </summary>
[Collection("Postgres")]
public class HelpTopicsSlasAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helptopics.html (B1) ---------------------------------------------------------

    [Fact]
    public async Task TopicsList_RendersTree_AndSearchFlattens()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/helptopics"));

        // Child row: indented └ prefix with the full "Üst / Alt" label (mockup DOM).
        Assert.Contains("Bordro / Yol Ücreti", html);
        Assert.Contains("<span class=\"rd-muted\">└</span>", html);
        Assert.Contains("Danışmanlık", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/helptopics?q=" + Uri.EscapeDataString("İzin")));
        Assert.Contains("İzin", filtered);
        Assert.DoesNotContain("Vardiya", filtered);
    }

    [Fact]
    public async Task TopicEdit_PrefillsRoutingFromSeedCanon()
    {
        int topicId, slaVipId, uakinId, thanksPageId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var topic = await s.Db.HelpTopics.SingleAsync(t => t.Name == "Yol Ücreti");
            topicId = topic.Id;
            slaVipId = topic.SlaId!.Value;
            uakinId = topic.StaffId!.Value;
            thanksPageId = topic.SitePageId!.Value;
        }

        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/helptopic-edit?id={topicId}"));

        // The routing selects carry the persisted state (B2/B3 prefill).
        Assert.Contains("Bordro / Yol Ücreti", html); // crumb h1
        Assert.Matches(new Regex($"<option value=\"{slaVipId}\" selected[^>]*>VIP</option>"), html);
        Assert.Matches(new Regex($"<option value=\"s:{uakinId}\" selected"), html);
        Assert.Matches(new Regex($"<option value=\"{thanksPageId}\" selected[^>]*>Talep Alındı</option>"), html);

        // Number-format radio gating (B3): custom checked, format input prefilled.
        var customRadio = Regex.Match(html, "<input[^>]*name=\"numMode\" value=\"custom\"[^>]*>").Value;
        Assert.Contains("checked", customRadio);
        Assert.Contains("data-gates=\"#hte-numfmt-input\"", customRadio);
        Assert.Contains("value=\"BRD-######\"", html);

        // Forms tab: the seeded attached form renders with its fields enabled.
        Assert.Contains("Ek form: Bordro Ek Bilgileri", html);
        Assert.Contains("personel_no", html);
    }

    [Fact]
    public async Task TopicCreate_ThenUpdate_RoundTripsRoutingFields()
    {
        int deptId, statusId, priorityId, slaId, teamId, seqId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            deptId = (await s.Db.Departments.SingleAsync(d => d.Name == "Danışmanlık")).Id;
            statusId = (await s.Db.TicketStatuses.SingleAsync(x => x.Key == "wait")).Id;
            priorityId = (await s.Db.TicketPriorities.SingleAsync(p => p.Key == "high")).Id;
            slaId = (await s.Db.SlaPlans.SingleAsync(x => x.Name == "Kritik")).Id;
            teamId = (await s.Db.Teams.SingleAsync(x => x.Name == "Destek Ekibi")).Id;
            seqId = (await s.Db.Sequences.SingleAsync(x => x.Name == "Genel Talepler")).Id;
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/helptopic-edit");
        var name = $"S7 Konu {Guid.NewGuid():N}"[..20];

        // Create with the full routing set; number format custom over a real sequence.
        var created = await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("name", name), ("status", "active"), ("isPublic", "false"), ("notes", "S7 konu notu"),
            ("deptId", deptId.ToString()), ("statusId", statusId.ToString()),
            ("priorityId", priorityId.ToString()), ("slaId", slaId.ToString()),
            ("assign", $"t:{teamId}"),
            ("numMode", "custom"), ("numberFormat", "S7K-####"), ("sequence", seqId.ToString()),
            ("noAutoresp", "true"));
        Assert.Equal("/admin/helptopics", PathOf(created));

        int topicId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var topic = await s.Db.HelpTopics.SingleAsync(t => t.Name == name);
            topicId = topic.Id;
            Assert.True(topic.IsActive);
            Assert.False(topic.IsPublic);
            Assert.Equal("S7 konu notu", topic.Notes);
            Assert.Equal(deptId, topic.DepartmentId);
            Assert.Equal(statusId, topic.StatusId);
            Assert.Equal(priorityId, topic.PriorityId);
            Assert.Equal(slaId, topic.SlaId);
            Assert.Null(topic.StaffId);
            Assert.Equal(teamId, topic.TeamId);
            Assert.Equal("S7K-####", topic.NumberFormat);
            Assert.Equal(seqId, topic.SequenceId);
            Assert.False(topic.UseRandomNumbers);
            Assert.True(topic.NoAutoResponse);
        }

        // Update: archive, system number format, random sequence, clear routing.
        var updated = await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("id", topicId.ToString()), ("name", name), ("status", "archived"), ("isPublic", "true"),
            ("assign", ""), ("numMode", "system"), ("numberFormat", ""), ("sequence", "random"));
        Assert.Equal("/admin/helptopics", PathOf(updated));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var topic = await s.Db.HelpTopics.SingleAsync(t => t.Id == topicId);
            Assert.False(topic.IsActive);
            Assert.True(topic.IsArchived);
            Assert.Null(topic.DepartmentId);
            Assert.Null(topic.TeamId);
            Assert.Null(topic.NumberFormat); // system radio wins over any input remnant
            Assert.Null(topic.SequenceId);
            Assert.True(topic.UseRandomNumbers);
        }
    }

    [Fact]
    public async Task TopicSave_ValidationRefusals_WriteNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/helptopic-edit");

        using var s = new ServiceScopeBundle(fixture);
        var before = await s.Db.HelpTopics.CountAsync();

        // Empty name refused.
        var noName = await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("name", "  "), ("status", "active"), ("isPublic", "true"), ("numMode", "system"), ("sequence", ""));
        Assert.Equal("/admin/helptopic-edit", PathOf(noName));

        // Custom format without a '#' digit slot refused (B3 server side).
        var badFormat = await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("name", $"S7 Hatalı {Guid.NewGuid():N}"[..20]), ("status", "active"), ("isPublic", "true"),
            ("numMode", "custom"), ("numberFormat", "SABIT"), ("sequence", ""));
        Assert.Equal("/admin/helptopic-edit", PathOf(badFormat));

        Assert.Equal(before, await s.Db.HelpTopics.CountAsync());

        // Parent cycle refused: Bordro cannot move under its own child Yol Ücreti.
        var bordro = await s.Db.HelpTopics.SingleAsync(t => t.Name == "Bordro");
        var child = await s.Db.HelpTopics.SingleAsync(t => t.Name == "Yol Ücreti");
        var cycle = await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("id", bordro.Id.ToString()), ("name", "Bordro"), ("status", "active"), ("isPublic", "true"),
            ("parentId", child.Id.ToString()), ("numMode", "system"), ("sequence", ""));
        Assert.Equal("/admin/helptopic-edit", PathOf(cycle));
        using var s2 = new ServiceScopeBundle(fixture);
        Assert.Null((await s2.Db.HelpTopics.SingleAsync(t => t.Id == bordro.Id)).ParentId);
    }

    [Fact]
    public async Task TopicNumberFormat_EditedFormat_UsedByTicketCreate()
    {
        // Admin edits the topic's number format over HTTP…
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/helptopic-edit");
        var name = $"S7 Numara {Guid.NewGuid():N}"[..20];
        await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("name", name), ("status", "active"), ("isPublic", "true"),
            ("numMode", "custom"), ("numberFormat", "S7N-#######"), ("sequence", ""));

        // …and a ticket created under the topic draws that format.
        using var s = new ServiceScopeBundle(fixture);
        var topic = await s.Db.HelpTopics.SingleAsync(t => t.Name == name);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Numara biçimi {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            HelpTopicId = topic.Id,
        }, owner);
        Assert.Matches(@"^S7N-\d{7}$", ticket.Number);

        // Flip the sequence to per-topic random (osTicket sequence_id 0): the format
        // still holds and the global counter stays untouched.
        long counterBefore;
        using (var s2 = new ServiceScopeBundle(fixture))
            counterBefore = (await s2.Db.Sequences.SingleAsync(x => x.Name == "Genel Talepler")).Next;
        await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("id", topic.Id.ToString()), ("name", name), ("status", "active"), ("isPublic", "true"),
            ("numMode", "custom"), ("numberFormat", "S7N-#######"), ("sequence", "random"));

        using var s3 = new ServiceScopeBundle(fixture);
        var random = await s3.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Rastgele numara {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            HelpTopicId = topic.Id,
        }, owner);
        Assert.Matches(@"^S7N-\d{7}$", random.Number);
        Assert.Equal(counterBefore, (await s3.Db.Sequences.SingleAsync(x => x.Name == "Genel Talepler")).Next);
    }

    // ---- helptopic-edit forms tab (B4) --------------------------------------------------

    [Fact]
    public async Task TopicFormsTab_AttachDetach_RoundTrips_AndOpenPageConsumesDisable()
    {
        int formId, keepFieldId, dropFieldId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Title == "Proje Talebi");
            formId = form.Id;
            keepFieldId = form.Fields.Single(f => f.Name == "proje_adi").Id;
            dropFieldId = form.Fields.Single(f => f.Name == "tahmini_sure").Id;
        }

        // Attach with only proje_adi enabled (tahmini_sure = per-topic disabled).
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/helptopic-edit");
        var name = $"S7 Form {Guid.NewGuid():N}"[..20];
        await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("name", name), ("status", "active"), ("isPublic", "true"),
            ("numMode", "system"), ("sequence", ""),
            ("formIds", formId.ToString()), ("enabledFields", keepFieldId.ToString()));

        int topicId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var topic = await s.Db.HelpTopics.Include(t => t.Forms)
                .SingleAsync(t => t.Name == name);
            topicId = topic.Id;
            var row = Assert.Single(topic.Forms);
            Assert.Equal(formId, row.FormDefinitionId);
            Assert.Equal(new[] { dropFieldId }, row.DisabledFieldIds().ToArray());
        }

        // Open-page consumption: the attached form's enabled field appears in the
        // portal topic-field output, the disabled one does not.
        var portal = await PortalClientAsync();
        var openHtml = WebUtility.HtmlDecode(await portal.GetStringAsync("/open"));
        Assert.Contains(name, openHtml);          // topic is selectable
        Assert.Contains("Proje Adı", openHtml);   // enabled field rendered
        Assert.DoesNotContain("Tahmini Süre", openHtml); // per-topic disabled field hidden

        // Detach: a save without formIds clears the attachment.
        await PostFormAsync(client, "/admin/helptopic-edit", token,
            ("id", topicId.ToString()), ("name", name), ("status", "active"), ("isPublic", "true"),
            ("numMode", "system"), ("sequence", ""));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.Empty((await s.Db.HelpTopics.Include(t => t.Forms)
                .SingleAsync(t => t.Id == topicId)).Forms);
        }
    }

    // ---- topic delete / bulk guards ------------------------------------------------------

    [Fact]
    public async Task TopicDelete_GuardsTicketsAndChildren_DeletesUnused()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/helptopics");

        // A topic referenced by tickets is undeletable (own rows — seed canon stays).
        int referencedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var referenced = new HelpTopic { Name = $"S7 Ref {Guid.NewGuid():N}"[..20], IsPublic = false };
            s.Db.HelpTopics.Add(referenced);
            await s.Db.SaveChangesAsync();
            referencedId = referenced.Id;
            var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
            await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Konu silme koruması {Guid.NewGuid():N}",
                Body = "<p>içerik</p>",
                HelpTopicId = referencedId,
            }, owner);
        }
        var refused = await PostFormAsync(client, "/admin/helptopic-edit/delete", token,
            ("id", referencedId.ToString()));
        Assert.Equal("/admin/helptopic-edit", PathOf(refused));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.NotNull(await s.Db.HelpTopics.SingleOrDefaultAsync(t => t.Id == referencedId));

        // Bulk: a parent with a child is skipped, the unused row deletes.
        int parentId, unusedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var parent = new HelpTopic { Name = $"S7 Üst {Guid.NewGuid():N}"[..20], IsPublic = false };
            s.Db.HelpTopics.Add(parent);
            await s.Db.SaveChangesAsync();
            var child = new HelpTopic { Name = $"S7 Alt {Guid.NewGuid():N}"[..20], IsPublic = false, ParentId = parent.Id };
            var unused = new HelpTopic { Name = $"S7 Boş {Guid.NewGuid():N}"[..20], IsPublic = false };
            s.Db.HelpTopics.AddRange(child, unused);
            await s.Db.SaveChangesAsync();
            (parentId, unusedId) = (parent.Id, unused.Id);
        }
        await PostFormAsync(client, "/admin/helptopics/bulk", token,
            ("act", "delete"), ("ids", parentId.ToString()), ("ids", unusedId.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.NotNull(await s.Db.HelpTopics.SingleOrDefaultAsync(t => t.Id == parentId));
            Assert.Null(await s.Db.HelpTopics.SingleOrDefaultAsync(t => t.Id == unusedId));
        }
    }

    // ---- slas.html (B1/B2) ----------------------------------------------------------------

    [Fact]
    public async Task SlaList_PerRowDialogPrefilled_FromSeedCanon()
    {
        int slaId, scheduleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var vip = await s.Db.SlaPlans.SingleAsync(x => x.Name == "VIP");
            (slaId, scheduleId) = (vip.Id, vip.ScheduleId!.Value);
        }

        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/slas?q=VIP"));

        // Row opens ITS dialog; the dialog carries the persisted state (B2).
        Assert.Contains($"data-dialog-open=\"dlg-sla-{slaId}\"", html);
        Assert.Contains("value=\"8\"", html); // grace prefilled
        Assert.Contains("Sözleşmeli VIP kullanıcılar için hızlandırılmış plan.", html);
        Assert.Matches(new Regex($"<option value=\"{scheduleId}\" selected"), html);

        // Advanced switches: transient unchecked, overdue alerts checked (inverted flag).
        var transientBox = Regex.Match(html, "<input[^>]*name=\"transient\"[^>]*>").Value;
        Assert.DoesNotContain("checked", transientBox);
        var alertsBox = Regex.Match(html, "<input[^>]*name=\"alerts\"[^>]*>").Value;
        Assert.Contains("checked", alertsBox);
    }

    [Fact]
    public async Task SlaCreateUpdate_RoundTrips_AndValidationWritesNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/slas");
        var name = $"S7 SLA {Guid.NewGuid():N}"[..20];

        int scheduleId;
        using (var s = new ServiceScopeBundle(fixture))
            scheduleId = (await s.Db.Schedules.SingleAsync(x => x.Name == "7/24")).Id;

        // Create: transient on, alerts OFF (persists inverted), schedule 7/24.
        var created = await PostFormAsync(client, "/admin/slas/create", token,
            ("name", name), ("grace", "12"), ("active", "true"),
            ("scheduleId", scheduleId.ToString()), ("transient", "true"), ("notes", "S7 sla notu"));
        Assert.Equal("/admin/slas", PathOf(created));

        int slaId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var plan = await s.Db.SlaPlans.SingleAsync(x => x.Name == name);
            slaId = plan.Id;
            Assert.Equal(12, plan.GracePeriodHours);
            Assert.True(plan.IsActive);
            Assert.Equal(scheduleId, plan.ScheduleId);
            Assert.True(plan.IsTransient);
            Assert.True(plan.DisableOverdueAlerts); // alerts switch off → inverted flag on
            Assert.Equal("S7 sla notu", plan.Notes);
        }

        // Update flips everything back.
        await PostFormAsync(client, "/admin/slas/update", token,
            ("id", slaId.ToString()), ("name", name), ("grace", "24"), ("active", "false"),
            ("scheduleId", ""), ("transient", "false"), ("alerts", "true"), ("notes", ""));
        using (var s = new ServiceScopeBundle(fixture))
        {
            var plan = await s.Db.SlaPlans.SingleAsync(x => x.Id == slaId);
            Assert.Equal(24, plan.GracePeriodHours);
            Assert.False(plan.IsActive);
            Assert.Null(plan.ScheduleId);
            Assert.False(plan.IsTransient);
            Assert.False(plan.DisableOverdueAlerts);
            Assert.Null(plan.Notes);
        }

        // Validation: grace below the mockup's min=1 writes nothing.
        var refused = await PostFormAsync(client, "/admin/slas/update", token,
            ("id", slaId.ToString()), ("name", name), ("grace", "0"), ("active", "true"), ("alerts", "true"));
        Assert.Equal("/admin/slas", PathOf(refused));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.Equal(24, (await s.Db.SlaPlans.SingleAsync(x => x.Id == slaId)).GracePeriodHours);
    }

    [Fact]
    public async Task SlaGrace_ConsumedByTicketCreate_AsEstimatedDue()
    {
        // Admin creates a plan with a 6-hour grace over HTTP…
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/slas");
        var name = $"S7 Grace {Guid.NewGuid():N}"[..20];
        await PostFormAsync(client, "/admin/slas/create", token,
            ("name", name), ("grace", "6"), ("active", "true"), ("alerts", "true"));

        // …and a ticket under the plan is due grace hours after creation.
        using var s = new ServiceScopeBundle(fixture);
        var plan = await s.Db.SlaPlans.SingleAsync(x => x.Name == name);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Grace tüketimi {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            SlaId = plan.Id,
        }, owner);

        Assert.NotNull(ticket.EstimatedDueDate);
        var expected = ticket.CreatedAt.AddHours(6);
        Assert.True((ticket.EstimatedDueDate!.Value - expected).Duration() < TimeSpan.FromMinutes(2),
            $"estimated due {ticket.EstimatedDueDate} not ~6h after {ticket.CreatedAt}");
    }

    [Fact]
    public async Task TransientSla_ReplacedByDepartmentSla_OnTransfer()
    {
        // A transient plan (admin/slas advanced switch) rides a ticket in Destek…
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/slas");
        var name = $"S7 Geçici {Guid.NewGuid():N}"[..20];
        await PostFormAsync(client, "/admin/slas/create", token,
            ("name", name), ("grace", "2"), ("active", "true"), ("transient", "true"), ("alerts", "true"));

        using var s = new ServiceScopeBundle(fixture);
        var plan = await s.Db.SlaPlans.SingleAsync(x => x.Name == name);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var tickets = s.Get<ITicketService>();
        var ticket = await tickets.CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Geçici SLA {Guid.NewGuid():N}",
            Body = "<p>içerik</p>",
            SlaId = plan.Id,
        }, owner);
        Assert.Equal(plan.Id, ticket.SlaId);

        // …until a transfer to Bordro (dept SLA: Standart) replaces it and the due
        // date follows the permanent plan.
        var bordro = await s.Db.Departments.SingleAsync(d => d.Name == "Bordro");
        var actor = await TestActors.StaffAsync(s.Db, "uakin");
        await tickets.TransferAsync(ticket.Id, bordro.Id, actor);

        var after = await s.Db.Tickets.AsNoTracking().SingleAsync(x => x.Id == ticket.Id);
        Assert.Equal(bordro.SlaId, after.SlaId);
        var standart = await s.Db.SlaPlans.SingleAsync(x => x.Id == bordro.SlaId);
        Assert.NotNull(after.EstimatedDueDate);
        Assert.True((after.EstimatedDueDate!.Value - DateTimeOffset.UtcNow.AddHours(standart.GracePeriodHours))
            .Duration() < TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task SlaBulk_DeleteGuardsReferencedPlans_DeletesUnused()
    {
        int referencedId, unusedId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var referenced = new SlaPlan { Name = $"S7 SLA R {Guid.NewGuid():N}"[..20], GracePeriodHours = 5 };
            var unused = new SlaPlan { Name = $"S7 SLA U {Guid.NewGuid():N}"[..20], GracePeriodHours = 5 };
            s.Db.SlaPlans.AddRange(referenced, unused);
            await s.Db.SaveChangesAsync();
            // Help-topic routing references the plan (the lightest real reference).
            s.Db.HelpTopics.Add(new HelpTopic
            {
                Name = $"S7 SLA Konu {Guid.NewGuid():N}"[..20],
                IsPublic = false,
                SlaId = referenced.Id,
            });
            await s.Db.SaveChangesAsync();
            (referencedId, unusedId) = (referenced.Id, unused.Id);
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/slas");
        var done = await PostFormAsync(client, "/admin/slas/bulk", token,
            ("act", "delete"), ("ids", referencedId.ToString()), ("ids", unusedId.ToString()));
        Assert.Equal("/admin/slas", PathOf(done));

        using var s2 = new ServiceScopeBundle(fixture);
        Assert.NotNull(await s2.Db.SlaPlans.SingleOrDefaultAsync(x => x.Id == referencedId));
        Assert.Null(await s2.Db.SlaPlans.SingleOrDefaultAsync(x => x.Id == unusedId));

        // Enable/disable round-trip through the same endpoint.
        await PostFormAsync(client, "/admin/slas/bulk", token,
            ("act", "disable"), ("ids", referencedId.ToString()));
        using (var s3 = new ServiceScopeBundle(fixture))
            Assert.False((await s3.Db.SlaPlans.SingleAsync(x => x.Id == referencedId)).IsActive);
        await PostFormAsync(client, "/admin/slas/bulk", token,
            ("act", "enable"), ("ids", referencedId.ToString()));
        using (var s4 = new ServiceScopeBundle(fixture))
            Assert.True((await s4.Db.SlaPlans.SingleAsync(x => x.Id == referencedId)).IsActive);
    }

    // ---- helpers (RolesTeamsAdminTests twins) ----------------------------------------------

    /// <summary>Fresh identity admin signed in over HTTP with mandatory 2FA, plus a
    /// matching Staff domain row (the endpoints resolve the acting staff for audit).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7TOPICSKEY3456ABC";
        var username = $"s7ht{Guid.NewGuid():N}"[..14];
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = new StaffUser
            {
                UserName = username,
                Email = $"{username}@rapidsol.com.tr",
                EmailConfirmed = true,
                FullName = $"S7 {username}",
            };
            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, "Agent");
            await users.AddToRoleAsync(user, "Admin");
            await users.AddClaimAsync(user, new System.Security.Claims.Claim("FullName", user.FullName!));
            await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", totpKey);
            await users.SetTwoFactorEnabledAsync(user, true);

            var db = scope.ServiceProvider.GetRequiredService<RapidsolDestek.Infrastructure.AppDbContext>();
            var deptId = await db.Departments.Select(d => d.Id).OrderBy(id => id).FirstAsync();
            var roleId = await db.Roles.Select(r => r.Id).OrderBy(id => id).FirstAsync();
            db.Staff.Add(new Staff
            {
                IdentityUserId = user.Id,
                Username = username,
                FirstName = "S7",
                LastName = username,
                Email = user.Email,
                DepartmentId = deptId,
                RoleId = roleId,
                IsAdmin = true,
                IsVisible = false,
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", PathOf(landed));
        return client;
    }

    /// <summary>Seeded portal customer over HTTP (portal /login form — no 2FA).</summary>
    private async Task<HttpClient> PortalClientAsync()
    {
        var client = fixture.Factory.CreateClient();
        var (token, _) = await GetWithTokenAsync(client, "/login");
        var landed = await PostFormAsync(client, "/login", token,
            ("User", "bourla.salehi@ulasim.com.tr"), ("Password", Password));
        Assert.Equal("/tickets", PathOf(landed));
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (ids/formIds/enabledFields checkbox groups).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    // ---- totp helper (AdminAuthTests twin) --------------------------------------------

    private static string ComputeTotp(string base32Key)
    {
        var keyBytes = FromBase32(base32Key.Replace(" ", "").ToUpperInvariant());
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var timestepBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestepBytes);
        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(timestepBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
