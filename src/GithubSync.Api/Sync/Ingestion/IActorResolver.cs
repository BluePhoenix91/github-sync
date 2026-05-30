using GithubSync.Sources.GitHub;

namespace GithubSync.Api.Sync.Ingestion;

public interface IActorResolver
{
    // Returns the CanonicalActor ID for the given source actor, ensuring the CanonicalActor row
    // and its IdentityMapping (configured or least-loaded fallback) exist within the unit of work.
    // Returns null when the source actor is null (system events with no actor).
    ValueTask<Guid?> ResolveAsync(GitHubActor? actor, CancellationToken ct);
}
