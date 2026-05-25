using System.Reflection;

namespace GithubSync.Api.Startup;

internal static class ReleaseStamp
{
    public static readonly string Current = Resolve();

    private static string Resolve()
    {
        var attribute = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion)
            ? "unknown"
            : attribute.InformationalVersion;
    }
}
