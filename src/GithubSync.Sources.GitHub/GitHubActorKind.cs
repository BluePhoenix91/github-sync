namespace GithubSync.Sources.GitHub;

// Derived from GraphQL __typename on actor selections. Any unrecognised value maps to Other.
public enum GitHubActorKind
{
    User = 1,
    Bot = 2,
    Mannequin = 3,
    Other = 4,
}
