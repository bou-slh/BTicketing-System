using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

[Collection("Postgres")]
public class SeedFileTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SeededStoredFiles_StreamBackAtTheirRecordedSize()
    {
        using var s = new ServiceScopeBundle(fixture);
        var files = s.Get<IFileStore>();

        var stored = await s.Db.StoredFiles.ToListAsync();
        Assert.NotEmpty(stored);
        foreach (var file in stored)
        {
            await using var stream = await files.OpenAsync(file);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(file.Size, buffer.Length);
        }
    }

    [Fact]
    public async Task HeroKbArticle_CarriesTheCanonAttachments()
    {
        using var s = new ServiceScopeBundle(fixture);
        var article = await s.Db.FaqArticles
            .SingleAsync(a => a.Question == "Yol ücreti nasıl hesaplanır?");

        var names = await s.Db.Attachments
            .Where(at => at.ObjectType == AttachmentObjectType.FaqArticle && at.ObjectId == article.Id)
            .Join(s.Db.StoredFiles, at => at.FileId, f => f.Id, (at, f) => at.Name ?? f.Name)
            .ToListAsync();

        Assert.Equal(2, names.Count);
        Assert.Contains("yol-ucreti-hesaplama-ornegi.xlsx", names);
        Assert.Contains("2026-vergi-istisna-tutarlari.pdf", names);
    }
}
