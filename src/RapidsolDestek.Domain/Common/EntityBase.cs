namespace RapidsolDestek.Domain.Common;

/// <summary>
/// Base for all domain entities with an int surrogate key (osTicket reference keeps
/// int auto-increment keys throughout; we mirror that).
/// </summary>
public abstract class EntityBase
{
    public int Id { get; set; }
}

/// <summary>
/// Nearly every osTicket table carries created/updated columns; entities deriving from
/// this get both stamped automatically by the SaveChanges interceptor.
/// </summary>
public abstract class TimestampedEntity : EntityBase, IHasTimestamps
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Opt-out marker for the audit interceptor (high-churn technical tables such as
/// drafts, locks and the audit trail itself).
/// </summary>
public interface INotAudited
{
}
