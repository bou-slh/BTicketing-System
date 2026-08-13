namespace RapidsolDestek.Web.Navigation;

/// <summary>
/// C# transcription of the mockup nav definitions (mockups/assets/js/app.js PORTAL_NAV / AGENT_NAV / ADMIN_NAV).
/// Keys and label i18n keys must stay in sync with the mockups; URLs mirror mockup filenames.
/// </summary>
public sealed record NavItem(string Key, string Url, string LabelKey, string? Icon = null, bool Live = false, string? CountKey = null);

public sealed record NavGroup(string LabelKey, IReadOnlyList<NavItem> Items);

public static class NavConfig
{
    public static readonly IReadOnlyList<NavItem> Portal =
    [
        new("home", "/", "nav.home"),
        new("new", "/open", "nav.newTicket"),
        new("tickets", "/tickets", "nav.myTickets"),
        new("kb", "/kb", "nav.kb"),
    ];

    public static readonly IReadOnlyList<NavGroup> Agent =
    [
        new("nav.group.workspace",
        [
            new("dashboard", "/agent/dashboard", "nav.dashboard", "▤"),
            new("live", "/agent/live", "nav.live", "◉", Live: true),
            new("tickets", "/agent/tickets", "nav.tickets", "◫", CountKey: "tickets"),
            new("tasks", "/agent/tasks", "nav.tasks", "☑", CountKey: "tasks"),
        ]),
        new("nav.group.records",
        [
            new("users", "/agent/users", "nav.users", "◔"),
            new("orgs", "/agent/orgs", "nav.orgs", "▣"),
            new("directory", "/agent/directory", "nav.directory", "☏"),
        ]),
        new("nav.group.knowledge",
        [
            new("kb", "/agent/kb", "nav.kb", "✎"),
            new("canned", "/agent/canned", "nav.canned", "❝"),
        ]),
        new("nav.group.personal",
        [
            new("profile", "/agent/profile", "nav.myProfile", "◍"),
        ]),
    ];

    public static readonly IReadOnlyList<NavGroup> Admin =
    [
        new("nav.group.overview",
        [
            new("dashboard", "/admin/dashboard", "nav.dashboard", "▤"),
            new("system-info", "/admin/system-info", "nav.systemInfo", "ℹ"),
            new("system-logs", "/admin/system-logs", "nav.systemLogs", "≡"),
            new("audit-logs", "/admin/audit-logs", "nav.auditLogs", "◷"),
        ]),
        new("nav.group.settings",
        [
            new("settings-company", "/admin/settings-company", "nav.company", "▣"),
            new("settings-system", "/admin/settings-system", "nav.system", "⚙"),
            new("settings-tickets", "/admin/settings-tickets", "nav.tickets", "◫"),
            new("settings-tasks", "/admin/settings-tasks", "nav.tasks", "☑"),
            new("settings-agents", "/admin/settings-agents", "nav.agents", "◍"),
            new("settings-users", "/admin/settings-users", "nav.users", "◔"),
            new("settings-kb", "/admin/settings-kb", "nav.kb", "✎"),
        ]),
        new("nav.group.manage",
        [
            new("helptopics", "/admin/helptopics", "nav.helptopics", "❓"),
            new("queues", "/admin/queues", "nav.queues", "☰"),
            new("filters", "/admin/filters", "nav.filters", "⏚"),
            new("slas", "/admin/slas", "nav.slas", "⏱"),
            new("schedules", "/admin/schedules", "nav.schedules", "▦"),
            new("forms", "/admin/forms", "nav.forms", "▤"),
            new("lists", "/admin/lists", "nav.lists", "≔"),
            new("pages", "/admin/pages", "nav.sitePages", "❐"),
            new("apikeys", "/admin/apikeys", "nav.apikeys", "⚿"),
            new("plugins", "/admin/plugins", "nav.plugins", "✦"),
        ]),
        new("nav.group.emails",
        [
            new("emails", "/admin/emails", "nav.emailAddresses", "✉"),
            new("email-settings", "/admin/email-settings", "nav.emailSettings", "⚙"),
            new("templates", "/admin/templates", "nav.templates", "❏"),
            new("banlist", "/admin/banlist", "nav.banlist", "⃠"),
            new("email-diagnostic", "/admin/email-diagnostic", "nav.emailDiagnostic", "➤"),
        ]),
        new("nav.group.team",
        [
            new("staff", "/admin/staff", "nav.staff", "◍"),
            new("teams", "/admin/teams", "nav.teams", "◎"),
            new("roles", "/admin/roles", "nav.roles", "⛨"),
            new("departments", "/admin/departments", "nav.departments", "▥"),
        ]),
    ];
}
