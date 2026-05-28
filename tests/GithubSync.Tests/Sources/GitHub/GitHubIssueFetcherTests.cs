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

    [Fact]
    public async Task Single_page_with_varied_content_yields_expected_events_in_order()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SinglePageVariedBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = await CollectAsync(fetcher);

        // One issue with: create (synth), label, comment, close — in event-time order.
        Assert.Equal(4, events.Count);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.IssueOpened, events[0].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Labeled, events[1].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Commented, events[2].Kind);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Closed, events[3].Kind);
        Assert.All(events, e => Assert.Equal("42", e.SourceEntityId));
    }

    [Fact]
    public async Task Null_actor_is_passed_through_not_skipped()
    {
        using var server = new WireMockGitHubServer();
        server.Server.Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(NullActorBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);

        var events = await CollectAsync(fetcher);

        // Two events: create (synth, with author), labeled (with null actor)
        Assert.Equal(2, events.Count);
        var labeled = events.Single(e => e.Kind == global::GithubSync.Sources.GitHub.GitHubEventKind.Labeled);
        Assert.Null(labeled.Actor);
    }

    [Fact]
    public async Task Since_filter_excludes_events_before_the_cursor()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SinceFilterBody));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var since = DateTimeOffset.Parse("2026-01-01T11:00:00Z");

        var events = new List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>();
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since, default))
            events.Add(e);

        // Issue created at 10:00 (before since); labeled at 12:00 (after since).
        // Only the labeled event should pass the filter.
        Assert.Single(events);
        Assert.Equal(global::GithubSync.Sources.GitHub.GitHubEventKind.Labeled, events[0].Kind);
    }

    private const string SinceFilterBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_60",
                    "number": 60,
                    "databaseId": 6060,
                    "createdAt": "2026-01-01T10:00:00Z",
                    "updatedAt": "2026-01-01T12:00:00Z",
                    "title": "old issue still updating",
                    "body": "body",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_60", "createdAt": "2026-01-01T12:00:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "regression" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

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

    private const string SinglePageVariedBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_42",
                    "number": 42,
                    "databaseId": 4242,
                    "createdAt": "2026-01-01T10:00:00Z",
                    "updatedAt": "2026-01-01T12:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "id": "C_1", "databaseId": 5001, "createdAt": "2026-01-01T10:30:00Z", "body": "hi",
                          "author": { "login": "octocat", "databaseId": 1, "__typename": "User" } }
                      ]
                    },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_1", "createdAt": "2026-01-01T10:15:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "bug" } },
                        { "__typename": "ClosedEvent", "id": "CE_1", "createdAt": "2026-01-01T12:00:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private const string NullActorBody = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_99",
                    "number": 99,
                    "databaseId": 9999,
                    "createdAt": "2026-01-01T10:00:00Z",
                    "updatedAt": "2026-01-01T11:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_99", "createdAt": "2026-01-01T11:00:00Z",
                          "actor": null,
                          "label": { "name": "stale" } }
                      ]
                    }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private static async Task<List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>> CollectAsync(
        global::GithubSync.Sources.GitHub.IGitHubIssueFetcher fetcher)
    {
        var list = new List<global::GithubSync.Sources.GitHub.GitHubIssueEvent>();
        await foreach (var e in fetcher.FetchAsync("octocat", "Hello-World", since: null, ct: default))
            list.Add(e);
        return list;
    }
}
