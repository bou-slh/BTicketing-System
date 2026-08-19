using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/filters.html + filter-edit.html: the B1 list over Filter rows with
/// bulk enable/disable/delete, the editor's B4 rule/action row builders
/// round-tripping + reconciling through the whole-page save, the FilterEngine's
/// pure matching semantics (AND/OR, operators, case behavior, target gating),
/// the live pipeline hook (actions applied inside TicketService.CreateAsync,
/// Reject refusing with the typed exception, exec order + stop-on-match) and the
/// match-count preview endpoint (mail-only rules reported honestly).
/// </summary>
[Collection("Postgres")]
public class FiltersAdminTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- FilterEngine pure semantics (no db) ----------------------------------------------

    [Fact]
    public void Engine_RuleOperators_AreCaseInsensitiveOrdinal()
    {
        static FilterRule Rule(string what, FilterMatchHow how, string value) =>
            new() { What = what, How = how, Value = value };
        var input = new FilterInput
        {
            Email = "spam@x.com",
            Subject = "Temmuz ACIL bordro",
            ReplyTo = null,
        };

        Assert.True(FilterEngine.RuleMatches(Rule("email", FilterMatchHow.Equal, "SPAM@X.COM"), input));
        Assert.True(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.Contains, "acil"), input));
        Assert.True(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.StartsWith, "temmuz"), input));
        Assert.True(FilterEngine.RuleMatches(Rule("email", FilterMatchHow.EndsWith, "@X.com"), input));
        Assert.False(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.Contains, "izin"), input));

        // Documented case behavior: ordinal (culture-invariant) folding — the
        // Turkish dotted 'İ' is NOT treated as 'i'.
        Assert.False(FilterEngine.RuleMatches(
            Rule("subject", FilterMatchHow.Contains, "acil"),
            input with { Subject = "ACİL bordro" }));

        // Regex runs verbatim; an invalid pattern never matches (also for Not).
        Assert.True(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.Matches, "ACIL|acil"), input));
        Assert.False(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.Matches, "("), input));
        Assert.False(FilterEngine.RuleMatches(Rule("subject", FilterMatchHow.NotMatches, "("), input));

        // Absent fields and unknown what-keys never match — negations included.
        Assert.False(FilterEngine.RuleMatches(Rule("replyto", FilterMatchHow.NotEqual, "x"), input));
        Assert.False(FilterEngine.RuleMatches(Rule("mystery", FilterMatchHow.Contains, "x"), input));

        // Source matches its enum name case-insensitively (seed canon "API").
        Assert.True(FilterEngine.RuleMatches(
            Rule("source", FilterMatchHow.Equal, "API"), input with { Source = TicketSource.Api }));
    }

    [Fact]
    public void Engine_MatchModeAndTargetGate_Behave()
    {
        var filter = new Filter
        {
            Name = "t",
            Target = FilterTarget.Web,
            Rules =
            [
                new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = "bordro" },
                new FilterRule { What = "email", How = FilterMatchHow.EndsWith, Value = "@tosyali.com.tr" },
            ],
        };
        var oneOfTwo = new FilterInput { Subject = "bordro sorunu", Email = "x@ulasim.com.tr" };

        filter.MatchAllRules = false;
        Assert.True(FilterEngine.Matches(filter, oneOfTwo));
        filter.MatchAllRules = true;
        Assert.False(FilterEngine.Matches(filter, oneOfTwo));

        // A rule-less filter matches nothing.
        Assert.False(FilterEngine.Matches(new Filter { Name = "empty" }, oneOfTwo));

        // Target gate: phone/other sources only meet "Any"; an email-account
        // restriction pins the receiving mailbox.
        Assert.True(FilterEngine.TargetApplies(new Filter { Name = "x" }, new FilterInput { Source = TicketSource.Phone }));
        Assert.False(FilterEngine.TargetApplies(
            new Filter { Name = "x", Target = FilterTarget.Web }, new FilterInput { Source = TicketSource.Phone }));
        Assert.True(FilterEngine.TargetApplies(
            new Filter { Name = "x", Target = FilterTarget.Email },
            new FilterInput { Source = TicketSource.Email, EmailAccountId = 6 }));
        Assert.False(FilterEngine.TargetApplies(
            new Filter { Name = "x", Target = FilterTarget.Email, EmailAccountId = 5 },
            new FilterInput { Source = TicketSource.Email, EmailAccountId = 6 }));
    }

    // ---- pipeline hook (TicketService.CreateAsync) ----------------------------------------

    [Fact]
    public async Task Actions_ApplyOnCreate_RoutingFlagsAndNote()
    {
        var marker = $"fltr-{Guid.NewGuid():N}";
        using var s = new ServiceScopeBundle(fixture);
        var danismanlik = await s.Db.Departments.SingleAsync(d => d.Name == "Danışmanlık");
        var low = await s.Db.TicketPriorities.SingleAsync(p => p.Key == "low");
        var kritik = await s.Db.SlaPlans.SingleAsync(p => p.Name == "Kritik");

        var filter = new Filter
        {
            Name = $"S7 test {marker}",
            ExecOrder = 500,
            Target = FilterTarget.Web,
            MatchAllRules = true,
            Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = marker }],
            Actions =
            [
                new FilterAction { Type = "dept", Sort = 1, Configuration = $$"""{"dept_id":{{danismanlik.Id}}}""" },
                new FilterAction { Type = "priority", Sort = 2, Configuration = $$"""{"priority_id":{{low.Id}}}""" },
                new FilterAction { Type = "sla", Sort = 3, Configuration = $$"""{"sla_id":{{kritik.Id}}}""" },
                new FilterAction { Type = "noautoresp", Sort = 4 },
                new FilterAction { Type = "note", Sort = 5, Configuration = $$"""{"note":"filtre notu {{marker}}"}""" },
            ],
        };
        s.Db.Filters.Add(filter);
        await s.Db.SaveChangesAsync();

        try
        {
            var owner = await TestActors.UserAsync(s.Db, "Can Koç");
            var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Filtre eylem testi {marker}",
                Body = "<p>x</p>",
            }, owner);

            // Filter actions beat the settings defaults (dept Destek, priority normal).
            Assert.Equal(danismanlik.Id, ticket.DepartmentId);
            Assert.Equal(low.Id, ticket.PriorityId);
            Assert.Equal(kritik.Id, ticket.SlaId);
            Assert.True(ticket.AutoResponseDisabled);
            // SLA action recomputed the grace due date (Kritik = 4h).
            Assert.NotNull(ticket.EstimatedDueDate);

            // Note action landed as a SYSTEM internal note on the fresh thread.
            var note = await s.Db.ThreadEntries.SingleAsync(
                e => e.ThreadId == ticket.ThreadId && e.Type == ThreadEntryType.Note);
            Assert.Contains(marker, note.Body);
        }
        finally
        {
            s.Db.Filters.Remove(filter);
            await s.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Reject_RefusesCreate_WithTypedException_AndPersistsNothing()
    {
        var marker = $"fltr-{Guid.NewGuid():N}";
        using var s = new ServiceScopeBundle(fixture);
        var filter = new Filter
        {
            Name = $"S7 reject {marker}",
            ExecOrder = 500,
            Target = FilterTarget.Web,
            Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = marker }],
            Actions = [new FilterAction { Type = "reject", Sort = 1 }],
        };
        s.Db.Filters.Add(filter);
        await s.Db.SaveChangesAsync();

        try
        {
            var owner = await TestActors.UserAsync(s.Db, "Can Koç");
            var ex = await Assert.ThrowsAsync<TicketRejectedByFilterException>(
                () => s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
                {
                    UserId = owner.Id!.Value,
                    Subject = $"Reddedilecek {marker}",
                    Body = "<p>x</p>",
                }, owner));
            Assert.Equal(filter.Name, ex.FilterName);
            Assert.False(await s.Db.Tickets.AnyAsync(t => t.Subject.Contains(marker)));
        }
        finally
        {
            s.Db.Filters.Remove(filter);
            await s.Db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task ExecOrder_LaterFilterOverrides_UntilStopOnMatch()
    {
        var marker = $"fltr-{Guid.NewGuid():N}";
        using var s = new ServiceScopeBundle(fixture);
        var high = await s.Db.TicketPriorities.SingleAsync(p => p.Key == "high");
        var low = await s.Db.TicketPriorities.SingleAsync(p => p.Key == "low");

        Filter Make(string name, int order, int priorityId) => new()
        {
            Name = $"{name} {marker}",
            ExecOrder = order,
            Target = FilterTarget.Web,
            Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = marker }],
            Actions = [new FilterAction { Type = "priority", Sort = 1, Configuration = $$"""{"priority_id":{{priorityId}}}""" }],
        };
        var first = Make("S7 A", 501, high.Id);
        var second = Make("S7 B", 502, low.Id);
        s.Db.Filters.AddRange(first, second);
        await s.Db.SaveChangesAsync();

        try
        {
            var owner = await TestActors.UserAsync(s.Db, "Can Koç");
            async Task<Ticket> CreateAsync() => await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = $"Sıra testi {marker} {Guid.NewGuid():N}",
                Body = "<p>x</p>",
            }, owner);

            // Both match; exec order applies them 501 → 502, the later value wins.
            Assert.Equal(low.Id, (await CreateAsync()).PriorityId);

            // Stop-on-match on the first filter halts the chain — 502 never runs.
            first.StopOnMatch = true;
            await s.Db.SaveChangesAsync();
            Assert.Equal(high.Id, (await CreateAsync()).PriorityId);
        }
        finally
        {
            s.Db.Filters.RemoveRange(first, second);
            await s.Db.SaveChangesAsync();
        }
    }
    // ---- filters.html (B1) ------------------------------------------------------------------

    [Fact]
    public async Task FiltersList_RendersSeedCanon_DefaultExecOrder_AndSearch()
    {
        var client = await AdminClientAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/admin/filters"));

        Assert.Contains("VIP kullanıcı önceliklendirme", html);
        Assert.Contains("Spam engelleme", html);
        Assert.Contains("Web formu yönlendirme", html);
        Assert.Contains("API talepleri etiketleme", html);
        Assert.Contains("/admin/filter-edit?id=", html);
        // Status pills: three active, the API filter disabled.
        Assert.Contains("rd-pill-closed", html);
        // Default order = execution order ascending (VIP execorder 1 first).
        Assert.True(html.IndexOf("VIP kullanıcı", StringComparison.Ordinal)
            < html.IndexOf("Spam engelleme", StringComparison.Ordinal));

        var filtered = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/filters?q=" + Uri.EscapeDataString("Spam")));
        Assert.Contains("Spam engelleme", filtered);
        Assert.DoesNotContain("VIP kullanıcı", filtered);
    }

    [Fact]
    public async Task FilterEdit_PrefillsSeedCanon_IncludingVerbatimTokens()
    {
        int vipId, spamId, prHighId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            vipId = await s.Db.Filters.Where(f => f.Name == "VIP kullanıcı önceliklendirme")
                .Select(f => f.Id).SingleAsync();
            spamId = await s.Db.Filters.Where(f => f.Name == "Spam engelleme")
                .Select(f => f.Id).SingleAsync();
            prHighId = await s.Db.TicketPriorities.Where(p => p.Key == "high")
                .Select(p => p.Id).SingleAsync();
        }

        var client = await AdminClientAsync();
        var vip = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/filter-edit?id={vipId}"));
        // Info fields = the mockup's editor state (filter-edit.html canon).
        Assert.Contains("VIP kullanıcı önceliklendirme", vip);
        Assert.Contains("name=\"stopOnMatch\" checked", vip);
        Assert.Contains("value=\"all\" checked", vip);
        Assert.Contains("value=\"email\" selected", vip);
        Assert.Contains("Tosyalı Holding sözleşmesi gereği 8 saatlik VIP SLA uygulanır.", vip);
        // Rule rows: org equals / email contains / subject contains.
        Assert.Contains("value=\"org\" selected", vip);
        Assert.Contains("value=\"Tosyalı Holding\"", vip);
        Assert.Contains("value=\"@tosyali.com\"", vip);
        Assert.Contains("value=\"acil\"", vip);
        // Action rows: priority high prefilled through the hidden actValues.
        Assert.Contains("value=\"priority\" selected", vip);
        Assert.Contains($"name=\"actValues\" value=\"{prHighId}\"", vip);
        Assert.Contains("value=\"dept\" selected", vip);
        Assert.Contains("value=\"sla\" selected", vip);

        // Seed tokens outside the mockup's selects render as verbatim appended
        // options (spam filter's "ends" operator) — schedules-timezone precedent.
        var spam = WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/filter-edit?id={spamId}"));
        Assert.Contains("value=\"ends\" selected", spam);
        Assert.Contains("value=\"any\" checked", spam); // match-any (VEYA)
    }

    // ---- filter-edit.html save (B4 round-trip + reconcile) ----------------------------------

    [Fact]
    public async Task FilterSave_RoundTripsRuleAndActionRows_AndReconciles()
    {
        var name = $"S7 Filtre {Guid.NewGuid():N}"[..24];
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/filter-edit");

        int prLowId;
        using (var s = new ServiceScopeBundle(fixture))
            prLowId = await s.Db.TicketPriorities.Where(p => p.Key == "low").Select(p => p.Id).SingleAsync();

        var landed = await PostFormAsync(client, "/admin/filter-edit", token,
            ("name", name), ("execOrder", "500"), ("active", "1"), ("target", "web"),
            ("stopOnMatch", "on"), ("match", "any"), ("notes", "S7 test notu"),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", "bordro"),
            ("ruleIds", "0"), ("ruleWhats", "email"), ("ruleHows", "starts"), ("ruleVals", "noreply@"),
            ("actIds", "0"), ("actTypes", "priority"), ("actValues", prLowId.ToString()),
            ("actIds", "0"), ("actTypes", "email"), ("actValues", "ops@rapidsol.com.tr"));
        Assert.Equal("/admin/filters", PathOf(landed));

        int filterId, keptRuleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var filter = await s.Db.Filters.Include(f => f.Rules).Include(f => f.Actions)
                .SingleAsync(f => f.Name == name);
            filterId = filter.Id;
            Assert.Equal(500, filter.ExecOrder);
            Assert.True(filter.IsActive);
            Assert.Equal(FilterTarget.Web, filter.Target);
            Assert.Null(filter.EmailAccountId);
            Assert.True(filter.StopOnMatch);
            Assert.False(filter.MatchAllRules);
            Assert.Equal("S7 test notu", filter.Notes);

            Assert.Equal(2, filter.Rules.Count);
            var subjectRule = filter.Rules.Single(r => r.What == "subject");
            keptRuleId = subjectRule.Id;
            Assert.Equal(FilterMatchHow.Contains, subjectRule.How);
            Assert.Equal("bordro", subjectRule.Value);
            Assert.Equal(FilterMatchHow.StartsWith, filter.Rules.Single(r => r.What == "email").How);

            Assert.Equal(2, filter.Actions.Count);
            var priorityAction = filter.Actions.Single(a => a.Type == "priority");
            Assert.Equal(1, priorityAction.Sort);
            Assert.Equal($$"""{"priority_id":{{prLowId}}}""", priorityAction.Configuration);
            var emailAction = filter.Actions.Single(a => a.Type == "email");
            Assert.Equal(2, emailAction.Sort);
            Assert.Contains("\"to\":\"ops@rapidsol.com.tr\"", emailAction.Configuration);
        }

        // Edit: keep the subject rule by id (new value), drop the email rule, add
        // a regex rule; swap the actions for a single noautoresp (no parameter).
        var (token2, _) = await GetWithTokenAsync(client, $"/admin/filter-edit?id={filterId}");
        var landed2 = await PostFormAsync(client, "/admin/filter-edit", token2,
            ("id", filterId.ToString()),
            ("name", name), ("execOrder", "501"), ("active", "0"), ("target", "any"),
            ("match", "all"), ("notes", ""),
            ("ruleIds", keptRuleId.ToString()), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", "izin"),
            ("ruleIds", "0"), ("ruleWhats", "name"), ("ruleHows", "regex"), ("ruleVals", "^VIP"),
            ("actIds", "0"), ("actTypes", "noautoresp"), ("actValues", ""));
        Assert.Equal("/admin/filters", PathOf(landed2));

        using (var s = new ServiceScopeBundle(fixture))
        {
            var filter = await s.Db.Filters.Include(f => f.Rules).Include(f => f.Actions)
                .SingleAsync(f => f.Id == filterId);
            Assert.False(filter.IsActive);
            Assert.False(filter.StopOnMatch); // unchecked checkbox posts nothing
            Assert.True(filter.MatchAllRules);
            Assert.Null(filter.Notes);
            Assert.Equal(2, filter.Rules.Count);
            var kept = filter.Rules.Single(r => r.What == "subject");
            Assert.Equal(keptRuleId, kept.Id); // reconciled in place, id stable
            Assert.Equal("izin", kept.Value);
            Assert.Equal(FilterMatchHow.Matches, filter.Rules.Single(r => r.What == "name").How);
            var action = Assert.Single(filter.Actions);
            Assert.Equal("noautoresp", action.Type);
            Assert.Null(action.Configuration);
        }

        // B3: duplicate rule rows are refused — PRG back to the editor, nothing written.
        var (token3, _) = await GetWithTokenAsync(client, $"/admin/filter-edit?id={filterId}");
        var refused = await PostFormAsync(client, "/admin/filter-edit", token3,
            ("id", filterId.ToString()), ("name", name), ("execOrder", "501"), ("active", "0"), ("target", "any"),
            ("match", "all"), ("notes", ""),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", "x"),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", "x"));
        Assert.Equal("/admin/filter-edit", PathOf(refused));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.Equal(2, (await s.Db.Filters.Include(f => f.Rules).SingleAsync(f => f.Id == filterId)).Rules.Count);

        // Editor delete: rules/actions cascade with the filter.
        var (token4, _) = await GetWithTokenAsync(client, $"/admin/filter-edit?id={filterId}");
        var afterDelete = await PostFormAsync(client, "/admin/filter-edit/delete", token4,
            ("id", filterId.ToString()));
        Assert.Equal("/admin/filters", PathOf(afterDelete));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False(await s.Db.Filters.AnyAsync(f => f.Id == filterId));
    }

    [Fact]
    public async Task FiltersBulk_EnableDisableDelete_OverSelection()
    {
        int idA, idB;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var a = new Filter
            {
                Name = $"S7 Bulk A {Guid.NewGuid():N}"[..28], ExecOrder = 510,
                Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = Guid.NewGuid().ToString("N") }],
            };
            var b = new Filter
            {
                Name = $"S7 Bulk B {Guid.NewGuid():N}"[..28], ExecOrder = 511,
                Rules = [new FilterRule { What = "subject", How = FilterMatchHow.Contains, Value = Guid.NewGuid().ToString("N") }],
            };
            s.Db.Filters.AddRange(a, b);
            await s.Db.SaveChangesAsync();
            (idA, idB) = (a.Id, b.Id);
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/filters");
        await PostFormAsync(client, "/admin/filters/bulk", token,
            ("act", "disable"), ("ids", idA.ToString()), ("ids", idB.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.Equal(2, await s.Db.Filters.CountAsync(f => (f.Id == idA || f.Id == idB) && !f.IsActive));

        var (token2, _) = await GetWithTokenAsync(client, "/admin/filters");
        await PostFormAsync(client, "/admin/filters/bulk", token2,
            ("act", "enable"), ("ids", idA.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.True(await s.Db.Filters.Where(f => f.Id == idA).Select(f => f.IsActive).SingleAsync());

        var (token3, _) = await GetWithTokenAsync(client, "/admin/filters");
        await PostFormAsync(client, "/admin/filters/bulk", token3,
            ("act", "delete"), ("ids", idA.ToString()), ("ids", idB.ToString()));
        using (var s = new ServiceScopeBundle(fixture))
            Assert.False(await s.Db.Filters.AnyAsync(f => f.Id == idA || f.Id == idB));
    }

    // ---- match-count preview -----------------------------------------------------------------

    [Fact]
    public async Task Preview_CountsMatchingTickets_AndReportsMailOnlyRules()
    {
        var marker = $"onizleme-{Guid.NewGuid():N}";
        using (var s = new ServiceScopeBundle(fixture))
        {
            var owner = await TestActors.UserAsync(s.Db, "Can Koç");
            for (var i = 0; i < 2; i++)
            {
                await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
                {
                    UserId = owner.Id!.Value,
                    Subject = $"Önizleme {marker} {i}",
                    Body = "<p>x</p>",
                }, owner);
            }
        }

        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/filter-edit");

        var one = await PreviewAsync(client, token,
        [
            ("match", "all"),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", marker),
        ]);
        Assert.Equal(2, one.GetProperty("matches").GetInt32());
        Assert.True(one.GetProperty("total").GetInt32() >= 2);
        Assert.Equal(0, one.GetProperty("mailOnly").GetInt32());

        // A mail-only rule (reply-to) cannot match stored tickets: it is skipped
        // from the evaluation and reported — the count stays honest.
        var withMailOnly = await PreviewAsync(client, token,
        [
            ("match", "all"),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", marker),
            ("ruleIds", "0"), ("ruleWhats", "replyto"), ("ruleHows", "contains"), ("ruleVals", "@x"),
        ]);
        Assert.Equal(2, withMailOnly.GetProperty("matches").GetInt32());
        Assert.Equal(1, withMailOnly.GetProperty("mailOnly").GetInt32());

        // AND of two evaluable rules where only one holds → 0.
        var noneMatch = await PreviewAsync(client, token,
        [
            ("match", "all"),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "contains"), ("ruleVals", marker),
            ("ruleIds", "0"), ("ruleWhats", "subject"), ("ruleHows", "starts"), ("ruleVals", "zzz-yok"),
        ]);
        Assert.Equal(0, noneMatch.GetProperty("matches").GetInt32());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<JsonElement> PreviewAsync(
        HttpClient client, string token, (string Key, string Value)[] fields)
    {
        var response = await PostFormAsync(client, "/admin/filter-edit/preview", token, fields);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7FILTERSKEY234ABC";
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
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", PathOf(toTwofa));
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", PathOf(landed));
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    /// <summary>Duplicate keys allowed (ids / row arrays).</summary>
    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private static string PathOf(HttpResponseMessage response) =>
        response.RequestMessage!.RequestUri!.AbsolutePath;

    // ---- totp helper (AdminAuthTests twin) ----------------------------------------------------

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
