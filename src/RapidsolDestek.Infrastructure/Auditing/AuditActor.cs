using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Auditing;

/// <summary>
/// Ambient actor attributed to audit rows written during SaveChanges. Web middleware
/// sets this per request (S5+); background jobs and seeds run as the System default.
/// AsyncLocal so parallel requests/jobs can't bleed into each other.
/// </summary>
public static class AuditActor
{
    private static readonly AsyncLocal<AuditActorInfo?> Ambient = new();

    public static AuditActorInfo Current => Ambient.Value ?? AuditActorInfo.System;

    /// <summary>Sets the ambient actor; dispose the return value to restore the previous one.</summary>
    public static IDisposable Use(AuditActorInfo actor)
    {
        var previous = Ambient.Value;
        Ambient.Value = actor;
        return new Scope(() => Ambient.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

public sealed record AuditActorInfo(ActorType Type, int? Id, string Name, string? IpAddress = null)
{
    public static readonly AuditActorInfo System = new(ActorType.System, null, "SYSTEM");
}
