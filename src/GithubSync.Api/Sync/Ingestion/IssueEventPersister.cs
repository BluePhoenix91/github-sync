using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace GithubSync.Api.Sync.Ingestion;

public class IssueEventPersister(
    AppDbContext db,
    ICanonicalEventMapper mapper,
    ILogger<IssueEventPersister> logger) : IIssueEventPersister
{
    public async Task<PersistResult> PersistAsync(
        Guid syncConfigurationId,
        IAsyncEnumerable<GitHubIssueEvent> source,
        CancellationToken ct)
    {
        var stats = new RunStats();

        // Group by contiguous SourceEntityId. The fetcher contract (#11) guarantees an issue's
        // events are emitted as one contiguous block, in non-decreasing IssueUpdatedAt order
        // across issues. We trust that ordering; see docs/superpowers/specs/2026-05-30-issue-event-persister-design.md#stream-contract.
        string? currentIssueId = null;
        var buffer = new List<GitHubIssueEvent>(16);

        await foreach (var ev in source.WithCancellation(ct))
        {
            if (currentIssueId is not null && ev.SourceEntityId != currentIssueId)
            {
                await CommitIssueAsync(syncConfigurationId, currentIssueId, buffer, stats, ct);
                buffer.Clear();
            }
            currentIssueId = ev.SourceEntityId;
            buffer.Add(ev);
        }

        if (currentIssueId is not null)
        {
            await CommitIssueAsync(syncConfigurationId, currentIssueId, buffer, stats, ct);
        }

        return new PersistResult(
            IssuesCommitted: stats.IssuesCommitted,
            EventsAttempted: stats.EventsAttempted,
            EventsInserted: stats.EventsInserted,
            EventsSkippedUnknownKind: stats.EventsSkippedUnknownKind,
            FinalCursor: stats.FinalCursor);
    }

    private async Task CommitIssueAsync(
        Guid syncConfigurationId,
        string sourceEntityId,
        IReadOnlyList<GitHubIssueEvent> buffered,
        RunStats stats,
        CancellationToken ct)
    {
        var issueUpdatedAt = buffered[0].IssueUpdatedAt;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // TODO Task 6: map source events and INSERT them via raw SQL ON CONFLICT DO NOTHING.
        // For now we only advance the cursor so test 5 passes.

        await UpsertCursorAsync(syncConfigurationId, issueUpdatedAt, ct);

        await tx.CommitAsync(ct);

        stats.IssuesCommitted++;
        stats.FinalCursor = stats.FinalCursor is null
            ? issueUpdatedAt
            : (issueUpdatedAt > stats.FinalCursor ? issueUpdatedAt : stats.FinalCursor);

        logger.LogInformation(
            "Issue commit {ConfigId} {SourceEntityId} {EventsAttempted} {EventsInserted} {CursorAdvancedTo}",
            syncConfigurationId, sourceEntityId, 0, 0, issueUpdatedAt);
    }

    private async Task UpsertCursorAsync(
        Guid syncConfigurationId, DateTimeOffset issueUpdatedAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO "SyncCursors" ("Id", "SyncConfigurationId", "LastEventTime")
            VALUES (@id, @configId, @issueUpdatedAt)
            ON CONFLICT ("SyncConfigurationId") DO UPDATE SET
              "LastEventTime" = GREATEST(
                EXCLUDED."LastEventTime",
                COALESCE("SyncCursors"."LastEventTime", EXCLUDED."LastEventTime"))
            """;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var efTx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("UpsertCursorAsync must run inside an EF transaction.");
        var tx = (NpgsqlTransaction)efTx.GetDbTransaction();

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter("@configId", NpgsqlDbType.Uuid) { Value = syncConfigurationId });
        cmd.Parameters.Add(new NpgsqlParameter("@issueUpdatedAt", NpgsqlDbType.TimestampTz) { Value = issueUpdatedAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class RunStats
    {
        public int IssuesCommitted;
        public int EventsAttempted;
        public int EventsInserted;
        public int EventsSkippedUnknownKind;
        public DateTimeOffset? FinalCursor;
    }
}
