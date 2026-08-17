using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// End-user ("Kullanıcı") — the person tickets are opened for. osTicket splits this
/// across <c>user</c> / <c>user_email</c> / <c>user_account</c>; we keep the
/// multi-address model and replace user_account with a link to the Customer Identity
/// principal (<see cref="IdentityUserId"/> null = guest without portal login).
/// </summary>
public class User : TimestampedEntity
{
    public required string Name { get; set; }

    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Primary address among <see cref="Emails"/> (osTicket default_email_id).</summary>
    public int? DefaultEmailId { get; set; }
    public UserEmail? DefaultEmail { get; set; }

    /// <summary>Customer Identity principal when the user registered on the portal.</summary>
    public Guid? IdentityUserId { get; set; }

    /// <summary>
    /// Per-user value; null inherits <see cref="Organization.Phone"/> (agent/user-view
    /// "Şirketten" badge — "Geçersiz kıl" stores an override here).
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>Per-user override of <see cref="Organization.Address"/>; null inherits.</summary>
    public string? Address { get; set; }

    /// <summary>Blocks new tickets from this user (osTicket lock/ban status bit).</summary>
    public bool IsBlocked { get; set; }

    public string? Notes { get; set; }

    public List<UserEmail> Emails { get; set; } = [];
}

/// <summary>
/// Internal staff note about a user (agent/user-view.html Notlar tab). osTicket keeps
/// only a notes text blob; the mockup's note cards carry author + badge + timestamp,
/// so notes append here — the smallest faithful mechanism (no thread machinery).
/// </summary>
public class UserNote : TimestampedEntity
{
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Author staff id (plain column, ThreadEntry.StaffId precedent).</summary>
    public int? StaffId { get; set; }

    /// <summary>Author display-name snapshot (ThreadEntry.Poster parity).</summary>
    public required string AuthorName { get; set; }

    public required string Body { get; set; }
}

/// <summary>One of a user's addresses (osTicket user_email); inbound mail matches on these.</summary>
public class UserEmail : EntityBase
{
    public int UserId { get; set; }
    public User? User { get; set; }

    public required string Address { get; set; }
}

/// <summary>
/// Customer organization (osTicket <c>organization</c>). osTicket packs sharing/assignment
/// behavior into status bits and the manager into an encoded varchar; both are unpacked
/// into explicit columns here.
/// </summary>
public class Organization : TimestampedEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Comma-separated email domains; registration/inbound mail from these domains
    /// auto-links the user to this organization (osTicket domain).
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Industry line (agent/orgs.html "Sektör" column + org-view meta). Product-original
    /// column — osTicket keeps no such field; the mockup lists it, so it is real data
    /// rather than a prefix inside <see cref="Notes"/>.
    /// </summary>
    public string? Sector { get; set; }

    public int? ManagerStaffId { get; set; }

    /// <summary>All members can see each other's tickets (osTicket COLLAB_ALL_MEMBERS).</summary>
    public bool ShareTicketsWithMembers { get; set; }

    /// <summary>Primary contacts are auto-CC'd on member tickets (osTicket COLLAB_PRIMARY_CONTACT).</summary>
    public bool CcPrimaryContacts { get; set; }

    /// <summary>New member tickets auto-assign to the account manager (osTicket ASSIGN_AGENT_MANAGER).</summary>
    public bool AssignToManager { get; set; }

    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Notes { get; set; }

    public List<User> Members { get; set; } = [];
}

/// <summary>
/// Internal staff note about an organization (agent/org-view.html Notlar tab).
/// Deliberately a parallel twin of <see cref="UserNote"/> (same columns) rather than a
/// polymorphic note table — the smaller change; generalizing both into one table would
/// churn the existing user_notes migration for no behavioral gain.
/// </summary>
public class OrgNote : TimestampedEntity
{
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Author staff id (plain column, ThreadEntry.StaffId precedent).</summary>
    public int? StaffId { get; set; }

    /// <summary>Author display-name snapshot (ThreadEntry.Poster parity).</summary>
    public required string AuthorName { get; set; }

    public required string Body { get; set; }
}
