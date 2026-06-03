using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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

    // Bounded queue size for the async file-sink wrapper. Matches the
    // Serilog.Sinks.Async default; set explicitly so the choice is visible.
    // With blockWhenFull: false, drops on overflow surface via Serilog SelfLog
    // (configure SelfLog.Enable to capture overruns when diagnosing slow disks).
    internal const int FileSinkAsyncBufferSize = 10_000;

    // EF Core emits this source context for every SQL command, including
    // CommandError (EventId 20102) which fires alongside the actual exception
    // from Microsoft.EntityFrameworkCore.Query. The Command record carries no
    // exception, just the SQL text — Sentry groups it as a separate issue
    // (and re-groups again on elapsed-ms variance), so one failure produces
    // multiple Sentry events. The Query event keeps the real exception alert.
    internal const string EfCommandSourceContext = "Microsoft.EntityFrameworkCore.Database.Command";

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
            configuration.WriteTo.Async(a => a.File(
                formatter: new CompactJsonFormatter(),
                path: "logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1L * 1024 * 1024 * 1024,
                retainedFileCountLimit: 14,
                shared: true),
                bufferSize: FileSinkAsyncBufferSize,
                blockWhenFull: false);

            if (ShouldEnableSeq(seqServerUrl))
            {
                configuration.WriteTo.Seq(seqServerUrl!);
            }
        }

        // Sub-logger so the filter only narrows what Sentry sees. The file sink
        // and Seq sink at the outer scope still receive every event — they're
        // the authoritative SQL history.
        configuration.WriteTo.Logger(sentryOnly => sentryOnly
            .Filter.ByExcluding(IsEfCommandLogEvent)
            .WriteTo.Sentry(o => o.InitializeSdk = false));
    }

    internal static bool ShouldEnableSeq(string? seqServerUrl) => !string.IsNullOrWhiteSpace(seqServerUrl);

    internal static bool IsEfCommandLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value))
        {
            return false;
        }

        return value is ScalarValue { Value: string sourceContext }
            && sourceContext == EfCommandSourceContext;
    }
}
