using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Auditing;

namespace RapidsolDestek.Infrastructure.Interceptors;

/// <summary>
/// Writes an <see cref="AuditEvent"/> for every create/update/delete of a domain entity
/// (roadmap: "every mutation writes an AuditEvent"). Attribution comes from the ambient
/// <see cref="AuditActor"/>. Entities marked <see cref="INotAudited"/> and the Identity
/// tables are skipped. Rows are collected before save (delete data would be gone after)
/// and appended after it so generated ids of added entities can be resolved; the
/// follow-up save runs inside the caller's transaction when one is open.
/// </summary>
public class AuditInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // Values longer than this are elided from the diff payload (message bodies etc.).
    private const int MaxValueLength = 500;

    private readonly List<(AuditEvent Audit, EntityEntry? PendingIdOf)> _pending = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (PrepareFlush(eventData.Context))
            eventData.Context!.SaveChanges();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (PrepareFlush(eventData.Context))
            await eventData.Context!.SaveChangesAsync(cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
            return;

        var actor = AuditActor.Current;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is INotAudited or AuditEvent)
                continue;
            if (entry.Entity.GetType().Namespace?.StartsWith("RapidsolDestek.Domain") != true)
                continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var audit = new AuditEvent
            {
                OccurredAt = now,
                ActorType = actor.Type,
                ActorId = actor.Id,
                ActorName = actor.Name,
                IpAddress = actor.IpAddress,
                Action = entry.State switch
                {
                    EntityState.Added => "Created",
                    EntityState.Deleted => "Deleted",
                    _ => "Updated",
                },
                ObjectType = entry.Entity.GetType().Name,
                ObjectId = entry.State == EntityState.Added ? "" : KeyOf(entry),
                ObjectLabel = LabelOf(entry.Entity),
                Data = entry.State == EntityState.Modified ? DiffOf(entry) : null,
            };

            // Skip no-op updates (only concurrency/timestamp churn).
            if (entry.State == EntityState.Modified && audit.Data is null)
                continue;

            _pending.Add((audit, entry.State == EntityState.Added ? entry : null));
        }
    }

    /// <summary>Resolves generated ids and stages audit rows; false when nothing to write.</summary>
    private bool PrepareFlush(DbContext? context)
    {
        if (context is null || _pending.Count == 0)
            return false;

        var rows = new List<AuditEvent>(_pending.Count);
        foreach (var (audit, pendingIdOf) in _pending)
        {
            if (pendingIdOf is not null)
                audit.ObjectId = KeyOf(pendingIdOf);
            rows.Add(audit);
        }
        _pending.Clear();

        context.Set<AuditEvent>().AddRange(rows);
        return true;
    }

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
            return "";
        return string.Join("/", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));
    }

    private static string? LabelOf(object entity) => entity switch
    {
        Ticket t => t.Number,
        TaskItem t => t.Number,
        User u => u.Name,
        Organization o => o.Name,
        Staff s => s.Username,
        _ => (entity.GetType().GetProperty("Name") ?? entity.GetType().GetProperty("Title"))
            ?.GetValue(entity)?.ToString(),
    };

    private static string? DiffOf(EntityEntry entry)
    {
        var diff = new Dictionary<string, object?>();
        foreach (var prop in entry.Properties)
        {
            if (!prop.IsModified || prop.Metadata.IsConcurrencyToken)
                continue;
            if (prop.Metadata.Name is nameof(IHasTimestamps.UpdatedAt) or nameof(IHasTimestamps.CreatedAt))
                continue;
            if (Equals(prop.OriginalValue, prop.CurrentValue))
                continue;

            diff[prop.Metadata.Name] = new { old = Clip(prop.OriginalValue), @new = Clip(prop.CurrentValue) };
        }

        return diff.Count == 0 ? null : JsonSerializer.Serialize(diff, JsonOptions);
    }

    private static object? Clip(object? value) =>
        value is string s && s.Length > MaxValueLength ? s[..MaxValueLength] + "…" : value;
}
