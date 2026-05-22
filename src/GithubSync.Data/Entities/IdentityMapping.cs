using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class IdentityMapping
{
    public Guid Id { get; set; }
    public Guid CanonicalActorId { get; set; }

    public TargetSystem TargetSystem { get; set; }
    public required string TargetUserId { get; set; }
    public required string TargetUserDisplayName { get; set; }

    public MappingSource MappingSource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public CanonicalActor CanonicalActor { get; set; } = null!;
}
