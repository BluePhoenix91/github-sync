using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class SyncConfiguration
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Source Source { get; set; }
    public required string SourceOwner { get; set; }
    public required string SourceRepo { get; set; }

    public TargetSystem TargetSystem { get; set; }
    public required string TargetOrganization { get; set; }
    public required string TargetProject { get; set; }

    public required string TargetTypeMapping { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SyncCursor? Cursor { get; set; }
    public ICollection<CanonicalEvent> Events { get; set; } = [];
    public ICollection<WorkItemMapping> WorkItemMappings { get; set; } = [];
}
