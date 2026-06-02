using GithubSync.Api.Startup;
using Hangfire.Dashboard;
using Microsoft.Extensions.Hosting;

namespace GithubSync.Tests.Startup;

public class HangfireDashboardAuthorizationFilterTests
{
    [Fact]
    public void Authorize_returns_true_in_Development()
    {
        var env = new TestHostEnvironment(Environments.Development);
        var filter = new HangfireDashboardAuthorizationFilter(env);
        Assert.True(filter.Authorize(context: null!));
    }

    [Fact]
    public void Authorize_returns_false_outside_Development()
    {
        var env = new TestHostEnvironment(Environments.Production);
        var filter = new HangfireDashboardAuthorizationFilter(env);
        Assert.False(filter.Authorize(context: null!));
    }
}
