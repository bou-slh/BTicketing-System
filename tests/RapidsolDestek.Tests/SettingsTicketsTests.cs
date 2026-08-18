using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Identity;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Tests;

/// <summary>
/// S7 admin/settings-tickets.html: settings round-trip persistence through the admin
/// controller, the S7 exit-gate flip (block-work-until-approved → S4 guard flips),
/// the newly wired behavior switches (claim-on-response, require-topic-to-close,
/// max-open, default priority, random numbering) and the dlg-seq CRUD guards.
/// </summary>
[Collection("Postgres")]
public class SettingsTicketsTests(PostgresFixture fixture)
{
    private const string Password = "rapidsol1dev";

    // ---- helpers ---------------------------------------------------------------------

    private async Task<(int TicketId, ActorContext Agent, ActorContext Owner)> CreateTicketAsync(
        ServiceScopeBundle s, int? topicId = null)
    {
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Ayar testi {Guid.NewGuid():N}",
            Body = "<p>Test içeriği</p>",
            HelpTopicId = topicId,
        }, owner);
        return (ticket.Id, agent, owner);
    }

    /// <summary>Fresh identity-only admin signed in over HTTP with mandatory 2FA (AdminAuthTests pattern).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        const string totpKey = "RAPIDSOLDESTEKS7SETTINGSKEY23456";
        var username = $"s7set{Guid.NewGuid():N}"[..14];
        using (var scope = fixture.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
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
        }

        var client = fixture.Factory.CreateClient();
        var (loginToken, _) = await GetWithTokenAsync(client, "/admin/login");
        var toTwofa = await PostFormAsync(client, "/admin/login", loginToken,
            ("User", username), ("Password", Password));
        Assert.Equal("/admin/login/2fa", toTwofa.RequestMessage!.RequestUri!.AbsolutePath);
        var twofaToken = Regex.Match(await toTwofa.Content.ReadAsStringAsync(),
            "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var landed = await PostFormAsync(client, "/admin/login/2fa", twofaToken, ("Code", ComputeTotp(totpKey)));
        Assert.Equal("/admin/dashboard", landed.RequestMessage!.RequestUri!.AbsolutePath);
        return client;
    }

    private static async Task<(string Token, string Html)> GetWithTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {url}");
        return (token, html);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(("__RequestVerificationToken", token))
                  .ToDictionary(f => f.Item1, f => f.Item2)));

    /// <summary>The full main-form payload with seed-default values; overrides patch it.</summary>
    private async Task<List<(string, string)>> DefaultSaveFormAsync(params (string Key, string Value)[] overrides)
    {
        using var s = new ServiceScopeBundle(fixture);
        var slaId = await s.Db.SlaPlans.Where(x => x.Name == "Standart").Select(x => x.Id).SingleAsync();
        var topicId = await s.Db.HelpTopics.Where(x => x.IsActive).OrderBy(x => x.Sort).Select(x => x.Id).FirstAsync();
        // The assignee:me child ("Bana Atanan") — the built-in landing queue, so saving
        // the form doesn't move the agent panel's default for later tests.
        var queueId = await s.Db.SavedQueues
            .Where(x => x.StaffId == null && x.ParentId != null && x.Criteria!.Contains("\"assignee\":\"me\""))
            .Select(x => x.Id).FirstAsync();

        var form = new List<(string, string)>
        {
            ("number_format", "R######"), ("number_mode", "sequential"),
            ("default_status", "open"), ("default_priority", "normal"),
            ("default_sla", slaId.ToString()), ("default_topic", topicId.ToString()),
            ("default_queue", queueId.ToString()), ("top_counts", "true"),
            ("lock_mode", "activity"), ("max_open", "0"),
            ("captcha", "true"), ("claim_on_response", "true"),
            ("ef_enabled", "true"), ("ef_reject_note", "true"),
            ("ef_unit", "hours"), ("ef_reminder", "3"),
            ("ef_autoapprove", "0"), ("ef_revlimit", "3"),
            ("ar_new_ticket", "true"), ("ar_agent_new_ticket", "true"),
            ("ar_new_message_collab", "true"), ("ar_overlimit", "true"), ("ar_effort", "true"),
            ("al_new_ticket", "true"), ("al_new_ticket_admin", "true"),
            ("al_effort", "true"), ("al_effort_assigned", "true"),
        };
        foreach (var (key, value) in overrides)
        {
            form.RemoveAll(f => f.Item1 == key);
            if (value != "")
                form.Add((key, value));
        }
        return form;
    }

    // ---- the S7 exit gate: admin flip → S4 guard flips -------------------------------

    [Fact]
    public async Task AdminPage_BlockWorkFlip_FlipsTheS4GuardEndToEnd()
    {
        var client = await AdminClientAsync();

        // The page renders (TR default culture) with the effort section present.
        var (token, html) = await GetWithTokenAsync(client, "/admin/settings-tickets");
        Assert.Contains("Talep Ayarları", html);
        Assert.Contains("ef_block_work", html);

        // A ticket with a pending 6h proposal, guard currently off → work allowed.
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        await s.Get<IEffortProposalService>().ProposeAsync(ticketId, 6, null, agent);
        Assert.True(await s.Get<ITicketService>().IsWorkAllowedAsync(ticketId));

        try
        {
            // Admin flips "Onaysız çalışma başlatılamaz" ON through the real form POST.
            var on = await DefaultSaveFormAsync(("ef_block_work", "true"));
            var saved = await PostFormAsync(client, "/admin/settings-tickets", token, on.ToArray());
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal("/admin/settings-tickets", saved.RequestMessage!.RequestUri!.AbsolutePath);

            // S4 guards observe the flip: staff response + closing refuse, work gate reports blocked.
            using (var s2 = new ServiceScopeBundle(fixture))
            {
                Assert.False(await s2.Get<ITicketService>().IsWorkAllowedAsync(ticketId));
                var threadId = await s2.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
                await Assert.ThrowsAsync<WorkBlockedByEffortException>(() =>
                    s2.Get<IThreadService>().PostAsync(threadId, ThreadEntryType.Response, "<p>çıktı</p>", agent));
                var solvedId = await s2.Db.TicketStatuses.Where(st => st.Key == "solved").Select(st => st.Id).SingleAsync();
                await Assert.ThrowsAsync<WorkBlockedByEffortException>(() =>
                    s2.Get<ITicketService>().TransitionStatusAsync(ticketId, solvedId, agent));
            }

            // Flip back OFF via the same form → the guard opens again.
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-tickets");
            var off = await DefaultSaveFormAsync(("ef_block_work", ""));
            await PostFormAsync(client, "/admin/settings-tickets", token2, off.ToArray());

            using var s3 = new ServiceScopeBundle(fixture);
            Assert.True(await s3.Get<ITicketService>().IsWorkAllowedAsync(ticketId));
            var thread = await s3.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
            var entry = await s3.Get<IThreadService>().PostAsync(thread, ThreadEntryType.Response, "<p>çıktı</p>", agent);
            Assert.Equal(ThreadEntryType.Response, entry.Type);
        }
        finally
        {
            await using var _ = await SettingOverride.SetAsync(fixture, "effort", "block_work_until_approved", "false");
        }
    }

    [Fact]
    public async Task AdminPage_SaveRoundTrips_AndRerendersPersistedState()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-tickets");

        try
        {
            var form = await DefaultSaveFormAsync(
                ("max_open", "7"), ("lock_mode", "view"), ("ef_reminder", "5"), ("captcha", ""));
            await PostFormAsync(client, "/admin/settings-tickets", token, form.ToArray());

            // Typed accessor sees the writes…
            using (var s = new ServiceScopeBundle(fixture))
            {
                var behavior = await s.Get<ISettingsService>().GetTicketBehaviorAsync();
                Assert.Equal(7, behavior.MaxOpenPerUser);
                Assert.Equal("view", behavior.LockMode);
                Assert.False(behavior.Captcha);
                Assert.Equal(5, (await s.Get<ISettingsService>().GetEffortAsync()).ReminderDays);
            }

            // …and the re-rendered page carries the persisted state back into the form.
            var (_, html) = await GetWithTokenAsync(client, "/admin/settings-tickets");
            Assert.Contains("value=\"7\"", html);
            Assert.Matches(new Regex("value=\"view\"\\s+selected"), html);
        }
        finally
        {
            var (token2, _) = await GetWithTokenAsync(client, "/admin/settings-tickets");
            await PostFormAsync(client, "/admin/settings-tickets", token2, (await DefaultSaveFormAsync()).ToArray());
        }
    }

    [Fact]
    public async Task AdminPage_InvalidNumberFormat_SavesNothing()
    {
        var client = await AdminClientAsync();
        var (token, _) = await GetWithTokenAsync(client, "/admin/settings-tickets");

        var before = (await ReadSettingAsync("tickets", "number_format")) ?? "R######";
        var form = await DefaultSaveFormAsync(("number_format", "HATALI"), ("max_open", "99"));
        await PostFormAsync(client, "/admin/settings-tickets", token, form.ToArray());

        Assert.Equal(before, (await ReadSettingAsync("tickets", "number_format")) ?? "R######");
        Assert.NotEqual("99", await ReadSettingAsync("tickets", "max_open_per_user"));
    }

    private async Task<string?> ReadSettingAsync(string ns, string key)
    {
        using var s = new ServiceScopeBundle(fixture);
        return await s.Get<ISettingsService>().GetAsync(ns, key);
    }

    // ---- newly wired behavior switches ------------------------------------------------

    [Fact]
    public async Task ClaimOnResponse_AutoClaims_AndFlipsOff()
    {
        // Default (on): a staff response on an unassigned ticket claims it + logs "assigned".
        using (var s = new ServiceScopeBundle(fixture))
        {
            var (ticketId, agent, _) = await CreateTicketAsync(s);
            var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
            await s.Get<IThreadService>().PostAsync(threadId, ThreadEntryType.Response, "<p>yanıt</p>", agent);
            var ticket = await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
            Assert.Equal(agent.Id, ticket.StaffId);
            Assert.True(await s.Db.ThreadEvents.AnyAsync(e => e.ThreadId == threadId
                && s.Db.ThreadEventTypes.Any(t => t.Id == e.EventTypeId && t.Name == "assigned")));
        }

        // Off: the ticket stays unassigned.
        await using (await SettingOverride.SetAsync(fixture, "tickets", "claim_on_response", "false"))
        {
            using var s = new ServiceScopeBundle(fixture);
            var (ticketId, agent, _) = await CreateTicketAsync(s);
            var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
            await s.Get<IThreadService>().PostAsync(threadId, ThreadEntryType.Response, "<p>yanıt</p>", agent);
            Assert.Null((await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).StaffId);
        }
    }

    [Fact]
    public async Task RequireTopicToClose_RefusesTopiclessClose_WhenOn()
    {
        await using (await SettingOverride.SetAsync(fixture, "tickets", "require_topic_to_close", "true"))
        {
            using var s = new ServiceScopeBundle(fixture);
            var tickets = s.Get<ITicketService>();
            var solvedId = await s.Db.TicketStatuses.Where(st => st.Key == "solved").Select(st => st.Id).SingleAsync();

            // Topic-less ticket cannot close…
            var (bareId, agent, _) = await CreateTicketAsync(s);
            var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
                tickets.TransitionStatusAsync(bareId, solvedId, agent));
            Assert.Equal("topic-required-to-close", ex.Code);

            // …a topic'd one can (permission scope: route via the topic's department owner uakin/Bordro).
            var topicId = await s.Db.HelpTopics.Where(t => t.Name == "Bordro").Select(t => t.Id).SingleAsync();
            var (topicTicket, _, _) = await CreateTicketAsync(s, topicId);
            await tickets.TransitionStatusAsync(topicTicket, solvedId, agent);
            Assert.NotNull((await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == topicTicket)).ClosedAt);
        }

        // Off (default): topic-less tickets close freely.
        using var s2 = new ServiceScopeBundle(fixture);
        var (freeId, agent2, _) = await CreateTicketAsync(s2);
        var solved = await s2.Db.TicketStatuses.Where(st => st.Key == "solved").Select(st => st.Id).SingleAsync();
        await s2.Get<ITicketService>().TransitionStatusAsync(freeId, solved, agent2);
    }

    [Fact]
    public async Task MaxOpenPerUser_RefusesEndUserOverLimit_StaffBypasses()
    {
        using var s = new ServiceScopeBundle(fixture);
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var tickets = s.Get<ITicketService>();

        // Ensure at least one open ticket exists, then clamp the limit to 1.
        await CreateTicketAsync(s);
        await using (await SettingOverride.SetAsync(fixture, "tickets", "max_open_per_user", "1"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            var t2 = s2.Get<ITicketService>();
            var request = new TicketCreateRequest
            {
                UserId = owner.Id!.Value,
                Subject = "Limit üstü",
                Body = "<p>x</p>",
            };
            var ex = await Assert.ThrowsAsync<DomainRuleException>(() => t2.CreateAsync(request, owner));
            Assert.Equal("max-open-exceeded", ex.Code);

            // Staff-opened tickets bypass the end-user limit (osTicket parity).
            var staffTicket = await t2.CreateAsync(request, agent);
            Assert.True(staffTicket.Id > 0);
        }
    }

    [Fact]
    public async Task DefaultPriority_FallsBackToSetting()
    {
        using (var s = new ServiceScopeBundle(fixture))
        {
            var (ticketId, _, _) = await CreateTicketAsync(s);
            var normalId = await s.Db.TicketPriorities.Where(p => p.Key == "normal").Select(p => p.Id).SingleAsync();
            Assert.Equal(normalId, (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).PriorityId);
        }

        await using (await SettingOverride.SetAsync(fixture, "core", "default_priority", "high"))
        {
            using var s = new ServiceScopeBundle(fixture);
            var (ticketId, _, _) = await CreateTicketAsync(s);
            var highId = await s.Db.TicketPriorities.Where(p => p.Key == "high").Select(p => p.Id).SingleAsync();
            Assert.Equal(highId, (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).PriorityId);
        }
    }

    [Fact]
    public async Task RandomNumberMode_DrawsUnguessableNumbers_WithoutAdvancingTheSequence()
    {
        await using var _ = await SettingOverride.SetAsync(fixture, "tickets", "number_mode", "random");
        using var s = new ServiceScopeBundle(fixture);

        var seqId = int.Parse((await s.Get<ISettingsService>().GetAsync("tickets", "sequence_id"))!);
        var nextBefore = (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next;

        var (ticketId, _, _) = await CreateTicketAsync(s);
        var number = (await s.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId)).Number;

        Assert.Matches(new Regex("^R\\d{6}$"), number);
        var nextAfter = (await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Id == seqId)).Next;
        Assert.Equal(nextBefore, nextAfter); // random mode never touches the counter
    }

    // ---- dlg-seq CRUD guards (B4) ------------------------------------------------------

    [Fact]
    public async Task SequenceCrud_CreateEditDelete_WithGuards()
    {
        using var s = new ServiceScopeBundle(fixture);
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var service = s.Get<ISequenceNumberService>();

        var existing = await s.Db.Sequences.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new SequenceRow(x.Id, x.Name, x.Next)).ToListAsync();

        // Create a new row alongside the seeded ones.
        var name = $"Test Sırası {Guid.NewGuid():N}"[..20];
        await service.SaveAsync([.. existing, new SequenceRow(0, name, 100)], agent);
        var created = await s.Db.Sequences.AsNoTracking().SingleAsync(x => x.Name == name);
        Assert.Equal(100, created.Next);
        Assert.False(created.IsInternal);

        // Raising Next is allowed; lowering it is refused (numbers already drawn stay unique).
        var all = await CurrentRowsAsync(s);
        await service.SaveAsync(all.Select(r => r.Id == created.Id ? r with { Next = 150 } : r).ToList(), agent);
        all = await CurrentRowsAsync(s);
        var lowered = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.SaveAsync(all.Select(r => r.Id == created.Id ? r with { Next = 10 } : r).ToList(), agent));
        Assert.Equal("sequence-next-below-current", lowered.Code);

        // Deleting the seeded (internal + settings-referenced) ticket sequence is refused.
        var ticketSeqId = int.Parse((await s.Get<ISettingsService>().GetAsync("tickets", "sequence_id"))!);
        all = await CurrentRowsAsync(s);
        var inUse = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.SaveAsync(all.Where(r => r.Id != ticketSeqId).ToList(), agent));
        Assert.Equal("sequence-in-use", inUse.Code);

        // The unreferenced test row deletes cleanly, and drawing still works after.
        all = await CurrentRowsAsync(s);
        await service.SaveAsync(all.Where(r => r.Id != created.Id).ToList(), agent);
        Assert.False(await s.Db.Sequences.AnyAsync(x => x.Id == created.Id));
        var drawn = await service.NextAsync(ticketSeqId, "R######");
        Assert.Matches(new Regex("^R\\d{6}$"), drawn);
    }

    private static async Task<List<SequenceRow>> CurrentRowsAsync(ServiceScopeBundle s) =>
        await s.Db.Sequences.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new SequenceRow(x.Id, x.Name, x.Next)).ToListAsync();

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
