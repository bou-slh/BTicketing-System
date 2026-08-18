using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;

namespace RapidsolDestek.Web.Hubs;

/// <summary>
/// B7 live board hub (mockups/agent/live.html). Server→client only — mutations go
/// through the LiveController POST endpoints so the S4 services (permissions, audit,
/// domain events) stay the single write path; the hub merely fans out notifications.
/// Mounted under /agent so the "Contextual" policy scheme picks the rd.staff cookie
/// and MaintenanceModeMiddleware leaves the path alone.
/// </summary>
[Authorize(Policy = "Staff")]
public sealed class LiveBoardHub(AppDbContext db, IPermissionService permissions) : Hub
{
    public const string Path = "/agent/live/hub";

    /// <summary>Client method invoked when column membership may have changed.</summary>
    public const string BoardChangedMethod = "BoardChanged";

    /// <summary>Client method carrying one Canlı Akış ticker item.</summary>
    public const string TickerMethod = "Ticker";

    /// <summary>
    /// Broadcast audience per QueueEngine.VisibleTicketsAsync: staff see their
    /// departments' tickets. Connections join one group per visible department
    /// (admins: all), and handlers target the ticket's department group.
    /// (Known narrowings not modeled by groups — assigned-to-me-outside-my-depts
    /// and AssignedOnly agents — refetch their own filtered state on any signal,
    /// so they can momentarily receive a ticker line for a department ticket
    /// they cannot open; flagged on the ROADMAP row.)
    /// </summary>
    public static string DepartmentGroup(int departmentId) => $"live:dept:{departmentId}";

    public override async Task OnConnectedAsync()
    {
        var staff = await db.ResolveStaffAsync(Context.User!, Context.ConnectionAborted);
        if (staff is null)
        {
            Context.Abort();
            return;
        }

        var set = await permissions.ResolveAsync(staff.Id, Context.ConnectionAborted);
        var departmentIds = set.IsAdmin
            ? await db.Departments.Select(d => d.Id).ToListAsync(Context.ConnectionAborted)
            : [.. set.DepartmentIds];
        foreach (var departmentId in departmentIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, DepartmentGroup(departmentId), Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }
}
