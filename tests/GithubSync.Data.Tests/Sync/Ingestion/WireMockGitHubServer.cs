using WireMock.Server;

namespace GithubSync.Data.Tests.Sync.Ingestion;

internal sealed class WireMockGitHubServer : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public string BaseUrl => _server.Url!;
    public WireMockServer Server => _server;
    public void Dispose() => _server.Stop();
}
