using System.Net.Http.Headers;
using GithubSync.Sources.GitHub.GraphQL;
using GithubSync.Sources.GitHub.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace GithubSync.Sources.GitHub;

public static class GitHubSourceServiceCollectionExtensions
{
    public const string TokenConfigKey = "GITHUB_TOKEN";

    public static IServiceCollection AddGitHubSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<GitHubRateLimitBudget>();

        services.AddHttpClient<GitHubGraphQLClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.github.com");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("github-sync/1.0");

                var token = configuration[TokenConfigKey];
                if (!string.IsNullOrWhiteSpace(token))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);
                }
            })
            .AddPolicyHandler(BuildTransientRetryPolicy());

        services.AddTransient<IGitHubIssueFetcher, GitHubIssueFetcher>();

        return services;
    }

    // 3 retries on top of the initial attempt; exponential backoff 1s -> 2s -> 4s.
    // Handles HttpRequestException and 5xx responses. Does not handle 403 — that's the client's job.
    private static IAsyncPolicy<HttpResponseMessage> BuildTransientRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
}
