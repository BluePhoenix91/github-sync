using System.Text.Json;

namespace GithubSync.Data;

// Shared serializer options for locator jsonb (de)serialization. Pinned in one place so the
// unique index on (Source, SourceLocator, TargetSystem, TargetLocator) sees canonical values:
// jsonb canonicalises keys and whitespace, but key *casing* depends on how we serialise.
public static class LocatorJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
