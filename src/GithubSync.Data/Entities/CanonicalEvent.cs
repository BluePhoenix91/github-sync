using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class CanonicalEvent
{
    public Guid Id { get; set; }
    public Guid SyncConfigurationId { get; set; }

    public Source Source { get; set; }
    public SourceEntityType SourceEntityType { get; set; }
    public required string SourceEntityId { get; set; }

    // Null only allowed for EventKind.IssueEdited — see docs/idempotency.md.
    public string? SourceEventId { get; set; }

    public EventKind EventKind { get; set; }
    public DateTimeOffset EventTime { get; set; }

    public Guid? ActorId { get; set; }

    public required string PayloadJson { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public SyncConfiguration SyncConfiguration { get; set; } = null!;
    public CanonicalActor? Actor { get; set; }
}
