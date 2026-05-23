using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class WorkItemMapping
{
    public Guid Id { get; set; }
    public Guid SyncConfigurationId { get; set; }

    public Source Source { get; set; }
    public SourceEntityType SourceEntityType { get; set; }
    public required string SourceEntityId { get; set; }

    public TargetSystem TargetSystem { get; set; }
    public required string TargetEntityId { get; set; }

    // Immutable after first write — see docs/data-model.md.
    public required string TargetWorkItemType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public SyncConfiguration SyncConfiguration { get; set; } = null!;
}
