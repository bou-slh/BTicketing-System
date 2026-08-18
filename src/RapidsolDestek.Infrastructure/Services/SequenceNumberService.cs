using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>One row of the settings-tickets "Numara sıraları" dialog (id 0 = new).</summary>
public sealed record SequenceRow(int Id, string Name, long Next);

public interface ISequenceNumberService
{
    /// <summary>
    /// Atomically draws the next sequence value (SELECT … FOR UPDATE — osTicket's
    /// row-locked sequence semantics) and formats it, e.g. "R######" → "R716567".
    /// Joins the caller's open transaction when there is one.
    /// </summary>
    Task<string> NextAsync(int sequenceId, string format, CancellationToken ct = default);

    /// <summary>
    /// dlg-seq CRUD (S7 admin/settings-tickets, B4): upserts the posted rows and
    /// deletes the missing ones. Guards: internal or in-use sequences (settings
    /// tickets/tasks pointer, help topics) cannot be deleted; Next can never move
    /// backwards — it must stay above the highest number already drawn.
    /// </summary>
    Task SaveAsync(IReadOnlyList<SequenceRow> rows, ActorContext actor, CancellationToken ct = default);
}

public sealed class SequenceNumberService(AppDbContext db, ISettingsService settings) : ISequenceNumberService
{
    public async Task SaveAsync(IReadOnlyList<SequenceRow> rows, ActorContext actor, CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                throw new DomainRuleException("sequence-name-required", "Sequence name cannot be empty.");
            if (row.Next < 1)
                throw new DomainRuleException("sequence-next-invalid", $"Sequence next value {row.Next} must be positive.");
        }

        var existing = await db.Sequences.ToListAsync(ct);
        var postedIds = rows.Where(r => r.Id != 0).Select(r => r.Id).ToHashSet();

        // Deletions = existing rows missing from the posted set, guarded.
        foreach (var seq in existing.Where(s => !postedIds.Contains(s.Id)))
        {
            if (seq.IsInternal || await InUseAsync(seq.Id, ct))
                throw new DomainRuleException("sequence-in-use",
                    $"Sequence {seq.Id} \"{seq.Name}\" is internal or in use and cannot be deleted.");
            db.Sequences.Remove(seq);
        }

        foreach (var row in rows)
        {
            if (row.Id == 0)
            {
                db.Sequences.Add(new Sequence
                {
                    Name = row.Name.Trim(),
                    Next = row.Next,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                continue;
            }

            var seq = existing.FirstOrDefault(s => s.Id == row.Id)
                ?? throw new DomainNotFoundException("Sequence", row.Id);
            // Invariant: Next only moves forward — the stored value is already above
            // the highest drawn number, so lowering it would mint duplicates.
            if (row.Next < seq.Next)
                throw new DomainRuleException("sequence-next-below-current",
                    $"Sequence {seq.Id} next {row.Next} is below the current counter {seq.Next}.");
            if (seq.Name == row.Name.Trim() && seq.Next == row.Next)
                continue;
            seq.Name = row.Name.Trim();
            seq.Next = row.Next;
            seq.UpdatedAt = DateTimeOffset.UtcNow;
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
    }

    private async Task<bool> InUseAsync(int sequenceId, CancellationToken ct)
    {
        if (await db.HelpTopics.AnyAsync(t => t.SequenceId == sequenceId, ct))
            return true;
        var idText = sequenceId.ToString();
        return await settings.GetAsync("tickets", "sequence_id", ct) == idText
            || await settings.GetAsync("tasks", "sequence_id", ct) == idText;
    }

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
