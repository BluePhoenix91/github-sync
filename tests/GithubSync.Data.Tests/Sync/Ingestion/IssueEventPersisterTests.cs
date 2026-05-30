using System.Runtime.CompilerServices;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;

namespace GithubSync.Data.Tests.Sync.Ingestion;

public class IssueEventPersisterTests : PostgresPersisterTestBase, IClassFixture<PostgresTestFixture>
{
    public IssueEventPersisterTests(PostgresTestFixture fixture) : base(fixture) { }

    [SkippableFact]
    public async Task Test_1_repeat_window_produces_zero_duplicates()
    {
        var ev1 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "e1",
            eventTime: At(2026, 5, 1));
        var ev2 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "e2", kind: GitHubEventKind.Commented,
            eventTime: At(2026, 5, 1, 1));
        var ev3 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "2", sourceEventId: "e3",
            eventTime: At(2026, 5, 2));

        var persister1 = BuildPersister();
        await persister1.PersistAsync(
            ConfigId,
            GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3),
            CancellationToken.None);

        var persister2 = BuildPersister();
        await persister2.PersistAsync(
            ConfigId,
            GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3),
            CancellationToken.None);

        await using var db = Fixture.CreateContext();
        var rowCount = await db.CanonicalEvents.CountAsync();
        Assert.Equal(3, rowCount);
    }

    [SkippableFact]
    public async Task Test_2_cursor_advances_after_each_call_to_max_IssueUpdatedAt()
    {
        var i1u = At(2026, 5, 1);
        var i2u = At(2026, 5, 2);
        var i3u = At(2026, 5, 3);

        var ev1 = GitHubIssueEventBuilder.Build(sourceEntityId: "1", sourceEventId: "e1", eventTime: i1u, issueUpdatedAt: i1u);
        var ev2 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "e2", eventTime: i2u, issueUpdatedAt: i2u);
        var ev3 = GitHubIssueEventBuilder.Build(sourceEntityId: "3", sourceEventId: "e3", eventTime: i3u, issueUpdatedAt: i3u);

        var p1 = BuildPersister();
        var r1 = await p1.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(ev1), CancellationToken.None);
        Assert.Equal(i1u, r1.FinalCursor);
        await AssertCursorAsync(i1u);

        var p2 = BuildPersister();
        var r2 = await p2.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(ev1, ev2), CancellationToken.None);
        Assert.Equal(i2u, r2.FinalCursor);
        // Issue 1 was deduped, issue 2 inserted: 1 + 1 attempted, 0 + 1 inserted.
        Assert.Equal(2, r2.EventsAttempted);
        Assert.Equal(1, r2.EventsInserted);
        await AssertCursorAsync(i2u);

        var p3 = BuildPersister();
        var r3 = await p3.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(ev1, ev2, ev3), CancellationToken.None);
        Assert.Equal(i3u, r3.FinalCursor);
        await AssertCursorAsync(i3u);

        await using var db = Fixture.CreateContext();
        Assert.Equal(3, await db.CanonicalEvents.CountAsync());
    }

    [SkippableFact]
    public async Task Test_7_pre_created_cursor_with_null_LastEventTime_is_advanced_on_first_commit()
    {
        // Orchestrator pre-created a cursor row before any sync ran.
        await using (var seedDb = Fixture.CreateContext())
        {
            seedDb.SyncCursors.Add(new Data.Entities.SyncCursor
            {
                Id = Guid.NewGuid(),
                SyncConfigurationId = ConfigId,
                LastEventTime = null,
            });
            await seedDb.SaveChangesAsync();
        }

        var issueUpdatedAt = At(2026, 5, 15);
        var ev = GitHubIssueEventBuilder.Build(
            sourceEntityId: "9", sourceEventId: "n9",
            eventTime: issueUpdatedAt, issueUpdatedAt: issueUpdatedAt);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            ConfigId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

        Assert.Equal(issueUpdatedAt, result.FinalCursor);
        await AssertCursorAsync(issueUpdatedAt);
    }

    [SkippableFact]
    public async Task Test_6_unknown_kind_events_are_skipped_and_counted()
    {
        var ts = At(2026, 5, 10);
        var unknown = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "u1",
            kind: (GitHubEventKind)999,
            eventTime: ts, issueUpdatedAt: ts);
        var normal = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "n1",
            kind: GitHubEventKind.IssueOpened,
            eventTime: ts, issueUpdatedAt: ts);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            ConfigId, GitHubIssueEventBuilder.AsStream(unknown, normal), CancellationToken.None);

        Assert.Equal(1, result.EventsSkippedUnknownKind);
        Assert.Equal(1, result.EventsAttempted);
        Assert.Equal(1, result.EventsInserted);
        Assert.Equal(1, result.IssuesCommitted);
        Assert.Equal(ts, result.FinalCursor);

        await using var db = Fixture.CreateContext();
        Assert.Equal(1, await db.CanonicalEvents.CountAsync());
    }

    [SkippableFact]
    public async Task Test_4_non_edit_with_null_SourceEventId_halts_the_run()
    {
        var goodTs = At(2026, 5, 1);
        var badTs = At(2026, 5, 2);

        var good = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: "g1",
            kind: GitHubEventKind.IssueOpened,
            eventTime: goodTs, issueUpdatedAt: goodTs);

        // Closed is a mapped, non-edit kind. SourceEventId=null violates the invariant from
        // docs/idempotency.md and the mapper throws InvalidOperationException.
        var bad = GitHubIssueEventBuilder.Build(
            sourceEntityId: "2", sourceEventId: null,
            kind: GitHubEventKind.Closed,
            eventTime: badTs, issueUpdatedAt: badTs);

        var persister = BuildPersister();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persister.PersistAsync(
                ConfigId,
                GitHubIssueEventBuilder.AsStream(good, bad),
                CancellationToken.None));

        await using var db = Fixture.CreateContext();
        // Issue 1 (good) was committed before issue 2 (bad) failed. Issue 2 rolled back.
        Assert.Equal(1, await db.CanonicalEvents.CountAsync());
        Assert.Equal("1", (await db.CanonicalEvents.SingleAsync()).SourceEntityId);

        var cursor = await db.SyncCursors.SingleAsync();
        Assert.Equal(goodTs, cursor.LastEventTime);
    }

    [SkippableFact]
    public async Task Test_3_cancellation_mid_issue_2_then_resume_via_dedup_produces_clean_state()
    {
        var i1u = At(2026, 5, 1);
        var i2u = At(2026, 5, 2);
        var i3u = At(2026, 5, 3);

        var i1e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "1", sourceEventId: "i1e1", eventTime: i1u, issueUpdatedAt: i1u);
        var i2e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "i2e1", eventTime: i2u, issueUpdatedAt: i2u);
        var i2e2 = GitHubIssueEventBuilder.Build(sourceEntityId: "2", sourceEventId: "i2e2", kind: GitHubEventKind.Commented, eventTime: i2u.AddSeconds(1), issueUpdatedAt: i2u);
        var i3e1 = GitHubIssueEventBuilder.Build(sourceEntityId: "3", sourceEventId: "i3e1", eventTime: i3u, issueUpdatedAt: i3u);

        var cts = new CancellationTokenSource();

        // Wrapper stream that cancels the token after yielding the first event of issue 2.
        static async IAsyncEnumerable<GitHubIssueEvent> CancellingStream(
            CancellationTokenSource cts,
            GitHubIssueEvent[] events,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            bool seenIssue2 = false;
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
                await Task.Yield();
                if (!seenIssue2 && e.SourceEntityId == "2")
                {
                    seenIssue2 = true;
                    cts.Cancel();
                }
            }
        }

        var persister1 = BuildPersister();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            persister1.PersistAsync(
                ConfigId,
                CancellingStream(cts, new[] { i1e1, i2e1, i2e2, i3e1 }, cts.Token),
                cts.Token));

        // After cancellation: issue 1 committed, issue 2 rolled back, cursor at i1u.
        await using (var db = Fixture.CreateContext())
        {
            Assert.Equal(1, await db.CanonicalEvents.CountAsync());
            Assert.Equal("1", (await db.CanonicalEvents.SingleAsync()).SourceEntityId);
            Assert.Equal(i1u, (await db.SyncCursors.SingleAsync()).LastEventTime);
        }

        // Resume: feed the same full stream again (the persister does not read the cursor — see
        // docs/superpowers/specs/2026-05-30-issue-event-persister-design.md, test 3 framing).
        var persister2 = BuildPersister();
        var result = await persister2.PersistAsync(
            ConfigId,
            GitHubIssueEventBuilder.AsStream(i1e1, i2e1, i2e2, i3e1),
            CancellationToken.None);

        Assert.Equal(3, result.IssuesCommitted);
        Assert.Equal(i3u, result.FinalCursor);

        await using (var db = Fixture.CreateContext())
        {
            Assert.Equal(4, await db.CanonicalEvents.CountAsync());
            Assert.Equal(i3u, (await db.SyncCursors.SingleAsync()).LastEventTime);
        }
    }

    [SkippableFact]
    public async Task Test_8_overlapping_window_tail_re_ingest_is_idempotent()
    {
        // First stream covers T1..T10 across 10 issues.
        var first = Enumerable.Range(1, 10).Select(i =>
            GitHubIssueEventBuilder.Build(
                sourceEntityId: i.ToString(),
                sourceEventId: $"e{i}",
                eventTime: At(2026, 5, i),
                issueUpdatedAt: At(2026, 5, i))).ToArray();

        // Second stream covers T5..T15 — overlaps the tail of the first window.
        var second = Enumerable.Range(5, 11).Select(i =>
            GitHubIssueEventBuilder.Build(
                sourceEntityId: i.ToString(),
                sourceEventId: $"e{i}",
                eventTime: At(2026, 5, i),
                issueUpdatedAt: At(2026, 5, i))).ToArray();

        var p1 = BuildPersister();
        await p1.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(first), CancellationToken.None);

        var p2 = BuildPersister();
        var r2 = await p2.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(second), CancellationToken.None);

        await using var db = Fixture.CreateContext();
        // 1..15 distinct issues, one event each.
        Assert.Equal(15, await db.CanonicalEvents.CountAsync());
        // T5..T10 were re-attempted but deduped.
        Assert.Equal(11, r2.EventsAttempted);
        Assert.Equal(5, r2.EventsInserted);
        Assert.Equal(At(2026, 5, 15), (await db.SyncCursors.SingleAsync()).LastEventTime);
    }

    [SkippableFact]
    public async Task Test_9_null_SourceEventId_dedup_via_NULLS_NOT_DISTINCT()
    {
        var ts = At(2026, 5, 10);
        var edit1 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: null,
            kind: GitHubEventKind.BodyEdited,
            eventTime: ts, issueUpdatedAt: ts);

        var edit2 = GitHubIssueEventBuilder.Build(
            sourceEntityId: "1", sourceEventId: null,
            kind: GitHubEventKind.BodyEdited,
            eventTime: ts, issueUpdatedAt: ts);

        var persister = BuildPersister();
        var result = await persister.PersistAsync(
            ConfigId,
            GitHubIssueEventBuilder.AsStream(edit1, edit2),
            CancellationToken.None);

        Assert.Equal(2, result.EventsAttempted);
        Assert.Equal(1, result.EventsInserted);

        await using var db = Fixture.CreateContext();
        Assert.Equal(1, await db.CanonicalEvents.CountAsync());
    }

    [SkippableFact]
    public async Task Test_10_concurrent_insert_of_same_event_absorbed_cleanly_on_loser_side()
    {
        var ts = At(2026, 5, 12);
        var ev = GitHubIssueEventBuilder.Build(
            sourceEntityId: "42", sourceEventId: "shared",
            eventTime: ts, issueUpdatedAt: ts);

        var p1 = BuildPersister();
        var p2 = BuildPersister();

        var task1 = p1.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);
        var task2 = p2.PersistAsync(ConfigId, GitHubIssueEventBuilder.AsStream(ev), CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        // Both calls complete without exceptions.
        Assert.Equal(2, results.Length);
        // Together they attempted 2 inserts; one was the winner, one was absorbed by ON CONFLICT.
        Assert.Equal(2, results.Sum(r => r.EventsAttempted));
        Assert.Equal(1, results.Sum(r => r.EventsInserted));

        await using var db = Fixture.CreateContext();
        Assert.Equal(1, await db.CanonicalEvents.CountAsync());
    }
}
