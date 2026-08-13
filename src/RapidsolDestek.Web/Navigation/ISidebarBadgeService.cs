namespace RapidsolDestek.Web.Navigation;

/// <summary>Provides live sidebar count badges. Stub until S6 wires real queries.</summary>
public interface ISidebarBadgeService
{
    string? GetCount(string countKey);
}

public sealed class StubSidebarBadgeService : ISidebarBadgeService
{
    // Mockup canon values (agent queue "Bana Atanan" = 8, tasks "Görevlerim" = 6).
    public string? GetCount(string countKey) => countKey switch
    {
        "tickets" => "8",
        "tasks" => "6",
        _ => null,
    };
}
