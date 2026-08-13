using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S4 gate namesake: the full B8 effort lifecycle over the real service graph and
/// database — propose/revise/withdraw/approve/reject, settings gates, thread events,
/// emails, and the work gate.
/// </summary>
[Collection("Postgres")]
public class EffortLifecycleTests(PostgresFixture fixture)
{
    private async Task<(int TicketId, ActorContext Agent, ActorContext Owner)> CreateTicketAsync(ServiceScopeBundle s)
    {
        var agent = await TestActors.StaffAsync(s.Db, "uakin");
        var owner = await TestActors.UserAsync(s.Db, "Bourla Salehi");
        var tickets = s.Get<ITicketService>();
        var ticket = await tickets.CreateAsync(new TicketCreateRequest
        {
            UserId = owner.Id!.Value,
            Subject = $"Efor testi {Guid.NewGuid():N}",
            Body = "<p>Test içeriği</p>",
        }, owner);
        return (ticket.Id, agent, owner);
    }

    [Fact]
    public async Task FullLifecycle_ProposeThenApprove()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        fixture.Factory.Emails.Clear();

        var proposal = await efforts.ProposeAsync(ticketId, 6, "Düzenleme + test dahil", agent);
        Assert.Equal(EffortState.Pending, proposal.State);
        Assert.Equal(1, proposal.RevisionNo);

        // effort.request email went to the ticket owner.
        var request = Assert.Single(fixture.Factory.Emails.Sent);
        Assert.Equal("bourla.salehi@ulasim.com.tr", request.To);
        Assert.Contains("Efor Onayı İsteği", request.Subject);

        await efforts.ApproveAsync(ticketId, owner);

        var active = await efforts.GetActiveAsync(ticketId);
        Assert.Equal(EffortState.Approved, active!.State);
        Assert.Equal(owner.Id, active.DecidedByUserId);
        Assert.NotNull(active.DecidedAt);

        // effort.response email went to the proposing agent.
        Assert.Contains(fixture.Factory.Emails.Sent, m => m.To == "umit.akin@rapidsol.com.tr");

        // Thread timeline carries both effort events.
        var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();
        var eventNames = await s.Db.ThreadEvents.Where(e => e.ThreadId == threadId)
            .Join(s.Db.ThreadEventTypes, e => e.EventTypeId, t => t.Id, (e, t) => t.Name).ToListAsync();
        Assert.Contains("effort-proposed", eventNames);
        Assert.Contains("effort-approved", eventNames);

        // Every mutation audited.
        Assert.True(await s.Db.AuditEvents.AnyAsync(a => a.ObjectType == nameof(EffortProposal)));
    }

    [Fact]
    public async Task RevisionLoop_RejectProposeAgainApprove()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();

        await efforts.ProposeAsync(ticketId, 8, null, agent);
        await efforts.RejectAsync(ticketId, "Kapsam netleşmeden onay veremiyoruz.", owner);

        var rejected = await efforts.GetActiveAsync(ticketId);
        Assert.Equal(EffortState.Rejected, rejected!.State);
        Assert.Equal("Kapsam netleşmeden onay veremiyoruz.", rejected.DecisionNote);

        var second = await efforts.ProposeAsync(ticketId, 5, "Kapsam daraltıldı", agent);
        Assert.Equal(2, second.RevisionNo);
        await efforts.ApproveAsync(ticketId, owner);

        var revisions = await s.Db.EffortProposals.Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.RevisionNo).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(EffortState.Rejected, revisions[0].State);
        Assert.Equal(EffortState.Approved, revisions[1].State);
    }

    [Fact]
    public async Task Revise_SupersedesThePendingRevision()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, _) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();

        await efforts.ProposeAsync(ticketId, 8, null, agent);
        var revised = await efforts.ReviseAsync(ticketId, 6, "İndirimli kapsam", agent);

        Assert.Equal(2, revised.RevisionNo);
        Assert.Equal(EffortState.Pending, revised.State);
        var first = await s.Db.EffortProposals.SingleAsync(p => p.TicketId == ticketId && p.RevisionNo == 1);
        Assert.Equal(EffortState.Superseded, first.State);
    }

    [Fact]
    public async Task Withdraw_EndsThePendingProposal()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, _) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();

        await efforts.ProposeAsync(ticketId, 4, null, agent);
        await efforts.WithdrawAsync(ticketId, agent);

        Assert.Equal(EffortState.Withdrawn, (await efforts.GetActiveAsync(ticketId))!.State);
    }

    [Fact]
    public async Task AutoApprove_UnderThreshold_SkipsRequestEmail()
    {
        await using var _ = await SettingOverride.SetAsync(fixture, "effort", "auto_approve_threshold_hours", "8");
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, _) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        fixture.Factory.Emails.Clear();

        var proposal = await efforts.ProposeAsync(ticketId, 6, null, agent);

        Assert.Equal(EffortState.Approved, proposal.State);
        Assert.Null(proposal.DecidedByUserId); // system-decided
        Assert.DoesNotContain(fixture.Factory.Emails.Sent, m => m.Subject.Contains("Efor Onayı İsteği"));

        // Over the threshold still goes to the customer.
        var (ticket2, _, _) = await CreateTicketAsync(s);
        var manual = await efforts.ProposeAsync(ticket2, 9, null, agent);
        Assert.Equal(EffortState.Pending, manual.State);
    }

    [Fact]
    public async Task RevisionLimit_BlocksFurtherProposals()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();

        for (var i = 0; i < 3; i++) // seeded effort.revision_limit = 3
        {
            await efforts.ProposeAsync(ticketId, 5 + i, null, agent);
            await efforts.RejectAsync(ticketId, "olmadı", owner);
        }

        var ex = await Assert.ThrowsAsync<EffortException>(() => efforts.ProposeAsync(ticketId, 9, null, agent));
        Assert.Equal(EffortError.RevisionLimitReached, ex.Code);
    }

    [Fact]
    public async Task Reject_NoteGate_BothDirections()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        await efforts.ProposeAsync(ticketId, 6, null, agent);

        // Seeded mandatory_reject_note = true → blank note denied.
        var ex = await Assert.ThrowsAsync<EffortException>(() => efforts.RejectAsync(ticketId, "  ", owner));
        Assert.Equal(EffortError.NoteRequired, ex.Code);

        await using (await SettingOverride.SetAsync(fixture, "effort", "mandatory_reject_note", "false"))
        {
            using var s2 = new ServiceScopeBundle(fixture);
            await s2.Get<IEffortProposalService>().RejectAsync(ticketId, null, owner);
        }

        // Fresh scope: the original context still tracks the pre-reject snapshot.
        using var s3 = new ServiceScopeBundle(fixture);
        Assert.Equal(EffortState.Rejected, (await s3.Get<IEffortProposalService>().GetActiveAsync(ticketId))!.State);
    }

    [Fact]
    public async Task OnlyTheTicketOwnerDecides()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, _) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        await efforts.ProposeAsync(ticketId, 6, null, agent);

        var otherUser = await TestActors.UserAsync(s.Db, "Mehmet Demir");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => efforts.ApproveAsync(ticketId, otherUser));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => efforts.ApproveAsync(ticketId, agent));
    }

    [Fact]
    public async Task AgentWithoutDeptAccess_CannotPropose()
    {
        using var s = new ServiceScopeBundle(fixture);
        // Ticket routes to Destek by default; mcetin's only department is Danışmanlık.
        var (ticketId, _, _) = await CreateTicketAsync(s);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");

        await Assert.ThrowsAsync<PermissionDeniedException>(
            () => s.Get<IEffortProposalService>().ProposeAsync(ticketId, 6, null, mcetin));
    }

    [Fact]
    public async Task DisabledGate_BlocksNewProposals()
    {
        await using var _ = await SettingOverride.SetAsync(fixture, "effort", "enabled", "false");
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, _) = await CreateTicketAsync(s);

        var ex = await Assert.ThrowsAsync<EffortException>(
            () => s.Get<IEffortProposalService>().ProposeAsync(ticketId, 6, null, agent));
        Assert.Equal(EffortError.Disabled, ex.Code);
    }

    [Fact]
    public async Task DoubleApprove_SecondLoses()
    {
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        await efforts.ProposeAsync(ticketId, 6, null, agent);

        await efforts.ApproveAsync(ticketId, owner);
        using var s2 = new ServiceScopeBundle(fixture);
        var ex = await Assert.ThrowsAsync<EffortException>(
            () => s2.Get<IEffortProposalService>().ApproveAsync(ticketId, owner));
        Assert.Equal(EffortError.NotPending, ex.Code);
    }

    [Fact]
    public async Task BlockWorkGate_HoldsResponsesAndClosingUntilApproved()
    {
        await using var _ = await SettingOverride.SetAsync(fixture, "effort", "block_work_until_approved", "true");
        using var s = new ServiceScopeBundle(fixture);
        var (ticketId, agent, owner) = await CreateTicketAsync(s);
        var efforts = s.Get<IEffortProposalService>();
        var tickets = s.Get<ITicketService>();
        var threads = s.Get<IThreadService>();

        await efforts.ProposeAsync(ticketId, 6, null, agent);
        Assert.False(await tickets.IsWorkAllowedAsync(ticketId));

        var threadId = await s.Db.Tickets.Where(t => t.Id == ticketId).Select(t => t.ThreadId).SingleAsync();

        // Staff response and closing are blocked; the customer can still write.
        await Assert.ThrowsAsync<WorkBlockedByEffortException>(() =>
            threads.PostAsync(threadId, ThreadEntryType.Response, "<p>çalışma çıktısı</p>", agent));
        var solvedId = await s.Db.TicketStatuses.Where(st => st.Key == "solved").Select(st => st.Id).SingleAsync();
        await Assert.ThrowsAsync<WorkBlockedByEffortException>(() =>
            tickets.TransitionStatusAsync(ticketId, solvedId, agent));
        await threads.PostAsync(threadId, ThreadEntryType.Message, "<p>müşteri mesajı</p>", owner);
        await threads.PostAsync(threadId, ThreadEntryType.Note, "<p>iç not</p>", agent);

        await efforts.ApproveAsync(ticketId, owner);
        Assert.True(await tickets.IsWorkAllowedAsync(ticketId));
        await threads.PostAsync(threadId, ThreadEntryType.Response, "<p>şimdi serbest</p>", agent);
    }
}
