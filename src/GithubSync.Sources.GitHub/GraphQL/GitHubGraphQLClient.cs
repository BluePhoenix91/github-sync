using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GithubSync.Sources.GitHub.Exceptions;
using GithubSync.Sources.GitHub.GraphQL.Dto;

namespace GithubSync.Sources.GitHub.GraphQL;

internal sealed class GitHubGraphQLClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IssuesPageResponse> QueryIssuesPageAsync(
        string owner, string repo, DateTimeOffset? since, string? cursor, CancellationToken ct)
    {
        var body = new
        {
            query = IssuesPageQuery.Outer,
            variables = new
            {
                owner,
                repo,
                since = since?.ToUniversalTime(),
                cursor,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(body),
        };

        using var response = await SendWithRateLimitRetryAsync(request, ct);

        var payload = await response.Content.ReadFromJsonAsync<IssuesPageResponse>(JsonOptions, ct)
            ?? throw new GitHubGraphQLException(["empty response body"]);

        if (payload.Errors is { Count: > 0 } errs)
        {
            throw new GitHubGraphQLException(errs.Select(e => e.Message).ToList());
        }

        return payload;
    }

    public Task<IssueFollowUpResponse> FollowUpTimelineAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueTimelineFollowUp, owner, repo, number, cursor, ct);

    public Task<IssueFollowUpResponse> FollowUpCommentsAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueCommentsFollowUp, owner, repo, number, cursor, ct);

    public Task<IssueFollowUpResponse> FollowUpEditsAsync(
        string owner, string repo, int number, string cursor, CancellationToken ct) =>
        FollowUpAsync(IssuesPageQuery.IssueEditsFollowUp, owner, repo, number, cursor, ct);

    private async Task<IssueFollowUpResponse> FollowUpAsync(
        string query, string owner, string repo, int number, string cursor, CancellationToken ct)
    {
        var body = new
        {
            query,
            variables = new { owner, repo, number, cursor },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(body),
        };
        using var response = await SendWithRateLimitRetryAsync(request, ct);

        var payload = await response.Content.ReadFromJsonAsync<IssueFollowUpResponse>(JsonOptions, ct)
            ?? throw new GitHubGraphQLException(["empty follow-up response body"]);
        if (payload.Errors is { Count: > 0 } errs)
            throw new GitHubGraphQLException(errs.Select(e => e.Message).ToList());
        return payload;
    }

    // Sends the request through the HttpClient (Polly handles 5xx transient retries).
    // Adds a one-shot retry for 403 rate-limit signals (Retry-After OR X-RateLimit-Remaining=0 + Reset).
    // Throws GitHubAuthException for 401 and for 403 with no rate-limit header signal.
    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await httpClient.SendAsync(await CloneRequestAsync(request, ct), ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new GitHubAuthException("GitHub returned 401 Unauthorized.");
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            EnsureSuccessOrThrow(response, string.Empty);
            return response;
        }

        // 403 — decide between rate limit and auth.
        if (TryGetRateLimitWait(response, out var wait))
        {
            response.Dispose();
            await Task.Delay(wait, ct);

            var retried = await httpClient.SendAsync(await CloneRequestAsync(request, ct), ct);
            if (retried.StatusCode == HttpStatusCode.Forbidden)
            {
                retried.Dispose();
                throw new GitHubRateLimitException("Rate-limit retry still returned 403.");
            }
            if (retried.StatusCode == HttpStatusCode.Unauthorized)
            {
                retried.Dispose();
                throw new GitHubAuthException("GitHub returned 401 Unauthorized after rate-limit retry.");
            }

            EnsureSuccessOrThrow(retried, " after rate-limit retry");
            return retried;
        }

        response.Dispose();
        throw new GitHubAuthException("GitHub returned 403 with no rate-limit header signal.");
    }

    private static bool TryGetRateLimitWait(HttpResponseMessage response, out TimeSpan wait)
    {
        // Prefer Retry-After (secondary limit) over header-based reset (primary limit).
        if (response.Headers.RetryAfter is { Delta: { } delta })
        {
            wait = delta;
            return true;
        }
        if (response.Headers.RetryAfter is { Date: { } date })
        {
            wait = date - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return true;
        }

        // Primary limit via headers.
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingVals)
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals)
            && int.TryParse(remainingVals.FirstOrDefault(), out var remaining)
            && long.TryParse(resetVals.FirstOrDefault(), out var resetEpoch)
            && remaining == 0)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            wait = resetAt - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return true;
        }

        wait = default;
        return false;
    }

    private static void EnsureSuccessOrThrow(HttpResponseMessage response, string contextSuffix)
    {
        // Non-success non-403 (typically a 5xx that exhausted Polly's retries) must surface as
        // a transport error, not be allowed to flow into JSON deserialization where a malformed
        // or HTML error body would throw a misleading exception type.
        if (response.IsSuccessStatusCode)
            return;

        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(
            $"GitHub returned non-success status {(int)status} {status}{contextSuffix}.", null, status);
    }

    // HttpRequestMessage instances cannot be re-sent; clone for retry.
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is not null)
        {
            var ms = new MemoryStream();
            await source.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var h in source.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in source.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }
}
