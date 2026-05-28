using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubIssueFetcherTests
{
    [Fact]
    public async Task Empty_page_yields_zero_events()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EmptyPageBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = new List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>();
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since: null, ct: default))
        {
            events.Add(e);
        }

        Assert.Empty(events);
    }

    private const string EmptyPageBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": []
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;
}
