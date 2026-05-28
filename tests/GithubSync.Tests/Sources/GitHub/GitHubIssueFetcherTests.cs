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

    [Fact]
    public async Task Outer_pagination_walks_two_pages_passing_endCursor_as_after()
    {
        using var server = new WireMockGitHubServer();

        // Page 1: hasNextPage true, returns 1 issue
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && !b.Contains("\"cursor\":\"page2cursor\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(PaginationPage1));

        // Page 2: cursor present, hasNextPage false
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("\"cursor\":\"page2cursor\"")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(PaginationPage2));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        // 2 issues across pages, each yielding only IssueOpened (no timeline content)
        Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "1", "2" }, events.Select(e => e.SourceEntityId).ToArray());
    }

    private const string PaginationPage1 = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": "page2cursor", "hasNextPage": true },
                "nodes": [
                  {
                    "id": "I_kw_1", "number": 1, "databaseId": 1, "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z",
                    "title": "first", "body": "b1",
                    "author": { "login": "a", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4999, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    private const string PaginationPage2 = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_2", "number": 2, "databaseId": 2, "createdAt": "2026-01-02T00:00:00Z", "updatedAt": "2026-01-02T00:00:00Z",
                    "title": "second", "body": "b2",
                    "author": { "login": "b", "databaseId": 2, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] }
                  }
                ]
              }
            },
            "rateLimit": { "remaining": 4998, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
          }
        }
        """;

    [Fact]
    public async Task Inner_pagination_follow_up_drains_overflowing_timeline()
    {
        using var server = new WireMockGitHubServer();

        // Outer query: 1 issue with timeline.hasNextPage = true, endCursor = "t-cursor"
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("IssuesPage")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(OuterWithOverflow));

        // Follow-up timeline: hasNextPage = false, returns one more event
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql")
                .WithBody(b => b is not null && b.Contains("IssueTimelineFollowUp")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(FollowUpTimeline));

        var fetcher = FetcherTestHarness.Build(server.BaseUrl);
        var events = await CollectAsync(fetcher);

        // Expected: IssueOpened + initial LabeledEvent + follow-up ClosedEvent = 3 events
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Kind == global::GithubSync.Sources.GitHub.GitHubEventKind.Closed);

        // Verify the follow-up query was called exactly once
        var followUps = server.Server.LogEntries
            .Count(le => le.RequestMessage.Body?.Contains("IssueTimelineFollowUp") == true);
        Assert.Equal(1, followUps);
    }

    private const string OuterWithOverflow = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw_77", "number": 77, "databaseId": 77,
                    "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T01:00:00Z",
                    "title": "long-running", "body": "issue 77",
                    "author": { "login": "x", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": "t-cursor", "hasNextPage": true },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_X", "createdAt": "2026-01-01T00:30:00Z",
                          "actor": { "login": "x", "databaseId": 1, "__typename": "User" },
                          "label": { "name": "bug" } }
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

    private const string FollowUpTimeline = """
        {
          "data": {
            "repository": {
              "issue": {
                "updatedAt": "2026-01-01T01:00:00Z",
                "timelineItems": {
                  "pageInfo": { "endCursor": null, "hasNextPage": false },
                  "nodes": [
                    { "__typename": "ClosedEvent", "id": "CE_X", "createdAt": "2026-01-01T01:00:00Z",
                      "actor": { "login": "x", "databaseId": 1, "__typename": "User" } }
                  ]
                }
              }
            },
            "rateLimit": { "remaining": 4998, "cost": 1, "resetAt": "2026-01-01T01:00:00Z", "limit": 5000 }
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
