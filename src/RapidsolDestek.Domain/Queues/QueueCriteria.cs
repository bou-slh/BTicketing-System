using System.Text.Json;

namespace RapidsolDestek.Domain.Queues;

/// <summary>
/// Parsed form of <see cref="Entities.SavedQueue.Criteria"/> — the flat JSON object
/// the queue tree is seeded with (v1; the S7 queue builder emits the same shape).
/// Unknown keys are collected, not rejected, so newer builders don't break older code.
/// </summary>
public sealed class QueueCriteria
{
    /// <summary>Ticket lifecycle state key: "open" | "closed" | "archived" | "deleted".</summary>
    public string? State { get; init; }

    /// <summary>Exact status key ("wait", "test", …).</summary>
    public string? Status { get; init; }

    public bool? IsAnswered { get; init; }
    public bool? IsOverdue { get; init; }

    /// <summary>"pending" — ticket has an active pending effort proposal.</summary>
    public string? Effort { get; init; }

    /// <summary>"me" | "my-teams" | "none" | a staff id as string.</summary>
    public string? Assignee { get; init; }

    public int? DepartmentId { get; init; }
    public int? HelpTopicId { get; init; }

    /// <summary>Priority key ("low"…"emergency").</summary>
    public string? Priority { get; init; }

    /// <summary>Closed-window filter: "today" | "week" | "month".</summary>
    public string? Closed { get; init; }

    /// <summary>Full-text term applied via tsvector search.</summary>
    public string? Search { get; init; }

    /// <summary>Criteria keys present in the JSON but not understood (logged by the engine).</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    public static QueueCriteria Empty { get; } = new();

    public static QueueCriteria Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Empty;

        string? state = null, status = null, effort = null, assignee = null;
        string? priority = null, closed = null, search = null;
        bool? isAnswered = null, isOverdue = null;
        int? dept = null, topic = null;
        List<string> unknown = [];

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "state": state = prop.Value.GetString(); break;
                case "status": status = prop.Value.GetString(); break;
                case "isanswered": isAnswered = ReadBool(prop.Value); break;
                case "isoverdue": isOverdue = ReadBool(prop.Value); break;
                case "effort": effort = prop.Value.GetString(); break;
                case "assignee": assignee = ReadString(prop.Value); break;
                case "dept": dept = ReadInt(prop.Value); break;
                case "topic": topic = ReadInt(prop.Value); break;
                case "priority": priority = prop.Value.GetString(); break;
                case "closed": closed = prop.Value.GetString(); break;
                case "search": search = prop.Value.GetString(); break;
                default: unknown.Add(prop.Name); break;
            }
        }

        return new QueueCriteria
        {
            State = state, Status = status, IsAnswered = isAnswered, IsOverdue = isOverdue,
            Effort = effort, Assignee = assignee, DepartmentId = dept, HelpTopicId = topic,
            Priority = priority, Closed = closed, Search = search, UnknownKeys = unknown,
        };
    }

    /// <summary>Assignee as an explicit staff id, when it isn't one of the symbolic values.</summary>
    public int? AssigneeStaffId =>
        Assignee is not null && int.TryParse(Assignee, out var id) ? id : null;

    private static bool? ReadBool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
        _ => null,
    };

    private static int? ReadInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(e.GetString(), out var i) => i,
        _ => null,
    };

    private static string? ReadString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };
}
