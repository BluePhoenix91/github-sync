using System.Text.Json;
using GithubSync.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GithubSync.Api.Startup;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";
    public const string DbCheckName = "db";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(name: DbCheckName, tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapAppHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Liveness: probe only the process. No dependency checks; the filter excludes everything.
            Predicate = _ => false,
            ResponseWriter = WriteResponse,
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteResponse,
        });

        return endpoints;
    }

    internal static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, BuildPayload(report), JsonOptions);
    }

    // Deliberately omits per-check Description/Exception/Data: those can carry connection strings,
    // host names, or stack traces. Probes only need the failing check name + status category.
    internal static HealthResponse BuildPayload(HealthReport report) => new(
        Status: report.Status.ToString(),
        TotalDurationMs: (long)report.TotalDuration.TotalMilliseconds,
        Checks: report.Entries
            .Select(kvp => new HealthCheckEntry(
                Name: kvp.Key,
                Status: kvp.Value.Status.ToString(),
                DurationMs: (long)kvp.Value.Duration.TotalMilliseconds))
            .ToList());

    internal sealed record HealthResponse(string Status, long TotalDurationMs, IReadOnlyList<HealthCheckEntry> Checks);
    internal sealed record HealthCheckEntry(string Name, string Status, long DurationMs);
}
