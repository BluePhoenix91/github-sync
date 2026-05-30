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

        var canonical = new List<Data.Entities.CanonicalEvent>(buffered.Count);
        foreach (var ev in buffered)
        {
            var mapped = await mapper.MapAsync(ev, syncConfigurationId, ct);
            if (mapped is null)
            {
                stats.EventsSkippedUnknownKind++;
                continue;
            }
            canonical.Add(mapped);
        }

        await db.SaveChangesAsync(ct);

        int inserted = 0;
        if (canonical.Count > 0)
        {
            inserted = await BulkInsertEventsAsync(canonical, ct);
            stats.EventsAttempted += canonical.Count;
            stats.EventsInserted += inserted;
        }

        await UpsertCursorAsync(syncConfigurationId, issueUpdatedAt, ct);
        await tx.CommitAsync(ct);

        stats.IssuesCommitted++;
        stats.FinalCursor = stats.FinalCursor is null
            ? issueUpdatedAt
            : (issueUpdatedAt > stats.FinalCursor ? issueUpdatedAt : stats.FinalCursor);

        logger.LogInformation(
            "Issue commit {ConfigId} {SourceEntityId} {EventsAttempted} {EventsInserted} {CursorAdvancedTo}",
            syncConfigurationId, sourceEntityId, canonical.Count, inserted, issueUpdatedAt);
    }

    private async Task<int> BulkInsertEventsAsync(
        IReadOnlyList<Data.Entities.CanonicalEvent> events,
        CancellationToken ct)
    {
        // Parameterised multi-row INSERT with ON CONFLICT (column-list) DO NOTHING.
        // ON CONFLICT ON CONSTRAINT is not used because the unique constraint was created by
        // CREATE UNIQUE INDEX (not ALTER TABLE ADD CONSTRAINT) — Postgres requires column-list
        // inference for that case. With NULLS NOT DISTINCT on the index, the column-list form
        // still matches.
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            INSERT INTO "CanonicalEvents" (
              "Id", "SyncConfigurationId", "Source", "SourceEntityType",
              "SourceEntityId", "SourceEventId", "EventKind", "EventTime",
              "ActorId", "PayloadJson", "IngestedAt")
            VALUES
            """);

        var parameters = new List<NpgsqlParameter>(events.Count * 11);
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($" (@id{i}, @cfg{i}, @src{i}, @set{i}, @sei{i}, @sev{i}, @ek{i}, @et{i}, @aid{i}, @pj{i}, @ia{i})");

            var e = events[i];
            parameters.Add(new NpgsqlParameter($"@id{i}", NpgsqlDbType.Uuid) { Value = e.Id });
            parameters.Add(new NpgsqlParameter($"@cfg{i}", NpgsqlDbType.Uuid) { Value = e.SyncConfigurationId });
            parameters.Add(new NpgsqlParameter($"@src{i}", NpgsqlDbType.Integer) { Value = (int)e.Source });
            parameters.Add(new NpgsqlParameter($"@set{i}", NpgsqlDbType.Integer) { Value = (int)e.SourceEntityType });
            parameters.Add(new NpgsqlParameter($"@sei{i}", NpgsqlDbType.Text) { Value = e.SourceEntityId });
            parameters.Add(new NpgsqlParameter($"@sev{i}", NpgsqlDbType.Text) { Value = (object?)e.SourceEventId ?? DBNull.Value });
            parameters.Add(new NpgsqlParameter($"@ek{i}", NpgsqlDbType.Integer) { Value = (int)e.EventKind });
            parameters.Add(new NpgsqlParameter($"@et{i}", NpgsqlDbType.TimestampTz) { Value = e.EventTime });
            parameters.Add(new NpgsqlParameter($"@aid{i}", NpgsqlDbType.Uuid) { Value = (object?)e.ActorId ?? DBNull.Value });
            parameters.Add(new NpgsqlParameter($"@pj{i}", NpgsqlDbType.Jsonb) { Value = e.PayloadJson });
            parameters.Add(new NpgsqlParameter($"@ia{i}", NpgsqlDbType.TimestampTz) { Value = e.IngestedAt });
        }

        sb.AppendLine();
        sb.Append("""
            ON CONFLICT ("Source", "SourceEntityType", "SourceEntityId", "EventKind", "EventTime", "SourceEventId") DO NOTHING
            """);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var efTx = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("BulkInsertEventsAsync must run inside an EF transaction.");
        var tx = (NpgsqlTransaction)efTx.GetDbTransaction();

        await using var cmd = new NpgsqlCommand(sb.ToString(), connection, tx);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        return await cmd.ExecuteNonQueryAsync(ct);
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
