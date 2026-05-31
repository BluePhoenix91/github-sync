using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GithubSync.Tests.Sync.Ingestion;

// Unit tests covering branching behaviour that does not require unique-constraint enforcement.
// Integration tests in GithubSync.Data.Tests cover NULLS NOT DISTINCT, ON CONFLICT DO NOTHING,
// and EF-tx + raw-SQL enlistment — InMemory does not enforce any of those (see spec for
// full reasoning). Treat these as cheap pre-flight checks.
public class IssueEventPersisterUnitTests
{
    [Fact]
    public async Task Empty_stream_returns_zeroed_PersistResult_with_null_FinalCursor()
    {
        await using var db = NewDb();
        var persister = NewPersister(db);

        var result = await persister.PersistAsync(
            Guid.NewGuid(),
            EmptyStream(),
            CancellationToken.None);

        Assert.Equal(0, result.IssuesCommitted);
        Assert.Equal(0, result.EventsAttempted);
        Assert.Equal(0, result.EventsInserted);
        Assert.Equal(0, result.EventsSkippedUnknownKind);
        Assert.Null(result.FinalCursor);
    }

    [Fact]
    public async Task Malformed_non_edit_with_null_SourceEventId_propagates_InvalidOperationException()
    {
        await using var db = NewDb();
        var persister = NewPersister(db);
        var bad = new GitHubIssueEvent(
            SourceEntityId: "1",
            SourceEventId: null,
            Kind: GitHubEventKind.Closed,
            EventTime: DateTimeOffset.UtcNow,
            IssueUpdatedAt: DateTimeOffset.UtcNow,
            Actor: null,
            PayloadJson: "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persister.PersistAsync(Guid.NewGuid(), AsStream(bad), CancellationToken.None));
    }

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IIssueEventPersister NewPersister(AppDbContext db)
    {
        var resolver = new ActorResolver(
            db,
            Options.Create(new IdentityMappingOptions()),
            NullLogger<ActorResolver>.Instance,
            TimeProvider.System);
        var mapper = new CanonicalEventMapper(
            resolver,
            NullLogger<CanonicalEventMapper>.Instance,
            TimeProvider.System);
        return new IssueEventPersister(db, mapper, NullLogger<IssueEventPersister>.Instance);
    }

    private static async IAsyncEnumerable<GitHubIssueEvent> EmptyStream()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<GitHubIssueEvent> AsStream(params GitHubIssueEvent[] events)
    {
        foreach (var e in events) { yield return e; await Task.Yield(); }
    }
}
