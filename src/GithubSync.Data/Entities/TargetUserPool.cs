using GithubSync.Data.Enums;

namespace GithubSync.Data.Entities;

public class TargetUserPool
{
    public Guid Id { get; set; }

    public TargetSystem TargetSystem { get; set; }
    public required string TargetUserId { get; set; }
    public required string TargetUserDisplayName { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
