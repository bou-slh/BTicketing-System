using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// B7 column-membership derivation for the live board (mockups/agent/live.html):
/// pure IQueryable transforms layered on IQueueEngine.BuildAsync output
/// (TicketListEngine precedent). The five mockup columns map onto seeded
/// TicketStatus rows + the derived flags the ticket list already uses:
/// Yeni = status "new"; Atanmamış = open-state unassigned (minus "new", which has
/// its own column); Yanıt Bekleyen = status "wait"; SLA Riskli = open-state with
/// the IsOverdue flag or a due instant inside <see cref="SlaRiskWindow"/>;
/// Efor Onayında = active (highest-revision) proposal pending. Columns are
/// independent queries — the mockup itself shows R716555 in two columns at once.
/// </summary>
public static class LiveBoardEngine
{
    /// <summary>
    /// How close to its due instant a ticket becomes "SLA Riskli". The mockup's
    /// sample cards enter the column under ~8 minutes remaining; no canon value
    /// exists, so 10 minutes is an invented threshold (flagged on the ROADMAP row).
    /// </summary>
    public static readonly TimeSpan SlaRiskWindow = TimeSpan.FromMinutes(10);

    public static IQueryable<Ticket> New(IQueryable<Ticket> visible) =>
        visible.Where(t => t.Status!.Key == "new");

    public static IQueryable<Ticket> Unassigned(IQueryable<Ticket> visible) =>
        visible.Where(t => t.Status!.State == TicketState.Open && t.Status.Key != "new"
            && t.StaffId == null && t.TeamId == null);

    public static IQueryable<Ticket> Waiting(IQueryable<Ticket> visible) =>
        visible.Where(t => t.Status!.Key == "wait");

    /// <summary>Due expression is the canonical DueDate ?? EstimatedDueDate.</summary>
    public static IQueryable<Ticket> SlaRisk(IQueryable<Ticket> visible, DateTimeOffset now) =>
        visible.Where(t => t.Status!.State == TicketState.Open
            && (t.IsOverdue || (t.DueDate ?? t.EstimatedDueDate) <= now + SlaRiskWindow));

    public static IQueryable<Ticket> EffortPending(IQueryable<Ticket> visible) =>
        visible.Where(t => t.Status!.State == TicketState.Open
            && t.EffortProposals.OrderByDescending(p => p.RevisionNo).Take(1)
                .Any(p => p.State == EffortState.Pending));
}
