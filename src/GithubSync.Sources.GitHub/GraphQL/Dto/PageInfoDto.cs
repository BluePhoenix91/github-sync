using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record PageInfoDto(
    [property: JsonPropertyName("endCursor")] string? EndCursor,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);
