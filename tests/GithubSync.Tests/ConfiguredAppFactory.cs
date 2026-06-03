using GithubSync.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GithubSync.Tests;

// Shared base for WebApplicationFactory<Program>-based tests.
// Sets Development env + in-memory AppDb placeholder + empty Sentry DSN so
// SentryWiring's no-op branch and RequiredSecrets' dev-leniency both apply.
internal class ConfiguredAppFactory : WebApplicationFactory<Program>
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
                [HangfireWiring.EnabledConfigKey] = "false",
            });
        });
        return base.CreateHost(builder);
    }
}
