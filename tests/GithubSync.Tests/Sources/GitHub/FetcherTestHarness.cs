using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GithubSync.Tests.Sources.GitHub;

internal static class FetcherTestHarness
{
    public static IGitHubIssueFetcher Build(string baseUrl, string token = "test-token", CapturingSink? sink = null) =>
        BuildProvider(baseUrl, token, sink).GetRequiredService<IGitHubIssueFetcher>();

    public static GitHubGraphQLClient BuildClient(string baseUrl, string token = "test-token") =>
        BuildProvider(baseUrl, token, sink: null).GetRequiredService<GitHubGraphQLClient>();

    // Backwards-compatible alias for tests that still call the old name.
    public static IGitHubIssueFetcher BuildWithSink(string baseUrl, CapturingSink sink, string token = "test-token") =>
        Build(baseUrl, token, sink);

    private static ServiceProvider BuildProvider(string baseUrl, string token, CapturingSink? sink)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        if (sink is null)
        {
            services.AddLogging();
        }
        else
        {
            services.AddLogging(b => b.AddSerilog(
                new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger(),
                dispose: true));
        }

        services.AddGitHubSource(config);
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");
        });

        return services.BuildServiceProvider();
    }
}
