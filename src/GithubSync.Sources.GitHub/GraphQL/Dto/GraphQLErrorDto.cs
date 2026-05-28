using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record GraphQLErrorDto(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string? Type);
