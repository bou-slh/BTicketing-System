using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Domain.Services;

/// <summary>
/// Content storage behind <see cref="StoredFile"/> metadata rows. osTicket's
/// attachment-in-database default is deliberately dropped — implementations write to
/// the filesystem (S4) or S3-compatible stores (later); the database only ever holds
/// <see cref="StoredFile"/> rows.
/// </summary>
public interface IFileStore
{
    /// <summary>Backend id recorded on saved rows, e.g. "fs".</summary>
    string Backend { get; }

    /// <summary>Streams content into the store and returns the (unsaved) metadata row.</summary>
    Task<StoredFile> SaveAsync(Stream content, string fileName, string mimeType, CancellationToken ct = default);

    Task<Stream> OpenAsync(StoredFile file, CancellationToken ct = default);

    Task DeleteAsync(StoredFile file, CancellationToken ct = default);
}
