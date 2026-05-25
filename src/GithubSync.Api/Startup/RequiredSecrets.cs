namespace GithubSync.Api.Startup;

public static class RequiredSecrets
{
    private static readonly IReadOnlyList<(string ConfigKey, string EnvVarName)> All = new[]
    {
        ("SENTRY_DSN", "SENTRY_DSN"),
        ("GITHUB_TOKEN", "GITHUB_TOKEN"),
        ("ADO_PAT", "ADO_PAT"),
        ("ConnectionStrings:AppDb", "ConnectionStrings__AppDb"),
    };

    public static IReadOnlyList<string> FindMissing(IConfiguration configuration)
    {
        var missing = new List<string>();
        foreach (var (configKey, envVar) in All)
        {
            if (string.IsNullOrWhiteSpace(configuration[configKey]))
            {
                missing.Add(envVar);
            }
        }
        return missing;
    }

    public static void Validate(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var missing = FindMissing(configuration);
        if (missing.Count == 0)
        {
            return;
        }

        var list = string.Join(", ", missing);

        // Development is intentionally lenient: a dev running an unrelated slice of the API
        // (e.g. EF migrations, a new endpoint) shouldn't be blocked by missing Sentry/GitHub/ADO
        // credentials they don't need yet. Production must fail fast — silent empty-string
        // fallback for secrets is the failure mode we're guarding against.
        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "Missing required secrets (Development, non-fatal): {Missing}. Set via 'dotnet user-secrets' or environment variables.",
                list);
            return;
        }

        throw new InvalidOperationException(
            $"Missing required secrets: {list}. Set them as environment variables on the IIS app pool (see docs/deploy.md).");
    }
}
