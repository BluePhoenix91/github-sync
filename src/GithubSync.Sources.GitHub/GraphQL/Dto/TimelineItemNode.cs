using System.Text.Json.Serialization;

namespace GithubSync.Sources.GitHub.GraphQL.Dto;

internal sealed record TimelineItemNode(
    [property: JsonPropertyName("__typename")] string TypeName,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("actor")] ActorDto? Actor,
    [property: JsonPropertyName("label")] LabelDto? Label,
    [property: JsonPropertyName("assignee")] UserAssigneeDto? Assignee,
    [property: JsonPropertyName("issueType")] IssueTypeRefDto? IssueType,
    [property: JsonPropertyName("prevIssueType")] IssueTypeRefDto? PrevIssueType,
    [property: JsonPropertyName("parent")] IssueParentRefDto? Parent);
