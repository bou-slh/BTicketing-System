using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Services;

namespace RapidsolDestek.Infrastructure.Services;

public interface ISequenceNumberService
{
    /// <summary>
    /// Atomically draws the next sequence value (SELECT … FOR UPDATE — osTicket's
    /// row-locked sequence semantics) and formats it, e.g. "R######" → "R716567".
    /// Joins the caller's open transaction when there is one.
    /// </summary>
    Task<string> NextAsync(int sequenceId, string format, CancellationToken ct = default);
}

public sealed class SequenceNumberService(AppDbContext db) : ISequenceNumberService
{
    public async Task<string> NextAsync(int sequenceId, string format, CancellationToken ct = default)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = db.Database.CurrentTransaction
                 ?? await db.Database.BeginTransactionAsync(ct);
        try
        {
            var seq = await db.Sequences
                .FromSql($"SELECT * FROM sequences WHERE id = {sequenceId} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
                ?? throw new DomainNotFoundException("Sequence", sequenceId);

            var value = seq.Next;
            seq.Next += seq.Increment;
            seq.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (ownsTransaction)
                await tx.CommitAsync(ct);

            return TicketNumberFormatter.Format(format, value, seq.Padding);
        }
        finally
        {
            if (ownsTransaction)
                await tx.DisposeAsync();
        }
    }
}
