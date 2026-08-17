using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>
/// Input for <see cref="ICannedResponseService.CreateAsync"/> / UpdateAsync
/// (agent/canned.html dlg-canned fields). DepartmentId null = all departments.
/// </summary>
public sealed record CannedUpsertRequest
{
    public required string Title { get; init; }
    public int? DepartmentId { get; init; }

    /// <summary>Raw dialog textarea input; normalized to stored HTML (KbService precedent).</summary>
    public required string Response { get; init; }

    public bool IsEnabled { get; init; } = true;
}

public interface ICannedResponseService
{
    /// <summary>Enabled responses visible to the actor: global + the ticket department's.</summary>
    Task<IReadOnlyList<CannedResponse>> ListForAsync(int? departmentId, CancellationToken ct = default);

    /// <summary>agent/canned dlg-canned: creates the response (duplicate-title guard).</summary>
    Task<CannedResponse> CreateAsync(CannedUpsertRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>agent/canned per-row edit dialog (B2): title/department/body/enabled.</summary>
    Task UpdateAsync(int cannedId, CannedUpsertRequest request, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Toolbar bulk Devre Dışı Bırak / edit-dialog Etkin toggle. Disabled responses
    /// drop out of <see cref="ListForAsync"/> and the composer selects immediately.
    /// </summary>
    Task SetEnabledAsync(int cannedId, bool enabled, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Hard delete (tasks-page Sil precedent): inserted text was copied into threads
    /// at insert time, so nothing references a canned response after the fact.
    /// </summary>
    Task DeleteAsync(int cannedId, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Expands the canned body's %{variables} against a ticket (osTicket
    /// VariableReplacer parity) and re-sanitizes — expansion inserts user data.
    /// </summary>
    Task<string> ExpandAsync(int cannedId, int ticketId, CancellationToken ct = default);

    /// <summary>The variable bag for a ticket — shared with S8 template rendering.</summary>
    Task<IReadOnlyDictionary<string, string?>> BuildVariablesAsync(int ticketId, CancellationToken ct = default);
}

public sealed class CannedResponseService(
    AppDbContext db, IHtmlSanitizerService sanitizer, IPermissionService permissions) : ICannedResponseService
{
    public async Task<IReadOnlyList<CannedResponse>> ListForAsync(int? departmentId, CancellationToken ct = default)
    {
        return await db.CannedResponses
            .Where(c => c.IsEnabled && (c.DepartmentId == null || c.DepartmentId == departmentId))
            .OrderBy(c => c.Title)
            .ToListAsync(ct);
    }

    public async Task<CannedResponse> CreateAsync(CannedUpsertRequest request, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.CannedManage, ct);
        var (title, response) = await ValidateAsync(request, excludeId: null, ct);

        var canned = new CannedResponse
        {
            Title = title,
            DepartmentId = request.DepartmentId,
            Response = response,
            IsEnabled = request.IsEnabled,
        };
        db.CannedResponses.Add(canned);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return canned;
    }

    public async Task UpdateAsync(int cannedId, CannedUpsertRequest request, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.CannedManage, ct);
        var canned = await LoadAsync(cannedId, ct);
        var (title, response) = await ValidateAsync(request, excludeId: cannedId, ct);

        canned.Title = title;
        canned.DepartmentId = request.DepartmentId;
        canned.Response = response;
        canned.IsEnabled = request.IsEnabled;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(int cannedId, bool enabled, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.CannedManage, ct);
        var canned = await LoadAsync(cannedId, ct);
        if (canned.IsEnabled == enabled)
            return;

        canned.IsEnabled = enabled;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int cannedId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.CannedManage, ct);
        var canned = await LoadAsync(cannedId, ct);

        db.CannedResponses.Remove(canned);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
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

    // ---- helpers ----------------------------------------------------------------------

    private async Task<CannedResponse> LoadAsync(int cannedId, CancellationToken ct) =>
        await db.CannedResponses.SingleOrDefaultAsync(c => c.Id == cannedId, ct)
            ?? throw new DomainNotFoundException("CannedResponse", cannedId);

    /// <summary>
    /// Shared create/update validation: non-empty title + body, duplicate-title guard
    /// (case-insensitive, OrgService name-in-use precedent), department existence, and
    /// body normalization to stored HTML (KbService answer precedent — the dialog
    /// textarea posts plain text or HTML; %{variables} survive both paths).
    /// </summary>
    private async Task<(string Title, string Response)> ValidateAsync(
        CannedUpsertRequest request, int? excludeId, CancellationToken ct)
    {
        var title = request.Title.Trim();
        if (title.Length == 0)
            throw new DomainRuleException("invalid", "Canned response title must not be empty.");
        if (string.IsNullOrWhiteSpace(request.Response))
            throw new DomainRuleException("invalid", "Canned response body must not be empty.");
        if (await db.CannedResponses.AnyAsync(
                c => c.Id != excludeId && c.Title.ToLower() == title.ToLower(), ct))
            throw new DomainRuleException("title-in-use", $"Canned response '{title}' already exists.");
        if (request.DepartmentId is { } deptId
            && !await db.Departments.AnyAsync(d => d.Id == deptId, ct))
            throw new DomainNotFoundException("Department", deptId);

        return (title, KbService.NormalizeAnswerFor(sanitizer, request.Response));
    }

    /// <summary>Non-departmental permission check (KbService faq.manage precedent — global responses exist).</summary>
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
}
