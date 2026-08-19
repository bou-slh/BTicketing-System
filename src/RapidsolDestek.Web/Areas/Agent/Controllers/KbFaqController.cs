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

namespace RapidsolDestek.Web.Areas.Agent.Controllers;

/// <summary>
/// Agent KB article editor (mockups/agent/kb-faq.html, ROADMAP §6.2): creates and
/// updates real FaqArticles (new + edit-by-id), the kf-listing radios persist as
/// IsPublished/IsFeatured, attachments upload/list/delete via IFileStore-backed
/// KbService, preview dialog, delete (B2/B5).
/// </summary>
[Area("Agent")]
[Authorize(Policy = "Staff")]
public class KbFaqController(AppDbContext db, IKbService kb, IFileStore files, ISettingsService settings) : Controller
{
    [HttpGet("/agent/kb-faq")]
    [NavKey("kb")]
    public async Task<IActionResult> Index(int? id, string? tab, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        FaqArticle? article = null;
        var attachments = new List<KbFaqAttachmentVm>();
        if (id is { } articleId)
        {
            article = await db.FaqArticles
                .Include(a => a.Category)
                .Include(a => a.HelpTopics)
                .SingleOrDefaultAsync(a => a.Id == articleId, ct);
            if (article is null)
                return NotFound();
            attachments = await db.Attachments
                .Where(at => at.ObjectType == AttachmentObjectType.FaqArticle && at.ObjectId == articleId)
                .Join(db.StoredFiles, at => at.FileId, f => f.Id,
                    (at, f) => new KbFaqAttachmentVm(at.Id, at.Name ?? f.Name))
                .ToListAsync(ct);
        }

        var categories = await db.KbCategories
            .OrderBy(c => c.Id)
            .Select(c => new KbFaqOptionVm(c.Id, c.Name))
            .ToListAsync(ct);

        // Yardım Konuları: every topic, inactive included — the mockup lists Vardiya
        // (seeded inactive); KB links are suggestions, not routing, unlike ticket-open's
        // active-only select. "Parent / Child" labels per the ticket-open precedent.
        var selectedTopics = article?.HelpTopics.Select(t => t.HelpTopicId).ToHashSet() ?? [];
        var topics = (await db.HelpTopics
                .OrderBy(t => t.Sort)
                .Include(t => t.Parent)
                .ToListAsync(ct))
            .Select(t => new KbFaqTopicVm(
                t.Id,
                t.Parent is null ? t.Name : $"{t.Parent.Name} / {t.Name}",
                selectedTopics.Contains(t.Id)))
            .ToList();

        return View(new KbFaqVm(
            article?.Id,
            article?.Question,
            article?.Answer,
            article?.Notes,
            article?.CategoryId,
            article?.Category?.Name,
            article is null
                ? KbListing.Public // mockup default: Herkese Açık checked
                : article.IsFeatured ? KbListing.Featured
                : article.IsPublished ? KbListing.Public
                : KbListing.Internal,
            article is null ? null : article.UpdatedAt ?? article.CreatedAt,
            categories,
            topics,
            attachments,
            tab is "attach" or "notes" ? tab : "content"));
    }

    /// <summary>Save (new + edit): article fields + topic links, then any chosen uploads (B5).</summary>
    [HttpPost("/agent/kb-faq/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        int? id, int categoryId, string? question, string? answer, string? listing,
        List<int> topicIds, string? notes, List<IFormFile> attachments, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        var save = new FaqArticleSave
        {
            CategoryId = categoryId,
            Question = question ?? "",
            Answer = answer ?? "",
            Listing = listing switch
            {
                "featured" => KbListing.Featured,
                "internal" => KbListing.Internal,
                _ => KbListing.Public,
            },
            HelpTopicIds = topicIds,
            Notes = notes,
        };

        // Attachment size cap (admin/settings-system Ekler, S7): refuse the whole
        // save before anything persists.
        var attachLimits = await settings.GetAttachmentsAsync(ct);
        if (attachments.Any(f => f.Length > attachLimits.MaxSizeBytes))
            return ToastBack(id, "kf.errAttachTooBig");

        int articleId;
        bool created = id is null;
        try
        {
            if (id is { } existingId)
            {
                await kb.UpdateArticleAsync(existingId, save, actor, ct);
                articleId = existingId;
            }
            else
            {
                articleId = (await kb.CreateArticleAsync(save, actor, ct)).Id;
            }

            var uploaded = false;
            foreach (var upload in attachments.Where(f => f.Length > 0))
            {
                await using var stream = upload.OpenReadStream();
                await kb.AddAttachmentAsync(articleId, stream, Path.GetFileName(upload.FileName),
                    upload.ContentType, actor, ct);
                uploaded = true;
            }

            TempData["KfToast"] = created ? "kf.toastCreated" : "kf.toastSaved";
            // Land on the Ekler tab when files were just uploaded so the list shows (B5).
            return Redirect($"/agent/kb-faq?id={articleId}{(uploaded ? "&tab=attach" : "")}");
        }
        catch (DomainRuleException)
        {
            return ToastBack(id, "kf.errInvalid");
        }
        catch (DomainException)
        {
            return ToastBack(id, "kf.errDenied");
        }
    }

    /// <summary>Header Sil (B2 confirm dialog): article + attachments, back to the KB list.</summary>
    [HttpPost("/agent/kb-faq/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await kb.DeleteArticleAsync(id, actor, ct);
        }
        catch (DomainException)
        {
            return ToastBack(id, "kf.errDenied");
        }

        TempData["KbToast"] = "kb.toastArticleDeleted";
        return Redirect("/agent/kb");
    }

    /// <summary>Ekler tab per-file delete (external row form — the panel sits inside the save form).</summary>
    [HttpPost("/agent/kb-faq/attachment/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAttachment(int id, int articleId, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();
        var actor = ActorContext.ForStaff(staff, HttpContext.Connection.RemoteIpAddress?.ToString());

        try
        {
            await kb.DeleteAttachmentAsync(id, actor, ct);
            TempData["KfToast"] = "kf.toastAttachDeleted";
        }
        catch (DomainException)
        {
            TempData["KfToast"] = "kf.errDenied";
            TempData["KfToastError"] = true;
        }
        return Redirect($"/agent/kb-faq?id={articleId}&tab=attach");
    }

    /// <summary>Attachment download — staff scope (no published/public gate, unlike the portal twin).</summary>
    [HttpGet("/agent/kb-faq/attachment")]
    public async Task<IActionResult> Attachment(int id, CancellationToken ct)
    {
        var staff = await db.ResolveStaffAsync(User, ct);
        if (staff is null)
            return Forbid();

        var attachment = await db.Attachments
            .Include(at => at.File)
            .SingleOrDefaultAsync(at => at.Id == id && at.ObjectType == AttachmentObjectType.FaqArticle, ct);
        if (attachment is null)
            return NotFound();

        var file = attachment.File!;
        var content = await files.OpenAsync(file, ct);
        return File(content, file.MimeType, attachment.Name ?? file.Name);
    }

    private IActionResult ToastBack(int? id, string key)
    {
        TempData["KfToast"] = key;
        TempData["KfToastError"] = true;
        return Redirect(id is { } articleId ? $"/agent/kb-faq?id={articleId}" : "/agent/kb-faq");
    }
}

public sealed record KbFaqVm(
    int? Id,
    string? Question,
    string? Answer,
    string? Notes,
    int? CategoryId,
    string? CategoryName,
    KbListing Listing,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<KbFaqOptionVm> Categories,
    IReadOnlyList<KbFaqTopicVm> Topics,
    IReadOnlyList<KbFaqAttachmentVm> Attachments,
    string ActiveTab);

public sealed record KbFaqOptionVm(int Id, string Name);

public sealed record KbFaqTopicVm(int Id, string Label, bool Selected);

public sealed record KbFaqAttachmentVm(int Id, string Name);
