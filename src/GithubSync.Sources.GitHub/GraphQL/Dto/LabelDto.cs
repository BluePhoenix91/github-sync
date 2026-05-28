using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record LabelDto([property: JsonPropertyName("name")] string Name);
