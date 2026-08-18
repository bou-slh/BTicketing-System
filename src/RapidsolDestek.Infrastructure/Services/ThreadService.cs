using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Domain.Events;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Events;
using Thread = RapidsolDestek.Domain.Entities.Thread;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Optional knobs for <see cref="IThreadService.PostAsync"/>.</summary>
public sealed record PostOptions
{
    public string? Title { get; init; }

    /// <summary>"html" (sanitized at ingress) or "text".</summary>
    public string Format { get; init; } = "html";

    /// <summary>Origin channel snapshot: Web, Email, API…</summary>
    public string? Source { get; init; } = "Web";

    /// <summary>Entry being replied to (osTicket pid).</summary>
    public int? ParentId { get; init; }

    /// <summary>JSON snapshot of to/cc recipients for outbound entries.</summary>
    public string? Recipients { get; init; }

    /// <summary>Skip the B8 work gate (used by system-generated entries).</summary>
    public bool BypassWorkGate { get; init; }
}

public interface IThreadService
{
    /// <summary>
    /// Appends a sanitized entry, maintains Thread.Last*At and the owning ticket's
    /// IsAnswered/LastUpdateAt (osTicket ThreadEntry::create parity). Staff Responses
    /// respect the B8 work gate.
    /// </summary>
    Task<ThreadEntry> PostAsync(int threadId, ThreadEntryType type, string body, ActorContext actor,
        PostOptions? options = null, CancellationToken ct = default);

    /// <summary>Writes a timeline event row (osTicket thread_event) by event-type name.</summary>
    Task<ThreadEvent> AddEventAsync(int threadId, string eventTypeName, ActorContext actor,
        object? data = null, CancellationToken ct = default);

    Task<ThreadCollaborator> AddCollaboratorAsync(int threadId, int userId, CollaboratorRole role,
        ActorContext actor, CancellationToken ct = default);

    /// <summary>
    /// Acquires (or re-enters) the composer lock on a ticket; null when another agent
    /// holds an unexpired lock (osTicket lock semantics).
    /// </summary>
    Task<EditLock?> AcquireTicketLockAsync(int ticketId, int staffId, TimeSpan ttl, CancellationToken ct = default);

    Task<bool> RenewLockAsync(int lockId, string code, TimeSpan ttl, CancellationToken ct = default);

    Task ReleaseTicketLockAsync(int ticketId, int staffId, CancellationToken ct = default);

    Task SaveDraftAsync(int staffId, string ns, string body, string? extra = null, CancellationToken ct = default);

    Task<Draft?> GetDraftAsync(int staffId, string ns, CancellationToken ct = default);

    Task DeleteDraftAsync(int staffId, string ns, CancellationToken ct = default);
}

public sealed class ThreadService(
    AppDbContext db,
    ISettingsService settings,
    IHtmlSanitizerService sanitizer,
    IDomainEventDispatcher dispatcher) : IThreadService
{
    private Dictionary<string, int>? _eventTypeIds;

    public async Task<ThreadEntry> PostAsync(int threadId, ThreadEntryType type, string body, ActorContext actor,
        PostOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PostOptions();

        var thread = await db.Threads.SingleOrDefaultAsync(t => t.Id == threadId, ct)
            ?? throw new DomainNotFoundException("Thread", threadId);
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.ThreadId == threadId, ct);

        // B8 gate: staff responses (customer-visible work output) wait for approval.
        if (type == ThreadEntryType.Response && actor.IsStaff && ticket is not null && !options.BypassWorkGate
            && !await EffortWorkGate.IsWorkAllowedAsync(db, settings, ticket.Id, ct))
        {
            throw new WorkBlockedByEffortException(ticket.Id);
        }

        var entry = new ThreadEntry
        {
            ThreadId = threadId,
            ParentId = options.ParentId,
            Type = type,
            StaffId = actor.IsStaff ? actor.Id : null,
            UserId = actor.IsUser ? actor.Id : null,
            Poster = actor.Name,
            Source = options.Source,
            Title = options.Title,
            Format = options.Format,
            Body = options.Format == "html" ? sanitizer.Sanitize(body) : body,
            IpAddress = actor.IpAddress,
            Recipients = options.Recipients,
        };
        db.ThreadEntries.Add(entry);

        var now = DateTimeOffset.UtcNow;
        if (type == ThreadEntryType.Message)
            thread.LastMessageAt = now;
        if (type == ThreadEntryType.Response)
            thread.LastResponseAt = now;

        var autoClaimed = false;
        if (ticket is not null)
        {
            ticket.LastUpdateAt = now;
            // osTicket parity: agent response marks answered, new user message clears it.
            if (type == ThreadEntryType.Response)
                ticket.IsAnswered = true;
            else if (type == ThreadEntryType.Message)
                ticket.IsAnswered = false;

            // tickets.claim_on_response (S7 admin/settings-tickets, osTicket
            // auto_claim_tickets): a staff response on an unassigned ticket claims it.
            if (type == ThreadEntryType.Response && actor.IsStaff && ticket.StaffId is null
                && (await settings.GetTicketBehaviorAsync(ct)).ClaimOnResponse)
            {
                ticket.StaffId = actor.Id;
                autoClaimed = true;
            }
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        var events = new List<Domain.Events.IDomainEvent>
        {
            new ThreadEntryAdded(threadId, entry.Id, type, ticket?.Id),
        };
        if (autoClaimed)
        {
            await AddEventAsync(threadId, "assigned", actor, new { staffId = ticket!.StaffId, teamId = ticket.TeamId }, ct);
            events.Add(new TicketAssigned(ticket.Id, ticket.StaffId, ticket.TeamId, actor.Name));
        }

        await dispatcher.DispatchAsync(events, ct);
        return entry;
    }

    public async Task<ThreadEvent> AddEventAsync(int threadId, string eventTypeName, ActorContext actor,
        object? data = null, CancellationToken ct = default)
    {
        _eventTypeIds ??= await db.ThreadEventTypes.ToDictionaryAsync(t => t.Name, t => t.Id, ct);
        if (!_eventTypeIds.TryGetValue(eventTypeName, out var eventTypeId))
            throw new DomainNotFoundException("ThreadEventType", eventTypeName);

        var evt = new ThreadEvent
        {
            ThreadId = threadId,
            EventTypeId = eventTypeId,
            ActorType = actor.Type,
            ActorId = actor.Id,
            Username = actor.Name,
            StaffId = actor.IsStaff ? actor.Id : null,
            Data = data is null ? null : data as string ?? JsonSerializer.Serialize(data),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.ThreadEvents.Add(evt);

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);
        return evt;
    }

    public async Task<ThreadCollaborator> AddCollaboratorAsync(int threadId, int userId, CollaboratorRole role,
        ActorContext actor, CancellationToken ct = default)
    {
        var existing = await db.ThreadCollaborators
            .SingleOrDefaultAsync(c => c.ThreadId == threadId && c.UserId == userId, ct);
        if (existing is not null)
        {
            existing.Role = role;
            existing.IsActive = true;
        }
        else
        {
            existing = new ThreadCollaborator { ThreadId = threadId, UserId = userId, Role = role };
            db.ThreadCollaborators.Add(existing);
        }

        using (actor.BeginAuditScope())
            await db.SaveChangesAsync(ct);

        await AddEventAsync(threadId, "collab-added", actor, new { userId }, ct);
        return existing;
    }

    public async Task<EditLock?> AcquireTicketLockAsync(int ticketId, int staffId, TimeSpan ttl, CancellationToken ct = default)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new DomainNotFoundException("Ticket", ticketId);

        var now = DateTimeOffset.UtcNow;
        if (ticket.LockId is { } lockId)
        {
            var current = await db.EditLocks.SingleOrDefaultAsync(l => l.Id == lockId, ct);
            if (current is not null && current.ExpiresAt > now && current.StaffId != staffId)
                return null; // somebody else holds it
            if (current is not null && current.StaffId == staffId)
            {
                current.ExpiresAt = now + ttl; // re-enter own lock
                await db.SaveChangesAsync(ct);
                return current;
            }
            if (current is not null)
                db.EditLocks.Remove(current); // expired foreign lock — steal
        }

        var edit = new EditLock
        {
            StaffId = staffId,
            ExpiresAt = now + ttl,
            Code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            CreatedAt = now,
        };
        db.EditLocks.Add(edit);
        await db.SaveChangesAsync(ct);

        ticket.LockId = edit.Id;
        await db.SaveChangesAsync(ct);
        return edit;
    }

    public async Task<bool> RenewLockAsync(int lockId, string code, TimeSpan ttl, CancellationToken ct = default)
    {
        var edit = await db.EditLocks.SingleOrDefaultAsync(l => l.Id == lockId, ct);
        if (edit is null || edit.Code != code || edit.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;
        edit.ExpiresAt = DateTimeOffset.UtcNow + ttl;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReleaseTicketLockAsync(int ticketId, int staffId, CancellationToken ct = default)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket?.LockId is not { } lockId)
            return;
        var edit = await db.EditLocks.SingleOrDefaultAsync(l => l.Id == lockId && l.StaffId == staffId, ct);
        if (edit is null)
            return;
        ticket.LockId = null;
        db.EditLocks.Remove(edit);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveDraftAsync(int staffId, string ns, string body, string? extra = null, CancellationToken ct = default)
    {
        var draft = await db.Drafts.SingleOrDefaultAsync(d => d.StaffId == staffId && d.Namespace == ns, ct);
        if (draft is null)
        {
            draft = new Draft { StaffId = staffId, Namespace = ns };
            db.Drafts.Add(draft);
        }
        draft.Body = body;
        draft.Extra = extra;
        await db.SaveChangesAsync(ct);
    }

    public Task<Draft?> GetDraftAsync(int staffId, string ns, CancellationToken ct = default) =>
        db.Drafts.SingleOrDefaultAsync(d => d.StaffId == staffId && d.Namespace == ns, ct);

    public async Task DeleteDraftAsync(int staffId, string ns, CancellationToken ct = default)
    {
        await db.Drafts.Where(d => d.StaffId == staffId && d.Namespace == ns).ExecuteDeleteAsync(ct);
    }
}
