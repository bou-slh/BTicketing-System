using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Admin.Controllers;

/// <summary>
/// Admin email template sets (mockups/admin/templates.html + template-edit.html,
/// ROADMAP §6.3). List: the B1 engine over <see cref="EmailTemplateSet"/> rows with
/// toolbar bulk enable/disable/delete (delete guard: sets referenced by the email
/// settings default or a department template-set pointer are skipped) and the
/// dlg-new-set create dialog whose submit really saves and closes (the audited B2
/// close-swallows-save bug: the mockup's Oluştur is a bare button). Editor: set
/// details + the 21-template canon in the mockup's three groups; each row opens its
/// OWN prefilled dialog (B2 — the mockup shares one hardcoded dlg-editor; split
/// per-code, settings-agents precedent), with click-to-insert variable pills (B5)
/// and a per-template preview rendered through the REAL substitution path
/// (CannedResponseService bag over the hero ticket + TemplateVariableExpander).
/// </summary>
[Area("Admin")]
[Authorize(Policy = "AdminOnly")]
public class TemplatesController(
    AppDbContext db,
    ISettingsService settings,
    ISystemTemplateService templates,
    ICannedResponseService variables,
    IHtmlSanitizerService sanitizer) : Controller
{
    public const int PageSize = 8;

    private static readonly string[] SortKeys = ["name", "status", "lang", "updated"];

    /// <summary>Template-edit page rows: canon code → the mockup's te.t* i18n key
    /// suffix (name; description = suffix + "D"). Order/groups live in
    /// <see cref="EmailTemplateCatalog"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> NameKeys = new Dictionary<string, string>
    {
        ["ticket.autoresp"] = "tNewTicketAutoResp",
        ["ticket.autoreply"] = "tNewTicketAutoReply",
        ["message.autoresp"] = "tNewMessageConfirm",
        ["ticket.notice"] = "tNewTicketNotice",
        ["ticket.overlimit"] = "tOverlimit",
        ["ticket.reply"] = "tResponse",
        ["ticket.activity.notice"] = "tNewActivityNotice",
        ["effort.request"] = "tEffortRequest",
        ["ticket.alert"] = "tNewTicketAlert",
        ["message.alert"] = "tNewMessageAlert",
        ["note.alert"] = "tInternalActivity",
        ["assigned.alert"] = "tAssignmentAlert",
        ["transfer.alert"] = "tTransferAlert",
        ["ticket.overdue"] = "tOverdueAlert",
        ["effort.response"] = "tEffortResponse",
        ["task.alert"] = "tTaskNewAlert",
        ["task.activity.alert"] = "tTaskActivityAlert",
        ["task.activity.notice"] = "tTaskActivityNotice",
        ["task.assigned.alert"] = "tTaskAssignment",
        ["task.transfer.alert"] = "tTaskTransfer",
        ["task.overdue.alert"] = "tTaskOverdue",
    };

    // ---- templates.html (B1 list) --------------------------------------------------------

    [HttpGet("/admin/templates")]
    [NavKey("templates")]
    public async Task<IActionResult> Index(
        string? q, string? sort, string? dir, int page = 1, CancellationToken ct = default)
    {
        var email = await settings.GetEmailAsync(ct);

        var query = db.EmailTemplateSets.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(s => EF.Functions.ILike(s.Name, $"%{q.Trim()}%"));

        var projected = query.Select(s => new
        {
            s.Id, s.Name, s.IsActive, s.Language,
            InUse = s.Id == email.DefaultTemplateSetId
                || db.Departments.Any(d => d.TemplateSetId == s.Id),
            Updated = s.UpdatedAt ?? s.CreatedAt,
        });

        // The mockup's sort indicator sits on the Şablon Seti column (sorted-desc).
        var sortKey = SortKeys.Contains(sort) ? sort! : "name";
        var desc = dir == "asc" ? false : dir == "desc" || sortKey is "name" or "updated";
        projected = (sortKey, desc) switch
        {
            ("status", true) => projected.OrderByDescending(s => s.IsActive).ThenBy(s => s.Name),
            ("status", false) => projected.OrderBy(s => s.IsActive).ThenBy(s => s.Name),
            ("lang", true) => projected.OrderByDescending(s => s.Language).ThenBy(s => s.Name),
            ("lang", false) => projected.OrderBy(s => s.Language).ThenBy(s => s.Name),
            ("updated", true) => projected.OrderByDescending(s => s.Updated).ThenByDescending(s => s.Id),
            ("updated", false) => projected.OrderBy(s => s.Updated).ThenBy(s => s.Id),
            (_, true) => projected.OrderByDescending(s => s.Name),
            _ => projected.OrderBy(s => s.Name),
        };

        var total = await projected.CountAsync(ct);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);
        var rows = await projected.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(s => new TemplateSetRowVm(s.Id, s.Name, s.IsActive, s.InUse, s.Language, s.Updated))
            .ToListAsync(ct);

        return View(new TemplatesIndexVm(
            q, sortKey, desc, page, pageCount, total,
            total == 0 ? 0 : (page - 1) * PageSize + 1,
            Math.Min(page * PageSize, total), rows,
            // dlg-new-set clone options ("the new set starts with this set's content").
            await db.EmailTemplateSets.OrderBy(s => s.Id)
                .Select(s => new OptionVm(s.Id, s.Name)).ToListAsync(ct)));
    }

    /// <summary>dlg-new-set (B2): validate → save → the dialog is gone with the PRG
    /// redirect (the S0-audited "create dialog closes properly" defect). Lands on the
    /// new set's editor.</summary>
    [HttpPost("/admin/templates/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? name, int? cloneId, string? lang, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        try
        {
            var setId = await templates.CreateSetAsync(
                name ?? "", lang == "en" ? "en" : "tr",
                cloneId is > 0 ? cloneId : null,
                ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
            TempData["TeToast"] = "te.toastCreated";
            return Redirect($"/admin/template-edit?id={setId}");
        }
        catch (DomainRuleException ex)
        {
            return ToastBack(ex.Code == "name-in-use" ? "tp.errNameInUse" : "tp.errName", error: true);
        }
        catch (DomainNotFoundException)
        {
            return ToastBack("tp.errName", error: true);
        }
    }

    /// <summary>Toolbar bulk enable/disable/delete over the selection. Delete guard:
    /// sets referenced by the email settings default template set or a department's
    /// template-set pointer are skipped (emails precedent).</summary>
    [HttpPost("/admin/templates/bulk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bulk(string act, int[] ids, string? returnUrl, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        if (ids.Length == 0)
            return ToastBack("tp.bulkNone", error: true, returnUrl: returnUrl);
        if (act is not ("enable" or "disable" or "delete"))
            return LocalRedirectOrIndex(returnUrl);

        var sets = await db.EmailTemplateSets.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        int ok = 0, skipped = ids.Length - sets.Count;

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            foreach (var set in sets)
            {
                switch (act)
                {
                    case "enable" or "disable":
                        set.IsActive = act == "enable";
                        ok++;
                        break;
                    case "delete":
                        if (await IsSetInUseAsync(set.Id, ct))
                        {
                            skipped++;
                        }
                        else
                        {
                            db.EmailTemplateSets.Remove(set); // templates cascade
                            ok++;
                        }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        TempData["TpToastOk"] = ok;
        TempData["TpToastSkipped"] = skipped;
        TempData["TpToast"] = skipped > 0 ? "tp.bulkPartial" : "tp.bulkDone";
        if (skipped > 0)
            TempData["TpToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    // ---- template-edit.html ---------------------------------------------------------------

    [HttpGet("/admin/template-edit")]
    [NavKey("templates")]
    public async Task<IActionResult> Edit(int id, CancellationToken ct = default)
    {
        var set = await db.EmailTemplateSets.Include(s => s.Templates)
            .AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (set is null)
            return NotFound();

        // The 21 canon rows in mockup order; a row missing from an older set
        // prefills with stock content (SaveTemplate upserts it on first save).
        var rows = EmailTemplateCatalog.All.Select(d =>
        {
            var stored = set.Templates.FirstOrDefault(t => t.CodeName == d.Code);
            var body = stored?.Body ?? EmailTemplateCatalog.StockBody(d);
            return new TemplateRowVm(
                d.Code, d.Group, NameKeys[d.Code],
                stored?.Subject ?? d.DefaultName, body,
                stored is null ? null : stored.UpdatedAt ?? stored.CreatedAt,
                d.Variables,
                // Honesty flag (B5): tokens present in the stored body that the real
                // substitution bag does not fill — they expand to empty at send time.
                [.. TemplateVariableExpander.ListVariables(body).Where(v => !d.Variables.Contains(v))]);
        }).ToList();

        return View(new TemplateEditVm(set, rows));
    }

    /// <summary>Set details save (name / status radios / language / notes).</summary>
    [HttpPost("/admin/template-edit/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int id, string? name, bool isActive, string? lang, string? notes, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var set = await db.EmailTemplateSets.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (set is null)
            return NotFound();

        name = (name ?? "").Trim();
        if (name.Length == 0)
            return EditToastBack(id, "te.errName");
        if (await db.EmailTemplateSets.AnyAsync(s => s.Id != id && s.Name.ToLower() == name.ToLower(), ct))
            return EditToastBack(id, "te.errNameInUse");

        using (ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()).BeginAuditScope())
        {
            set.Name = name;
            set.IsActive = isActive;
            set.Language = lang == "en" ? "en" : "tr";
            set.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            await db.SaveChangesAsync(ct);
        }

        TempData["TeToast"] = "te.toastSaved";
        return Redirect($"/admin/template-edit?id={id}");
    }

    /// <summary>Per-template dialog save (B2): one code of THIS set only — the fixed
    /// close-swallows-save submit (the mockup's Kaydet carries data-dialog-close).</summary>
    [HttpPost("/admin/template-edit/save-template")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTemplate(
        int id, string? code, string? subject, string? body, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        try
        {
            await templates.SaveTemplateAsync(id, code ?? "", subject ?? "", body ?? "",
                ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        }
        catch (DomainRuleException)
        {
            return EditToastBack(id, "te.errTemplate");
        }
        catch (DomainNotFoundException)
        {
            return NotFound();
        }

        TempData["TeToast"] = "te.toastTplSaved";
        return Redirect($"/admin/template-edit?id={id}");
    }

    /// <summary>
    /// Per-template preview (ROADMAP row; invented UI — the mockup's dialog has no
    /// preview control): expands the CURRENT editor state (unsaved subject/body)
    /// through the real substitution path — the S4 variable bag built over the hero
    /// ticket (§2 R716555; the oldest ticket when renumbered, a static canon bag on
    /// an empty database). The body is sanitized before it renders as HTML.
    /// </summary>
    [HttpPost("/admin/template-edit/preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(string? subject, string? body, CancellationToken ct)
    {
        var heroId = await db.Tickets.Where(t => t.Number == "R716555").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
            ?? await db.Tickets.OrderBy(t => t.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);

        var bag = heroId is { } ticketId
            ? await variables.BuildVariablesAsync(ticketId, ct)
            : new Dictionary<string, string?>
            {
                // §2 canon fallback so the preview stays meaningful before any ticket exists.
                ["ticket.number"] = "R716555",
                ["ticket.subject"] = "Yol ücreti hatası hakkında",
                ["ticket.status"] = "Açık",
                ["ticket.dept.name"] = "Bordro",
                ["ticket.user.name"] = "Bourla Salehi",
                ["ticket.staff.name"] = "Ümit Yaşar Akın",
                ["ticket.create_date"] = "04.08.2026 16:20",
                ["effort.hours"] = "6",
                ["effort.note"] = "Analiz + düzeltme",
                ["effort.revision"] = "1",
            };

        return Json(new
        {
            subject = TemplateVariableExpander.Expand(subject ?? "", bag),
            body = sanitizer.Sanitize(TemplateVariableExpander.Expand(body ?? "", bag)),
        });
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Referenced sets are undeletable: the email settings default template
    /// set and department template-set pointers name them by id.</summary>
    private async Task<bool> IsSetInUseAsync(int setId, CancellationToken ct)
    {
        if (await db.Departments.AnyAsync(d => d.TemplateSetId == setId, ct))
            return true;
        return (await settings.GetEmailAsync(ct)).DefaultTemplateSetId == setId;
    }

    private IActionResult EditToastBack(int id, string key)
    {
        TempData["TeToast"] = key;
        TempData["TeToastError"] = true;
        return Redirect($"/admin/template-edit?id={id}");
    }

    private IActionResult ToastBack(string key, bool error = false, string? returnUrl = null)
    {
        TempData["TpToast"] = key;
        if (error)
            TempData["TpToastError"] = true;
        return LocalRedirectOrIndex(returnUrl);
    }

    private IActionResult LocalRedirectOrIndex(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
}

public sealed record TemplatesIndexVm(
    string? Query,
    string Sort,
    bool Desc,
    int Page,
    int PageCount,
    int Total,
    int From,
    int To,
    IReadOnlyList<TemplateSetRowVm> Rows,
    IReadOnlyList<OptionVm> CloneOptions);

public sealed record TemplateSetRowVm(
    int Id,
    string Name,
    bool IsActive,
    bool InUse,
    string Language,
    DateTimeOffset Updated);

public sealed record TemplateEditVm(
    EmailTemplateSet Set,
    IReadOnlyList<TemplateRowVm> Rows);

/// <summary>One canon template row: <see cref="NameKey"/> is the te.t* i18n suffix
/// (description key = suffix + "D"); <see cref="ExtraVariables"/> are body tokens
/// outside the honest per-code list (they expand to empty at send time).</summary>
public sealed record TemplateRowVm(
    string Code,
    string Group,
    string NameKey,
    string Subject,
    string Body,
    DateTimeOffset? Updated,
    IReadOnlyList<string> Variables,
    IReadOnlyList<string> ExtraVariables)
{
    public string DomId => "dlg-editor-" + Code.Replace('.', '-');
}
