using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Areas.Agent.Controllers;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 agent/ticket-view: the B8 effort loop driven through the controller path
/// (propose → revise → withdraw) so routing/actor/toast wiring is covered on top
/// of the S4 service suite, plus the composer POSTs appending real entries.
/// </summary>
[Collection("Postgres")]
public class AgentTicketViewTests(PostgresFixture fixture)
{
    private static TicketViewController CreateController(ServiceScopeBundle s, Staff staff) =>
        new(s.Db, s.Get<IQueueEngine>(), s.Get<IThreadService>(), s.Get<ITicketService>(),
            s.Get<IEffortProposalService>(), s.Get<ICannedResponseService>(),
            s.Get<IPermissionService>(), s.Get<ISettingsService>(), s.Get<IFileStore>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, staff.IdentityUserId!.Value.ToString())], "test")),
                },
            },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider()),
        };

    private async Task<int> CreateTicketAsync(ServiceScopeBundle s)
    {
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var ticket = await s.Get<ITicketService>().CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Ticket-view efor testi {Guid.NewGuid():N}",
            Body = "<p>Test içeriği</p>",
        }, owner);
        return ticket.Id;
    }

    [Fact]
    public async Task EffortLoop_ProposeReviseWithdraw_ThroughController()
    {
        int ticketId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            ticketId = await CreateTicketAsync(s);
            var staff = await s.Db.Staff.SingleAsync(x => x.Username == "uakin");
            var controller = CreateController(s, staff);

            // Propose (dialog posts hours with the browser's invariant dot).
            var propose = await controller.Effort(ticketId, "6.5", "Düzenleme + test dahil", default);
            Assert.IsType<RedirectToActionResult>(propose);
            Assert.Equal("tv.toastEffort", controller.TempData["TvToast"]);

            // Same dialog while pending → revise.
            controller.TempData.Clear();
            var revise = await controller.Effort(ticketId, "8", "Kapsam genişledi", default);
            Assert.IsType<RedirectToActionResult>(revise);
            Assert.Equal("tv.toastEffortRevised", controller.TempData["TvToast"]);

            // Banner "Geri Çek".
            controller.TempData.Clear();
            var withdraw = await controller.Withdraw(ticketId, default);
            Assert.IsType<RedirectToActionResult>(withdraw);
            Assert.Equal("tv.toastWithdrawn", controller.TempData["TvToast"]);
        }

        // Assert via a fresh scope (EF identity map returns stale entities otherwise).
        using var fresh = new ServiceScopeBundle(fixture);
        var proposals = await fresh.Db.EffortProposals
            .Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.RevisionNo)
            .ToListAsync();
        Assert.Equal(2, proposals.Count);
        Assert.Equal(EffortState.Superseded, proposals[0].State);
        Assert.Equal(6.5m, proposals[0].Hours);
        Assert.Equal(EffortState.Withdrawn, proposals[1].State);
        Assert.Equal(8m, proposals[1].Hours);

        // Every transition left its B8 thread event.
        var threadId = await fresh.Db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => t.ThreadId).SingleAsync();
        var eventNames = await fresh.Db.ThreadEvents
            .Where(ev => ev.ThreadId == threadId)
            .Select(ev => ev.EventType!.Name)
            .ToListAsync();
        Assert.Contains("effort-proposed", eventNames);
        Assert.Contains("effort-revised", eventNames);
        Assert.Contains("effort-withdrawn", eventNames);
    }

    [Fact]
    public async Task Composers_AppendResponseAndNote_AndApplyStatus()
    {
        int ticketId;
        int waitStatusId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            ticketId = await CreateTicketAsync(s);
            waitStatusId = await s.Db.TicketStatuses.Where(x => x.Key == "wait")
                .Select(x => x.Id).SingleAsync();
            var staff = await s.Db.Staff.SingleAsync(x => x.Username == "uakin");
            var controller = CreateController(s, staff);

            var reply = await controller.Reply(ticketId, "Merhaba, inceliyoruz.",
                fromAccountId: null, sig: "none", statusId: waitStatusId, attachments: [], default);
            Assert.IsType<RedirectToActionResult>(reply);
            Assert.Equal("tv.toastReply", controller.TempData["TvToast"]);

            controller.TempData.Clear();
            var note = await controller.Note(ticketId, "İç kontrol", "Parametre kaydına bakılacak.",
                statusId: null, default);
            Assert.IsType<RedirectToActionResult>(note);
            Assert.Equal("tv.toastNote", controller.TempData["TvToast"]);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var ticket = await fresh.Db.Tickets.SingleAsync(t => t.Id == ticketId);
        Assert.Equal(waitStatusId, ticket.StatusId); // after-reply status applied
        Assert.True(ticket.IsAnswered);

        var entries = await fresh.Db.ThreadEntries
            .Where(e => e.ThreadId == ticket.ThreadId)
            .OrderBy(e => e.Id)
            .ToListAsync();
        Assert.Contains(entries, e => e.Type == ThreadEntryType.Response && e.Body.Contains("inceliyoruz"));
        Assert.Contains(entries, e => e.Type == ThreadEntryType.Note && e.Title == "İç kontrol");
    }

    /// <summary>Minimal ITempDataProvider so controller TempData works without a session.</summary>
    private sealed class NullTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
