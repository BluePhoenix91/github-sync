using System.Text.Json;

namespace GithubSync.Data;

// Postgres jsonb canonicalises whitespace and key order but not key casing — pinning these
// options keeps the unique index on (Source, SourceLocator, TargetSystem, TargetLocator)
// effective.
public static class LocatorJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
