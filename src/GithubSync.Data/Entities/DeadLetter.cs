using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class DeadLetter
{
    public Guid Id { get; set; }
    public Guid CanonicalEventId { get; set; }

    public TargetSystem TargetSystem { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }
    public int AttemptCount { get; set; }

    public required string Reason { get; set; }
    public string? RawResponse { get; set; }

    public bool Resolved { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public CanonicalEvent CanonicalEvent { get; set; } = null!;
}
