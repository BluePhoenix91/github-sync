namespace GithubSync.Sources.GitHub;

public sealed record GitHubActor(
    string Login,         // GitHub login at observation time — can change; do not use as a join key.
    string DatabaseId,    // GitHub numeric ID, string-encoded; the stable join key (matches CanonicalActor.SourceActorId).
    GitHubActorKind Kind);
