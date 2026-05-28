using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record UserAssigneeDto(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("databaseId")] long DatabaseId);
