namespace RapidsolDestek.Domain.Services;

/// <summary>
/// One of the 21 canon email templates every <c>EmailTemplateSet</c> carries
/// (mockups/admin/template-edit.html, ROADMAP §2 "Templates"). <see cref="Group"/>
/// is the page's section: "ticket-user" (Talep → Kullanıcı), "ticket-agent"
/// (Talep → Temsilci), "task" (Görev). <see cref="Variables"/> is the honest
/// per-code pill list: the keys the REAL substitution bag
/// (CannedResponseService.BuildVariablesAsync) actually fills at send time —
/// osTicket's wider variable canon (%{recipient}, %{signature}, task.* fields …)
/// joins when the S8 mail pipeline extends the bag.
/// </summary>
public sealed record EmailTemplateDefinition(
    string Code,
    string Group,
    string DefaultName,
    IReadOnlyList<string> Variables);

/// <summary>
/// The canonical 21-template catalog in template-edit.html DOM order. Single
/// source for the seeder (stock content), set create/clone, and the per-template
/// editor rows — extend here, never inline.
/// </summary>
public static class EmailTemplateCatalog
{
    /// <summary>Variables the S4 ticket bag fills for every ticket/task template.</summary>
    private static readonly string[] TicketVars =
    [
        "ticket.number", "ticket.subject", "ticket.status", "ticket.dept.name",
        "ticket.user.name", "ticket.staff.name", "ticket.create_date",
    ];

    /// <summary>The two effort templates additionally see the latest proposal.</summary>
    private static readonly string[] EffortVars =
    [
        .. TicketVars, "effort.hours", "effort.note", "effort.revision",
    ];

    public static readonly IReadOnlyList<EmailTemplateDefinition> All =
    [
        // ---- Talep → Kullanıcı (template-edit.html group 1) -------------------------
        new("ticket.autoresp", "ticket-user", "Yeni Talep Otomatik Yanıtı", TicketVars),
        new("ticket.autoreply", "ticket-user", "Yeni Talep Oto-cevap", TicketVars),
        new("message.autoresp", "ticket-user", "Yeni Mesaj Onayı", TicketVars),
        new("ticket.notice", "ticket-user", "Yeni Talep Bildirimi", TicketVars),
        new("ticket.overlimit", "ticket-user", "Limit Aşımı Bildirimi", TicketVars),
        new("ticket.reply", "ticket-user", "Yanıt Şablonu", TicketVars),
        new("ticket.activity.notice", "ticket-user", "Yeni Aktivite Bildirimi", TicketVars),
        new("effort.request", "ticket-user", "Efor Onayı İsteği", EffortVars),

        // ---- Talep → Temsilci (group 2) ----------------------------------------------
        new("ticket.alert", "ticket-agent", "Yeni Talep Uyarısı", TicketVars),
        new("message.alert", "ticket-agent", "Yeni Mesaj Uyarısı", TicketVars),
        new("note.alert", "ticket-agent", "İç Aktivite Uyarısı", TicketVars),
        new("assigned.alert", "ticket-agent", "Atama Uyarısı", TicketVars),
        new("transfer.alert", "ticket-agent", "Aktarım Uyarısı", TicketVars),
        new("ticket.overdue", "ticket-agent", "Gecikme Uyarısı", TicketVars),
        new("effort.response", "ticket-agent", "Efor Yanıtı Uyarısı", EffortVars),

        // ---- Görev (group 3) ----------------------------------------------------------
        new("task.alert", "task", "Yeni Görev Uyarısı", TicketVars),
        new("task.activity.alert", "task", "Yeni Aktivite Uyarısı", TicketVars),
        new("task.activity.notice", "task", "Yeni Aktivite Bildirimi (katılımcı)", TicketVars),
        new("task.assigned.alert", "task", "Görev Atama Uyarısı", TicketVars),
        new("task.transfer.alert", "task", "Görev Aktarım Uyarısı", TicketVars),
        new("task.overdue.alert", "task", "Görev Gecikme Uyarısı", TicketVars),
    ];

    public static EmailTemplateDefinition? Find(string code) =>
        All.FirstOrDefault(d => d.Code == code);

    /// <summary>Stock body a fresh (non-cloned) set starts with — the S3 seed canon.</summary>
    public static string StockBody(EmailTemplateDefinition definition) =>
        $"<p>{definition.DefaultName} — %{{ticket.number}}</p>";
}
