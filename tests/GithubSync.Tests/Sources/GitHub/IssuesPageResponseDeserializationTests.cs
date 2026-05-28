using System.Text.Json;
using GithubSync.Sources.GitHub.GraphQL.Dto;

namespace GithubSync.Tests.Sources.GitHub;

public class IssuesPageResponseDeserializationTests
{
    [Fact]
    public void Deserializes_minimal_response_with_one_issue_and_one_label_event()
    {
        const string json = """
        {
          "data": {
            "repository": {
              "issues": {
                "pageInfo": { "endCursor": null, "hasNextPage": false },
                "nodes": [
                  {
                    "id": "I_kw",
                    "number": 1,
                    "databaseId": 1001,
                    "createdAt": "2026-01-01T00:00:00Z",
                    "updatedAt": "2026-01-02T00:00:00Z",
                    "author": { "login": "octocat", "databaseId": 1, "__typename": "User" },
                    "userContentEdits": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "comments": { "pageInfo": { "endCursor": null, "hasNextPage": false }, "nodes": [] },
                    "timelineItems": {
                      "pageInfo": { "endCursor": null, "hasNextPage": false },
                      "nodes": [
                        { "__typename": "LabeledEvent", "id": "LE_1", "createdAt": "2026-01-02T00:00:00Z",
                          "actor": { "login": "octocat", "databaseId": 1, "__typename": "User" },
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

        var response = JsonSerializer.Deserialize<IssuesPageResponse>(json);

        Assert.NotNull(response);
        Assert.Null(response!.Errors);
        Assert.NotNull(response.Data?.Repository?.Issues);
        var issue = Assert.Single(response.Data.Repository.Issues.Nodes);
        Assert.Equal(1, issue.Number);
        Assert.Equal("octocat", issue.Author?.Login);
        var ev = Assert.Single(issue.TimelineItems!.Nodes);
        Assert.Equal("LabeledEvent", ev.TypeName);
        Assert.Equal("bug", ev.Label?.Name);
        Assert.Equal(4999, response.Data.RateLimit?.Remaining);
    }
}
