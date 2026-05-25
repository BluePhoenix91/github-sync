using GithubSync.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests;

public class LoggingWiringIntegrationTests
{
    [Fact]
    public void Host_resolves_ILogger_backed_by_Serilog()
    {
        using var factory = new TestFactory();

        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<LoggingWiringIntegrationTests>();

        // Smoke: logging through the resolved factory must not throw,
        // and Serilog must be the registered ILoggerFactory once UseSerilog has run.
        logger.LogInformation("Integration smoke message");

        Assert.IsType<SerilogLoggerFactory>(loggerFactory);
    }

    [Fact]
    public void Host_starts_without_throwing()
    {
        using var factory = new TestFactory();

        // Forcing CreateClient builds the host end-to-end; if Sentry/Serilog
        // ordering broke, this would surface here.
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private sealed class TestFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = "Host=placeholder;Database=placeholder;Username=placeholder;Password=placeholder",
                    [SentryWiring.DsnConfigKey] = "",
                });
            });
            return base.CreateHost(builder);
        }
    }
}
