using GithubSync.Api.Sync;
using GithubSync.Api.Sync.Ingestion;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GithubSync.Api.Startup;

public static class HangfireWiring
{
    // Stable recurring-job ID — CLAUDE.md notes that updates to cron require re-registering
    // with the same ID. Don't generate this name dynamically.
    public const string SchedulerRecurringJobId = "ingest-github-scheduler";

    // Separate schema for Hangfire storage so its tables don't mingle with app tables.
    // Hangfire creates the schema if missing on first call.
    private const string HangfireSchemaName = "hangfire";

    // Opt-out flag for WebApplicationFactory<Program>-based tests where there is no real
    // Postgres — the background server's connection attempt against the placeholder connection
    // string otherwise hangs host startup until Npgsql gives up.
    public const string EnabledConfigKey = "Hangfire:Enabled";

    public static IServiceCollection AddHangfireScheduler(
        this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(EnabledConfigKey, defaultValue: true))
        {
            return services;
        }

        var connectionString = configuration.GetConnectionString("AppDb");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opt =>
            {
                opt.UseNpgsqlConnection(connectionString);
            }, new PostgreSqlStorageOptions
            {
                SchemaName = HangfireSchemaName,
                // PrepareSchemaIfNecessary defaults to true. Leave it: first run on a fresh DB
                // creates the hangfire schema and its tables.
            }));

        // The background server processes jobs in-process. Keep the worker count low —
        // v1 traffic is small (one tick per 15 min) and a high count costs nothing but
        // makes hot-loop bugs noisier.
        services.AddHangfireServer(opt =>
        {
            opt.WorkerCount = 2;
            // Hangfire times are UTC per CLAUDE.md gotcha; server defaults to UTC for cron
            // when TimeZoneInfo is unspecified.
        });

        // IBackgroundJobClient comes from AddHangfire above. Register job and emitter inside
        // the enabled guard so the test host (Hangfire:Enabled=false) skips them cleanly.
        services.AddScoped<IssueIngestionJob>();
        services.AddScoped<SyncRunMetricsEmitter>();

        return services;
    }

    public static WebApplication MapHangfireDashboard(this WebApplication app)
    {
        if (!app.Configuration.GetValue(EnabledConfigKey, defaultValue: true))
        {
            return app;
        }

        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[]
            {
                new HangfireDashboardAuthorizationFilter(app.Environment),
            },
        });
        return app;
    }

    public static WebApplication RegisterRecurringIngestion(this WebApplication app)
    {
        if (!app.Configuration.GetValue(EnabledConfigKey, defaultValue: true))
        {
            return app;
        }

        // AddOrUpdate is idempotent on the job ID, so re-running it on every startup re-binds
        // the cron expression rather than duplicating the recurring entry.
        var options = app.Services
            .GetRequiredService<IOptions<IngestionOptions>>()
            .Value;

        RecurringJob.AddOrUpdate<IssueIngestionJob>(
            SchedulerRecurringJobId,
            job => job.RunSchedulerAsync(CancellationToken.None),
            options.CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });

        return app;
    }
}
