using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

public interface ICannedResponseService
{
    /// <summary>Enabled responses visible to the actor: global + the ticket department's.</summary>
    Task<IReadOnlyList<CannedResponse>> ListForAsync(int? departmentId, CancellationToken ct = default);

    /// <summary>
    /// Expands the canned body's %{variables} against a ticket (osTicket
    /// VariableReplacer parity) and re-sanitizes — expansion inserts user data.
    /// </summary>
    Task<string> ExpandAsync(int cannedId, int ticketId, CancellationToken ct = default);

    /// <summary>The variable bag for a ticket — shared with S8 template rendering.</summary>
    Task<IReadOnlyDictionary<string, string?>> BuildVariablesAsync(int ticketId, CancellationToken ct = default);
}

public sealed class CannedResponseService(AppDbContext db, IHtmlSanitizerService sanitizer) : ICannedResponseService
{
    public async Task<IReadOnlyList<CannedResponse>> ListForAsync(int? departmentId, CancellationToken ct = default)
    {
        return await db.CannedResponses
            .Where(c => c.IsEnabled && (c.DepartmentId == null || c.DepartmentId == departmentId))
            .OrderBy(c => c.Title)
            .ToListAsync(ct);
    }

    public async Task<string> ExpandAsync(int cannedId, int ticketId, CancellationToken ct = default)
    {
        var canned = await db.CannedResponses.SingleOrDefaultAsync(c => c.Id == cannedId, ct)
            ?? throw new DomainNotFoundException("CannedResponse", cannedId);

        var variables = await BuildVariablesAsync(ticketId, ct);
        return sanitizer.Sanitize(TemplateVariableExpander.Expand(canned.Response, variables));
    }

    public async Task<IReadOnlyDictionary<string, string?>> BuildVariablesAsync(int ticketId, CancellationToken ct = default)
    {
        var ticket = await db.Tickets
            .Include(t => t.User)
            .Include(t => t.Department)
            .Include(t => t.Staff)
            .Include(t => t.Status)
            .AsSplitQuery()
            .SingleOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new DomainNotFoundException("Ticket", ticketId);

        var effort = await db.EffortProposals
            .Where(p => p.TicketId == ticketId)
            .OrderByDescending(p => p.RevisionNo)
            .FirstOrDefaultAsync(ct);

        return new Dictionary<string, string?>
        {
            ["ticket.number"] = ticket.Number,
            ["ticket.subject"] = ticket.Subject,
            ["ticket.status"] = ticket.Status?.Name,
            ["ticket.dept.name"] = ticket.Department?.Name,
            ["ticket.user.name"] = ticket.User?.Name,
            ["ticket.staff.name"] = ticket.Staff?.FullName,
            ["ticket.create_date"] = ticket.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
            ["effort.hours"] = effort?.Hours.ToString("0.##"),
            ["effort.note"] = effort?.Note,
            ["effort.revision"] = effort?.RevisionNo.ToString(),
        };
    }
}
