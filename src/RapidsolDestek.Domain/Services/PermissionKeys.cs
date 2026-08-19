namespace RapidsolDestek.Domain.Services;

/// <summary>
/// Dotted permission keys stored in <see cref="Entities.Role.Permissions"/> — the same
/// naming osTicket uses (ticket.create, canned.manage, …) plus the product-original
/// effort key. Keep in sync with the seeded roles (DomainSeeder) and the role-edit
/// matrix (S7). Constants so typos are compile errors.
/// </summary>
public static class PermissionKeys
{
    public const string TicketCreate = "ticket.create";
    public const string TicketEdit = "ticket.edit";
    public const string TicketAssign = "ticket.assign";
    public const string TicketRelease = "ticket.release";
    public const string TicketTransfer = "ticket.transfer";
    public const string TicketRefer = "ticket.refer";
    public const string TicketMerge = "ticket.merge";
    public const string TicketLink = "ticket.link";
    public const string TicketReply = "ticket.reply";
    public const string TicketMarkAnswered = "ticket.markanswered";
    public const string TicketClose = "ticket.close";
    public const string TicketDelete = "ticket.delete";

    /// <summary>Product-original: propose/revise/withdraw effort (Efor Onayı, B8).</summary>
    public const string EffortPropose = "effort.propose";

    public const string TaskCreate = "task.create";
    public const string TaskEdit = "task.edit";
    public const string TaskAssign = "task.assign";
    public const string TaskTransfer = "task.transfer";
    public const string TaskReply = "task.reply";
    public const string TaskClose = "task.close";
    public const string TaskDelete = "task.delete";

    // user.create/user.delete/org.create/org.delete: role-edit matrix boxes persisted
    // for parity with the osTicket canon; the S6 services still gate create/delete on
    // user.edit / user.manage / org.edit (annotated there) — consuming these finer
    // keys is TODO with the S7 role rollout across endpoints.
    public const string UserCreate = "user.create";
    public const string UserEdit = "user.edit";
    public const string UserDelete = "user.delete";
    public const string UserManage = "user.manage";
    public const string UserDirectory = "user.dir";
    public const string OrgCreate = "org.create";
    public const string OrgEdit = "org.edit";
    public const string OrgDelete = "org.delete";

    public const string FaqManage = "faq.manage";
    public const string CannedManage = "canned.manage";
    public const string ThreadEdit = "thread.edit";

    public const string DeptManage = "dept.manage";
    public const string StaffManage = "staff.manage";
    public const string BanlistManage = "banlist.manage";
    public const string StatsView = "stats.view";
    public const string SearchAdvanced = "search.advanced";
}
