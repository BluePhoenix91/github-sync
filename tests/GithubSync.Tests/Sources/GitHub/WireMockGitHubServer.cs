using WireMock.Server;

namespace GithubSync.Tests.Sources.GitHub;

// Thin wrapper around WireMockServer.Start() so tests don't repeat the lifecycle dance.
// Exposes the base URL the typed HttpClient is pointed at.
internal sealed class WireMockGitHubServer : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public string BaseUrl => _server.Url!;

    public WireMockServer Server => _server;

    public void Dispose() => _server.Stop();
}
