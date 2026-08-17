using System.Text;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 KbService (agent/kb.html + kb-faq.html): category CRUD with the duplicate-name
/// and has-articles guards, article create/update with the kf-listing mapping
/// (Featured implies Published) + answer normalization + help-topic links, and
/// attachment add/delete through the file store. Assertions read through fresh
/// scopes (EF identity-map staleness across scopes).
/// </summary>
[Collection("Postgres")]
public class KbServiceTests(PostgresFixture fixture)
{
    private static string UniqueName(string prefix = "Test Kategori") =>
        $"{prefix} {Guid.NewGuid():N}";

    // ---- categories -------------------------------------------------------------------

    [Fact]
    public async Task CreateCategory_Trims_AndIsPortalVisible()
    {
        var name = UniqueName();
        int categoryId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            // mcetin = Kıdemli Temsilci: holds faq.manage.
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Get<IKbService>().CreateCategoryAsync($"  {name}  ", mcetin);
            categoryId = category.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.KbCategories.SingleAsync(c => c.Id == categoryId);
        Assert.Equal(name, saved.Name);
        Assert.True(saved.IsPublic); // kb.catsHelp: categories group on the portal
    }

    [Fact]
    public async Task CreateCategory_DuplicateNameRefused_CaseInsensitive()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IKbService>().CreateCategoryAsync("BORDRO", mcetin)); // seeded canon category
        Assert.Equal("name-in-use", ex.Code);
    }

    [Fact]
    public async Task CategoryMutations_RequireFaqManage()
    {
        using var s = new ServiceScopeBundle(fixture);
        // dkaya = Temsilci: role without faq.manage.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        var kb = s.Get<IKbService>();

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            kb.CreateCategoryAsync(UniqueName(), dkaya));
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            kb.CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = 1, Question = "q", Answer = "a",
            }, dkaya));
    }

    [Fact]
    public async Task RenameCategory_Persists()
    {
        var renamed = UniqueName("Yeni Ad");
        int categoryId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var kb = s.Get<IKbService>();
            categoryId = (await kb.CreateCategoryAsync(UniqueName(), mcetin)).Id;
            await kb.RenameCategoryAsync(categoryId, renamed, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.Equal(renamed, (await fresh.Db.KbCategories.SingleAsync(c => c.Id == categoryId)).Name);
    }

    [Fact]
    public async Task DeleteCategory_WithArticles_Refused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var bordro = await s.Db.KbCategories.SingleAsync(c => c.Name == "Bordro"); // seeded, has articles

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IKbService>().DeleteCategoryAsync(bordro.Id, mcetin));
        Assert.Equal("has-articles", ex.Code);
    }

    [Fact]
    public async Task DeleteCategory_Empty_Deletes()
    {
        int categoryId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var kb = s.Get<IKbService>();
            categoryId = (await kb.CreateCategoryAsync(UniqueName(), mcetin)).Id;
            await kb.DeleteCategoryAsync(categoryId, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.False(await fresh.Db.KbCategories.AnyAsync(c => c.Id == categoryId));
    }

    // ---- articles ---------------------------------------------------------------------

    [Fact]
    public async Task CreateArticle_Featured_ImpliesPublished_WrapsPlainText_LinksTopics()
    {
        int articleId, topicId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Sistem");
            topicId = (await s.Db.HelpTopics.SingleAsync(t => t.Name == "Bordro" && t.ParentId == null)).Id;

            var article = await s.Get<IKbService>().CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = $"  Test soru {Guid.NewGuid():N}? ",
                Answer = "İlk paragraf.\nAynı paragrafın ikinci satırı.\n\nİkinci paragraf.",
                Listing = KbListing.Featured,
                HelpTopicIds = [topicId],
                Notes = " dahili not ",
            }, mcetin);
            articleId = article.Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.FaqArticles
            .Include(a => a.HelpTopics)
            .SingleAsync(a => a.Id == articleId);
        Assert.True(saved.IsFeatured);
        Assert.True(saved.IsPublished); // osTicket ispublished=2 implies published
        Assert.StartsWith("Test soru", saved.Question);
        // Plain text becomes portal-renderable HTML: blank line = <p>, newline = <br>.
        Assert.Equal(
            "<p>İlk paragraf.<br>Aynı paragrafın ikinci satırı.</p><p>İkinci paragraf.</p>",
            saved.Answer);
        Assert.Equal("dahili not", saved.Notes);
        Assert.Equal(new[] { topicId }, saved.HelpTopics.Select(t => t.HelpTopicId).ToArray());
    }

    [Fact]
    public async Task CreateArticle_HtmlAnswer_IsSanitized()
    {
        int articleId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Sistem");
            articleId = (await s.Get<IKbService>().CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = $"HTML {Guid.NewGuid():N}",
                Answer = "<p>Güvenli içerik</p><script>alert(1)</script>",
            }, mcetin)).Id;
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.FaqArticles.SingleAsync(a => a.Id == articleId);
        Assert.Contains("<p>Güvenli içerik</p>", saved.Answer);
        Assert.DoesNotContain("<script>", saved.Answer);
    }

    [Fact]
    public async Task CreateArticle_EmptyFields_Refused()
    {
        using var s = new ServiceScopeBundle(fixture);
        var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
        var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Sistem");

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            s.Get<IKbService>().CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id, Question = "  ", Answer = "x",
            }, mcetin));
        Assert.Equal("invalid", ex.Code);

        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            s.Get<IKbService>().CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = -1, Question = "q", Answer = "a",
            }, mcetin));
    }

    [Fact]
    public async Task UpdateArticle_ReplacesTopics_AndListing()
    {
        int articleId, topicBordro, topicIzin;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Vardiya");
            topicBordro = (await s.Db.HelpTopics.SingleAsync(t => t.Name == "Bordro" && t.ParentId == null)).Id;
            topicIzin = (await s.Db.HelpTopics.SingleAsync(t => t.Name == "İzin")).Id;

            var kb = s.Get<IKbService>();
            articleId = (await kb.CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = $"Güncellenecek {Guid.NewGuid():N}",
                Answer = "ilk",
                Listing = KbListing.Featured,
                HelpTopicIds = [topicBordro],
            }, mcetin)).Id;

            await kb.UpdateArticleAsync(articleId, new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = "Güncellendi",
                Answer = "yeni",
                Listing = KbListing.Internal,
                HelpTopicIds = [topicIzin],
            }, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        var saved = await fresh.Db.FaqArticles
            .Include(a => a.HelpTopics)
            .SingleAsync(a => a.Id == articleId);
        Assert.Equal("Güncellendi", saved.Question);
        Assert.False(saved.IsPublished); // Dahili
        Assert.False(saved.IsFeatured);
        Assert.Equal(new[] { topicIzin }, saved.HelpTopics.Select(t => t.HelpTopicId).ToArray());
    }

    // ---- attachments ------------------------------------------------------------------

    [Fact]
    public async Task Attachments_AddThenDeleteArticle_RemovesRowsAndStoredFiles()
    {
        int articleId, attachmentId, fileId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Sistem");
            var kb = s.Get<IKbService>();
            articleId = (await kb.CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = $"Ekli makale {Guid.NewGuid():N}",
                Answer = "gövde",
            }, mcetin)).Id;

            using var content = new MemoryStream(Encoding.UTF8.GetBytes("test attachment bytes"));
            var attachment = await kb.AddAttachmentAsync(
                articleId, content, "ornek-hesap.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", mcetin);
            attachmentId = attachment.Id;
            fileId = attachment.FileId;
        }

        using (var check = new ServiceScopeBundle(fixture))
        {
            var att = await check.Db.Attachments.SingleAsync(a => a.Id == attachmentId);
            Assert.Equal(AttachmentObjectType.FaqArticle, att.ObjectType);
            Assert.Equal(articleId, att.ObjectId);
            Assert.True(await check.Db.StoredFiles.AnyAsync(f => f.Id == fileId));
        }

        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            await s.Get<IKbService>().DeleteArticleAsync(articleId, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.False(await fresh.Db.FaqArticles.AnyAsync(a => a.Id == articleId));
        Assert.False(await fresh.Db.Attachments.AnyAsync(a => a.Id == attachmentId));
        Assert.False(await fresh.Db.StoredFiles.AnyAsync(f => f.Id == fileId));
    }

    [Fact]
    public async Task DeleteAttachment_RemovesRowAndStoredFile()
    {
        int articleId, attachmentId, fileId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var mcetin = await TestActors.StaffAsync(s.Db, "mcetin");
            var category = await s.Db.KbCategories.SingleAsync(c => c.Name == "Sistem");
            var kb = s.Get<IKbService>();
            articleId = (await kb.CreateArticleAsync(new FaqArticleSave
            {
                CategoryId = category.Id,
                Question = $"Ek silme {Guid.NewGuid():N}",
                Answer = "gövde",
            }, mcetin)).Id;

            using var content = new MemoryStream(Encoding.UTF8.GetBytes("delete me"));
            var attachment = await kb.AddAttachmentAsync(articleId, content, "silinecek.pdf", "application/pdf", mcetin);
            attachmentId = attachment.Id;
            fileId = attachment.FileId;

            await kb.DeleteAttachmentAsync(attachmentId, mcetin);
        }

        using var fresh = new ServiceScopeBundle(fixture);
        Assert.True(await fresh.Db.FaqArticles.AnyAsync(a => a.Id == articleId)); // article survives
        Assert.False(await fresh.Db.Attachments.AnyAsync(a => a.Id == attachmentId));
        Assert.False(await fresh.Db.StoredFiles.AnyAsync(f => f.Id == fileId));
    }
}
