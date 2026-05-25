using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GithubSync.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace GithubSync.Tests;

public class HealthEndpointsTests
{
    [Fact]
    public void BuildPayload_includes_status_total_duration_and_per_check_entries()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(HealthStatus.Unhealthy, description: "should not leak", duration: TimeSpan.FromMilliseconds(7), exception: null, data: null),
            ["other"] = new(HealthStatus.Healthy, description: null, duration: TimeSpan.FromMilliseconds(3), exception: null, data: null),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(15));

        var payload = HealthEndpoints.BuildPayload(report);

        Assert.Equal("Unhealthy", payload.Status);
        Assert.Equal(15, payload.TotalDurationMs);
        Assert.Collection(payload.Checks.OrderBy(c => c.Name),
            db =>
            {
                Assert.Equal("db", db.Name);
                Assert.Equal("Unhealthy", db.Status);
                Assert.Equal(7, db.DurationMs);
            },
            other =>
            {
                Assert.Equal("other", other.Name);
                Assert.Equal("Healthy", other.Status);
            });
    }

    [Fact]
    public void BuildPayload_does_not_surface_descriptions_or_exception_messages()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["db"] = new(
                HealthStatus.Unhealthy,
                description: "Host=secret-host;Password=secret",
                duration: TimeSpan.FromMilliseconds(1),
                exception: new InvalidOperationException("connection failure: leaked credential"),
                data: new Dictionary<string, object> { ["server"] = "secret-host" }),
        };
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(1));

        var json = JsonSerializer.Serialize(HealthEndpoints.BuildPayload(report));

        Assert.DoesNotContain("secret-host", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("leaked", json);
    }

    [Fact]
    public async Task Live_returns_200_with_Healthy_even_when_dependency_check_is_unhealthy()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthPayload>();
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        Assert.Empty(body.Checks);
    }

    [Fact]
    public async Task Ready_returns_503_with_failing_check_named_when_dependency_is_unhealthy()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthPayload>();
        Assert.NotNull(body);
        Assert.Equal("Unhealthy", body.Status);
        var failing = Assert.Single(body.Checks);
        Assert.Equal(HealthEndpoints.DbCheckName, failing.Name);
        Assert.Equal("Unhealthy", failing.Status);
    }

    [Fact]
    public async Task Ready_returns_200_when_dependency_is_healthy()
    {
        await using var factory = new HealthFactory(HealthStatus.Healthy);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthPayload>();
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        Assert.Equal(HealthEndpoints.DbCheckName, Assert.Single(body.Checks).Name);
    }

    private sealed record HealthPayload(string Status, long TotalDurationMs, List<HealthCheckPayload> Checks);
    private sealed record HealthCheckPayload(string Name, string Status, long DurationMs);

    private sealed class HealthFactory(HealthStatus dbStatus) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // AppDb registration requires a parseable connection string; the real check is swapped below.
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = "Host=placeholder;Database=placeholder;Username=placeholder;Password=placeholder",
                });
            });
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.Configure<HealthCheckServiceOptions>(options =>
                {
                    var existing = options.Registrations.FirstOrDefault(r => r.Name == HealthEndpoints.DbCheckName);
                    if (existing is not null)
                    {
                        options.Registrations.Remove(existing);
                    }

                    options.Registrations.Add(new HealthCheckRegistration(
                        name: HealthEndpoints.DbCheckName,
                        factory: _ => new StubHealthCheck(dbStatus),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: [HealthEndpoints.ReadyTag]));
                });
            });
        }
    }

    private sealed class StubHealthCheck(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(status));
    }
}
