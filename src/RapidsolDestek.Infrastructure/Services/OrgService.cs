using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Input for <see cref="IOrgService.CreateAsync"/> (agent/orgs.html dlg-addorg fields).</summary>
public sealed record OrgCreateRequest
{
    public required string Name { get; init; }

    /// <summary>Comma-separated auto-link domains; "@" prefixes are stripped (dialog placeholder "@sirket.com.tr").</summary>
    public string? Domain { get; init; }

    public string? Sector { get; init; }
    public int? ManagerStaffId { get; init; }
}

/// <summary>Input for <see cref="IOrgService.UpdateAsync"/> (org-view dlg-edit fields).</summary>
public sealed record OrgProfileUpdate
{
    public required string Name { get; init; }
    public string? Domain { get; init; }
    public string? Sector { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public int? ManagerStaffId { get; init; }
}

/// <summary>
/// Organization mutations for agent/orgs.html + org-view.html (ROADMAP §6.2), shaped
/// after <see cref="UserService"/>: AuditEvent via the interceptor (actor audit scope),
/// permission key org.edit checked "anywhere" (organizations are non-departmental).
/// A finer org.manage/org.delete split does not exist in the seeded role matrix — all
/// writes ride org.edit; flagged in ROADMAP for a canon decision.
/// </summary>
public interface IOrgService
{
    /// <summary>dlg-addorg: creates the organization (duplicate-name guard, unique index backing).</summary>
    Task<Organization> CreateAsync(OrgCreateRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>org-view dlg-edit: profile fields (name/domain/sector/phone/address/manager).</summary>
    Task UpdateAsync(int orgId, OrgProfileUpdate update, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// org-view sync card: persists the three osTicket collaboration/assignment flags
    /// (ShareTicketsWithMembers / CcPrimaryContacts / AssignToManager).
    /// </summary>
    Task SetSyncFlagsAsync(int orgId, bool shareTickets, bool ccPrimary, bool assignManager,
        ActorContext actor, CancellationToken ct = default);

    /// <summary>org-view note composer (B5): appends an OrgNote with the actor as author.</summary>
    Task<OrgNote> AddNoteAsync(int orgId, string body, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Hard delete (organization + notes). Refused with <see cref="DomainRuleException"/>
    /// "has-members"/"has-tickets" while users or their tickets still point at the org.
    /// </summary>
    Task DeleteAsync(int orgId, ActorContext actor, CancellationToken ct = default);
}

public sealed class OrgService(AppDbContext db, IPermissionService permissions) : IOrgService
{
    public async Task<Organization> CreateAsync(OrgCreateRequest request, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.OrgEdit, ct);

        var name = request.Name.Trim();
        if (name.Length == 0)
            throw new DomainRuleException("invalid", "Organization name must not be empty.");
        if (await db.Organizations.AnyAsync(o => o.Name.ToLower() == name.ToLower(), ct))
            throw new DomainRuleException("name-in-use", $"Organization '{name}' already exists.");
        if (request.ManagerStaffId is { } managerId)
            await EnsureStaffExistsAsync(managerId, ct);

        var org = new Organization
        {
            Name = name,
            Domain = NormalizeDomains(request.Domain),
            Sector = string.IsNullOrWhiteSpace(request.Sector) ? null : request.Sector.Trim(),
            ManagerStaffId = request.ManagerStaffId,
        };
        db.Organizations.Add(org);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return org;
    }

    public async Task UpdateAsync(int orgId, OrgProfileUpdate update, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.OrgEdit, ct);
        var org = await LoadAsync(orgId, ct);

        var name = update.Name.Trim();
        if (name.Length == 0)
            throw new DomainRuleException("invalid", "Organization name must not be empty.");
        if (await db.Organizations.AnyAsync(o => o.Id != orgId && o.Name.ToLower() == name.ToLower(), ct))
            throw new DomainRuleException("name-in-use", $"Organization '{name}' already exists.");
        if (update.ManagerStaffId is { } managerId && managerId != org.ManagerStaffId)
            await EnsureStaffExistsAsync(managerId, ct);

        org.Name = name;
        org.Domain = NormalizeDomains(update.Domain);
        org.Sector = string.IsNullOrWhiteSpace(update.Sector) ? null : update.Sector.Trim();
        org.Phone = string.IsNullOrWhiteSpace(update.Phone) ? null : update.Phone.Trim();
        org.Address = string.IsNullOrWhiteSpace(update.Address) ? null : update.Address.Trim();
        org.ManagerStaffId = update.ManagerStaffId;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task SetSyncFlagsAsync(int orgId, bool shareTickets, bool ccPrimary, bool assignManager,
        ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.OrgEdit, ct);
        var org = await LoadAsync(orgId, ct);
        if (org.ShareTicketsWithMembers == shareTickets
            && org.CcPrimaryContacts == ccPrimary
            && org.AssignToManager == assignManager)
            return;

        org.ShareTicketsWithMembers = shareTickets;
        org.CcPrimaryContacts = ccPrimary;
        org.AssignToManager = assignManager;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task<OrgNote> AddNoteAsync(int orgId, string body, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.OrgEdit, ct);
        _ = await LoadAsync(orgId, ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new DomainRuleException("invalid", "Note body must not be empty.");

        var note = new OrgNote
        {
            OrganizationId = orgId,
            StaffId = actor.IsStaff ? actor.Id : null,
            AuthorName = actor.Name,
            Body = body.Trim(),
        };
        db.OrgNotes.Add(note);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task DeleteAsync(int orgId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.OrgEdit, ct);
        var org = await LoadAsync(orgId, ct);

        // Friendly guards (users.organization_id is SetNull, but silently orphaning
        // members would lie to the orgs list): refuse while members or their tickets
        // exist — surfaced as toasts, never a 500.
        if (await db.Tickets.AnyAsync(t => t.User!.OrganizationId == orgId, ct))
            throw new DomainRuleException("has-tickets", $"Organization {orgId} still has member tickets.");
        if (await db.Users.AnyAsync(u => u.OrganizationId == orgId, ct))
            throw new DomainRuleException("has-members", $"Organization {orgId} still has members.");

        db.Organizations.Remove(org); // org_notes cascade at the DB level
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<Organization> LoadAsync(int orgId, CancellationToken ct) =>
        await db.Organizations.SingleOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new DomainNotFoundException("Organization", orgId);

    private async Task EnsureStaffExistsAsync(int staffId, CancellationToken ct)
    {
        if (!await db.Staff.AnyAsync(s => s.Id == staffId, ct))
            throw new DomainNotFoundException("Staff", staffId);
    }

    /// <summary>Non-departmental permission check (UserService precedent).</summary>
    private async Task EnsureAnywhereAsync(ActorContext actor, string permission, CancellationToken ct)
    {
        if (actor.Type == ActorType.System)
            return;
        if (!actor.IsStaff)
            throw new PermissionDeniedException(permission);
        var set = await permissions.ResolveAsync(actor.Id!.Value, ct);
        if (!set.IsActive || !set.CanAnywhere(permission))
            throw new PermissionDeniedException(permission);
    }

    /// <summary>
    /// "@ulasim.com.tr, tosyali.com.tr" → "ulasim.com.tr,tosyali.com.tr" — stored the
    /// way <see cref="UserService"/>'s domain auto-link matcher reads it.
    /// </summary>
    private static string? NormalizeDomains(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var parts = raw
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.TrimStart('@'))
            .Where(d => d.Length > 0)
            .ToArray();
        return parts.Length == 0 ? null : string.Join(",", parts);
    }
}
