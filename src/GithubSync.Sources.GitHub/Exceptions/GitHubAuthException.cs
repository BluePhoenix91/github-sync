namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown on 401, or 403 without any rate-limit header signal.
// Token invalid, missing scopes, repo not accessible.
public sealed class GitHubAuthException(string message) : Exception(message);
