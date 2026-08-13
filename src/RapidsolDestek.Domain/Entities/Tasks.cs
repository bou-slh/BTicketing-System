using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// Internal work item (osTicket <c>task</c>). osTicket attaches tasks polymorphically
/// (object_id/object_type, in practice always a ticket); here that collapses to a
/// nullable <see cref="TicketId"/> — null means a standalone task. Title/description
/// live in osTicket dynamic form data; we promote the title to a column.
/// </summary>
public class TaskItem : TimestampedEntity
{
    /// <summary>Formatted public number from the task sequence.</summary>
    public required string Number { get; set; }

    public required string Title { get; set; }

    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? StaffId { get; set; }
    public Staff? Staff { get; set; }

    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    public int ThreadId { get; set; }
    public Thread? Thread { get; set; }

    public int? LockId { get; set; }

    public DateTimeOffset? DueDate { get; set; }

    /// <summary>Null while open (osTicket flags bit ISOPEN + closed datetime).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOverdue { get; set; }
}
