using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueFollowUpResponse(
    [property: JsonPropertyName("data")] IssueFollowUpData? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQLErrorDto>? Errors);

internal sealed record IssueFollowUpData(
    [property: JsonPropertyName("repository")] IssueFollowUpRepository? Repository,
    [property: JsonPropertyName("rateLimit")] RateLimitDto? RateLimit);

internal sealed record IssueFollowUpRepository(
    [property: JsonPropertyName("issue")] IssueFollowUpIssue? Issue);

internal sealed record IssueFollowUpIssue(
    [property: JsonPropertyName("timelineItems")] TimelineItemsConnection? TimelineItems,
    [property: JsonPropertyName("comments")] CommentsConnection? Comments,
    [property: JsonPropertyName("userContentEdits")] EditsConnection? UserContentEdits);
