using Microsoft.Extensions.Hosting;
using Serilog;

namespace GithubSync.Api.Startup;

public static class LoggingWiring
{
    internal const string ApplicationNameProperty = "github-sync";

    internal static void ApplyEnrichers(LoggerConfiguration configuration, IHostEnvironment environment)
    {
        configuration.Enrich.WithProperty("ApplicationName", ApplicationNameProperty);
    }
}
