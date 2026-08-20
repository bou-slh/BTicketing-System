using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One alert/notice recipient. <see cref="StaffId"/> is null for the
/// admin-email pseudo recipient and for end-user recipients (owner/collaborators).</summary>
public sealed record MailRecipient(string Name, string Email, int? StaffId = null, int? UserId = null);

/// <summary>
/// S8 alert fan-out recipient resolution (osTicket class.ticket.php alert recipient
/// semantics, modeled — never copied). Staff recipients are "available" only:
/// active, not on vacation, with an email address. Callers own dedupe + skip-actor
/// (the resolver returns honest raw sets).
/// </summary>
public interface IAlertRecipientResolver
{
    /// <summary>The department manager, when available.</summary>
    Task<MailRecipient?> DeptManagerAsync(int departmentId, CancellationToken ct = default);

    /// <summary>
    /// Department members for alerts, honoring the department's AlertGroup
    /// (department-edit de.alertGroup ↔ osTicket group_membership): All = primary
    /// members only, MembersAndPrimary = primary + extended-access members,
    /// ManagerOnly = the manager alone. Per-member alert opt-outs
    /// (Staff.PrimaryDepartmentAlerts / StaffDepartmentAccess.AlertsEnabled) apply;
    /// the manager is NOT part of the member expansion (own checkbox) except in
    /// ManagerOnly mode.
    /// </summary>
    Task<IReadOnlyList<MailRecipient>> DeptMembersAsync(int departmentId, CancellationToken ct = default);

    /// <summary>Team fan-out (osTicket onAssign): nothing when the team has NoAlerts;
    /// members (alert-enabled) when <paramref name="members"/>, else the lead when
    /// <paramref name="lead"/> (members checkbox wins over lead, osTicket elseif).</summary>
    Task<IReadOnlyList<MailRecipient>> TeamAsync(int teamId, bool members, bool lead, CancellationToken ct = default);

    /// <summary>The organization account manager of the given end user's org.</summary>
    Task<MailRecipient?> AccountManagerAsync(int userId, CancellationToken ct = default);

    /// <summary>Staff of the latest Response entry on the thread (osTicket getLastRespondent).</summary>
    Task<MailRecipient?> LastRespondentAsync(int threadId, CancellationToken ct = default);

    /// <summary>A single staff member, when available.</summary>
    Task<MailRecipient?> StaffAsync(int staffId, CancellationToken ct = default);

    /// <summary>Ticket owner with their default email.</summary>
    Task<MailRecipient?> OwnerAsync(int ticketId, CancellationToken ct = default);

    /// <summary>Active collaborators on a thread with their default emails.</summary>
    Task<IReadOnlyList<MailRecipient>> CollaboratorsAsync(int threadId, CancellationToken ct = default);
}

public sealed class AlertRecipientResolver(AppDbContext db) : IAlertRecipientResolver
{
    public async Task<MailRecipient?> DeptManagerAsync(int departmentId, CancellationToken ct = default)
    {
        var managerId = await db.Departments.Where(d => d.Id == departmentId)
            .Select(d => d.ManagerStaffId).SingleOrDefaultAsync(ct);
        return managerId is { } id ? await StaffAsync(id, ct) : null;
    }

    public async Task<IReadOnlyList<MailRecipient>> DeptMembersAsync(int departmentId, CancellationToken ct = default)
    {
        var dept = await db.Departments.Where(d => d.Id == departmentId)
            .Select(d => new { d.AlertGroup, d.ManagerStaffId })
            .SingleOrDefaultAsync(ct);
        if (dept is null)
            return [];
        if (dept.AlertGroup == DepartmentAlertGroup.ManagerOnly)
            return await DeptManagerAsync(departmentId, ct) is { } manager ? [manager] : [];

        // Primary members with the primary-department alert flag on.
        var members = await db.Staff
            .Where(s => s.DepartmentId == departmentId && s.PrimaryDepartmentAlerts
                && s.IsActive && !s.OnVacation && s.Email != null && s.Id != dept.ManagerStaffId)
            .Select(s => new MailRecipient(s.FirstName + " " + s.LastName, s.Email!, s.Id, null))
            .ToListAsync(ct);

        // Extended-access members join only in MembersAndPrimary mode
        // (osTicket ALERTS_DEPT_AND_EXTENDED; per-row Uyarılar checkbox).
        if (dept.AlertGroup == DepartmentAlertGroup.MembersAndPrimary)
        {
            members.AddRange(await db.Set<StaffDepartmentAccess>()
                .Where(a => a.DepartmentId == departmentId && a.AlertsEnabled
                    && a.Staff!.IsActive && !a.Staff.OnVacation && a.Staff.Email != null
                    && a.StaffId != dept.ManagerStaffId)
                .Select(a => new MailRecipient(a.Staff!.FirstName + " " + a.Staff.LastName, a.Staff.Email!, a.StaffId, null))
                .ToListAsync(ct));
        }
        return members;
    }

    public async Task<IReadOnlyList<MailRecipient>> TeamAsync(int teamId, bool members, bool lead, CancellationToken ct = default)
    {
        var team = await db.Teams.Where(t => t.Id == teamId)
            .Select(t => new { t.NoAlerts, t.LeadStaffId })
            .SingleOrDefaultAsync(ct);
        if (team is null || team.NoAlerts)
            return [];
        if (members)
        {
            return await db.Set<TeamMember>()
                .Where(m => m.TeamId == teamId && m.AlertsEnabled
                    && m.Staff!.IsActive && !m.Staff.OnVacation && m.Staff.Email != null)
                .Select(m => new MailRecipient(m.Staff!.FirstName + " " + m.Staff.LastName, m.Staff.Email!, m.StaffId, null))
                .ToListAsync(ct);
        }
        if (lead && team.LeadStaffId is { } leadId)
            return await StaffAsync(leadId, ct) is { } leadRecipient ? [leadRecipient] : [];
        return [];
    }

    public async Task<MailRecipient?> AccountManagerAsync(int userId, CancellationToken ct = default)
    {
        var managerId = await db.Users.Where(u => u.Id == userId)
            .Select(u => u.Organization != null ? u.Organization.ManagerStaffId : null)
            .SingleOrDefaultAsync(ct);
        return managerId is { } id ? await StaffAsync(id, ct) : null;
    }

    public async Task<MailRecipient?> LastRespondentAsync(int threadId, CancellationToken ct = default)
    {
        var staffId = await db.ThreadEntries
            .Where(e => e.ThreadId == threadId && e.Type == ThreadEntryType.Response && e.StaffId != null)
            .OrderByDescending(e => e.Id)
            .Select(e => e.StaffId)
            .FirstOrDefaultAsync(ct);
        return staffId is { } id ? await StaffAsync(id, ct) : null;
    }

    public Task<MailRecipient?> StaffAsync(int staffId, CancellationToken ct = default) =>
        db.Staff
            .Where(s => s.Id == staffId && s.IsActive && !s.OnVacation && s.Email != null)
            .Select(s => (MailRecipient?)new MailRecipient(s.FirstName + " " + s.LastName, s.Email!, s.Id, null))
            .FirstOrDefaultAsync(ct);

    public Task<MailRecipient?> OwnerAsync(int ticketId, CancellationToken ct = default) =>
        db.Tickets.Where(t => t.Id == ticketId)
            .Select(t => new
            {
                t.User!.Name,
                t.UserId,
                Email = t.User!.Emails.Where(e => e.Id == t.User!.DefaultEmailId)
                    .Select(e => e.Address).FirstOrDefault()
                    ?? t.User!.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .Where(x => x.Email != null)
            .Select(x => (MailRecipient?)new MailRecipient(x.Name, x.Email!, null, x.UserId))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<MailRecipient>> CollaboratorsAsync(int threadId, CancellationToken ct = default) =>
        await db.ThreadCollaborators
            .Where(c => c.ThreadId == threadId && c.IsActive)
            .Select(c => new
            {
                c.User!.Name,
                c.UserId,
                Email = c.User!.Emails.Where(e => e.Id == c.User!.DefaultEmailId)
                    .Select(e => e.Address).FirstOrDefault()
                    ?? c.User!.Emails.Select(e => e.Address).FirstOrDefault(),
            })
            .Where(x => x.Email != null)
            .Select(x => new MailRecipient(x.Name, x.Email!, null, x.UserId))
            .ToListAsync(ct);
}
