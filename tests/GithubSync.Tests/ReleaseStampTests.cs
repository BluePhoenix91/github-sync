using GithubSync.Api.Startup;

namespace GithubSync.Tests;

public class ReleaseStampTests
{
    [Fact]
    public void Current_returns_non_empty_value()
    {
        var stamp = ReleaseStamp.Current;

        Assert.False(string.IsNullOrWhiteSpace(stamp));
    }

    [Fact]
    public void Current_returns_same_value_on_repeated_access()
    {
        var first = ReleaseStamp.Current;
        var second = ReleaseStamp.Current;

        Assert.Same(first, second);
    }
}
