namespace GithubSync.Sources.GitHub.GraphQL;

internal static class IssuesPageQuery
{
    // Outer query — one page of issues with first 100 of each nested connection.
    // Variables: $owner (String!), $repo (String!), $since (DateTime), $cursor (String).
    public const string Outer = """
        query IssuesPage($owner: String!, $repo: String!, $since: DateTime, $cursor: String) {
          repository(owner: $owner, name: $repo) {
            issues(first: 100, after: $cursor, filterBy: { since: $since },
                   orderBy: { field: UPDATED_AT, direction: ASC }) {
              pageInfo { endCursor hasNextPage }
              nodes {
                id number databaseId createdAt updatedAt title body
                author { login databaseId __typename }
                userContentEdits(first: 100) {
                  pageInfo { endCursor hasNextPage }
                  nodes { id editedAt diff editor { login databaseId __typename } }
                }
                comments(first: 100) {
                  pageInfo { endCursor hasNextPage }
                  nodes { id databaseId createdAt body author { login databaseId __typename } }
                }
                timelineItems(first: 100, itemTypes: [
                  LABELED_EVENT, UNLABELED_EVENT, ASSIGNED_EVENT, UNASSIGNED_EVENT,
                  CLOSED_EVENT, REOPENED_EVENT, TYPED_EVENT, UNTYPED_EVENT,
                  PARENT_ISSUE_ADDED_EVENT, PARENT_ISSUE_REMOVED_EVENT
                ]) {
                  pageInfo { endCursor hasNextPage }
                  nodes {
                    __typename
                    ... on LabeledEvent   { id createdAt actor { login databaseId __typename } label { name } }
                    ... on UnlabeledEvent { id createdAt actor { login databaseId __typename } label { name } }
                    ... on AssignedEvent   { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                    ... on UnassignedEvent { id createdAt actor { login databaseId __typename } assignee { ... on User { login databaseId } } }
                    ... on ClosedEvent     { id createdAt actor { login databaseId __typename } }
                    ... on ReopenedEvent   { id createdAt actor { login databaseId __typename } }
                    ... on TypedEvent      { id createdAt actor { login databaseId __typename } issueType { name } }
                    ... on UntypedEvent    { id createdAt actor { login databaseId __typename } prevIssueType { name } }
                    ... on ParentIssueAddedEvent   { id createdAt actor { login databaseId __typename } parent { number } }
                    ... on ParentIssueRemovedEvent { id createdAt actor { login databaseId __typename } parent { number } }
                  }
                }
              }
            }
          }
          rateLimit { remaining cost resetAt limit }
        }
        """;
}
