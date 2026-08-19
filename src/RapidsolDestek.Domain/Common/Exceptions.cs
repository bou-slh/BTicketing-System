namespace RapidsolDestek.Domain.Common;

/// <summary>Base for expected, user-presentable domain failures (mapped to 4xx in Web).</summary>
public abstract class DomainException(string message) : Exception(message);

/// <summary>The acting staff member lacks a permission key in the relevant department.</summary>
public class PermissionDeniedException(string permission, int? departmentId = null)
    : DomainException($"Missing permission '{permission}'{(departmentId is { } d ? $" in department {d}" : "")}.")
{
    public string Permission { get; } = permission;
    public int? DepartmentId { get; } = departmentId;
}

/// <summary>
/// A user-correctable rule violation (duplicate value, restricted delete, …).
/// <see cref="Code"/> is stable for UI toast mapping (S6 users pages).
/// </summary>
public class DomainRuleException(string code, string message) : DomainException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A ticket filter's Reject action refused the incoming ticket before creation
/// (admin/filters "Talebi Reddet", osTicket FilterDataChanged/RejectedException).
/// </summary>
public class TicketRejectedByFilterException(string filterName)
    : DomainRuleException("filter-rejected", $"Ticket rejected by filter '{filterName}'.")
{
    public string FilterName { get; } = filterName;
}

/// <summary>An entity referenced by id does not exist (mapped to 404 in Web).</summary>
public class DomainNotFoundException(string entity, object key)
    : DomainException($"{entity} '{key}' not found.")
{
    public string Entity { get; } = entity;
    public object Key { get; } = key;
}

/// <summary>
/// An effort-loop action violated the B8 state machine or a settings gate.
/// <see cref="Code"/> is stable for UI mapping (dialog error strings in S5/S6).
/// </summary>
public class EffortException(Entities.EffortError code)
    : DomainException($"Effort action rejected: {code}.")
{
    public Entities.EffortError Code { get; } = code;
}

/// <summary>
/// Work on the ticket is blocked while an effort proposal awaits approval
/// (B8 settings gate effort.block_work_until_approved).
/// </summary>
public class WorkBlockedByEffortException(int ticketId)
    : DomainException($"Ticket {ticketId} is blocked until the pending effort proposal is decided.")
{
    public int TicketId { get; } = ticketId;
}
