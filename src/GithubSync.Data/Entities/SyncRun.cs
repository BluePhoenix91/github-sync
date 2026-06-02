using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class SyncRun
{
    public Guid Id { get; set; }

    public Guid SyncConfigurationId { get; set; }

    public Source Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    public SyncRunStatus Status { get; set; }

    public int IssuesCommitted { get; set; }
    public int EventsAttempted { get; set; }
    public int EventsInserted { get; set; }
    public int EventsSkippedUnknownKind { get; set; }

    public long DurationMs { get; set; }

    public string? Message { get; set; }

    public SyncConfiguration SyncConfiguration { get; set; } = null!;
}
