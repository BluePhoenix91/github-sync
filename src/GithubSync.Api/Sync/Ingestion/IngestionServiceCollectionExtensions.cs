using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Api.Sync.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    public static IServiceCollection AddIngestion(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityMappingOptions>(
            configuration.GetSection(IdentityMappingOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        // Scoped: both services capture the request/job-scoped AppDbContext and the resolver
        // holds a per-run cache. A new scope per sync run gives us a clean cache by construction.
        services.AddScoped<IActorResolver, ActorResolver>();
        services.AddScoped<ICanonicalEventMapper, CanonicalEventMapper>();

        return services;
    }
}
