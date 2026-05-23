using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class SyncConfiguration
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Source Source { get; set; }

    // Shape depends on Source — see docs/data-model.md and Locators/.
    public required string SourceLocator { get; set; }

    public TargetSystem TargetSystem { get; set; }

    // Shape depends on TargetSystem — see docs/data-model.md and Locators/.
    public required string TargetLocator { get; set; }

    // Shape owned by #14 — see docs/data-model.md.
    public required string TargetTypeMapping { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public SyncCursor? Cursor { get; set; }
    public ICollection<CanonicalEvent> Events { get; set; } = [];
    public ICollection<WorkItemMapping> WorkItemMappings { get; set; } = [];
}
