namespace GithubSync.Api.Sync.Ingestion;

public class IdentityMappingOptions
{
    public const string SectionName = "IdentityMapping";

    // Explicit GitHub-login → ADO-user mappings. Lookup is case-insensitive on GitHubLogin.
    // Unknown logins fall through to least-loaded selection against the TargetUser pool.
    public List<ConfiguredIdentityMapping> Mappings { get; init; } = new();
}

public class ConfiguredIdentityMapping
{
    public required string GitHubLogin { get; init; }
    public required string TargetUserId { get; init; }
    public required string TargetUserDisplayName { get; init; }
}
