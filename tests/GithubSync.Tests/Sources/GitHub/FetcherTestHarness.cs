using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

internal static class FetcherTestHarness
{
    public static IGitHubIssueFetcher Build(string baseUrl, string token = "test-token")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubSource(config);
        // Override base address to point at the WireMock URL.
        services.AddHttpClient<GitHubGraphQLClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IGitHubIssueFetcher>();
    }
}
