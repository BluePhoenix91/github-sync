using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests;

public class LoggingWiringIntegrationTests
{
    [Fact]
    public void Host_resolves_ILogger_backed_by_Serilog()
    {
        using var factory = new ConfiguredAppFactory();

        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<LoggingWiringIntegrationTests>();

        logger.LogInformation("Integration smoke message");

        Assert.IsType<SerilogLoggerFactory>(loggerFactory);
    }

    [Fact]
    public void Host_starts_without_throwing()
    {
        using var factory = new ConfiguredAppFactory();

        // Forcing CreateClient builds the host end-to-end; if Sentry/Serilog
        // ordering broke, this would surface here.
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
