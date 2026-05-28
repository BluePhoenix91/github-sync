using GithubSync.Sources.GitHub;
using GithubSync.Sources.GitHub.GraphQL;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubIntegrationTests
{
    [SkippableFact]
    public async Task Hits_octocat_Hello_World_and_yields_events()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var runFlag = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS");

        Skip.If(string.IsNullOrWhiteSpace(token) || runFlag != "true",
            "Integration tests require GITHUB_TOKEN and RUN_INTEGRATION_TESTS=true.");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GitHubSourceServiceCollectionExtensions.TokenConfigKey] = token,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGitHubSource(config);
        using var provider = services.BuildServiceProvider();

        var fetcher = provider.GetRequiredService<IGitHubIssueFetcher>();

        var events = new List<GitHubIssueEvent>();
        // Tight time window: just want to prove the query runs against real GitHub.
        var since = DateTimeOffset.UtcNow.AddYears(-1);
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since, default))
        {
            events.Add(e);
            if (events.Count >= 5) break;
        }

        Assert.NotEmpty(events);
    }
}
