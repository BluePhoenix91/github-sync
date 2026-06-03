namespace GithubSync.Api.Sync.Ingestion;

public class IngestionOptions
{
    public const string SectionName = "Ingestion";

    // Cron used for the recurring scheduler job. Defaults align with appsettings.json
    // and are documented in docs/deploy.md.
    public string CronExpression { get; set; } = "*/15 * * * *";
}
