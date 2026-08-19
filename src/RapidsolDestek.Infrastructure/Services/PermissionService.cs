using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// A staff member's resolved rights (osTicket Staff::getPerm + staff_dept_access):
/// primary department carries the primary role's keys; each extended-access row
/// carries its own role's keys. Admins bypass everything.
/// </summary>
public sealed class StaffPermissionSet
{
    public required int StaffId { get; init; }
    public required bool IsAdmin { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>Agent only sees tickets assigned to them (osTicket assigned_only).</summary>
    public required bool AssignedOnly { get; init; }

    /// <summary>Departments the agent can see (primary + extended access).</summary>
    public required IReadOnlySet<int> DepartmentIds { get; init; }

    /// <summary>Permission keys granted per department.</summary>
    public required IReadOnlyDictionary<int, IReadOnlySet<string>> PermissionsByDepartment { get; init; }

    public bool Can(string permission, int departmentId)
    {
        if (IsAdmin)
            return true;
        return PermissionsByDepartment.TryGetValue(departmentId, out var keys) && keys.Contains(permission);
    }

    /// <summary>True when the key is granted in any visible department (topbar-level checks).</summary>
    public bool CanAnywhere(string permission) =>
        IsAdmin || PermissionsByDepartment.Values.Any(k => k.Contains(permission));
}

public interface IPermissionService
{
    Task<StaffPermissionSet> ResolveAsync(int staffId, CancellationToken ct = default);

    /// <summary>Throws <see cref="PermissionDeniedException"/> unless the staff actor holds the key in the department.</summary>
    Task EnsureAsync(ActorContext actor, string permission, int departmentId, CancellationToken ct = default);
}

public sealed class PermissionService(AppDbContext db) : IPermissionService
{
    private readonly Dictionary<int, StaffPermissionSet> _memo = [];

    public async Task<StaffPermissionSet> ResolveAsync(int staffId, CancellationToken ct = default)
    {
        if (_memo.TryGetValue(staffId, out var cached))
            return cached;

        var staff = await db.Staff
            .Include(s => s.Role)
            .Include(s => s.DepartmentAccess).ThenInclude(a => a.Role)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == staffId, ct)
            ?? throw new DomainNotFoundException("Staff", staffId);

        // Per-staff override (staff-edit İzinler, osTicket staff.permissions): a
        // non-null Staff.Permissions replaces the role's grants for the 24
        // staff-matrix keys in EVERY accessible department — the mockup's matrix is
        // agent-global ("kişisel yetkiler"); null inherits the role untouched.
        IReadOnlySet<string> Effective(List<string> rolePermissions)
        {
            var set = ToSet(rolePermissions);
            if (staff.Permissions is null)
                return set;
            set.RemoveWhere(PermissionKeys.StaffOverridable.Contains);
            set.UnionWith(staff.Permissions.Where(PermissionKeys.StaffOverridable.Contains));
            return set;
        }

        var byDept = new Dictionary<int, IReadOnlySet<string>>
        {
            [staff.DepartmentId] = Effective(staff.Role?.IsEnabled == true ? staff.Role.Permissions : []),
        };
        foreach (var access in staff.DepartmentAccess)
        {
            byDept[access.DepartmentId] = Effective(access.Role?.IsEnabled == true ? access.Role.Permissions : []);
        }

        var set = new StaffPermissionSet
        {
            StaffId = staff.Id,
            IsAdmin = staff.IsAdmin,
            IsActive = staff.IsActive,
            AssignedOnly = staff.AssignedOnly,
            DepartmentIds = byDept.Keys.ToHashSet(),
            PermissionsByDepartment = byDept,
        };
        _memo[staffId] = set;
        return set;
    }

    public async Task EnsureAsync(ActorContext actor, string permission, int departmentId, CancellationToken ct = default)
    {
        if (actor.Type == ActorType.System)
            return; // jobs and seeds act with full rights

        if (!actor.IsStaff)
            throw new PermissionDeniedException(permission, departmentId);

        var set = await ResolveAsync(actor.Id!.Value, ct);
        if (!set.IsActive || !set.Can(permission, departmentId))
            throw new PermissionDeniedException(permission, departmentId);
    }

    private static HashSet<string> ToSet(List<string> permissions) => [.. permissions];
}
