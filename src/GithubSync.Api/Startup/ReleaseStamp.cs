using System.Reflection;

namespace GithubSync.Api.Startup;

internal static class ReleaseStamp
{
    private static readonly Lazy<string> Cached = new(Resolve);

    public static string Current => Cached.Value;

    private static string Resolve()
    {
        var attribute = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
            ? "unknown"
            : attribute.InformationalVersion;
    }
}
