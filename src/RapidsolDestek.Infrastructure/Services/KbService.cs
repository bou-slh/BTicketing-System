using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Listing choice of the kb-faq editor (mockup kf-listing radios → osTicket ispublished 2/1/0).</summary>
public enum KbListing
{
    /// <summary>Dahili — agent panel only (ispublished=0).</summary>
    Internal,
    /// <summary>Herkese Açık — portal KB (ispublished=1).</summary>
    Public,
    /// <summary>Öne Çıkan — portal KB + home page (ispublished=2; implies published).</summary>
    Featured,
}

/// <summary>Input for <see cref="IKbService.CreateArticleAsync"/> / UpdateArticleAsync (agent/kb-faq.html fields).</summary>
public sealed record FaqArticleSave
{
    public required int CategoryId { get; init; }
    public required string Question { get; init; }

    /// <summary>
    /// Editor content. HTML passes through sanitized; plain text (no tags) is
    /// paragraph-wrapped so the portal's raw-HTML render keeps the line breaks.
    /// </summary>
    public required string Answer { get; init; }

    public KbListing Listing { get; init; } = KbListing.Public;

    /// <summary>Yardım Konuları multi-select → FaqArticleTopic rows.</summary>
    public IReadOnlyList<int> HelpTopicIds { get; init; } = [];

    /// <summary>Notlar tab (internal note about the article).</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Knowledge-base mutations for agent/kb.html + kb-faq.html (ROADMAP §6.2), shaped
/// after <see cref="UserService"/>: AuditEvent via the interceptor (actor audit scope),
/// permission key faq.manage checked "anywhere" (the KB is non-departmental).
/// </summary>
public interface IKbService
{
    /// <summary>Manage-categories dialog add: portal-visible (IsPublic) category, duplicate-name guard.</summary>
    Task<KbCategory> CreateCategoryAsync(string name, ActorContext actor, CancellationToken ct = default);

    /// <summary>Manage-categories dialog rename (duplicate-name guard).</summary>
    Task RenameCategoryAsync(int categoryId, string name, ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Manage-categories dialog delete. Refused with <see cref="DomainRuleException"/>
    /// "has-articles" while articles exist — honest guard instead of osTicket's
    /// cascade-delete of the category's FAQs.
    /// </summary>
    Task DeleteCategoryAsync(int categoryId, ActorContext actor, CancellationToken ct = default);

    /// <summary>kb-faq save (new): article + help-topic links.</summary>
    Task<FaqArticle> CreateArticleAsync(FaqArticleSave save, ActorContext actor, CancellationToken ct = default);

    /// <summary>kb-faq save (edit): fields + replaced help-topic links.</summary>
    Task UpdateArticleAsync(int articleId, FaqArticleSave save, ActorContext actor, CancellationToken ct = default);

    /// <summary>kb-faq delete: article + topic links + attachments (stored files removed from the file store).</summary>
    Task DeleteArticleAsync(int articleId, ActorContext actor, CancellationToken ct = default);

    /// <summary>Ekler tab upload: stores the file and links it to the article.</summary>
    Task<Attachment> AddAttachmentAsync(int articleId, Stream content, string fileName, string mimeType,
        ActorContext actor, CancellationToken ct = default);

    /// <summary>Ekler tab per-file delete (attachment row + stored file).</summary>
    Task DeleteAttachmentAsync(int attachmentId, ActorContext actor, CancellationToken ct = default);
}

public sealed class KbService(
    AppDbContext db,
    IPermissionService permissions,
    IHtmlSanitizerService sanitizer,
    IFileStore files) : IKbService
{
    public async Task<KbCategory> CreateCategoryAsync(string name, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var trimmed = await ValidateCategoryNameAsync(null, name, ct);

        // Portal-visible by default — kb.catsHelp: categories group on the portal.
        var category = new KbCategory { Name = trimmed, IsPublic = true };
        db.KbCategories.Add(category);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task RenameCategoryAsync(int categoryId, string name, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var category = await LoadCategoryAsync(categoryId, ct);
        var trimmed = await ValidateCategoryNameAsync(categoryId, name, ct);
        if (category.Name == trimmed)
            return;

        category.Name = trimmed;
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCategoryAsync(int categoryId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var category = await LoadCategoryAsync(categoryId, ct);

        // Friendly guard (Org/UserService precedent): refusing beats silently
        // cascade-deleting the category's articles — surfaced as a toast, never a 500.
        if (await db.FaqArticles.AnyAsync(a => a.CategoryId == categoryId, ct))
            throw new DomainRuleException("has-articles", $"Category {categoryId} still has articles.");

        db.KbCategories.Remove(category);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task<FaqArticle> CreateArticleAsync(FaqArticleSave save, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var (question, answer) = await ValidateArticleAsync(save, ct);

        var article = new FaqArticle
        {
            CategoryId = save.CategoryId,
            Question = question,
            Answer = answer,
            IsPublished = save.Listing != KbListing.Internal,
            IsFeatured = save.Listing == KbListing.Featured,
            Notes = string.IsNullOrWhiteSpace(save.Notes) ? null : save.Notes.Trim(),
            HelpTopics = [.. save.HelpTopicIds.Distinct().Select(id => new FaqArticleTopic { HelpTopicId = id })],
        };
        db.FaqArticles.Add(article);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return article;
    }

    public async Task UpdateArticleAsync(int articleId, FaqArticleSave save, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var article = await db.FaqArticles
            .Include(a => a.HelpTopics)
            .SingleOrDefaultAsync(a => a.Id == articleId, ct)
            ?? throw new DomainNotFoundException("FaqArticle", articleId);
        var (question, answer) = await ValidateArticleAsync(save, ct);

        article.CategoryId = save.CategoryId;
        article.Question = question;
        article.Answer = answer;
        article.IsPublished = save.Listing != KbListing.Internal;
        article.IsFeatured = save.Listing == KbListing.Featured;
        article.Notes = string.IsNullOrWhiteSpace(save.Notes) ? null : save.Notes.Trim();

        var wanted = save.HelpTopicIds.Distinct().ToHashSet();
        article.HelpTopics.RemoveAll(t => !wanted.Contains(t.HelpTopicId));
        foreach (var topicId in wanted.Where(id => article.HelpTopics.All(t => t.HelpTopicId != id)))
            article.HelpTopics.Add(new FaqArticleTopic { HelpTopicId = topicId });

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task DeleteArticleAsync(int articleId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var article = await db.FaqArticles
            .Include(a => a.HelpTopics)
            .SingleOrDefaultAsync(a => a.Id == articleId, ct)
            ?? throw new DomainNotFoundException("FaqArticle", articleId);

        // (ObjectType, ObjectId) is a soft reference — the owning service deletes
        // attachments with their object (SystemEntities doc contract).
        var attachments = await db.Attachments
            .Include(at => at.File)
            .Where(at => at.ObjectType == AttachmentObjectType.FaqArticle && at.ObjectId == articleId)
            .ToListAsync(ct);
        foreach (var attachment in attachments)
        {
            await files.DeleteAsync(attachment.File!, ct);
            db.StoredFiles.Remove(attachment.File!);
        }
        db.Attachments.RemoveRange(attachments);
        db.FaqArticles.Remove(article); // topic links cascade with the article row

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    public async Task<Attachment> AddAttachmentAsync(int articleId, Stream content, string fileName, string mimeType,
        ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        if (!await db.FaqArticles.AnyAsync(a => a.Id == articleId, ct))
            throw new DomainNotFoundException("FaqArticle", articleId);

        var file = await files.SaveAsync(content, fileName,
            string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType, ct);
        db.StoredFiles.Add(file);
        var attachment = new Attachment
        {
            ObjectType = AttachmentObjectType.FaqArticle,
            ObjectId = articleId,
            File = file,
        };
        db.Attachments.Add(attachment);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return attachment;
    }

    public async Task DeleteAttachmentAsync(int attachmentId, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureAnywhereAsync(actor, PermissionKeys.FaqManage, ct);
        var attachment = await db.Attachments
            .Include(at => at.File)
            .SingleOrDefaultAsync(at =>
                at.Id == attachmentId && at.ObjectType == AttachmentObjectType.FaqArticle, ct)
            ?? throw new DomainNotFoundException("Attachment", attachmentId);

        await files.DeleteAsync(attachment.File!, ct);
        db.StoredFiles.Remove(attachment.File!);
        db.Attachments.Remove(attachment);
        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    // ---- helpers ----------------------------------------------------------------------

    private async Task<KbCategory> LoadCategoryAsync(int categoryId, CancellationToken ct) =>
        await db.KbCategories.SingleOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new DomainNotFoundException("KbCategory", categoryId);

    private async Task<string> ValidateCategoryNameAsync(int? categoryId, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new DomainRuleException("invalid", "Category name must not be empty.");
        if (await db.KbCategories.AnyAsync(
                c => c.Id != categoryId && c.Name.ToLower() == trimmed.ToLower(), ct))
            throw new DomainRuleException("name-in-use", $"Category '{trimmed}' already exists.");
        return trimmed;
    }

    private async Task<(string Question, string Answer)> ValidateArticleAsync(FaqArticleSave save, CancellationToken ct)
    {
        var question = save.Question?.Trim() ?? "";
        if (question.Length == 0 || string.IsNullOrWhiteSpace(save.Answer))
            throw new DomainRuleException("invalid", "Article question/answer must not be empty.");
        if (!await db.KbCategories.AnyAsync(c => c.Id == save.CategoryId, ct))
            throw new DomainNotFoundException("KbCategory", save.CategoryId);
        foreach (var topicId in save.HelpTopicIds.Distinct())
        {
            if (!await db.HelpTopics.AnyAsync(t => t.Id == topicId, ct))
                throw new DomainNotFoundException("HelpTopic", topicId);
        }
        return (question, NormalizeAnswer(save.Answer));
    }

    /// <summary>
    /// The editor is a plain textarea while the stored Answer is HTML (portal renders
    /// it raw). HTML input is sanitized as-is; tag-less plain text is encoded and
    /// paragraph-wrapped (blank line = new &lt;p&gt;, newline = &lt;br&gt;) so the
    /// portal keeps the formatting. TODO(S7): rich text editor per the admin pages.
    /// </summary>
    internal static string NormalizeAnswerFor(IHtmlSanitizerService sanitizer, string answer)
    {
        var body = answer.Replace("\r\n", "\n").Trim();
        if (Regex.IsMatch(body, "<[a-zA-Z]"))
            return sanitizer.Sanitize(body);

        var paragraphs = body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => "<p>" + string.Join("<br>",
                p.Split('\n').Select(System.Net.WebUtility.HtmlEncode)) + "</p>");
        return string.Join("", paragraphs);
    }

    private string NormalizeAnswer(string answer) => NormalizeAnswerFor(sanitizer, answer);

    /// <summary>Non-departmental permission check (UserService precedent).</summary>
    private async Task EnsureAnywhereAsync(ActorContext actor, string permission, CancellationToken ct)
    {
        if (actor.Type == ActorType.System)
            return;
        if (!actor.IsStaff)
            throw new PermissionDeniedException(permission);
        var set = await permissions.ResolveAsync(actor.Id!.Value, ct);
        if (!set.IsActive || !set.CanAnywhere(permission))
            throw new PermissionDeniedException(permission);
    }
}
