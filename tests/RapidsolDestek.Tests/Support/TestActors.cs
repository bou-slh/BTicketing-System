using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Auditing;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Tests.Support;

/// <summary>Builds ActorContexts from the seeded canon roster.</summary>
public static class TestActors
{
    public static async Task<ActorContext> StaffAsync(AppDbContext db, string username)
    {
        var staff = await db.Staff.SingleAsync(s => s.Username == username);
        return ActorContext.ForStaff(staff);
    }

    public static async Task<ActorContext> UserAsync(AppDbContext db, string name)
    {
        var user = await db.Users.SingleAsync(u => u.Name == name);
        return ActorContext.ForUser(user);
    }
}

/// <summary>Scoped service bundle for integration tests.</summary>
public sealed class ServiceScopeBundle : IDisposable
{
    private readonly IServiceScope _scope;

    public ServiceScopeBundle(PostgresFixture fixture)
    {
        _scope = fixture.CreateScope();
        Db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public AppDbContext Db { get; }
    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose() => _scope.Dispose();
}

/// <summary>Temporarily flips a setting; restores on dispose (tests share the container DB).</summary>
public sealed class SettingOverride : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _ns;
    private readonly string _key;
    private readonly string? _original;

    private SettingOverride(PostgresFixture fixture, string ns, string key, string? original)
    {
        _fixture = fixture;
        _ns = ns;
        _key = key;
        _original = original;
    }

    public static async Task<SettingOverride> SetAsync(PostgresFixture fixture, string ns, string key, string value)
    {
        using var scope = fixture.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetAsync(ns, key);
        await settings.SetAsync(ns, key, value);
        return new SettingOverride(fixture, ns, key, original);
    }

    public async ValueTask DisposeAsync()
    {
        using var scope = _fixture.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(_ns, _key, _original ?? "");
    }
}
