using GithubSync.Sources.GitHub.RateLimiting;

namespace GithubSync.Tests.Sources.GitHub;

public class GitHubRateLimitBudgetTests
{
    [Fact]
    public async Task With_plenty_of_budget_WaitIfLowAsync_returns_immediately()
    {
        var budget = new GitHubRateLimitBudget();
        budget.Update(remaining: 5000, cost: 5, resetAt: DateTimeOffset.UtcNow.AddMinutes(30));

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(100),
            $"Expected immediate return, took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task With_remaining_below_safety_multiplier_sleeps_until_reset()
    {
        var budget = new GitHubRateLimitBudget();
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(1);
        // remaining (5) < cost (5) * 2 -> must wait
        budget.Update(remaining: 5, cost: 5, resetAt: resetAt);

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(900),
            $"Expected ~1s wait, took {elapsed.TotalMilliseconds}ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"Wait took too long: {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Cancellation_during_wait_throws_OperationCanceledException()
    {
        var budget = new GitHubRateLimitBudget();
        budget.Update(remaining: 1, cost: 100, resetAt: DateTimeOffset.UtcNow.AddSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await budget.WaitIfLowAsync(cts.Token));
    }

    [Fact]
    public async Task Before_any_update_WaitIfLowAsync_returns_immediately()
    {
        // Fresh budget with no observations yet should not block — the first real query is allowed.
        var budget = new GitHubRateLimitBudget();

        var elapsed = await MeasureAsync(() => budget.WaitIfLowAsync(CancellationToken.None));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(100));
    }

    private static async Task<TimeSpan> MeasureAsync(Func<Task> work)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await work();
        sw.Stop();
        return sw.Elapsed;
    }
}
