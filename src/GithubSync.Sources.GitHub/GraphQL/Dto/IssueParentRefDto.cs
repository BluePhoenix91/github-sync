using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssueParentRefDto([property: JsonPropertyName("number")] int Number);
