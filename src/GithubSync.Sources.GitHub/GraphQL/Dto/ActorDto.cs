using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record ActorDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("__typename")] string? TypeName);
