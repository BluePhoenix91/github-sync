using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class CanonicalActor
{
    public Guid Id { get; set; }

    public Source Source { get; set; }
    public required string SourceActorId { get; set; }
    public required string SourceActorLogin { get; set; }
    public string? DisplayName { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
