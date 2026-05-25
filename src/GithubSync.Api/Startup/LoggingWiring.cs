using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Formatting.Compact;

namespace GithubSync.Api.Startup;

public static class LoggingWiring
{
    internal const string ApplicationNameProperty = "github-sync";

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, _, configuration) =>
        {
            configuration.ReadFrom.Configuration(context.Configuration);
            ApplyEnrichers(configuration, context.HostingEnvironment);
            ApplyDestinations(configuration, context.HostingEnvironment);
        });
    }

    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration
            .Enrich.WithProperty("ApplicationName", ApplicationNameProperty)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("Release", ReleaseStamp.Current)
            .Enrich.WithMachineName();
    }

    internal static void ApplyDestinations(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            configuration.WriteTo.Console();
        }
        else
        {
            configuration.WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: "logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1L * 1024 * 1024 * 1024,
                retainedFileCountLimit: 14,
                shared: true);
        }

        configuration.WriteTo.Sentry(o => o.InitializeSdk = false);
    }
}
