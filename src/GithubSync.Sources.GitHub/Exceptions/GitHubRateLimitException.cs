namespace GithubSync.Sources.GitHub.Exceptions;

// Thrown when the one-shot rate-limit retry (secondary Retry-After or primary X-RateLimit-*) still returns 403.
public sealed class GitHubRateLimitException(string message) : Exception(message);
