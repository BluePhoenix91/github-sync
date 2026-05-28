using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueTypeRefDto([property: JsonPropertyName("name")] string Name);
