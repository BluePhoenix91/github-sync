using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record CommentNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("author")] ActorDto? Author);
