using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Auditing;

/// <summary>
/// Resolved caller identity passed explicitly into every S4 service method
/// (controllers build it from claims in S5+; background jobs use <see cref="System"/>).
/// Converts to <see cref="AuditActorInfo"/> so permission checks and the audit
/// interceptor attribute the same actor.
/// </summary>
public sealed record ActorContext(ActorType Type, int? Id, string Name, string? IpAddress = null)
{
    public static readonly ActorContext System = new(ActorType.System, null, "SYSTEM");

    public static ActorContext ForStaff(Staff staff, string? ip = null) =>
        new(ActorType.Staff, staff.Id, staff.FullName, ip);

    public static ActorContext ForUser(User user, string? ip = null) =>
        new(ActorType.User, user.Id, user.Name, ip);

    public bool IsStaff => Type == ActorType.Staff && Id is not null;
    public bool IsUser => Type == ActorType.User && Id is not null;

    public AuditActorInfo ToAuditActor() => new(Type, Id, Name, IpAddress);

    /// <summary>Sets this actor as the ambient audit actor; dispose to restore.</summary>
    public IDisposable BeginAuditScope() => AuditActor.Use(ToAuditActor());
}
