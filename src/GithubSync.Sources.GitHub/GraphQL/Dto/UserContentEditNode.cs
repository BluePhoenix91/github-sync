using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record UserContentEditNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("editedAt")] DateTimeOffset EditedAt,
    [property: JsonPropertyName("diff")] string? Diff,
    [property: JsonPropertyName("editor")] ActorDto? Editor);
