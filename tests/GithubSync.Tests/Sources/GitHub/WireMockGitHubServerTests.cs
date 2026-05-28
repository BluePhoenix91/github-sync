using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace GithubSync.Tests.Sources.GitHub;

public class WireMockGitHubServerTests
{
    [Fact]
    public async Task Stubbed_endpoint_responds_to_post()
    {
        using var server = new WireMockGitHubServer();
        server.Server
            .Given(Request.Create().UsingPost().WithPath("/graphql"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"ok":true}"""));

        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var resp = await http.PostAsync("/graphql", new StringContent(""));

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("ok", await resp.Content.ReadAsStringAsync());
    }
}
