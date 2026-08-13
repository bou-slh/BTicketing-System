using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Web.Navigation;

namespace RapidsolDestek.Web.Areas.Portal.Controllers;

/// <summary>
/// Portal knowledge base (mockups/portal/kb.html + kb-article.html).
/// Read-only over the seeded KB; the helpful vote is the single mutation.
/// </summary>
[Area("Portal")]
[Authorize(Policy = "PortalUser")]
public class KbController(AppDbContext db) : Controller
{
    [HttpGet("/kb")]
    [NavKey("kb")]
    public async Task<IActionResult> Index(string? q, int? cat, CancellationToken ct)
    {
        var articles = db.FaqArticles
            .Where(a => a.IsPublished && a.Category!.IsPublic);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            articles = articles.Where(a =>
                EF.Functions.ILike(a.Question, term)
                || EF.Functions.ILike(a.Keywords ?? "", term)
                || EF.Functions.ILike(a.Answer, term));
        }

        if (cat is { } categoryId)
            articles = articles.Where(a => a.CategoryId == categoryId);

        var rows = await articles
            .OrderBy(a => a.Question)
            .Select(a => new KbArticleRowVm(
                a.Id, a.CategoryId, a.Question,
                a.Keywords, a.UpdatedAt ?? a.CreatedAt))
            .ToListAsync(ct);

        var categories = await db.KbCategories
            .Where(c => c.IsPublic)
            .OrderBy(c => c.Id)
            .Select(c => new KbCategoryVm(c.Id, c.Name, c.Description))
            .ToListAsync(ct);

        return View(new KbIndexVm(q, cat, categories, rows));
    }

    [HttpGet("/kb-article")]
    [NavKey("kb")]
    public async Task<IActionResult> Article(int id, CancellationToken ct)
    {
        var article = await db.FaqArticles
            .Include(a => a.Category)
            .SingleOrDefaultAsync(a => a.Id == id && a.IsPublished && a.Category!.IsPublic, ct);
        if (article is null)
            return NotFound();

        var attachments = await db.Attachments
            .Where(at => at.ObjectType == AttachmentObjectType.FaqArticle && at.ObjectId == id)
            .Join(db.StoredFiles, at => at.FileId, f => f.Id, (at, f) => at.Name ?? f.Name)
            .ToListAsync(ct);

        var related = await db.FaqArticles
            .Where(a => a.IsPublished && a.Id != id && a.CategoryId == article.CategoryId)
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .Take(3)
            .Select(a => new KbArticleRowVm(a.Id, a.CategoryId, a.Question, null, a.UpdatedAt ?? a.CreatedAt))
            .ToListAsync(ct);

        return View(new KbArticleVm(
            article.Id,
            article.Question,
            article.Answer,
            article.Category!.Name,
            article.UpdatedAt ?? article.CreatedAt,
            attachments,
            related,
            Voted: TempData[VotedKey(id)] is not null));
    }

    /// <summary>Helpful Evet/Hayır — counters only, one thank-you state per visit (B-spec §6.1).</summary>
    [HttpPost("/kb-article/vote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Vote(int id, bool helpful, CancellationToken ct)
    {
        var updated = helpful
            ? await db.FaqArticles.Where(a => a.Id == id && a.IsPublished)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.HelpfulYes, a => a.HelpfulYes + 1), ct)
            : await db.FaqArticles.Where(a => a.Id == id && a.IsPublished)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.HelpfulNo, a => a.HelpfulNo + 1), ct);
        if (updated == 0)
            return NotFound();

        TempData[VotedKey(id)] = true;
        return RedirectToAction(nameof(Article), new { id });
    }

    private static string VotedKey(int id) => $"kb-voted-{id}";
}

public sealed record KbIndexVm(
    string? Query,
    int? CategoryId,
    IReadOnlyList<KbCategoryVm> Categories,
    IReadOnlyList<KbArticleRowVm> Articles);

public sealed record KbCategoryVm(int Id, string Name, string Description);

public sealed record KbArticleRowVm(int Id, int CategoryId, string Question, string? Summary, DateTimeOffset UpdatedAt);

public sealed record KbArticleVm(
    int Id,
    string Question,
    string AnswerHtml,
    string CategoryName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Attachments,
    IReadOnlyList<KbArticleRowVm> Related,
    bool Voted);
