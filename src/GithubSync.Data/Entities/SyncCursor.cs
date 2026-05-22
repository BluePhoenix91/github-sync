using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class SyncCursor
{
    public Guid Id { get; set; }
    public Guid SyncConfigurationId { get; set; }

    public DateTimeOffset? LastEventTime { get; set; }
    public string? LastETag { get; set; }

    public DateTimeOffset? LastRunStartedAt { get; set; }
    public DateTimeOffset? LastRunCompletedAt { get; set; }
    public SyncRunStatus? LastRunStatus { get; set; }
    public string? LastRunMessage { get; set; }

    public SyncConfiguration SyncConfiguration { get; set; } = null!;
}
