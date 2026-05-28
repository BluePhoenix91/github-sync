namespace GithubSync.Sources.GitHub.RateLimiting;

// Tracks GitHub GraphQL rate-limit budget across queries. Thread-safe for a single fetcher
// instance — concurrent calls from multiple fetchers are not the v1 topology.
public sealed class GitHubRateLimitBudget
{
    private int _remaining = int.MaxValue;       // No observation yet -> assume plenty.
    private int _lastObservedCost = 1;
    private DateTimeOffset _resetAt = DateTimeOffset.UtcNow;
    private bool _hasObservation;
    private readonly object _lock = new();

    public void Update(int remaining, int cost, DateTimeOffset resetAt)
    {
        lock (_lock)
        {
            _remaining = remaining;
            _lastObservedCost = Math.Max(1, cost);  // Defensive against zero/negative.
            _resetAt = resetAt;
            _hasObservation = true;
        }
    }

    public async Task WaitIfLowAsync(CancellationToken ct)
    {
        TimeSpan? wait = null;
        lock (_lock)
        {
            if (_hasObservation && _remaining < _lastObservedCost * 2)
            {
                var delta = _resetAt - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    wait = delta;
                }
            }
        }

        if (wait is { } w)
        {
            await Task.Delay(w, ct);
        }
    }
}
