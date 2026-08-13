using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RapidsolDestek.Domain.Services;
using RapidsolDestek.Infrastructure.Events;
using RapidsolDestek.Infrastructure.Files;
using RapidsolDestek.Infrastructure.Services;

namespace RapidsolDestek.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-access layer (AppDbContext on PostgreSQL with snake_case naming).
    /// </summary>
    public static IServiceCollection AddRapidsolData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(configuration.GetConnectionString("Default"))
            .UseSnakeCaseNamingConvention());

        return services;
    }

    /// <summary>
    /// Registers the S4 core services (ticket/thread/effort/queue/canned + plumbing).
    /// Domain event handlers are registered by their consumers (Web, S8 jobs).
    /// </summary>
    public static IServiceCollection AddRapidsolServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IHtmlSanitizerService, HtmlSanitizerService>();
        services.AddSingleton<IFileStore>(_ => new FileSystemFileStore(
            configuration["Files:Root"]
            ?? Path.Combine(AppContext.BaseDirectory, "filestore")));

        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<ISequenceNumberService, SequenceNumberService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IThreadService, ThreadService>();
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<IEffortProposalService, EffortProposalService>();
        services.AddScoped<IQueueEngine, QueueEngine>();
        services.AddScoped<ICannedResponseService, CannedResponseService>();

        return services;
    }
}
