using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("author")] ActorDto? Author,
    [property: JsonPropertyName("userContentEdits")] EditsConnection? UserContentEdits,
    [property: JsonPropertyName("comments")] CommentsConnection? Comments,
    [property: JsonPropertyName("timelineItems")] TimelineItemsConnection? TimelineItems);

internal sealed record EditsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<UserContentEditNode> Nodes);

internal sealed record CommentsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<CommentNode> Nodes);

internal sealed record TimelineItemsConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<TimelineItemNode> Nodes);
