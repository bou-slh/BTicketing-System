using RapidsolDestek.Domain.Common;

namespace RapidsolDestek.Domain.Entities;

/// <summary>Object kinds a custom form/attachment can hang off (osTicket char codes).</summary>
public enum FormObjectType
{
    /// <summary>'T'</summary>
    Ticket,
    /// <summary>'A'</summary>
    Task,
    /// <summary>'U'</summary>
    User,
    /// <summary>'O'</summary>
    Organization,
}

/// <summary>osTicket form.type: which built-in slot a form fills, or a generic custom form.</summary>
public enum FormKind
{
    /// <summary>'G' — custom form attachable to help topics.</summary>
    General,
    /// <summary>'T' — the built-in ticket details form.</summary>
    Ticket,
    /// <summary>'A' — the built-in task details form.</summary>
    Task,
    /// <summary>'U' — the built-in user contact form.</summary>
    User,
    /// <summary>'O' — the built-in organization form.</summary>
    Organization,
}

/// <summary>
/// Form designer output (osTicket <c>form</c>): a titled, ordered set of fields rendered
/// on tickets/tasks/users/orgs. Built-in forms are protected from deletion.
/// </summary>
public class FormDefinition : TimestampedEntity
{
    public required string Title { get; set; }

    /// <summary>Variable-name stem for template variables (osTicket name).</summary>
    public string? Name { get; set; }

    public FormKind Kind { get; set; } = FormKind.General;

    /// <summary>Built-in forms cannot be deleted (osTicket flag DELETABLE inverted).</summary>
    public bool IsSystem { get; set; }

    /// <summary>Shown above the fields on entry forms (osTicket instructions).</summary>
    public string? Instructions { get; set; }

    public string? Notes { get; set; }

    public List<FormField> Fields { get; set; } = [];
}

/// <summary>
/// One designed field (osTicket <c>form_field</c>). Type is an extensible key
/// ("text", "memo", "choices", "list-N", "date", "phone", …) with a JSON configuration;
/// visibility/requirement masks are unpacked into explicit booleans.
/// </summary>
public class FormField : TimestampedEntity
{
    public int FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }

    public required string Type { get; set; }

    public required string Label { get; set; }

    /// <summary>Variable name, unique within the form (osTicket name).</summary>
    public required string Name { get; set; }

    /// <summary>JSON per-type configuration (choices, length limits, formats…).</summary>
    public string? Configuration { get; set; }

    public int Sort { get; set; }

    public string? Hint { get; set; }

    public bool RequiredForAgents { get; set; }
    public bool RequiredForUsers { get; set; }
    public bool VisibleToAgents { get; set; } = true;
    public bool VisibleToUsers { get; set; } = true;

    /// <summary>Answers stay when the field is deleted; field is soft-disabled (osTicket flag).</summary>
    public bool IsDisabled { get; set; }
}

/// <summary>A filled form instance bound to an object (osTicket form_entry).</summary>
public class FormEntry : TimestampedEntity
{
    public int FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }

    public FormObjectType ObjectType { get; set; }
    public int ObjectId { get; set; }

    public int Sort { get; set; } = 1;

    public List<FormEntryValue> Values { get; set; } = [];
}

/// <summary>One field's answer within an entry (osTicket form_entry_values).</summary>
public class FormEntryValue
{
    public int FormEntryId { get; set; }
    public FormEntry? FormEntry { get; set; }

    public int FormFieldId { get; set; }
    public FormField? FormField { get; set; }

    public string? Value { get; set; }

    /// <summary>Referenced list-item/lookup id when the field is a selection (osTicket value_id).</summary>
    public int? ValueId { get; set; }
}

/// <summary>osTicket list.sort_mode.</summary>
public enum ListSortMode
{
    Alpha,
    AlphaDescending,
    SortColumn,
}

/// <summary>
/// Custom list (osTicket <c>list</c>) used by "choices" form fields; system lists
/// (e.g. ticket statuses surface as one in osTicket) are protected via <see cref="Type"/>.
/// </summary>
public class ListDefinition : TimestampedEntity
{
    public required string Name { get; set; }

    public string? PluralName { get; set; }

    public ListSortMode SortMode { get; set; } = ListSortMode.Alpha;

    /// <summary>Non-null marks a protected system list (osTicket type).</summary>
    public string? Type { get; set; }

    /// <summary>JSON: extra item-property form definition (osTicket configuration).</summary>
    public string? Configuration { get; set; }

    public string? Notes { get; set; }

    public List<ListItem> Items { get; set; } = [];
}

public class ListItem : EntityBase
{
    public int ListDefinitionId { get; set; }
    public ListDefinition? ListDefinition { get; set; }

    public required string Value { get; set; }

    /// <summary>Abbreviation/extra value (osTicket extra).</summary>
    public string? Abbrev { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int Sort { get; set; } = 1;

    /// <summary>JSON per-item property values (osTicket properties).</summary>
    public string? Properties { get; set; }
}
