using System.Net;
using GithubSync.Sources.GitHub.GraphQL;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubGraphQLClientTests
{
    [Fact]
    public async Task Attaches_bearer_token_from_configuration()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithHeader("Authorization", "Bearer test-token"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"data":{"repository":{"issues":{"pageInfo":{"endCursor":null,"hasNextPage":false},"nodes":[]}},"rateLimit":{"remaining":4999,"cost":1,"resetAt":"2026-01-01T01:00:00Z","limit":5000}}}"""));

        var client = FetcherTestHarness.BuildClient(server.BaseUrl);

        var resp = await client.QueryIssuesPageAsync("o", "r", since: null, cursor: null, ct: default);

        Assert.NotNull(resp);
        // WireMock would have returned 404 if the Authorization header didn't match the stub above.
    }

    [Fact]
    public async Task Polly_retries_on_503_then_succeeds()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "transient";
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
#pragma warning disable CS8625 // WireMock.Net WhenStateIs(null) is the documented API for "initial state"
            .InScenario(scenario).WhenStateIs(null)
#pragma warning restore CS8625
            .WillSetStateTo("got-one")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("got-one")
            .WillSetStateTo("got-two")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("got-two")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"data":{"repository":{"issues":{"pageInfo":{"endCursor":null,"hasNextPage":false},"nodes":[]}},"rateLimit":{"remaining":4999,"cost":1,"resetAt":"2026-01-01T01:00:00Z","limit":5000}}}"""));

        var client = FetcherTestHarness.BuildClient(server.BaseUrl);

        var resp = await client.QueryIssuesPageAsync("o", "r", null, null, default);

        Assert.NotNull(resp);
        Assert.Equal(3, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task Secondary_rate_limit_retry_followed_by_401_throws_GitHubAuthException()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "retry-then-401";

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
#pragma warning disable CS8625 // WireMock.Net WhenStateIs(null) is the documented API for "initial state"
            .InScenario(scenario).WhenStateIs(null)
#pragma warning restore CS8625
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Retry-After", "1"));

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Unauthorized));

        var client = FetcherTestHarness.BuildClient(server.BaseUrl);

        await Assert.ThrowsAsync<global::GithubSync.Sources.GitHub.Exceptions.GitHubAuthException>(
            async () => await client.QueryIssuesPageAsync("o", "r", since: null, cursor: null, ct: default));

        Assert.Equal(2, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

    [Fact]
    public async Task Primary_rate_limit_retry_followed_by_401_throws_GitHubAuthException()
    {
        using var server = new WireMockGitHubServer();
        var scenario = "primary-retry-then-401";
        var resetEpoch = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds().ToString();

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
#pragma warning disable CS8625 // WireMock.Net WhenStateIs(null) is the documented API for "initial state"
            .InScenario(scenario).WhenStateIs(null)
#pragma warning restore CS8625
            .WillSetStateTo("retried")
            .RespondWith(Response.Create().WithStatusCode(403)
                .WithHeader("X-RateLimit-Remaining", "0")
                .WithHeader("X-RateLimit-Reset", resetEpoch));

        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .InScenario(scenario).WhenStateIs("retried")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Unauthorized));

        var client = FetcherTestHarness.BuildClient(server.BaseUrl);

        await Assert.ThrowsAsync<global::GithubSync.Sources.GitHub.Exceptions.GitHubAuthException>(
            async () => await client.QueryIssuesPageAsync("o", "r", since: null, cursor: null, ct: default));

        Assert.Equal(2, server.Server.LogEntries.Count(le => le.RequestMessage.Path == "/graphql"));
    }

}
