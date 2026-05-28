using GithubSync.Sources.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubSourceDIRegistrationTests
{
    [Fact]
    public void Fetcher_resolves_from_app_factory()
    {
        using var factory = new ConfiguredAppFactory();
        using var scope = factory.Services.CreateScope();

        var fetcher = scope.ServiceProvider.GetRequiredService<IGitHubIssueFetcher>();

        Assert.NotNull(fetcher);
    }
}
