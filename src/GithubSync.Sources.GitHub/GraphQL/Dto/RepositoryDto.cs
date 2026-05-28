using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record RepositoryDto(
    [property: JsonPropertyName("issues")] IssuesConnection? Issues);
