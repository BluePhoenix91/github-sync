using Microsoft.EntityFrameworkCore;

namespace GithubSync.Data.Tests.Sync.Ingestion;

public class FirstRunCursorTests : PostgresPersisterTestBase, IClassFixture<PostgresTestFixture>
{
    public FirstRunCursorTests(PostgresTestFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task First_call_creates_SyncCursor_row_with_IssueUpdatedAt()
    {
        var issueUpdatedAt = new DateTimeOffset(2026, 5, 28, 10, 0, 0, TimeSpan.Zero);
        var ev = GitHubIssueEventBuilder.Build(
            sourceEntityId: "42",
            eventTime: issueUpdatedAt,
            issueUpdatedAt: issueUpdatedAt);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            ConfigId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

        Assert.Equal(1, result.IssuesCommitted);
        Assert.Equal(issueUpdatedAt, result.FinalCursor);

        await using var db = Fixture.CreateContext();
        var cursor = await db.SyncCursors.SingleAsync();
        Assert.Equal(ConfigId, cursor.SyncConfigurationId);
        Assert.Equal(issueUpdatedAt, cursor.LastEventTime);
        Assert.Null(cursor.LastRunStartedAt);
        Assert.Null(cursor.LastRunCompletedAt);
        Assert.Null(cursor.LastRunStatus);
        Assert.Null(cursor.LastRunMessage);
    }
}
