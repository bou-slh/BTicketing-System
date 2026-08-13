using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RapidsolDestek.Domain.Events;

namespace RapidsolDestek.Infrastructure.Events;

/// <summary>Subscribe to a domain event by registering this in DI (any lifetime).</summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct = default);
}

public interface IDomainEventDispatcher
{
    /// <summary>Dispatches post-commit; handler failures are logged, never rethrown.</summary>
    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken ct = default);
}

/// <summary>
/// Minimal in-proc dispatcher (deliberately no MediatR — the codebase is hand-rolled).
/// S8's email pipeline and B7's live board subscribe by registering
/// <see cref="IDomainEventHandler{TEvent}"/> implementations; S4 code never changes.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider services, ILogger<DomainEventDispatcher> logger)
    : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle)> Cache = new();

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken ct = default)
    {
        foreach (var evt in events)
        {
            var (handlerType, handle) = Cache.GetOrAdd(evt.GetType(), t =>
            {
                var ht = typeof(IDomainEventHandler<>).MakeGenericType(t);
                return (typeof(IEnumerable<>).MakeGenericType(ht), ht.GetMethod("HandleAsync")!);
            });

            foreach (var handler in (System.Collections.IEnumerable)services.GetRequiredService(handlerType))
            {
                try
                {
                    await (Task)handle.Invoke(handler, [evt, ct])!;
                }
                catch (Exception ex)
                {
                    // A broken subscriber must never fail the mutation that raised the event.
                    logger.LogError(ex, "Domain event handler {Handler} failed for {Event}",
                        handler!.GetType().Name, evt.GetType().Name);
                }
            }
        }
    }
}
