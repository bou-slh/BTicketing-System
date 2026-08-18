using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// Agent/admin profile (osTicket <c>staff</c>). Authentication state lives on the Staff
/// Identity principal (<see cref="IdentityUserId"/>); this row carries the helpdesk-side
/// profile, routing and preferences. <see cref="IsAdmin"/> mirrors osTicket's isadmin and
/// is the source of truth the Identity "Admin" role is synced from.
/// </summary>
public class Staff : TimestampedEntity
{
    public Guid? IdentityUserId { get; set; }

    public required string Username { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? PhoneExt { get; set; }
    public string? Mobile { get; set; }

    /// <summary>Primary department (extended access via <see cref="DepartmentAccess"/>).</summary>
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Role in the primary department.</summary>
    public int RoleId { get; set; }
    public Role? Role { get; set; }

    public string Signature { get; set; } = "";

    public string? Language { get; set; }
    public string? Timezone { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }

    /// <summary>Shown in the agent directory (osTicket isvisible).</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Vacation mode — assignment guard skips this agent (osTicket onvacation).</summary>
    public bool OnVacation { get; set; }

    /// <summary>Agent only sees tickets assigned to them (osTicket assigned_only).</summary>
    public bool AssignedOnly { get; set; }

    public SignatureType DefaultSignatureType { get; set; } = SignatureType.None;

    /// <summary>
    /// Two-step verification method (agent/profile.html pf-2fa select). App/Email
    /// imply the Identity user's TwoFactorEnabled; App additionally requires a
    /// confirmed authenticator enrollment ("Yapılandır" dialog).
    /// </summary>
    public TwoFactorMethod TwoFactorMethod { get; set; } = TwoFactorMethod.None;

    /// <summary>Stamped by the profile password change (pf.passHelp "Son değişiklik").</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    // ----- Working preferences (agent/profile.html Tercihler tab). Persisted from
    // the profile save; list-engine/rendering consumption is TODO(S7) with the
    // admin settings work (ROADMAP §6.3). -----

    /// <summary>Rows per page on list pages (pf-pagesize: 10/25/50).</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>Auto-refresh interval for ticket lists in minutes; 0 = off (pf-refresh).</summary>
    public int AutoRefreshMinutes { get; set; }

    /// <summary>Queue shown when the Tickets tab opens (pf-queue).</summary>
    public AgentDefaultQueue DefaultQueue { get; set; } = AgentDefaultQueue.Open;

    /// <summary>Thread view order (pf-order: newest/oldest first).</summary>
    public bool ThreadOrderNewestFirst { get; set; } = true;

    /// <summary>Time format (pf-timefmt: 24-hour vs 12-hour AM/PM).</summary>
    public bool Use24HourTime { get; set; } = true;

    public string? Notes { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public List<StaffDepartmentAccess> DepartmentAccess { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}";
}

public enum SignatureType
{
    None,
    Mine,
    Department,
}

/// <summary>Profile pf-2fa options: Devre dışı / Doğrulama uygulaması / E-posta kodu.</summary>
public enum TwoFactorMethod
{
    None,
    App,
    Email,
}

/// <summary>Profile pf-queue options: Açık Talepler / Taleplerim.</summary>
public enum AgentDefaultQueue
{
    Open,
    Mine,
}

/// <summary>
/// Extended department access with a per-department role
/// (osTicket <c>staff_dept_access</c>).
/// </summary>
public class StaffDepartmentAccess
{
    public int StaffId { get; set; }
    public Staff? Staff { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>osTicket flags bit 1 — member receives department alerts.</summary>
    public bool AlertsEnabled { get; set; } = true;
}

/// <summary>
/// Permission set assignable per staff/department (osTicket <c>role</c>). Permission
/// keys follow osTicket's dotted naming (ticket.create, ticket.assign, task.close,
/// canned.manage, thread.edit, …) so the role-edit matrix maps 1:1.
/// </summary>
public class Role : TimestampedEntity
{
    public required string Name { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Granted permission keys, stored as jsonb.</summary>
    public List<string> Permissions { get; set; } = [];

    public string? Notes { get; set; }
}

/// <summary>Mirrors osTicket team.</summary>
public class Team : TimestampedEntity
{
    public required string Name { get; set; }

    public int? LeadStaffId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>osTicket flags bit — suppress alerts on team assignment.</summary>
    public bool NoAlerts { get; set; }

    public string? Notes { get; set; }

    public List<TeamMember> Members { get; set; } = [];
}

public class TeamMember
{
    public int TeamId { get; set; }
    public Team? Team { get; set; }

    public int StaffId { get; set; }
    public Staff? Staff { get; set; }

    /// <summary>osTicket flags bit 1 — member receives team alerts.</summary>
    public bool AlertsEnabled { get; set; } = true;
}

/// <summary>
/// Mirrors osTicket <c>department</c>: routing target, SLA/schedule defaults, outgoing
/// identity and auto-response behavior. Hierarchy via <see cref="ParentId"/> +
/// materialized <see cref="Path"/> ("/1/3/"), exactly as the reference.
/// </summary>
public class Department : TimestampedEntity
{
    public required string Name { get; set; }

    public int? ParentId { get; set; }
    public Department? Parent { get; set; }

    /// <summary>Materialized ancestor path, e.g. "/1/3/" (osTicket path).</summary>
    public string Path { get; set; } = "/";

    public int? SlaId { get; set; }
    public SlaPlan? Sla { get; set; }

    public int? ScheduleId { get; set; }
    public Schedule? Schedule { get; set; }

    /// <summary>Template set used for this department's emails (osTicket tpl_id).</summary>
    public int? TemplateSetId { get; set; }

    /// <summary>Outgoing email identity (osTicket email_id).</summary>
    public int? EmailAccountId { get; set; }

    /// <summary>Address used for auto-responses (osTicket autoresp_email_id).</summary>
    public int? AutoResponseEmailAccountId { get; set; }

    public int? ManagerStaffId { get; set; }

    /// <summary>Public departments are selectable on the portal (osTicket ispublic).</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>Archived departments are hidden from routing (osTicket flags bit).</summary>
    public bool IsArchived { get; set; }

    /// <summary>Send "ticket created" auto-response (osTicket ticket_auto_response).</summary>
    public bool TicketAutoResponse { get; set; } = true;

    /// <summary>Send auto-response on every new message (osTicket message_auto_response).</summary>
    public bool MessageAutoResponse { get; set; }

    /// <summary>Restrict assignment to department members (osTicket flags bit).</summary>
    public bool AssignMembersOnly { get; set; }

    public string Signature { get; set; } = "";
}
