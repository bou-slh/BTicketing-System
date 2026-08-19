using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>
/// Mirrors osTicket <c>help_topic</c> — the routing rule applied when a ticket is opened:
/// every nullable id here is an override cascaded onto the new ticket (department,
/// priority, SLA, status, assignee, numbering).
/// </summary>
public class HelpTopic : TimestampedEntity
{
    public required string Name { get; set; }

    public int? ParentId { get; set; }
    public HelpTopic? Parent { get; set; }

    /// <summary>Selectable on the portal (osTicket ispublic).</summary>
    public bool IsPublic { get; set; } = true;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Archived topics cannot be picked on new tickets but stay visible on old ones
    /// (osTicket FLAG_ARCHIVED; Department twin — archived implies inactive).
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>Suppress the new-ticket auto-response (osTicket noautoresp).</summary>
    public bool NoAutoResponse { get; set; }

    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public int? PriorityId { get; set; }
    public int? SlaId { get; set; }
    public int? StatusId { get; set; }
    public int? StaffId { get; set; }
    public int? TeamId { get; set; }

    /// <summary>Thank-you page shown after portal submission (osTicket page_id).</summary>
    public int? SitePageId { get; set; }

    public int? SequenceId { get; set; }

    /// <summary>
    /// Draw random digits instead of advancing a sequence (osTicket's special
    /// sequence_id 0 "Random"); wins over <see cref="SequenceId"/>.
    /// </summary>
    public bool UseRandomNumbers { get; set; }

    /// <summary>Number format override, e.g. "R######" (osTicket number_format).</summary>
    public string? NumberFormat { get; set; }

    public int Sort { get; set; }

    public string? Notes { get; set; }

    public List<HelpTopicForm> Forms { get; set; } = [];
}

/// <summary>Custom forms attached to a topic, ordered (osTicket help_topic_form).</summary>
public class HelpTopicForm : EntityBase
{
    public int HelpTopicId { get; set; }
    public HelpTopic? HelpTopic { get; set; }

    public int FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }

    public int Sort { get; set; } = 1;

    /// <summary>JSON: per-topic field overrides (osTicket extra), e.g. {"disable":[fieldIds]}.</summary>
    public string? Extra { get; set; }

    /// <summary>
    /// Per-topic disabled field ids from <see cref="Extra"/> (osTicket extra.disable):
    /// unchecked fields on the admin forms tab are hidden from tickets opened with the
    /// topic, without touching the form definition itself.
    /// </summary>
    public IReadOnlySet<int> DisabledFieldIds()
    {
        if (string.IsNullOrWhiteSpace(Extra))
            return EmptyIds;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Extra);
            if (!doc.RootElement.TryGetProperty("disable", out var disable)
                || disable.ValueKind != System.Text.Json.JsonValueKind.Array)
                return EmptyIds;
            var ids = new HashSet<int>();
            foreach (var item in disable.EnumerateArray())
            {
                if (item.TryGetInt32(out var id))
                    ids.Add(id);
            }
            return ids;
        }
        catch (System.Text.Json.JsonException)
        {
            return EmptyIds;
        }
    }

    /// <summary>Inverse of <see cref="DisabledFieldIds"/>: null when nothing is disabled.</summary>
    public static string? BuildExtra(IReadOnlyCollection<int> disabledFieldIds) =>
        disabledFieldIds.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new { disable = disabledFieldIds.Order().ToArray() });

    private static readonly IReadOnlySet<int> EmptyIds = new HashSet<int>();
}

/// <summary>Mirrors osTicket <c>sla</c>; flag bits unpacked into explicit booleans.</summary>
public class SlaPlan : TimestampedEntity
{
    public required string Name { get; set; }

    /// <summary>Hours until a ticket under this SLA is overdue (osTicket grace_period).</summary>
    public int GracePeriodHours { get; set; }

    public int? ScheduleId { get; set; }
    public Schedule? Schedule { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Escalate priority when overdue (osTicket flag 2).</summary>
    public bool EscalateOnOverdue { get; set; }

    /// <summary>Do not send overdue alert emails (osTicket flag 4).</summary>
    public bool DisableOverdueAlerts { get; set; }

    /// <summary>SLA follows department/topic changes (osTicket flag 8, "transient").</summary>
    public bool IsTransient { get; set; }

    public string? Notes { get; set; }
}

public enum ScheduleKind
{
    BusinessHours,
    Holidays,
}

/// <summary>Mirrors osTicket <c>schedule</c> (business hours / holiday calendars).</summary>
public class Schedule : TimestampedEntity
{
    public required string Name { get; set; }

    public ScheduleKind Kind { get; set; } = ScheduleKind.BusinessHours;

    public string? Timezone { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Inactive schedules stay referenced but drop out of new-assignment
    /// pickers (admin schedules bulk enable/disable — S7).</summary>
    public bool IsActive { get; set; } = true;

    public List<ScheduleEntry> Entries { get; set; } = [];
}

/// <summary>osTicket schedule_entry.repeats.</summary>
public enum ScheduleRepeat
{
    Never,
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>One (possibly recurring) span within a schedule (osTicket schedule_entry).</summary>
public class ScheduleEntry : TimestampedEntity
{
    public int ScheduleId { get; set; }
    public Schedule? Schedule { get; set; }

    public int Sort { get; set; }

    public required string Name { get; set; }

    /// <summary>Holiday rows (schedule-edit "Tatiller" tab) close the schedule on
    /// their date instead of opening it; entry rows open it. Both live in the one
    /// osTicket-parity schedule_entry table (S7).</summary>
    public bool IsHoliday { get; set; }

    public ScheduleRepeat Repeats { get; set; } = ScheduleRepeat.Never;

    public DateOnly? StartsOn { get; set; }
    public TimeOnly? StartsAt { get; set; }
    public DateOnly? EndsOn { get; set; }
    public TimeOnly? EndsAt { get; set; }

    /// <summary>Recurrence stops after this instant (osTicket stops_on).</summary>
    public DateTimeOffset? StopsOn { get; set; }

    /// <summary>Day-of-week bitmask or day-of-month, per repeat mode (osTicket day).</summary>
    public int? Day { get; set; }

    /// <summary>Week-of-month for monthly repeats (osTicket week).</summary>
    public int? Week { get; set; }

    /// <summary>Month for yearly repeats (osTicket month).</summary>
    public int? Month { get; set; }
}

public enum FilterTarget
{
    Any,
    Web,
    Email,
    Api,
}

/// <summary>
/// Mirrors osTicket <c>filter</c>: ordered rule sets applied to incoming tickets/mail,
/// with actions executed on match.
/// </summary>
public class Filter : TimestampedEntity
{
    public required string Name { get; set; }

    /// <summary>Execution order among filters (osTicket execorder).</summary>
    public int ExecOrder { get; set; } = 99;

    public bool IsActive { get; set; } = true;

    /// <summary>All rules must match (true) vs any rule (false) (osTicket match_all_rules).</summary>
    public bool MatchAllRules { get; set; }

    /// <summary>Stop processing further filters on match (osTicket stop_onmatch).</summary>
    public bool StopOnMatch { get; set; }

    public FilterTarget Target { get; set; } = FilterTarget.Any;

    /// <summary>Restrict to tickets arriving on this mail account (osTicket email_id).</summary>
    public int? EmailAccountId { get; set; }

    public string? Notes { get; set; }

    public List<FilterRule> Rules { get; set; } = [];
    public List<FilterAction> Actions { get; set; } = [];
}

/// <summary>osTicket filter_rule.how.</summary>
public enum FilterMatchHow
{
    Equal,
    NotEqual,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Matches,
    NotMatches,
}

public class FilterRule : TimestampedEntity
{
    public int FilterId { get; set; }
    public Filter? Filter { get; set; }

    /// <summary>Matched property key, e.g. "email", "subject", "body" (osTicket what).</summary>
    public required string What { get; set; }

    public FilterMatchHow How { get; set; }

    public required string Value { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Action executed when a filter matches (osTicket filter_action): type key such as
/// "dept", "topic", "assign", "reject", "canned", with a JSON configuration payload.
/// </summary>
public class FilterAction : EntityBase
{
    public int FilterId { get; set; }
    public Filter? Filter { get; set; }

    public int Sort { get; set; }

    public required string Type { get; set; }

    /// <summary>JSON payload interpreted per action type (osTicket configuration).</summary>
    public string? Configuration { get; set; }
}

/// <summary>
/// Saved/custom queue (osTicket <c>queue</c>): a node in the agent panel's queue tree.
/// Criteria/columns/sort drive the B1 list engine; built by the v1 queue builder.
/// </summary>
public class SavedQueue : TimestampedEntity
{
    public required string Title { get; set; }

    public int? ParentId { get; set; }
    public SavedQueue? Parent { get; set; }

    /// <summary>Materialized path in the tree (osTicket path).</summary>
    public string Path { get; set; } = "/";

    /// <summary>Owner for personal queues; null = shared/public queue (osTicket staff_id 0).</summary>
    public int? StaffId { get; set; }

    /// <summary>Searched root type, "Ticket" or "Task" (osTicket root).</summary>
    public string Root { get; set; } = "Ticket";

    public int Sort { get; set; }

    /// <summary>JSON search criteria tree (osTicket config).</summary>
    public string? Criteria { get; set; }

    /// <summary>Quick-filter field path shown as chips (osTicket filter).</summary>
    public string? QuickFilter { get; set; }

    /// <summary>Inherit columns from parent instead of own set (osTicket flags bit).</summary>
    public bool InheritColumns { get; set; } = true;

    /// <summary>Queue is visible/enabled (osTicket flags bit).</summary>
    public bool IsEnabled { get; set; } = true;

    public List<SavedQueueColumn> Columns { get; set; } = [];
    public List<SavedQueueSort> Sorts { get; set; } = [];
    public List<SavedQueueExportField> ExportFields { get; set; } = [];
}

/// <summary>
/// Reusable column definition (osTicket <c>queue_column</c>): what to render (primary /
/// secondary data paths), decorations and conditional styles as JSON.
/// </summary>
public class QueueColumn : EntityBase
{
    public required string Name { get; set; }

    /// <summary>Primary data path, e.g. "number", "user__name" (osTicket primary).</summary>
    public required string PrimaryPath { get; set; }

    public string? SecondaryPath { get; set; }

    /// <summary>Value decorator key (osTicket filter, e.g. link-to-ticket).</summary>
    public string? Decorator { get; set; }

    public string? Truncate { get; set; }

    /// <summary>JSON annotation config (unread count bubbles, attachment clips…).</summary>
    public string? Annotations { get; set; }

    /// <summary>JSON conditional-style rules (osTicket conditions).</summary>
    public string? Conditions { get; set; }
}

/// <summary>Column membership + presentation within one queue (osTicket queue_columns).</summary>
public class SavedQueueColumn
{
    public int QueueId { get; set; }
    public SavedQueue? Queue { get; set; }

    public int ColumnId { get; set; }
    public QueueColumn? Column { get; set; }

    public int Sort { get; set; } = 1;

    /// <summary>Heading override for this queue (osTicket heading).</summary>
    public string? Heading { get; set; }

    public int Width { get; set; } = 100;
}

/// <summary>Named sort option (osTicket <c>queue_sort</c>): ordered column list as JSON.</summary>
public class QueueSortOption : EntityBase
{
    public required string Name { get; set; }

    /// <summary>Root type this sort applies to (osTicket root).</summary>
    public string? Root { get; set; }

    /// <summary>JSON array of "field" / "-field" entries (osTicket columns).</summary>
    public string? Columns { get; set; }
}

/// <summary>Sort options attached to a queue; one may be default (osTicket queue_sorts).</summary>
public class SavedQueueSort
{
    public int QueueId { get; set; }
    public SavedQueue? Queue { get; set; }

    public int SortOptionId { get; set; }
    public QueueSortOption? SortOption { get; set; }

    public int Sort { get; set; }

    public bool IsDefault { get; set; }
}

/// <summary>CSV export column config per queue (osTicket queue_export).</summary>
public class SavedQueueExportField : EntityBase
{
    public int QueueId { get; set; }
    public SavedQueue? Queue { get; set; }

    /// <summary>Data path to export (osTicket path).</summary>
    public required string FieldPath { get; set; }

    public string? Heading { get; set; }

    public int Sort { get; set; } = 1;
}

/// <summary>
/// Row-locked number generator (osTicket <c>sequence</c>); tickets and tasks draw their
/// public numbers from here through the topic/settings number format.
/// </summary>
public class Sequence : EntityBase
{
    public required string Name { get; set; }

    public long Next { get; set; } = 1;

    public int Increment { get; set; } = 1;

    /// <summary>Padding character for fixed-width formats (osTicket padding).</summary>
    public char Padding { get; set; } = '0';

    /// <summary>System sequences cannot be deleted (osTicket flag INTERNAL).</summary>
    public bool IsInternal { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
