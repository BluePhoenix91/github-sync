using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssuesPageResponse(
    [property: JsonPropertyName("data")] IssuesPageData? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQLErrorDto>? Errors);

internal sealed record IssuesPageData(
    [property: JsonPropertyName("repository")] RepositoryDto? Repository,
    [property: JsonPropertyName("rateLimit")] RateLimitDto? RateLimit);
