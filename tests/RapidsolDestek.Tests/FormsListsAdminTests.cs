using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Forms;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Admin.Controllers;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/forms.html + form-edit.html + lists.html + list-edit.html: the form
/// designer over the existing FormDefinition/FormField model (field CRUD + ⚙
/// config write-back + options editor + drag order + soft-disable guard + live
/// preview) with the GATE PROOF that a designer-built form renders and persists
/// through BOTH open pages, and the list editor (item CRUD, sort-mode ordering,
/// honest import counts, properties JSON, system-list protection everywhere).
/// </summary>
[Collection("Postgres")]
public class FormsListsAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- forms.html (B1) -------------------------------------------------------------

    [Fact]
    public async Task FormsList_SplitsBuiltinAndCustom_AndSearches()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/forms"));

        // Built-in table carries the five seeded system forms, custom the two others.
        var builtin = html[html.IndexOf("tbl-builtin", StringComparison.Ordinal)
            ..html.IndexOf("tbl-forms-wrap", StringComparison.Ordinal)];
        Assert.Contains("Talep Detayları", builtin);
        Assert.Contains("Kullanıcı Bilgileri", builtin);
        Assert.DoesNotContain("Bordro Ek Bilgileri", builtin);
        Assert.Contains("Bordro Ek Bilgileri", html);
        Assert.Contains("Proje Talebi", html);

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/forms?q=" + Uri.EscapeDataString("Proje")));
        Assert.Contains("Proje Talebi", filtered);
        Assert.DoesNotContain("Bordro Ek Bilgileri", filtered);
    }

    // ---- form-edit.html: designer round-trip -------------------------------------------

    [Fact]
    public async Task FormDesigner_Save_RoundTripsFieldsConfigAndOrder()
    {
        var marker = $"S7 Form {Guid.NewGuid():N}"[..20];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/form-edit");

        int modullerId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            modullerId = (await s.Db.ListDefinitions.SingleAsync(l => l.Name == "Modül")).Id;
        }

        // Three rows: text w/ full ⚙ config, inline-options choice, list-backed choice.
        await PostFormAsync(client, "/admin/form-edit", token,
        [
            ("title", marker), ("instructions", "S7 yönerge"), ("notes", "S7 not"),
            ("fieldIds", "0"), ("fieldLabels", "Sefer Kodu"), ("fieldTypes", "text"), ("fieldVars", ""),
            ("fieldLists", "0"), ("fieldChoices", ""),
            ("cfgHints", "6 haneli kod"), ("cfgDefaults", "SFR-"), ("cfgValidations", ""),
            ("fieldMarks", "row"), ("fieldMarks", "req"),
            ("fieldIds", "0"), ("fieldLabels", "Hat Tipi"), ("fieldTypes", "choices"), ("fieldVars", "hat_tipi"),
            ("fieldLists", "0"), ("fieldChoices", "Metro\nOtobüs\n\nMetro\nTramvay"),
            ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"),
            ("fieldIds", "0"), ("fieldLabels", "İlgili Modül"), ("fieldTypes", "choices"), ("fieldVars", "ilgili_modul"),
            ("fieldLists", modullerId.ToString()), ("fieldChoices", ""),
            ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"), ("fieldMarks", "int"),
        ]);

        int formId, seferId, hatId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Title == marker);
            formId = form.Id;
            Assert.Equal("S7 yönerge", form.Instructions);
            Assert.False(form.IsSystem);
            Assert.True(form.IsActive);

            Assert.Collection(form.Fields.OrderBy(f => f.Sort),
                f =>
                {
                    seferId = f.Id;
                    Assert.Equal("Sefer Kodu", f.Label);
                    Assert.Equal("text", f.Type);
                    Assert.Equal("sefer_kodu", f.Name); // auto-slug from the label (TR-aware)
                    Assert.True(f.RequiredForUsers);
                    Assert.True(f.RequiredForAgents);
                    Assert.True(f.VisibleToUsers);
                    Assert.Equal("6 haneli kod", f.Hint);
                    Assert.Equal("SFR-", FormFieldConfig.Parse(f.Configuration).Default);
                },
                f =>
                {
                    // Options editor: blank + duplicate lines dropped, order kept.
                    Assert.Equal(["Metro", "Otobüs", "Tramvay"],
                        FormFieldConfig.Parse(f.Configuration).Choices);
                    Assert.Null(FormFieldConfig.Parse(f.Configuration).ListId);
                },
                f =>
                {
                    Assert.Equal(modullerId, FormFieldConfig.Parse(f.Configuration).ListId);
                    Assert.False(f.VisibleToUsers); // Dahili
                    Assert.False(f.RequiredForUsers);
                });
            seferId = form.Fields.Single(f => f.Name == "sefer_kodu").Id;
            hatId = form.Fields.Single(f => f.Name == "hat_tipi").Id;
        }

        // Designer page prefills the rows (B2: hidden ⚙ inputs + options editor).
        var (editToken, editHtmlRaw) = await GetWithTokenAsync(client, $"/admin/form-edit?id={formId}");
        var editHtml = WebUtility.HtmlDecode(editHtmlRaw);
        Assert.Contains("value=\"6 haneli kod\"", editHtml);
        Assert.Contains("Metro\nOtobüs\nTramvay", editHtml.Replace("\r", ""));
        Assert.Contains($"value=\"{modullerId}\" selected", editHtml);

        // Drag result: Hat Tipi first (type changed to text — options are dropped),
        // Sefer Kodu second with a ⚙ validation, list row removed (no answers → deleted).
        await PostFormAsync(client, "/admin/form-edit", editToken,
        [
            ("id", formId.ToString()), ("title", marker),
            ("fieldIds", hatId.ToString()), ("fieldLabels", "Hat Tipi"), ("fieldTypes", "text"), ("fieldVars", "hat_tipi"),
            ("fieldLists", "0"), ("fieldChoices", "Metro\nOtobüs\nTramvay"),
            ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"),
            ("fieldIds", seferId.ToString()), ("fieldLabels", "Sefer Kodu"), ("fieldTypes", "text"), ("fieldVars", "sefer_kodu"),
            ("fieldLists", "0"), ("fieldChoices", ""),
            ("cfgHints", "6 haneli kod"), ("cfgDefaults", ""), ("cfgValidations", "number"),
            ("fieldMarks", "row"), ("fieldMarks", "req"),
        ]);

        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Id == formId);
            Assert.Equal(2, form.Fields.Count); // list-backed row deleted (no answers)
            Assert.Collection(form.Fields.OrderBy(f => f.Sort),
                f =>
                {
                    Assert.Equal(hatId, f.Id);
                    Assert.Equal("text", f.Type); // type change persists (mockup allows it)
                    Assert.Empty(FormFieldConfig.Parse(f.Configuration).Choices); // non-choice drops options
                },
                f =>
                {
                    Assert.Equal(seferId, f.Id);
                    Assert.Equal("number", FormFieldConfig.Parse(f.Configuration).Validation);
                });
        }
    }

    [Fact]
    public async Task FormDesigner_SoftDisablesAnsweredFields_DeletesUnanswered()
    {
        var marker = $"S7 Cevap {Guid.NewGuid():N}"[..20];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/form-edit");

        await PostFormAsync(client, "/admin/form-edit", token,
        [
            ("title", marker),
            ("fieldIds", "0"), ("fieldLabels", "Cevaplı"), ("fieldTypes", "text"), ("fieldVars", "cevapli"),
            ("fieldLists", "0"), ("fieldChoices", ""), ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"),
            ("fieldIds", "0"), ("fieldLabels", "Cevapsız"), ("fieldTypes", "text"), ("fieldVars", "cevapsiz"),
            ("fieldLists", "0"), ("fieldChoices", ""), ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"),
        ]);

        int formId, answeredId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Title == marker);
            formId = form.Id;
            answeredId = form.Fields.Single(f => f.Name == "cevapli").Id;
            var ticketId = await s.Db.Tickets.Select(t => t.Id).OrderBy(x => x).FirstAsync();
            s.Db.FormEntries.Add(new FormEntry
            {
                FormDefinitionId = formId,
                ObjectType = FormObjectType.Ticket,
                ObjectId = ticketId,
                Values = { new FormEntryValue { FormFieldId = answeredId, Value = "42" } },
            });
            await s.Db.SaveChangesAsync();
        }

        // Save with BOTH rows removed: answered → soft-disabled, unanswered → gone.
        var (editToken, _) = await GetWithTokenAsync(client, $"/admin/form-edit?id={formId}");
        await PostFormAsync(client, "/admin/form-edit", editToken,
            [("id", formId.ToString()), ("title", marker)]);

        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Id == formId);
            var kept = Assert.Single(form.Fields);
            Assert.Equal(answeredId, kept.Id);
            Assert.True(kept.IsDisabled); // answers preserved, field archived
            Assert.True(await s.Db.Set<FormEntryValue>().AnyAsync(v => v.FormFieldId == answeredId));
        }

        // The archived field no longer renders as an editable designer row.
        var (_, html) = await GetWithTokenAsync(client, $"/admin/form-edit?id={formId}");
        Assert.DoesNotContain("value=\"Cevaplı\"", WebUtility.HtmlDecode(html));
    }

    // ---- GATE PROOF: designer output consumed by BOTH open pages ------------------------

    [Fact]
    public async Task GateProof_DesignerForm_RendersAndPersistsThroughOpenPages()
    {
        var formName = $"S7 Kapı {Guid.NewGuid():N}"[..20];
        var topicName = $"S7 Konu {Guid.NewGuid():N}"[..20];
        var admin = await AdminClientAsync();

        // 1. Build the form in the designer: a required email field (⚙ validation)
        //    and an inline-options choice field.
        var (formToken, _) = await GetWithTokenAsync(admin, "/admin/form-edit");
        await PostFormAsync(admin, "/admin/form-edit", formToken,
        [
            ("title", formName),
            ("fieldIds", "0"), ("fieldLabels", "Yetkili E-posta"), ("fieldTypes", "text"), ("fieldVars", "yetkili_eposta"),
            ("fieldLists", "0"), ("fieldChoices", ""),
            ("cfgHints", "Bildirimler bu adrese gider"), ("cfgDefaults", ""), ("cfgValidations", "email"),
            ("fieldMarks", "row"), ("fieldMarks", "req"),
            ("fieldIds", "0"), ("fieldLabels", "Sefer Bölgesi"), ("fieldTypes", "choices"), ("fieldVars", "sefer_bolgesi"),
            ("fieldLists", "0"), ("fieldChoices", "Anadolu\nAvrupa"),
            ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"),
        ]);

        int formId;
        int[] fieldIds;
        int emailFieldId, choiceFieldId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = await s.Db.FormDefinitions.Include(f => f.Fields)
                .SingleAsync(f => f.Title == formName);
            formId = form.Id;
            fieldIds = [.. form.Fields.Select(f => f.Id)];
            emailFieldId = form.Fields.Single(f => f.Name == "yetkili_eposta").Id;
            choiceFieldId = form.Fields.Single(f => f.Name == "sefer_bolgesi").Id;
        }

        // 2. Attach it to a fresh public topic through the helptopics admin page.
        var (topicToken, _) = await GetWithTokenAsync(admin, "/admin/helptopic-edit");
        await PostFormAsync(admin, "/admin/helptopic-edit", topicToken,
        [
            ("name", topicName), ("status", "active"), ("isPublic", "true"),
            ("numMode", "system"), ("sequence", ""),
            ("formIds", formId.ToString()),
            .. fieldIds.Select(f => ("enabledFields", f.ToString())),
        ]);

        int topicId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            topicId = (await s.Db.HelpTopics.SingleAsync(t => t.Name == topicName)).Id;
        }

        // 3. Portal /open renders the designer's fields for the new topic.
        var portal = await PortalClientAsync();
        var (openToken, openHtmlRaw) = await GetWithTokenAsync(portal, "/open");
        var openHtml = WebUtility.HtmlDecode(openHtmlRaw);
        Assert.Contains(topicName, openHtml);
        Assert.Contains("Yetkili E-posta", openHtml);
        Assert.Contains("Bildirimler bu adrese gider", openHtml); // ⚙ hint
        Assert.Contains(">Anadolu</option>", openHtml);           // inline choice option

        // 4. Agent ticket-open renders them too.
        var agentHtml = WebUtility.HtmlDecode(await admin.GetStringAsync("/agent/ticket-open"));
        Assert.Contains("Yetkili E-posta", agentHtml);
        Assert.Contains(">Avrupa</option>", agentHtml);

        // 5. The ⚙ validation is enforced: a malformed email refuses the create…
        var bad = await PostFormAsync(portal, "/open", openToken,
        [
            ("Topic", topicId.ToString()),
            ("Summary", "Sefer talebi"), ("Details", "detay"),
            ($"Fields[{emailFieldId}]", "not-an-email"),
            ($"Fields[{choiceFieldId}]", "Anadolu"),
        ]);
        Assert.Contains("biçimi geçersiz", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));

        // …and a valid submit persists the designer fields as FormEntry values.
        var subject = $"Sefer talebi {Guid.NewGuid():N}"[..24];
        var (retryToken, _) = await GetWithTokenAsync(portal, "/open");
        var ok = await PostFormAsync(portal, "/open", retryToken,
        [
            ("Topic", topicId.ToString()),
            ("Summary", subject), ("Details", "detay"),
            ($"Fields[{emailFieldId}]", "filo@ulasim.com.tr"),
            ($"Fields[{choiceFieldId}]", "Anadolu"),
        ]);
        Assert.Equal("/tickets", PathOf(ok));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var ticket = await s.Db.Tickets.SingleAsync(t => t.Subject == subject);
            var entry = await s.Db.FormEntries.Include(e => e.Values)
                .SingleAsync(e => e.FormDefinitionId == formId
                    && e.ObjectType == FormObjectType.Ticket && e.ObjectId == ticket.Id);
            Assert.Equal("filo@ulasim.com.tr", entry.Values.Single(v => v.FormFieldId == emailFieldId).Value);
            var choice = entry.Values.Single(v => v.FormFieldId == choiceFieldId);
            Assert.Equal("Anadolu", choice.Value);
            Assert.Null(choice.ValueId); // inline option — no list item behind it
        }
    }

    // ---- live preview ---------------------------------------------------------------------

    [Fact]
    public async Task FormPreview_RendersUnsavedState_UserVisibleFieldsOnly()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/form-edit");

        var res = await PostFormAsync(client, "/admin/form-edit/preview", token,
        [
            ("fieldIds", "0"), ("fieldLabels", "Önizleme Alanı"), ("fieldTypes", "choices"), ("fieldVars", ""),
            ("fieldLists", "0"), ("fieldChoices", "Bir\nİki"),
            ("cfgHints", "ipucu satırı"), ("cfgDefaults", "İki"), ("cfgValidations", ""),
            ("fieldMarks", "row"),
            ("fieldIds", "0"), ("fieldLabels", "Gizli Alan"), ("fieldTypes", "text"), ("fieldVars", ""),
            ("fieldLists", "0"), ("fieldChoices", ""), ("cfgHints", ""), ("cfgDefaults", ""), ("cfgValidations", ""),
            ("fieldMarks", "row"), ("fieldMarks", "int"),
        ]);
        res.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Contains("Önizleme Alanı", html);
        Assert.Contains(">Bir</option>", html);
        Assert.Contains("ipucu satırı", html);
        Assert.Contains("selected", html);              // default pre-selects its option
        Assert.DoesNotContain("Gizli Alan", html);      // internal fields don't face users
    }

    // ---- delete + bulk guards ----------------------------------------------------------------

    [Fact]
    public async Task FormDelete_GuardsBuiltin_RemovesCustomWithItsData()
    {
        var client = await AdminClientAsync();

        int systemId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            systemId = (await s.Db.FormDefinitions.SingleAsync(f => f.Title == "Talep Detayları")).Id;
        }
        var (token, _) = await GetWithTokenAsync(client, $"/admin/form-edit?id={systemId}");
        await PostFormAsync(client, "/admin/form-edit/delete", token, [("id", systemId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.FormDefinitions.AnyAsync(f => f.Id == systemId)); // protected
        }

        // Custom form with an entry: delete removes both (fme.deleteWarn canon).
        var marker = $"S7 Sil {Guid.NewGuid():N}"[..18];
        int formId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = new FormDefinition
            {
                Title = marker,
                Fields = { new FormField { Type = "text", Label = "X", Name = "x", Sort = 1 } },
            };
            s.Db.FormDefinitions.Add(form);
            await s.Db.SaveChangesAsync();
            formId = form.Id;
            var ticketId = await s.Db.Tickets.Select(t => t.Id).OrderBy(x => x).FirstAsync();
            s.Db.FormEntries.Add(new FormEntry
            {
                FormDefinitionId = formId,
                ObjectType = FormObjectType.Ticket,
                ObjectId = ticketId,
                Values = { new FormEntryValue { FormFieldId = form.Fields[0].Id, Value = "cevap" } },
            });
            await s.Db.SaveChangesAsync();
        }

        var (delToken, _) = await GetWithTokenAsync(client, $"/admin/form-edit?id={formId}");
        var landed = await PostFormAsync(client, "/admin/form-edit/delete", delToken, [("id", formId.ToString())]);
        Assert.Equal("/admin/forms", PathOf(landed));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.FormDefinitions.AnyAsync(f => f.Id == formId));
            Assert.False(await s.Db.FormEntries.AnyAsync(e => e.FormDefinitionId == formId));
        }
    }

    [Fact]
    public async Task FormsBulk_DisableHidesFromTopicAttachList_SystemSkipped()
    {
        var marker = $"S7 Pasif {Guid.NewGuid():N}"[..18];
        var client = await AdminClientAsync();
        int formId, systemId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var form = new FormDefinition { Title = marker };
            s.Db.FormDefinitions.Add(form);
            await s.Db.SaveChangesAsync();
            formId = form.Id;
            systemId = (await s.Db.FormDefinitions.SingleAsync(f => f.Title == "Talep Detayları")).Id;
        }

        // Attach list offers the active custom form…
        var attach = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/helptopic-edit"));
        Assert.Contains(marker, attach);

        var (token, _) = await GetWithTokenAsync(client, "/admin/forms");
        await PostFormAsync(client, "/admin/forms/bulk", token,
            [("act", "disable"), ("ids", formId.ToString()), ("ids", systemId.ToString())]);

        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False((await s.Db.FormDefinitions.SingleAsync(f => f.Id == formId)).IsActive);
            Assert.True((await s.Db.FormDefinitions.SingleAsync(f => f.Id == systemId)).IsActive); // skipped
        }

        // …and hides it once bulk-disabled (schedules IsActive precedent).
        attach = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/helptopic-edit"));
        Assert.DoesNotContain(marker, attach);
    }

    // ---- lists.html (B1) ------------------------------------------------------------------

    [Fact]
    public async Task ListsList_ShowsMirrorCountsAndProtectsSystemRows()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/lists"));

        // Plural display names + built-in pills; system item counts mirror the
        // real status/priority tables (mockup canon 7 / 4).
        Assert.Contains("Talep Durumları", html);
        Assert.Contains("Modüller", html);
        int statusCount, priorityCount;
        using (var s = new ServiceScopeBundle(fixture))
        {
            statusCount = await s.Db.TicketStatuses.CountAsync();
            priorityCount = await s.Db.TicketPriorities.CountAsync();
        }
        Assert.Contains($"<td class=\"num\">{statusCount}</td>", html);
        Assert.Contains($"<td class=\"num\">{priorityCount}</td>", html);

        // System rows: disabled selection checkbox; bulk delete skips them anyway.
        int statusesId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            statusesId = (await s.Db.ListDefinitions.SingleAsync(l => l.Type == "ticket-status")).Id;
        }
        Assert.Matches(new Regex($"value=\"{statusesId}\"[^>]*disabled"), html);

        var (token, _) = await GetWithTokenAsync(client, "/admin/lists");
        await PostFormAsync(client, "/admin/lists/bulk", token,
            [("act", "delete"), ("ids", statusesId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.ListDefinitions.AnyAsync(l => l.Id == statusesId));
        }
    }

    // ---- list-edit.html: item CRUD + sort-mode + import ---------------------------------------

    [Fact]
    public async Task ListEditor_ItemCrud_SortModeOrders_DupRefused()
    {
        var marker = $"S7 Liste {Guid.NewGuid():N}"[..20];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/list-edit");

        // Manual mode keeps the posted Sıra numbers (drag-free reorder per mockup).
        await PostFormAsync(client, "/admin/list-edit", token,
        [
            ("name", marker), ("plural", marker + "ler"), ("sortMode", "manual"), ("notes", "not"),
            ("itemIds", "0"), ("itemValues", "Çorlu"), ("itemAbbrevs", "CRL"), ("itemSorts", "2"),
            ("itemMarks", "row"), ("itemMarks", "on"),
            ("itemIds", "0"), ("itemValues", "Ankara"), ("itemAbbrevs", "ANK"), ("itemSorts", "1"),
            ("itemMarks", "row"), ("itemMarks", "on"),
            ("itemIds", "0"), ("itemValues", "Bursa"), ("itemAbbrevs", ""), ("itemSorts", "3"),
            ("itemMarks", "row"),
        ]);

        int listId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.Include(l => l.Items)
                .SingleAsync(l => l.Name == marker);
            listId = list.Id;
            Assert.Equal(ListSortMode.SortColumn, list.SortMode);
            Assert.Collection(list.Items.OrderBy(i => i.Sort),
                i => { Assert.Equal("Ankara", i.Value); Assert.Equal("ANK", i.Abbrev); Assert.True(i.IsEnabled); },
                i => Assert.Equal("Çorlu", i.Value),
                i => { Assert.Equal("Bursa", i.Value); Assert.False(i.IsEnabled); Assert.Null(i.Abbrev); });
        }

        // Alphabetical mode ignores the (gated-off, unposted) Sıra inputs and
        // re-orders by TR collation; one item removed.
        int corluId, ankaraId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.Include(l => l.Items).SingleAsync(l => l.Id == listId);
            corluId = list.Items.Single(i => i.Value == "Çorlu").Id;
            ankaraId = list.Items.Single(i => i.Value == "Ankara").Id;
        }
        var (editToken, _) = await GetWithTokenAsync(client, $"/admin/list-edit?id={listId}");
        await PostFormAsync(client, "/admin/list-edit", editToken,
        [
            ("id", listId.ToString()), ("name", marker), ("sortMode", "alpha"),
            ("itemIds", corluId.ToString()), ("itemValues", "Çorlu"), ("itemAbbrevs", "CRL"),
            ("itemMarks", "row"), ("itemMarks", "on"),
            ("itemIds", ankaraId.ToString()), ("itemValues", "Ceyhan"), ("itemAbbrevs", ""),
            ("itemMarks", "row"), ("itemMarks", "on"),
        ]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.Include(l => l.Items).SingleAsync(l => l.Id == listId);
            Assert.Equal(ListSortMode.Alpha, list.SortMode);
            Assert.Equal(2, list.Items.Count); // Bursa row removed
            // TR collation: Ceyhan < Çorlu (c before ç).
            Assert.Collection(list.Items.OrderBy(i => i.Sort),
                i => Assert.Equal("Ceyhan", i.Value),
                i => Assert.Equal("Çorlu", i.Value));
        }

        // Duplicate values are refused — nothing written (B3).
        await PostFormAsync(client, "/admin/list-edit", editToken,
        [
            ("id", listId.ToString()), ("name", marker), ("sortMode", "alpha"),
            ("itemIds", corluId.ToString()), ("itemValues", "Aynı"), ("itemMarks", "row"),
            ("itemIds", "0"), ("itemValues", "aynı"), ("itemMarks", "row"),
        ]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.Include(l => l.Items).SingleAsync(l => l.Id == listId);
            Assert.DoesNotContain(list.Items, i => i.Value == "Aynı");
            Assert.Equal(2, list.Items.Count);
        }
    }

    [Fact]
    public async Task ListImport_AddsAndSkipsHonestly()
    {
        var marker = $"S7 Aktar {Guid.NewGuid():N}"[..20];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/list-edit");
        await PostFormAsync(client, "/admin/list-edit", token,
        [
            ("name", marker), ("sortMode", "manual"),
            ("itemIds", "0"), ("itemValues", "Bordro"), ("itemAbbrevs", "BRD"), ("itemSorts", "1"),
            ("itemMarks", "row"), ("itemMarks", "on"),
        ]);
        int listId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            listId = (await s.Db.ListDefinitions.SingleAsync(l => l.Name == marker)).Id;
        }

        // 2 added (one with an abbrev), 2 skipped (existing dup + in-paste dup).
        var (editToken, _) = await GetWithTokenAsync(client, $"/admin/list-edit?id={listId}");
        var landed = await PostFormAsync(client, "/admin/list-edit/import", editToken,
        [
            ("id", listId.ToString()),
            ("text", "İzin, IZN\n\nbordro\nVardiya\nVardiya"),
        ]);
        landed.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await landed.Content.ReadAsStringAsync());
        Assert.Contains("2 öğe eklendi, 2 satır atlandı.", html); // honest toast

        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.Include(l => l.Items).SingleAsync(l => l.Id == listId);
            Assert.Collection(list.Items.OrderBy(i => i.Sort),
                i => Assert.Equal("Bordro", i.Value),
                i => { Assert.Equal("İzin", i.Value); Assert.Equal("IZN", i.Abbrev); },
                i => Assert.Equal("Vardiya", i.Value));
        }
    }

    // ---- properties designer + system protection -----------------------------------------------

    [Fact]
    public async Task ListProperties_RoundTripConfigurationJson()
    {
        var marker = $"S7 Özellik {Guid.NewGuid():N}"[..20];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/list-edit");

        await PostFormAsync(client, "/admin/list-edit", token,
        [
            ("name", marker), ("sortMode", "alpha"),
            ("propLabels", "Açıklama"), ("propTypes", "text"), ("propVars", ""),
            ("propHints", "kısa açıklama"), ("propDefaults", ""), ("propValidations", ""),
            ("propMarks", "row"),
            ("propLabels", "Sorumlu Ekip"), ("propTypes", "choices"), ("propVars", "sorumlu_ekip"),
            ("propHints", ""), ("propDefaults", ""), ("propValidations", ""),
            ("propMarks", "row"), ("propMarks", "req"), ("propMarks", "int"),
        ]);

        int listId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var list = await s.Db.ListDefinitions.SingleAsync(l => l.Name == marker);
            listId = list.Id;
            using var doc = JsonDocument.Parse(list.Configuration!);
            var props = doc.RootElement.GetProperty("properties");
            Assert.Equal(2, props.GetArrayLength());
            Assert.Equal("aciklama", props[0].GetProperty("name").GetString()); // auto-slug
            Assert.Equal("kısa açıklama", props[0].GetProperty("help").GetString());
            Assert.Equal("choices", props[1].GetProperty("type").GetString());
            Assert.True(props[1].GetProperty("required").GetBoolean());
            Assert.True(props[1].GetProperty("internal").GetBoolean());
        }

        // The designer page renders the persisted rows back (B2 prefill).
        var (_, htmlRaw) = await GetWithTokenAsync(client, $"/admin/list-edit?id={listId}");
        var html = WebUtility.HtmlDecode(htmlRaw);
        Assert.Contains("value=\"Sorumlu Ekip\"", html);
        Assert.Contains("value=\"kısa açıklama\"", html);
    }

    [Fact]
    public async Task SystemList_ReadOnlyEditor_SaveDeleteRefused()
    {
        var client = await AdminClientAsync();
        int statusesId;
        string name;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var statuses = await s.Db.ListDefinitions.SingleAsync(l => l.Type == "ticket-status");
            statusesId = statuses.Id;
            name = statuses.Name;
        }

        // Editor mirrors the real status rows read-only.
        var (token, htmlRaw) = await GetWithTokenAsync(client, $"/admin/list-edit?id={statusesId}");
        var html = WebUtility.HtmlDecode(htmlRaw);
        Assert.Contains("value=\"Yanıt bekliyor\"", html);    // mirrored item row
        Assert.Matches(new Regex("name=\"itemValues\"[^>]*disabled|disabled[^>]*name=\"itemValues\""),
            htmlRaw); // controls disabled

        // Save + delete refuse server-side regardless of the disabled UI.
        await PostFormAsync(client, "/admin/list-edit", token,
            [("id", statusesId.ToString()), ("name", "Ele Geçirildi"), ("sortMode", "manual")]);
        await PostFormAsync(client, "/admin/list-edit/delete", token, [("id", statusesId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            var statuses = await s.Db.ListDefinitions.SingleAsync(l => l.Id == statusesId);
            Assert.Equal(name, statuses.Name); // untouched
        }
    }

    [Fact]
    public async Task ListDelete_GuardsFormFieldReference_DeletesUnused()
    {
        var client = await AdminClientAsync();
        int modullerId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            modullerId = (await s.Db.ListDefinitions.SingleAsync(l => l.Name == "Modül")).Id;
        }

        // Referenced by the seeded "Modül" choices field → refused.
        var (token, _) = await GetWithTokenAsync(client, $"/admin/list-edit?id={modullerId}");
        await PostFormAsync(client, "/admin/list-edit/delete", token, [("id", modullerId.ToString())]);
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.True(await s.Db.ListDefinitions.AnyAsync(l => l.Id == modullerId));
        }

        // An unreferenced custom list deletes (items cascade).
        var marker = $"S7 SilL {Guid.NewGuid():N}"[..18];
        var (createToken, _) = await GetWithTokenAsync(client, "/admin/list-edit");
        await PostFormAsync(client, "/admin/list-edit", createToken,
        [
            ("name", marker), ("sortMode", "alpha"),
            ("itemIds", "0"), ("itemValues", "Tek"), ("itemMarks", "row"), ("itemMarks", "on"),
        ]);
        int listId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            listId = (await s.Db.ListDefinitions.SingleAsync(l => l.Name == marker)).Id;
        }
        var (delToken, _) = await GetWithTokenAsync(client, $"/admin/list-edit?id={listId}");
        var landed = await PostFormAsync(client, "/admin/list-edit/delete", delToken, [("id", listId.ToString())]);
        Assert.Equal("/admin/lists", PathOf(landed));
        using (var s = new ServiceScopeBundle(fixture))
        {
            Assert.False(await s.Db.ListDefinitions.AnyAsync(l => l.Id == listId));
        }
    }

    // ---- helpers (QueuesAdminTests twins) ---------------------------------------------------

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7FORMSKEY2345ABCD";
        var username = $"s7fl{Guid.NewGuid():N}"[..14];
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
            var deptId = await db.Departments.Select(d => d.Id).OrderBy(x => x).FirstAsync();
            var roleId = await db.Roles.Select(r => r.Id).OrderBy(x => x).FirstAsync();
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
            [("User", username), ("Password", Password)]);
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, [("Code", ComputeTotp(totpKey))]);
        Assert.Equal("/admin/dashboard", PathOf(landed));
        return client;
    }

    /// <summary>Seeded portal customer over HTTP (portal /login form — no 2FA).</summary>
    private async Task<HttpClient> PortalClientAsync()
    {
        var client = fixture.Factory.CreateClient();
        var (token, _) = await GetWithTokenAsync(client, "/login");
        var landed = await PostFormAsync(client, "/login", token,
            [("User", "bourla.salehi@ulasim.com.tr"), ("Password", Password)]);
        Assert.Equal("/tickets", PathOf(landed));
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (row arrays / marker sequences).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

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
