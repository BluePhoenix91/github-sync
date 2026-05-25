using GithubSync.Api.Sync;
using GithubSync.Data.Enums;

namespace GithubSync.Tests.Sync;

public class SyncRunMetricsTests
{
    [Fact]
    public void New_run_has_zero_counters_and_distinct_run_id()
    {
        var a = new SyncRunMetrics(Source.GitHub);
        var b = new SyncRunMetrics(Source.GitHub);

        Assert.Equal(0, a.Fetched);
        Assert.Equal(0, a.Mapped);
        Assert.Equal(0, a.Persisted);
        Assert.Equal(0, a.Deduped);
        Assert.Equal(0, a.Skipped);
        Assert.Equal(0, a.Failed);
        Assert.Equal(0, a.DurationMs);
        Assert.Equal(Source.GitHub, a.Source);
        Assert.NotEqual(Guid.Empty, a.RunId);
        Assert.NotEqual(a.RunId, b.RunId);
    }

    [Fact]
    public void Increments_each_counter_independently()
    {
        var m = new SyncRunMetrics(Source.GitHub);

        m.IncrementFetched();
        m.IncrementMapped();
        m.IncrementPersisted();
        m.IncrementDeduped();
        m.IncrementSkipped();
        m.IncrementFailed();

        Assert.Equal(1, m.Fetched);
        Assert.Equal(1, m.Mapped);
        Assert.Equal(1, m.Persisted);
        Assert.Equal(1, m.Deduped);
        Assert.Equal(1, m.Skipped);
        Assert.Equal(1, m.Failed);
    }

    [Fact]
    public void Mixed_inputs_aggregate_to_expected_totals()
    {
        var m = new SyncRunMetrics(Source.GitHub);

        for (var i = 0; i < 3; i++)
        {
            m.IncrementFetched();
            m.IncrementMapped();
            m.IncrementPersisted();
        }

        for (var i = 0; i < 2; i++)
        {
            m.IncrementFetched();
            m.IncrementMapped();
            m.IncrementDeduped();
        }

        m.IncrementFetched();
        m.IncrementSkipped();

        m.IncrementFailed();

        Assert.Equal(6, m.Fetched);
        Assert.Equal(5, m.Mapped);
        Assert.Equal(3, m.Persisted);
        Assert.Equal(2, m.Deduped);
        Assert.Equal(1, m.Skipped);
        Assert.Equal(1, m.Failed);
    }

    [Fact]
    public void Complete_freezes_duration_at_a_nonnegative_value()
    {
        var m = new SyncRunMetrics(Source.GitHub);

        m.Complete();
        var first = m.DurationMs;
        Thread.Sleep(10);
        m.Complete();
        var second = m.DurationMs;

        Assert.True(first >= 0);
        Assert.Equal(first, second);
    }
}
