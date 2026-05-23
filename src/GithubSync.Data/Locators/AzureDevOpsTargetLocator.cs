namespace GithubSync.Data.Locators;

// Serialise via LocatorJsonOptions.Default to keep the SyncConfiguration unique index
// effective — see LocatorJsonOptions for the canonicalisation invariant.
public sealed record AzureDevOpsTargetLocator(string Organization, string Project);
