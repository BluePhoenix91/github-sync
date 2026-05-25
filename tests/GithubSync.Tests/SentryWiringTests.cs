using GithubSync.Api.Startup;

namespace GithubSync.Tests;

public class SentryWiringTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldInitialize_returns_false_when_dsn_blank(string? dsn) =>
        Assert.False(SentryWiring.ShouldInitialize(dsn));

    [Fact]
    public void ShouldInitialize_returns_true_when_dsn_present() =>
        Assert.True(SentryWiring.ShouldInitialize("https://key@sentry.example.invalid/1"));

    // No IHub.IsEnabled assertion: Sentry's hub is process-wide singleton state, leaks across xunit tests.
}
