using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent team directory (mockups/agent/directory.html). Read-only roster of
/// active, visible staff with search + department filter.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class DirectoryController(AppDbContext db) : Controller
{
    [HttpGet("/agent/directory")]
    [NavKey("directory")]
    public async Task<IActionResult> Index(string? q, int? dept, CancellationToken ct)
    {
        var staff = db.Staff.Where(s => s.IsActive && s.IsVisible);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            staff = staff.Where(s =>
                EF.Functions.ILike(s.FirstName + " " + s.LastName, term)
                || EF.Functions.ILike(s.Email ?? "", term));
        }

        if (dept is { } departmentId)
            staff = staff.Where(s => s.DepartmentId == departmentId);

        // TODO(S6): presence from real sessions per ROADMAP §6.2 directory row.
        // Until then this is a session-presence stub derived from login/vacation data:
        // Online = logged in within the last 15 minutes, Away = on vacation.
        var onlineSince = DateTimeOffset.UtcNow.AddMinutes(-15);

        var rows = (await staff
                .Include(s => s.Department)
                .OrderBy(s => s.FirstName)
                .ThenBy(s => s.LastName)
                .ToListAsync(ct))
            .Select(s => new DirectoryRowVm(
                Initials: $"{(s.FirstName.Length > 0 ? s.FirstName[0] : ' ')}{(s.LastName.Length > 0 ? s.LastName[0] : ' ')}".Trim(),
                FullName: s.FullName,
                DepartmentName: s.Department?.Name ?? "",
                Email: s.Email,
                Phone: s.Phone,
                PhoneExt: s.PhoneExt,
                Presence: s.LastLoginAt >= onlineSince ? DirectoryPresence.Online
                    : s.OnVacation ? DirectoryPresence.Away
                    : DirectoryPresence.Offline))
            .ToList();

        var departments = await db.Departments
            .Where(d => !d.IsArchived)
            .OrderBy(d => d.Name)
            .Select(d => new DirectoryDeptVm(d.Id, d.Name))
            .ToListAsync(ct);

        return View(new DirectoryIndexVm(q, dept, departments, rows));
    }
}

public enum DirectoryPresence
{
    Online,
    Away,
    Offline,
}

public sealed record DirectoryIndexVm(
    string? Query,
    int? DepartmentId,
    IReadOnlyList<DirectoryDeptVm> Departments,
    IReadOnlyList<DirectoryRowVm> Rows);

public sealed record DirectoryDeptVm(int Id, string Name);

public sealed record DirectoryRowVm(
    string Initials,
    string FullName,
    string DepartmentName,
    string? Email,
    string? Phone,
    string? PhoneExt,
    DirectoryPresence Presence);
