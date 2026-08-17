using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Web.Identity;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent KB manager (mockups/agent/kb.html, ROADMAP §6.2): category tabs (fixed in
/// S0) filter server-side, search searches (portal KB approach), per-category counts
/// are live, and the manage-categories dialog is real CRUD through KbService.
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class KbController(AppDbContext db, IKbService kb) : Controller
{
    [HttpGet("/agent/kb")]
    [NavKey("kb")]
    public async Task<IActionResult> Index(string? q, int? cat, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        // Agent scope: every category and article, drafts (Dahili) included —
        // unlike the portal list, which filters IsPublished + Category.IsPublic.
        var categories = await db.KbCategories
            .OrderBy(c => c.Id)
            .Select(c => new AgentKbCategoryVm(c.Id, c.Name, c.Articles.Count))
            .ToListAsync(ct);
        if (cat is { } categoryId && categories.All(c => c.Id != categoryId))
            cat = null;

        var articles = db.FaqArticles.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            articles = articles.Where(a =>
                EF.Functions.ILike(a.Question, term)
                || EF.Functions.ILike(a.Keywords ?? "", term)
                || EF.Functions.ILike(a.Answer, term));
        }
        if (cat is { } id)
            articles = articles.Where(a => a.CategoryId == id);

        var rows = await articles
            .OrderBy(a => a.Question)
            .Select(a => new AgentKbArticleVm(a.Id, a.CategoryId, a.Question, a.IsPublished))
            .ToListAsync(ct);

        return View(new AgentKbIndexVm(q, cat, categories, rows));
    }

    /// <summary>Manage-categories dialog add row.</summary>
    [HttpPost("/agent/kb/cats/create")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CreateCategory(string? name, string? q, int? cat, CancellationToken ct) =>
        MutateAsync(actor => kb.CreateCategoryAsync(name ?? "", actor, ct), "kb.toastCatAdded", q, cat, ct);

    /// <summary>Manage-categories dialog per-row rename (invented row form — mockup rows are static).</summary>
    [HttpPost("/agent/kb/cats/rename")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RenameCategory(int id, string? name, string? q, int? cat, CancellationToken ct) =>
        MutateAsync(actor => kb.RenameCategoryAsync(id, name ?? "", actor, ct), "kb.toastCatRenamed", q, cat, ct);

    /// <summary>Manage-categories dialog per-row delete; refused while the category has articles.</summary>
    [HttpPost("/agent/kb/cats/delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteCategory(int id, string? q, int? cat, CancellationToken ct) =>
        MutateAsync(actor => kb.DeleteCategoryAsync(id, actor, ct), "kb.toastCatDeleted", q, cat, ct);

    // ---- helpers ----------------------------------------------------------------------

    private async Task<IActionResult> MutateAsync(
        Func<ActorContext, Task> action, string toastKey, string? q, int? cat, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await action(actor);
            TempData["KbToast"] = toastKey;
        }
        catch (DomainRuleException ex)
        {
            TempData["KbToast"] = ex.Code switch
            {
                "name-in-use" => "kb.errCatNameInUse",
                "has-articles" => "kb.errCatHasArticles",
                _ => "kb.errInvalid",
            };
            TempData["KbToastError"] = true;
        }
        catch (DomainException)
        {
            TempData["KbToast"] = "kb.errDenied";
            TempData["KbToastError"] = true;
        }
        return RedirectToAction(nameof(Index), new { q, cat });
    }
}

public sealed record AgentKbIndexVm(
    string? Query,
    int? CategoryId,
    IReadOnlyList<AgentKbCategoryVm> Categories,
    IReadOnlyList<AgentKbArticleVm> Articles);

public sealed record AgentKbCategoryVm(int Id, string Name, int ArticleCount);

public sealed record AgentKbArticleVm(int Id, int CategoryId, string Question, bool IsPublished);
