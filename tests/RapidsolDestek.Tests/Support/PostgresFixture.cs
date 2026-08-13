using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Seed;

namespace RapidsolDestek.Tests.Support;

/// <summary>
/// One PostgreSQL container per test collection: starts postgres:17-alpine, applies
/// migrations and runs the Identity + Domain seeders once. Tests get contexts via
/// <see cref="CreateContext"/> or a full app via <see cref="AppFactory"/>.
/// Seeded canon rows (hero ticket R716555 etc.) are shared read-only fixtures —
/// tests that mutate must create their own rows with unique natural keys.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("rapidsoldestek_test")
        .WithUsername("rapidsol")
        .WithPassword("rapidsol-test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public AppFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using (var ctx = CreateContext())
        {
            await ctx.Database.MigrateAsync();
        }

        // AppFactory boots the real Program (Development), whose dev bootstrap
        // migrates (no-op now) and seeds Identity + Domain idempotently.
        Factory = new AppFactory(ConnectionString);
        using var scope = Factory.Services.CreateScope();
        await DomainSeeder.SeedAsync(scope.ServiceProvider); // no-op if Program already ran it
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Standalone context with the production conventions + interceptors.</summary>
    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Scope over the app's real DI graph — resolve S4 services from here.</summary>
    public IServiceScope CreateScope() => Factory.Services.CreateScope();
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
