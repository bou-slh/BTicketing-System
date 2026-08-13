using System.Security.Cryptography;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;

namespace RapidsolDestek.Infrastructure.Files;

/// <summary>
/// Filesystem <see cref="IFileStore"/> (backend id "fs"): contents live under
/// <c>{root}/{yyyy}/{MM}/{guid}{ext}</c>, the database only holds StoredFile rows
/// (osTicket's file_chunk-in-database default is deliberately dropped).
/// </summary>
public sealed class FileSystemFileStore(string rootPath) : IFileStore
{
    public string Backend => "fs";

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var extension = Path.GetExtension(fileName);
        var key = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{extension}";
        var fullPath = FullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        long size;
        string signature;
        await using (var target = File.Create(fullPath))
        using (var sha = SHA256.Create())
        {
            await using (var hashing = new CryptoStream(target, sha, CryptoStreamMode.Write, leaveOpen: true))
            {
                await content.CopyToAsync(hashing, ct);
            }
            size = target.Length;
            signature = Convert.ToHexStringLower(sha.Hash!);
        }

        return new StoredFile
        {
            Backend = Backend,
            StorageKey = key,
            Signature = signature,
            Name = Path.GetFileName(fileName),
            MimeType = mimeType,
            Size = size,
            CreatedAt = now,
        };
    }

    public Task<Stream> OpenAsync(StoredFile file, CancellationToken ct = default)
    {
        EnsureBackend(file);
        Stream stream = File.OpenRead(FullPath(file.StorageKey));
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(StoredFile file, CancellationToken ct = default)
    {
        EnsureBackend(file);
        var path = FullPath(file.StorageKey);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private void EnsureBackend(StoredFile file)
    {
        if (file.Backend != Backend)
            throw new InvalidOperationException($"StoredFile {file.Id} belongs to backend '{file.Backend}', not '{Backend}'.");
    }

    private string FullPath(string key) =>
        Path.Combine(rootPath, key.Replace('/', Path.DirectorySeparatorChar));
}
