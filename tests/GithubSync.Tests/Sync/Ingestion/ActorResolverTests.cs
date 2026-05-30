using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests.Sync.Ingestion;

// Unit tests for ActorResolver use the EF Core InMemory provider to cover branching:
// existing/new actor, configured/least-loaded fallback, login refresh, empty pool.
// Concurrency and unique-constraint behaviour is verified against real PostgreSQL by #13.
public class ActorResolverTests
{
    [Fact]
    public async Task Null_actor_returns_null_and_writes_nothing()
    {
        await using var db = NewDb();
        var resolver = NewResolver(db);

        var result = await resolver.ResolveAsync(actor: null, default);
        await db.SaveChangesAsync();

        Assert.Null(result);
        Assert.Empty(db.CanonicalActors);
        Assert.Empty(db.IdentityMappings);
    }

    [Fact]
    public async Task New_actor_with_configured_mapping_creates_actor_and_configured_identity_mapping()
    {
        await using var db = NewDb();
        db.TargetUsers.Add(NewTargetUser("octo@ado", "Octo Cat"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db, configured: OptionsWith(
            new ConfiguredIdentityMapping { GitHubLogin = "octocat", TargetUserId = "octo@ado", TargetUserDisplayName = "Octo Cat" }));
        var actor = new GitHubActor("octocat", "1", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        Assert.NotNull(actorId);
        var savedActor = Assert.Single(db.CanonicalActors);
        Assert.Equal(actorId, savedActor.Id);
        Assert.Equal("1", savedActor.SourceActorId);
        Assert.Equal("octocat", savedActor.SourceActorLogin);

        var mapping = Assert.Single(db.IdentityMappings);
        Assert.Equal(savedActor.Id, mapping.CanonicalActorId);
        Assert.Equal(MappingSource.Configured, mapping.MappingSource);
        Assert.Equal("octo@ado", mapping.TargetUserId);
        Assert.Equal("Octo Cat", mapping.TargetUserDisplayName);
    }

    [Fact]
    public async Task Configured_mapping_referencing_unknown_TargetUserId_throws()
    {
        await using var db = NewDb();
        // No TargetUsers seeded — the configured TargetUserId "ghost@ado" is not in the pool.
        var resolver = NewResolver(db, configured: OptionsWith(
            new ConfiguredIdentityMapping { GitHubLogin = "octocat", TargetUserId = "ghost@ado", TargetUserDisplayName = "Ghost" }));
        var actor = new GitHubActor("octocat", "1", GitHubActorKind.User);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await resolver.ResolveAsync(actor, default));

        Assert.Contains("octocat", ex.Message);
        Assert.Contains("ghost@ado", ex.Message);
    }

    [Fact]
    public async Task Configured_mapping_accepts_disabled_TargetUser_when_present_in_pool()
    {
        // Configured mapping is admin intent — disabled status doesn't block it (disabled is
        // honoured by least-loaded selection, not by configured assignment).
        await using var db = NewDb();
        db.TargetUsers.Add(NewTargetUser("octo@ado", "Octo Cat", enabled: false));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db, configured: OptionsWith(
            new ConfiguredIdentityMapping { GitHubLogin = "octocat", TargetUserId = "octo@ado", TargetUserDisplayName = "Octo Cat" }));
        var actor = new GitHubActor("octocat", "1", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        Assert.NotNull(actorId);
        var mapping = Assert.Single(db.IdentityMappings);
        Assert.Equal("octo@ado", mapping.TargetUserId);
        Assert.Equal(MappingSource.Configured, mapping.MappingSource);
    }

    [Fact]
    public async Task New_actor_without_configured_mapping_falls_back_to_least_loaded_target_user()
    {
        await using var db = NewDb();
        db.TargetUsers.AddRange(
            NewTargetUser("alice@ado", "Alice"),
            NewTargetUser("bob@ado", "Bob"));

        // Two existing actors already mapped to alice — bob has zero, so bob should win.
        var existingActorA = NewActor("10", "a");
        var existingActorB = NewActor("11", "b");
        db.CanonicalActors.AddRange(existingActorA, existingActorB);
        db.IdentityMappings.AddRange(
            NewMapping(existingActorA.Id, "alice@ado", "Alice"),
            NewMapping(existingActorB.Id, "alice@ado", "Alice"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);
        var actor = new GitHubActor("stranger", "99", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        Assert.NotNull(actorId);
        var newMapping = Assert.Single(db.IdentityMappings.Where(m => m.CanonicalActorId == actorId));
        Assert.Equal("bob@ado", newMapping.TargetUserId);
        Assert.Equal(MappingSource.LeastLoadedFallback, newMapping.MappingSource);
    }

    [Fact]
    public async Task Least_loaded_selection_skips_disabled_target_users()
    {
        await using var db = NewDb();
        db.TargetUsers.AddRange(
            NewTargetUser("disabled@ado", "Disabled", enabled: false),
            NewTargetUser("enabled@ado", "Enabled"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);
        var actor = new GitHubActor("anon", "7", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        var mapping = Assert.Single(db.IdentityMappings);
        Assert.Equal("enabled@ado", mapping.TargetUserId);
    }

    [Fact]
    public async Task Empty_target_user_pool_creates_actor_but_no_identity_mapping_and_warns()
    {
        await using var db = NewDb();
        var sink = new CapturingSink();
        var resolver = NewResolver(db, logger: BuildLogger<ActorResolver>(sink));
        var actor = new GitHubActor("nobody", "5", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        Assert.NotNull(actorId);
        Assert.Single(db.CanonicalActors);
        Assert.Empty(db.IdentityMappings);
        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public async Task Existing_actor_with_login_change_refreshes_login_and_last_seen()
    {
        await using var db = NewDb();
        var initialSeen = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var firstSeen = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        db.CanonicalActors.Add(new CanonicalActor
        {
            Id = Guid.NewGuid(), Source = Source.GitHub, SourceActorId = "42",
            SourceActorLogin = "oldlogin", FirstSeenAt = firstSeen, LastSeenAt = initialSeen,
        });
        await db.SaveChangesAsync();

        var laterNow = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(laterNow);
        var resolver = NewResolver(db, time: time);

        var actorId = await resolver.ResolveAsync(new GitHubActor("newlogin", "42", GitHubActorKind.User), default);
        await db.SaveChangesAsync();

        var stored = Assert.Single(db.CanonicalActors);
        Assert.Equal("newlogin", stored.SourceActorLogin);
        Assert.Equal(laterNow, stored.LastSeenAt);
        Assert.Equal(firstSeen, stored.FirstSeenAt);
        Assert.Equal(stored.Id, actorId);
    }

    [Fact]
    public async Task Existing_identity_mapping_is_reused_not_replaced()
    {
        await using var db = NewDb();
        var actor = NewActor("20", "u");
        var mapping = NewMapping(actor.Id, "preferred@ado", "Preferred", MappingSource.Configured);
        db.CanonicalActors.Add(actor);
        db.IdentityMappings.Add(mapping);
        await db.SaveChangesAsync();

        // Even though configured mappings now say something different, the existing row wins.
        var resolver = NewResolver(db, configured: OptionsWith(
            new ConfiguredIdentityMapping { GitHubLogin = "u", TargetUserId = "different@ado", TargetUserDisplayName = "Different" }));

        var actorId = await resolver.ResolveAsync(new GitHubActor("u", "20", GitHubActorKind.User), default);
        await db.SaveChangesAsync();

        Assert.Equal(actor.Id, actorId);
        var stored = Assert.Single(db.IdentityMappings);
        Assert.Equal("preferred@ado", stored.TargetUserId);
        Assert.Equal(MappingSource.Configured, stored.MappingSource);
    }

    [Fact]
    public async Task Repeated_resolve_in_same_unit_of_work_creates_only_one_actor_and_mapping()
    {
        await using var db = NewDb();
        db.TargetUsers.Add(NewTargetUser("only@ado", "Only"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db);
        var actor = new GitHubActor("dup", "8", GitHubActorKind.User);

        var first = await resolver.ResolveAsync(actor, default);
        var second = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        Assert.Equal(first, second);
        Assert.Single(db.CanonicalActors);
        Assert.Single(db.IdentityMappings);
    }

    [Fact]
    public async Task Configured_lookup_is_case_insensitive_on_GitHub_login()
    {
        await using var db = NewDb();
        db.TargetUsers.Add(NewTargetUser("octo@ado", "Octo"));
        await db.SaveChangesAsync();

        var resolver = NewResolver(db, configured: OptionsWith(
            new ConfiguredIdentityMapping { GitHubLogin = "Octocat", TargetUserId = "octo@ado", TargetUserDisplayName = "Octo" }));
        var actor = new GitHubActor("OCTOCAT", "1", GitHubActorKind.User);

        var actorId = await resolver.ResolveAsync(actor, default);
        await db.SaveChangesAsync();

        var mapping = Assert.Single(db.IdentityMappings);
        Assert.Equal("octo@ado", mapping.TargetUserId);
        Assert.Equal(MappingSource.Configured, mapping.MappingSource);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"actor-resolver-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static ActorResolver NewResolver(
        AppDbContext db,
        IdentityMappingOptions? configured = null,
        TimeProvider? time = null,
        ILogger<ActorResolver>? logger = null) =>
        new(db,
            Options.Create(configured ?? new IdentityMappingOptions()),
            logger ?? NullLogger<ActorResolver>.Instance,
            time ?? TimeProvider.System);

    private static IdentityMappingOptions OptionsWith(params ConfiguredIdentityMapping[] mappings) =>
        new() { Mappings = mappings.ToList() };

    private static ILogger<T> BuildLogger<T>(CapturingSink sink)
    {
        var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true).CreateLogger<T>();
    }

    private static TargetUser NewTargetUser(string id, string name, bool enabled = true) =>
        new()
        {
            Id = Guid.NewGuid(), TargetSystem = TargetSystem.AzureDevOps, TargetUserId = id,
            TargetUserDisplayName = name, Enabled = enabled, CreatedAt = DateTimeOffset.UtcNow,
        };

    private static CanonicalActor NewActor(string sourceActorId, string login) =>
        new()
        {
            Id = Guid.NewGuid(), Source = Source.GitHub, SourceActorId = sourceActorId,
            SourceActorLogin = login, FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        };

    private static IdentityMapping NewMapping(
        Guid canonicalActorId, string targetUserId, string displayName,
        MappingSource mappingSource = MappingSource.LeastLoadedFallback) =>
        new()
        {
            Id = Guid.NewGuid(), CanonicalActorId = canonicalActorId, TargetSystem = TargetSystem.AzureDevOps,
            TargetUserId = targetUserId, TargetUserDisplayName = displayName,
            MappingSource = mappingSource, CreatedAt = DateTimeOffset.UtcNow,
        };

}
