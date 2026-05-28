using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record RateLimitDto(
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("cost")] int Cost,
    [property: JsonPropertyName("resetAt")] DateTimeOffset ResetAt,
    [property: JsonPropertyName("limit")] int Limit);
