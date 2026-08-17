using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Input for <see cref="IUserService.CreateAsync"/> (agent/users.html dlg-adduser fields).</summary>
public sealed record UserCreateRequest
{
    public required string Name { get; init; }
    public required string Email { get; init; }
    public string? Phone { get; init; }

    /// <summary>Explicit company; null auto-links by email domain (Organization.Domain).</summary>
    public int? OrganizationId { get; init; }
}

/// <summary>Input for <see cref="IUserService.UpdateAsync"/> (user-view dlg-edit fields).</summary>
public sealed record UserProfileUpdate
{
    public required string Name { get; init; }

    /// <summary>Null/blank clears the per-user override → inherits Organization.Phone again.</summary>
    public string? Phone { get; init; }

    /// <summary>Null/blank clears the per-user override → inherits Organization.Address again.</summary>
    public string? Address { get; init; }

    public int? OrganizationId { get; init; }
}

/// <summary>Outcome of <see cref="IUserService.ImportCsvAsync"/> (created/skipped report).</summary>
public sealed record CsvImportResult(int Created, int Skipped);

/// <summary>
/// User mutations for agent/users.html + user-view.html (ROADMAP §6.2), shaped after
/// <see cref="TaskService"/>: AuditEvent via the interceptor (actor audit scope),
/// permission keys user.edit (profile writes) / user.manage (block/org/delete).
/// Users are non-departmental, so keys check via <see cref="StaffPermissionSet.CanAnywhere"/>.
/// </summary>
public interface IUserService
{
    /// <summary>dlg-adduser: User + UserEmail, org auto-link by email domain unless explicit.</summary>
    Task<User> CreateAsync(UserCreateRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>dlg-edit: name + phone/address overrides (blank = inherit) + company.</summary>
    Task UpdateAsync(int userId, UserProfileUpdate update, ActorContext actor, CancellationToken ct = default);

    /// <summary>"Geçersiz kıl": stores a per-user value for an org-inherited field ("phone"/"address"); blank reverts to inherited.</summary>
    Task OverrideAsync(int userId, string field, string? value, ActorContext actor, CancellationToken ct = default);

    /// <summary>Kilitle / Kilidi Aç (IsBlocked; osTicket lock status bit).</summary>
    Task SetBlockedAsync(int userId, bool blocked, ActorContext actor, CancellationToken ct = default);

    /// <summary>Şirkete Ekle bulk action (null detaches).</summary>
    Task SetOrganizationAsync(int userId, int? organizationId, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Hard delete (user + emails + notes + portal Identity account). Refused with
    /// <see cref="DomainRuleException"/> "has-tickets"/"referenced" while tickets or
    /// collaborator rows point at the user (DB FKs are Restrict).
    /// </summary>
    Task DeleteAsync(int userId, ActorContext actor, CancellationToken ct = default);

    /// <summary>user-view note composer (B5): appends a UserNote with the actor as author.</summary>
    Task<UserNote> AddNoteAsync(int userId, string body, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// CSV import (users toolbar "İçe Aktar"): strict small parser over
    /// <c>name,email[,org]</c> lines (optional header, double-quoted fields allowed).
    /// Invalid rows and duplicate emails are skipped and counted, never fatal.
    /// </summary>
    Task<CsvImportResult> ImportCsvAsync(TextReader reader, ActorContext actor, CancellationToken ct = default);
}

public sealed class UserService(AppDbContext db, IPermissionService permissions) : IUserService
{
    public async Task<User> CreateAsync(UserCreateRequest request, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserEdit, ct);

        var name = request.Name.Trim();
        var address = request.Email.Trim();
        if (name.Length == 0 || !new EmailAddressAttribute().IsValid(address))
            throw new DomainRuleException("invalid", "User name/email invalid.");
        if (await db.UserEmails.AnyAsync(e => e.Address.ToLower() == address.ToLower(), ct))
            throw new DomainRuleException("email-in-use", $"Email '{address}' already belongs to a user.");

        int? orgId = request.OrganizationId;
        if (orgId is { } explicitOrg)
        {
            _ = await db.Organizations.SingleOrDefaultAsync(o => o.Id == explicitOrg, ct)
                ?? throw new DomainNotFoundException("Organization", explicitOrg);
        }
        else
        {
            orgId = MatchOrganizationByDomain(await OrgDomainsAsync(ct), address);
        }

        var user = new User
        {
            Name = name,
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            OrganizationId = orgId,
            Emails = [new UserEmail { Address = address }],
        };
        db.Users.Add(user);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        user.DefaultEmailId = user.Emails[0].Id;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateAsync(int userId, UserProfileUpdate update, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserEdit, ct);
        var user = await LoadAsync(userId, ct);

        var name = update.Name.Trim();
        if (name.Length == 0)
            throw new DomainRuleException("invalid", "User name must not be empty.");
        if (update.OrganizationId is { } orgId && orgId != user.OrganizationId)
        {
            _ = await db.Organizations.SingleOrDefaultAsync(o => o.Id == orgId, ct)
                ?? throw new DomainNotFoundException("Organization", orgId);
        }

        user.Name = name;
        user.Phone = string.IsNullOrWhiteSpace(update.Phone) ? null : update.Phone.Trim();
        user.Address = string.IsNullOrWhiteSpace(update.Address) ? null : update.Address.Trim();
        user.OrganizationId = update.OrganizationId;

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task OverrideAsync(int userId, string field, string? value, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserEdit, ct);
        var user = await LoadAsync(userId, ct);

        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (field)
        {
            case "phone":
                user.Phone = trimmed;
                break;
            case "address":
                user.Address = trimmed;
                break;
            default:
                throw new DomainRuleException("invalid", $"Unknown override field '{field}'.");
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task SetBlockedAsync(int userId, bool blocked, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserManage, ct);
        var user = await LoadAsync(userId, ct);
        if (user.IsBlocked == blocked)
            return;

        user.IsBlocked = blocked;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task SetOrganizationAsync(int userId, int? organizationId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserManage, ct);
        var user = await LoadAsync(userId, ct);
        if (organizationId is { } orgId)
        {
            _ = await db.Organizations.SingleOrDefaultAsync(o => o.Id == orgId, ct)
                ?? throw new DomainNotFoundException("Organization", orgId);
        }
        if (user.OrganizationId == organizationId)
            return;

        user.OrganizationId = organizationId;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int userId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserManage, ct);
        var user = await LoadAsync(userId, ct);

        // Friendly guards for the DB-level Restrict FKs (tickets.user_id,
        // thread_collaborators.user_id) — surfaced as toasts, never a 500.
        if (await db.Tickets.AnyAsync(t => t.UserId == userId, ct))
            throw new DomainRuleException("has-tickets", $"User {userId} has tickets and cannot be deleted.");
        if (await db.ThreadCollaborators.AnyAsync(c => c.UserId == userId, ct))
            throw new DomainRuleException("referenced", $"User {userId} is a collaborator on a thread.");

        // Portal Identity account goes with the domain row (osTicket account delete parity).
        if (user.IdentityUserId is { } identityId)
        {
            var identity = await db.CustomerUsers.SingleOrDefaultAsync(c => c.Id == identityId, ct);
            if (identity is not null)
                db.CustomerUsers.Remove(identity);
        }

        // Break the users.default_email_id ↔ user_emails cycle first; emails + notes
        // then cascade at the DB level with the user row.
        user.DefaultEmailId = null;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        db.Users.Remove(user);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task<UserNote> AddNoteAsync(int userId, string body, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserEdit, ct);
        _ = await LoadAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new DomainRuleException("invalid", "Note body must not be empty.");

        var note = new UserNote
        {
            UserId = userId,
            StaffId = actor.IsStaff ? actor.Id : null,
            AuthorName = actor.Name,
            Body = body.Trim(),
        };
        db.UserNotes.Add(note);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task<CsvImportResult> ImportCsvAsync(TextReader reader, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.UserEdit, ct);

        var orgDomains = await OrgDomainsAsync(ct);
        var orgsByName = orgDomains.ToDictionary(o => o.Name.Trim(), o => o.Id, StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(
            await db.UserEmails.Select(e => e.Address.ToLower()).ToListAsync(ct));

        int created = 0, skipped = 0;
        var pending = new List<User>();
        var emailChecker = new EmailAddressAttribute();
        bool first = true;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var fields = SplitCsvLine(line);
            if (first)
            {
                first = false;
                // Optional header row: "name,email[,org]" (or TR "ad,e-posta[,şirket]").
                var head = fields.Count > 0 ? fields[0].Trim().ToLowerInvariant() : "";
                if (head is "name" or "ad" or "ad soyad")
                    continue;
            }

            var name = fields.Count > 0 ? fields[0].Trim() : "";
            var email = fields.Count > 1 ? fields[1].Trim() : "";
            var orgName = fields.Count > 2 ? fields[2].Trim() : "";
            if (fields.Count is < 2 or > 3 || name.Length == 0 || !emailChecker.IsValid(email)
                || !existing.Add(email.ToLowerInvariant()))
            {
                skipped++;
                continue;
            }

            var orgId = orgName.Length > 0 && orgsByName.TryGetValue(orgName, out var byName)
                ? byName
                : MatchOrganizationByDomain(orgDomains, email);
            pending.Add(new User
            {
                Name = name,
                OrganizationId = orgId,
                Emails = [new UserEmail { Address = email }],
            });
            created++;
        }

        if (pending.Count > 0)
        {
            db.Users.AddRange(pending);
            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);
            foreach (var user in pending)
                user.DefaultEmailId = user.Emails[0].Id;
            using (actor.BeginAuditScope())
                await db.SaveChangesAsync(ct);
        }
        return new CsvImportResult(created, skipped);
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<User> LoadAsync(int userId, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainNotFoundException("User", userId);

    /// <summary>Non-departmental permission check (users span departments).</summary>
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

    private async Task<List<(int Id, string Name, string? Domain)>> OrgDomainsAsync(CancellationToken ct) =>
        (await db.Organizations
            .Select(o => new { o.Id, o.Name, o.Domain })
            .ToListAsync(ct))
        .Select(o => (o.Id, o.Name, o.Domain))
        .ToList();

    /// <summary>Org auto-link by email domain (AccountController.LinkDomainUserAsync pattern).</summary>
    private static int? MatchOrganizationByDomain(
        List<(int Id, string Name, string? Domain)> orgs, string email)
    {
        var at = email.IndexOf('@');
        if (at < 0)
            return null;
        var host = email[(at + 1)..];
        foreach (var (id, _, domain) in orgs)
        {
            if (domain is null)
                continue;
            if (domain.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(d => d.Equals(host, StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        return null;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
