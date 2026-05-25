namespace GithubSync.Api.Startup;

public static class SentryWiring
{
    public const string DsnConfigKey = "SENTRY_DSN";

    // No-op when DSN is absent. Production cannot reach that branch — RequiredSecrets.Validate
    // rejects a missing SENTRY_DSN before the worker starts. See docs/deploy.md#sentry.
    public static void Configure(WebApplicationBuilder builder)
    {
        var dsn = builder.Configuration[DsnConfigKey];
        if (!ShouldInitialize(dsn))
        {
            return;
        }

        builder.WebHost.UseSentry(options =>
        {
            options.Dsn = dsn;
            options.Environment = builder.Environment.EnvironmentName;
        });
    }

    internal static bool ShouldInitialize(string? dsn) => !string.IsNullOrWhiteSpace(dsn);
}
