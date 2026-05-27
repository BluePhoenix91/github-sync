using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Formatting.Compact;

namespace GithubSync.Api.Startup;

public static class LoggingWiring
{
    internal const string ApplicationNameValue = "github-sync";
    internal const string ApplicationNameKey = "ApplicationName";
    internal const string EnvironmentKey = "Environment";
    internal const string ReleaseKey = "Release";
    internal const string MachineNameKey = "MachineName";
    public const string SeqServerUrlConfigKey = "SEQ_SERVER_URL";

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, _, configuration) =>
        {
            configuration.ReadFrom.Configuration(context.Configuration);
            ApplyEnrichers(configuration, context.HostingEnvironment);
            ApplyDestinations(configuration, context.HostingEnvironment, context.Configuration[SeqServerUrlConfigKey]);
        });
    }

    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration
            .Enrich.WithProperty(ApplicationNameKey, ApplicationNameValue)
            .Enrich.WithProperty(EnvironmentKey, environment.EnvironmentName)
            .Enrich.WithProperty(ReleaseKey, ReleaseStamp.Current)
            .Enrich.WithMachineName();
    }

    internal static void ApplyDestinations(LoggerConfiguration configuration, IHostEnvironment environment, string? seqServerUrl = null)
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

            if (ShouldEnableSeq(seqServerUrl))
            {
                configuration.WriteTo.Seq(seqServerUrl!);
            }
        }

        configuration.WriteTo.Sentry(o => o.InitializeSdk = false);
    }

    internal static bool ShouldEnableSeq(string? seqServerUrl) => !string.IsNullOrWhiteSpace(seqServerUrl);
}
