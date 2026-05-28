using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record IssuesConnection(
    [property: JsonPropertyName("pageInfo")] PageInfoDto PageInfo,
    [property: JsonPropertyName("nodes")] IReadOnlyList<IssueNode> Nodes);
