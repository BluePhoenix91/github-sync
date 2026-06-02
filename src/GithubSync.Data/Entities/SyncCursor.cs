using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class SyncCursor
{
    public Guid Id { get; set; }
    public Guid SyncConfigurationId { get; set; }

    public DateTimeOffset? LastEventTime { get; set; }
    public string? LastETag { get; set; }

    public SyncConfiguration SyncConfiguration { get; set; } = null!;
}
