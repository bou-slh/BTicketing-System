using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 3: the recurring sweep jobs invoked directly (no Hangfire server in
/// tests) with a fixed <see cref="TimeProvider"/>. All test rows live around
/// 2019/2020 — far before the seeded 2026 canon — so the global sweeps never mark
/// seeded rows; the ticket sweep's EstimatedDueDate recompute DOES touch every open
/// SLA ticket by design, so <see cref="RunTicketSweepAsync"/> snapshots and restores
/// the shared seed state around each run (fixture contract: shared rows stay
/// pristine for other tests).
/// </summary>
[Collection("Postgres")]
public class SweepJobsTests(PostgresFixture fixture)
{
    /// <summary>Fixed "now" for most tests: 2020-01-01 00:00 UTC.</summary>
    private static readonly DateTimeOffset Now = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- helpers ----------------------------------------------------------------------

    private IReadOnlyList<CapturedEmail> Sent => fixture.Factory.Emails.Sent;

    private void Clear()
    {
        fixture.Factory.Emails.Clear();
        fixture.Factory.Transport.Clear();
    }

    private static async Task<Ticket> AddTicketAsync(AppDbContext db, int? slaId,
        DateTimeOffset created, DateTimeOffset? estimatedDue = null, bool overdue = false,
        DateTimeOffset? hardDue = null)
    {
        var t = new Ticket
        {
            Number = $"SWP{Guid.NewGuid():N}"[..11],
            Subject = "Sweep testi",
            UserId = await db.Users.Where(u => u.Name == "Bourla Salehi").Select(u => u.Id).SingleAsync(),
            StatusId = await db.TicketStatuses.Where(s => s.Key == "open").Select(s => s.Id).SingleAsync(),
            DepartmentId = await db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync(),
            SlaId = slaId,
            CreatedAt = created,
            EstimatedDueDate = estimatedDue,
            IsOverdue = overdue,
            DueDate = hardDue,
            Thread = new Thread { CreatedAt = created },
        };
        db.Tickets.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Own SLA plan: 09:00–17:00 UTC daily schedule, or schedule-less.</summary>
    private static async Task<SlaPlan> AddSlaAsync(AppDbContext db, int graceHours, bool withSchedule)
    {
        var sla = new SlaPlan
        {
            Name = $"Sweep SLA {Guid.NewGuid():N}"[..20],
            GracePeriodHours = graceHours,
            Schedule = withSchedule
                ? new Schedule
                {
                    Name = $"Sweep 09-17 {Guid.NewGuid():N}"[..20],
                    Timezone = "UTC",
                    Entries =
                    [
                        new ScheduleEntry
                        {
                            Name = "Gün", Repeats = ScheduleRepeat.Daily,
                            StartsAt = new TimeOnly(9, 0), EndsAt = new TimeOnly(17, 0), Sort = 1,
                        },
                    ],
                }
                : null,
        };
        db.SlaPlans.Add(sla);
        await db.SaveChangesAsync();
        return sla;
    }

    /// <summary>Runs the SLA sweep and restores every shared (non-own) ticket's
    /// EstimatedDueDate/IsOverdue afterwards — the recompute pass is global.</summary>
    private async Task RunTicketSweepAsync(DateTimeOffset now, params int[] ownIds)
    {
        await using (var db = fixture.CreateContext())
        {
            var before = await db.Tickets.AsNoTracking()
                .Where(t => !ownIds.Contains(t.Id))
                .Select(t => new { t.Id, t.EstimatedDueDate, t.IsOverdue })
                .ToListAsync();

            using (var s = new ServiceScopeBundle(fixture))
            {
                var job = new SlaOverdueSweepJob(s.Db, s.Get<ISettingsService>(),
                    s.Get<IThreadService>(), s.Get<IDomainEventDispatcher>(),
                    new FixedTime(now), NullLogger<SlaOverdueSweepJob>.Instance);
                await job.RunAsync(CancellationToken.None);
            }

            var map = before.ToDictionary(x => x.Id);
            var ids = map.Keys.ToList();
            var rows = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync();
            foreach (var row in rows)
            {
                if (!map.TryGetValue(row.Id, out var b))
                    continue;
                row.EstimatedDueDate = b.EstimatedDueDate;
                row.IsOverdue = b.IsOverdue;
            }
            await db.SaveChangesAsync();
        }
    }

    private async Task RunTaskSweepAsync(DateTimeOffset now)
    {
        using var s = new ServiceScopeBundle(fixture);
        var job = new TaskOverdueSweepJob(s.Db, s.Get<IThreadService>(),
            s.Get<IDomainEventDispatcher>(), new FixedTime(now),
            NullLogger<TaskOverdueSweepJob>.Instance);
        await job.RunAsync(CancellationToken.None);
    }

    // ---- SLA sweep: schedule-aware recompute ------------------------------------------

    [Fact]
    public async Task SlaSweep_RecomputesDue_SchedulePausesClock_NoScheduleStaysWallClock()
    {
        using var s = new ServiceScopeBundle(fixture);
        var scheduled = await AddSlaAsync(s.Db, graceHours: 4, withSchedule: true);
        var wallClock = await AddSlaAsync(s.Db, graceHours: 4, withSchedule: false);
        // Created 15:00 — the 09–17 schedule leaves 2h that day; the remaining 2h
        // run the next morning: due 11:00. Wall clock would say 19:00 the same day.
        var created = new DateTimeOffset(2019, 12, 30, 15, 0, 0, TimeSpan.Zero);
        var t1 = await AddTicketAsync(s.Db, scheduled.Id, created);
        var t2 = await AddTicketAsync(s.Db, wallClock.Id, created);

        await RunTicketSweepAsync(Now, t1.Id, t2.Id);

        await using var check = fixture.CreateContext();
        Assert.Equal(new DateTimeOffset(2019, 12, 31, 11, 0, 0, TimeSpan.Zero),
            (await check.Tickets.SingleAsync(x => x.Id == t1.Id)).EstimatedDueDate);
        Assert.Equal(new DateTimeOffset(2019, 12, 30, 19, 0, 0, TimeSpan.Zero),
            (await check.Tickets.SingleAsync(x => x.Id == t2.Id)).EstimatedDueDate);
    }

    // ---- SLA sweep: mark overdue + alert exactly once ---------------------------------

    [Fact]
    public async Task SlaSweep_MarksOverdue_AlertsOnce_SecondRunSilent()
    {
        await using var overdueOn = await SettingOverride.SetAsync(fixture, "alerts", "overdue", "true");
        await using var mgrOn = await SettingOverride.SetAsync(fixture, "alerts", "overdue_dept_manager", "true");

        using var s = new ServiceScopeBundle(fixture);
        var sla = await AddSlaAsync(s.Db, graceHours: 1, withSchedule: false);
        var ticket = await AddTicketAsync(s.Db, sla.Id, created: Now.AddHours(-3));

        Clear();
        await RunTicketSweepAsync(Now, ticket.Id);

        await using (var check = fixture.CreateContext())
        {
            var row = await check.Tickets.SingleAsync(x => x.Id == ticket.Id);
            Assert.True(row.IsOverdue);
            Assert.Equal(Now.AddHours(-2), row.EstimatedDueDate); // create + 1h grace
            // osTicket markOverdue logs the "overdue" thread event.
            Assert.True(await check.ThreadEvents
                .AnyAsync(e => e.ThreadId == row.ThreadId && e.EventType!.Name == "overdue"));
        }
        // TicketOverdue raised → mail handler alerted the department manager.
        Assert.Contains(Sent, m => m.Subject == "Gecikme Uyarısı" && m.To == "merve.cetin@rapidsol.com.tr");

        // Second sweep: the flag guards the once-semantics — nothing new is raised.
        Clear();
        await RunTicketSweepAsync(Now, ticket.Id);
        Assert.DoesNotContain(Sent, m => m.Subject == "Gecikme Uyarısı");
    }

    [Fact]
    public async Task SlaSweep_HardDueDateWins_OverSlaDue()
    {
        using var s = new ServiceScopeBundle(fixture);
        var sla = await AddSlaAsync(s.Db, graceHours: 1, withSchedule: false);
        // SLA due is past, but the agent's hard due date is in the future — osTicket
        // checkOverdue only consults est_duedate when no hard due date exists.
        var ticket = await AddTicketAsync(s.Db, sla.Id, created: Now.AddHours(-5),
            hardDue: Now.AddDays(2));

        await RunTicketSweepAsync(Now, ticket.Id);

        await using var check = fixture.CreateContext();
        Assert.False((await check.Tickets.SingleAsync(x => x.Id == ticket.Id)).IsOverdue);
    }

    // ---- SLA sweep: flag clear rules --------------------------------------------------

    [Fact]
    public async Task SlaSweep_RecomputeIntoFuture_ClearsStaleOverdueFlag()
    {
        using var s = new ServiceScopeBundle(fixture);
        var sla = await AddSlaAsync(s.Db, graceHours: 4, withSchedule: true);
        // Wall-clock stamp said 19:00 (past at 20:00) and the ticket got flagged;
        // the schedule-aware recompute moves the due to tomorrow 11:00 → flag clears
        // (osTicket updateEstDueDate parity).
        var created = new DateTimeOffset(2019, 12, 30, 15, 0, 0, TimeSpan.Zero);
        var ticket = await AddTicketAsync(s.Db, sla.Id, created,
            estimatedDue: created.AddHours(4), overdue: true);

        var now = new DateTimeOffset(2019, 12, 30, 20, 0, 0, TimeSpan.Zero);
        await RunTicketSweepAsync(now, ticket.Id);

        await using var check = fixture.CreateContext();
        var row = await check.Tickets.SingleAsync(x => x.Id == ticket.Id);
        Assert.Equal(new DateTimeOffset(2019, 12, 31, 11, 0, 0, TimeSpan.Zero), row.EstimatedDueDate);
        Assert.False(row.IsOverdue);

        // Cleanup: close the ticket so its (deliberately) past due date cannot be
        // re-marked by another test's sweep run — the shared DB outlives this test.
        row.StatusId = await check.TicketStatuses.Where(x => x.Key == "closed").Select(x => x.Id).SingleAsync();
        row.ClosedAt = now;
        await check.SaveChangesAsync();
    }

    [Fact]
    public async Task TicketClose_ClearsOverdueFlag()
    {
        // osTicket clears the overdue flag when a ticket closes (setStatus 'closed'
        // → clearOverdue); replies do NOT clear it. Verifies the modeled rule.
        await using var topicRule = await SettingOverride.SetAsync(
            fixture, "tickets", "require_topic_to_close", "false");
        using var s = new ServiceScopeBundle(fixture);
        var ticket = await AddTicketAsync(s.Db, slaId: null, created: Now.AddDays(-1), overdue: true);
        var closed = await s.Db.TicketStatuses.SingleAsync(x => x.Key == "closed");

        await s.Get<ITicketService>().TransitionStatusAsync(ticket.Id, closed.Id, ActorContext.System);

        await using var check = fixture.CreateContext();
        Assert.False((await check.Tickets.SingleAsync(x => x.Id == ticket.Id)).IsOverdue);
    }

    // ---- task sweep -------------------------------------------------------------------

    [Fact]
    public async Task TaskSweep_MarksOverdue_AlertsOnce_SecondRunSilent()
    {
        await using var masterOn = await SettingOverride.SetAsync(fixture, "alerts", "task_overdue", "true");
        await using var assignedOn = await SettingOverride.SetAsync(fixture, "alerts", "task_overdue_assigned", "true");

        using var s = new ServiceScopeBundle(fixture);
        var task = new TaskItem
        {
            Number = $"TSW{Guid.NewGuid():N}"[..11],
            Title = "Sweep görevi",
            DepartmentId = await s.Db.Departments.Where(d => d.Name == "Destek").Select(d => d.Id).SingleAsync(),
            StaffId = await s.Db.Staff.Where(x => x.Username == "uakin").Select(x => x.Id).SingleAsync(),
            CreatedAt = Now.AddDays(-2),
            DueDate = Now.AddDays(-1),
            Thread = new Thread { CreatedAt = Now.AddDays(-2) },
        };
        s.Db.TaskItems.Add(task);
        await s.Db.SaveChangesAsync();

        Clear();
        await RunTaskSweepAsync(Now);

        await using (var check = fixture.CreateContext())
        {
            var row = await check.TaskItems.SingleAsync(x => x.Id == task.Id);
            Assert.True(row.IsOverdue);
            Assert.True(await check.ThreadEvents
                .AnyAsync(e => e.ThreadId == row.ThreadId && e.EventType!.Name == "overdue"));
        }
        Assert.Contains(Sent, m => m.Subject == "Görev Gecikme Uyarısı" && m.To == "umit.akin@rapidsol.com.tr");

        Clear();
        await RunTaskSweepAsync(Now);
        Assert.DoesNotContain(Sent, m => m.Subject == "Görev Gecikme Uyarısı");
    }

    // ---- effort reminder --------------------------------------------------------------

    [Fact]
    public async Task EffortReminder_RemindsStalePending_TracksSentAt_SkipsYoung()
    {
        await using var enabled = await SettingOverride.SetAsync(fixture, "effort", "enabled", "true");
        await using var days = await SettingOverride.SetAsync(fixture, "effort", "reminder_days", "3");
        await using var master = await SettingOverride.SetAsync(fixture, "autoresp", "effort_proposal", "true");

        using var s = new ServiceScopeBundle(fixture);
        var proposer = await s.Db.Staff.Where(x => x.Username == "uakin").Select(x => x.Id).SingleAsync();
        var staleTicket = await AddTicketAsync(s.Db, slaId: null, created: Now.AddDays(-10));
        var youngTicket = await AddTicketAsync(s.Db, slaId: null, created: Now.AddDays(-10));
        var stale = new EffortProposal
        {
            TicketId = staleTicket.Id, RevisionNo = 1, Hours = 5,
            ProposedByStaffId = proposer, CreatedAt = Now.AddDays(-5),
        };
        var young = new EffortProposal
        {
            TicketId = youngTicket.Id, RevisionNo = 1, Hours = 2,
            ProposedByStaffId = proposer, CreatedAt = Now.AddDays(-2),
        };
        s.Db.EffortProposals.AddRange(stale, young);
        await s.Db.SaveChangesAsync();

        async Task RunAsync()
        {
            using var scope = new ServiceScopeBundle(fixture);
            var job = new EffortReminderJob(scope.Db, scope.Get<ISettingsService>(),
                scope.Get<IEmailTemplateRenderer>(), scope.Get<IMailQueue>(),
                new FixedTime(Now), NullLogger<EffortReminderJob>.Instance);
            await job.RunAsync(CancellationToken.None);
        }

        Clear();
        await RunAsync();

        // Stale proposal → one effort.request re-send to the owner; young untouched.
        var reminder = Sent.Single(m => m.Subject == "Efor Onayı İsteği");
        Assert.Equal("bourla.salehi@ulasim.com.tr", reminder.To);
        await using (var check = fixture.CreateContext())
        {
            Assert.Equal(Now, (await check.EffortProposals.SingleAsync(p => p.Id == stale.Id)).ReminderSentAt);
            Assert.Null((await check.EffortProposals.SingleAsync(p => p.Id == young.Id)).ReminderSentAt);
        }

        // Same day again: ReminderSentAt keeps the cadence — no double send.
        Clear();
        await RunAsync();
        Assert.DoesNotContain(Sent, m => m.Subject == "Efor Onayı İsteği");
    }

    // ---- retention --------------------------------------------------------------------

    [Fact]
    public async Task Retention_PurgesOldRows_KeepsYoungAndUnsent_ZeroMeansNever()
    {
        var now = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);
        int oldLog, youngLog, oldSent, oldPending, youngSent;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var l1 = new SystemLogEntry { Type = SystemLogType.Error, Title = "Sweep eski", CreatedAt = now.AddMonths(-5) };
            var l2 = new SystemLogEntry { Type = SystemLogType.Error, Title = "Sweep yeni", CreatedAt = now.AddDays(-10) };
            var m1 = new EmailOutbound { ToAddress = "a@x.tr", Subject = "s", HtmlBody = "b", Status = EmailOutboundStatus.Sent, SentAt = now.AddMonths(-5), CreatedAt = now.AddMonths(-5) };
            var m2 = new EmailOutbound { ToAddress = "a@x.tr", Subject = "s", HtmlBody = "b", Status = EmailOutboundStatus.Pending, CreatedAt = now.AddMonths(-5) };
            var m3 = new EmailOutbound { ToAddress = "a@x.tr", Subject = "s", HtmlBody = "b", Status = EmailOutboundStatus.Sent, SentAt = now.AddDays(-10), CreatedAt = now.AddDays(-10) };
            s.Db.SystemLogEntries.AddRange(l1, l2);
            s.Db.EmailOutbounds.AddRange(m1, m2, m3);
            await s.Db.SaveChangesAsync();
            (oldLog, youngLog, oldSent, oldPending, youngSent) = (l1.Id, l2.Id, m1.Id, m2.Id, m3.Id);
        }

        async Task RunAsync()
        {
            using var scope = new ServiceScopeBundle(fixture);
            var job = new RetentionPurgeJob(scope.Db, scope.Get<ISettingsService>(),
                scope.Get<ISystemLogService>(), new FixedTime(now),
                NullLogger<RetentionPurgeJob>.Instance);
            await job.RunAsync(CancellationToken.None);
        }

        // 0 = "Asla": nothing is purged.
        await using (await SettingOverride.SetAsync(fixture, "system", "log_purge_months", "0"))
        {
            await RunAsync();
            await using var check = fixture.CreateContext();
            Assert.True(await check.SystemLogEntries.AnyAsync(e => e.Id == oldLog));
            Assert.True(await check.EmailOutbounds.AnyAsync(e => e.Id == oldSent));
        }

        // 3 months: old syslog + old SENT outbox rows go; young and Pending stay.
        await using (await SettingOverride.SetAsync(fixture, "system", "log_purge_months", "3"))
        {
            await RunAsync();
            await using var check = fixture.CreateContext();
            Assert.False(await check.SystemLogEntries.AnyAsync(e => e.Id == oldLog));
            Assert.True(await check.SystemLogEntries.AnyAsync(e => e.Id == youngLog));
            Assert.False(await check.EmailOutbounds.AnyAsync(e => e.Id == oldSent));
            Assert.True(await check.EmailOutbounds.AnyAsync(e => e.Id == oldPending));
            Assert.True(await check.EmailOutbounds.AnyAsync(e => e.Id == youngSent));
        }
    }
}
