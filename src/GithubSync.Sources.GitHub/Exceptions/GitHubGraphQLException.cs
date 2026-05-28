namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown on 200 OK with non-empty `errors` array in the body — schema drift, malformed query, semantic error.
public sealed class GitHubGraphQLException : Exception
{
    public IReadOnlyList<string> ErrorMessages { get; }

    public GitHubGraphQLException(IReadOnlyList<string> errorMessages)
        : base("GitHub GraphQL response contained errors: " + string.Join("; ", errorMessages))
    {
        ErrorMessages = errorMessages;
    }
}
