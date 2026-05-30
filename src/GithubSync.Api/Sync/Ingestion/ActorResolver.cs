using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GithubSync.Api.Sync.Ingestion;

// Resolves source actors to CanonicalActor IDs and ensures an IdentityMapping per
// (CanonicalActor, TargetSystem). For unmapped actors the precedence is:
//   1. existing IdentityMapping row wins (insert-once per docs/idempotency.md);
//   2. configured GitHub-login → ADO-user mapping (case-insensitive);
//   3. least-loaded selection over enabled TargetUsers — count is queried at decision
//      time so assignments self-correct (CLAUDE.md identity-mapping rule).
//
// Persists changes via the injected DbContext's ChangeTracker; callers own SaveChangesAsync.
public class ActorResolver(
    AppDbContext db,
    IOptions<IdentityMappingOptions> options,
    ILogger<ActorResolver> logger,
    TimeProvider timeProvider) : IActorResolver
{
    // Per-instance caches: a sync run resolves the same actor across many events. The cache
    // both avoids redundant DB round-trips and prevents re-adding entities already tracked
    // by EF in the current unit of work.
    private readonly Dictionary<string, CanonicalActor> actorCache = new();
    private readonly HashSet<Guid> resolvedMappings = new();

    public async ValueTask<Guid?> ResolveAsync(GitHubActor? actor, CancellationToken ct)
    {
        if (actor is null) return null;

        var now = timeProvider.GetUtcNow();
        var canonicalActor = await GetOrCreateActorAsync(actor, now, ct);
        await EnsureIdentityMappingAsync(canonicalActor, actor, now, ct);
        return canonicalActor.Id;
    }

    private async Task<CanonicalActor> GetOrCreateActorAsync(
        GitHubActor actor, DateTimeOffset now, CancellationToken ct)
    {
        if (actorCache.TryGetValue(actor.DatabaseId, out var cached))
        {
            cached.SourceActorLogin = actor.Login;
            cached.LastSeenAt = now;
            return cached;
        }

        // CanonicalActor.DisplayName is intentionally not refreshed here even though
        // docs/idempotency.md lists it as a per-sight field. The source-side GitHubActor DTO
        // doesn't carry a display name today; revisit once the fetcher surfaces it.
        var existing = await db.CanonicalActors
            .FirstOrDefaultAsync(a => a.Source == Source.GitHub && a.SourceActorId == actor.DatabaseId, ct);
        if (existing is not null)
        {
            existing.SourceActorLogin = actor.Login;
            existing.LastSeenAt = now;
            actorCache[actor.DatabaseId] = existing;
            return existing;
        }

        var created = new CanonicalActor
        {
            Id = Guid.NewGuid(),
            Source = Source.GitHub,
            SourceActorId = actor.DatabaseId,
            SourceActorLogin = actor.Login,
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        db.CanonicalActors.Add(created);
        actorCache[actor.DatabaseId] = created;
        return created;
    }

    private async Task EnsureIdentityMappingAsync(
        CanonicalActor canonicalActor, GitHubActor sourceActor, DateTimeOffset now, CancellationToken ct)
    {
        if (!resolvedMappings.Add(canonicalActor.Id)) return;

        var existing = await db.IdentityMappings.AnyAsync(
            m => m.CanonicalActorId == canonicalActor.Id && m.TargetSystem == TargetSystem.AzureDevOps, ct);
        if (existing) return;

        var configured = options.Value.Mappings.FirstOrDefault(
            m => string.Equals(m.GitHubLogin, sourceActor.Login, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            // Catch configured-mapping typos at first sight of the affected actor rather than
            // letting an unresolvable TargetUserId reach the exporter. Throwing here halts the
            // sync run loudly so the operator fixes config before any data lands; falling
            // through to least-loaded would silently rebind the actor and lock the mistake in
            // (IdentityMapping is insert-once). When the sync orchestrator (#14/#15) exists,
            // this check should move to start-of-run for earlier failure.
            var exists = await db.TargetUsers.AnyAsync(
                u => u.TargetSystem == TargetSystem.AzureDevOps && u.TargetUserId == configured.TargetUserId, ct);
            if (!exists)
            {
                throw new InvalidOperationException(
                    $"Configured IdentityMapping for GitHub login '{configured.GitHubLogin}' references " +
                    $"TargetUserId '{configured.TargetUserId}', which is not present in the TargetUsers table. " +
                    "Add the TargetUser row or correct the configuration.");
            }

            AddMapping(canonicalActor.Id, configured.TargetUserId, configured.TargetUserDisplayName, MappingSource.Configured, now);
            return;
        }

        var pick = await SelectLeastLoadedAsync(ct);
        if (pick is null)
        {
            logger.LogWarning(
                "No enabled TargetUser for least-loaded fallback {Source} {ExternalId} {Reason}",
                "github", sourceActor.DatabaseId, "empty or all-disabled TargetUser pool");
            return;
        }

        AddMapping(canonicalActor.Id, pick.TargetUserId, pick.TargetUserDisplayName, MappingSource.LeastLoadedFallback, now);
    }

    private void AddMapping(
        Guid canonicalActorId, string targetUserId, string displayName, MappingSource source, DateTimeOffset now)
    {
        db.IdentityMappings.Add(new IdentityMapping
        {
            Id = Guid.NewGuid(),
            CanonicalActorId = canonicalActorId,
            TargetSystem = TargetSystem.AzureDevOps,
            TargetUserId = targetUserId,
            TargetUserDisplayName = displayName,
            MappingSource = source,
            CreatedAt = now,
        });
    }

    private Task<TargetUser?> SelectLeastLoadedAsync(CancellationToken ct) =>
        (from u in db.TargetUsers
         where u.Enabled && u.TargetSystem == TargetSystem.AzureDevOps
         let count = db.IdentityMappings.Count(
             m => m.TargetSystem == TargetSystem.AzureDevOps && m.TargetUserId == u.TargetUserId)
         orderby count, u.TargetUserId
         select u)
        .FirstOrDefaultAsync(ct);
}
