using System.Text.Json;
using GithubSync.Api.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace GithubSync.Tests;

public class LoggingWiringTests
{
    [Theory]
    [InlineData(LoggingWiring.ApplicationNameKey, "\"github-sync\"")]
    [InlineData(LoggingWiring.EnvironmentKey, "\"Production\"")]
    public void ApplyEnrichers_attaches_constant_property(string propertyName, string expectedToString)
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogInformation("any");

        var captured = Assert.Single(sink.Events);
        Assert.Equal(expectedToString, captured.Properties[propertyName].ToString());
    }

    [Theory]
    [InlineData(LoggingWiring.ReleaseKey)]
    [InlineData(LoggingWiring.MachineNameKey)]
    public void ApplyEnrichers_attaches_machine_derived_property_non_empty(string propertyName)
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogInformation("any");

        var captured = Assert.Single(sink.Events);
        var rendered = captured.Properties[propertyName].ToString();
        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.NotEqual("\"\"", rendered);
    }

    [Fact]
    public void Named_placeholder_template_produces_discrete_top_level_properties()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogWarning(
            "Skipped {Source} item {ExternalId}: {Reason}",
            "github", "issue-123", "rate-limited");

        var captured = Assert.Single(sink.Events);
        Assert.Equal("\"github\"", captured.Properties["Source"].ToString());
        Assert.Equal("\"issue-123\"", captured.Properties["ExternalId"].ToString());
        Assert.Equal("\"rate-limited\"", captured.Properties["Reason"].ToString());
    }

    [Fact]
    public void CompactJsonFormatter_renders_all_seven_property_names_as_top_level_keys()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogWarning(
            "Skipped {Source} item {ExternalId}: {Reason}",
            "github", "issue-123", "rate-limited");

        var captured = Assert.Single(sink.Events);

        var formatter = new CompactJsonFormatter();
        var writer = new StringWriter();
        formatter.Format(captured, writer);

        using var doc = JsonDocument.Parse(writer.ToString());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("ApplicationName", out _), "ApplicationName missing");
        Assert.True(root.TryGetProperty("Environment", out _), "Environment missing");
        Assert.True(root.TryGetProperty("Release", out _), "Release missing");
        Assert.True(root.TryGetProperty("MachineName", out _), "MachineName missing");
        Assert.True(root.TryGetProperty("Source", out var source) && source.GetString() == "github", "Source missing or wrong");
        Assert.True(root.TryGetProperty("ExternalId", out var extId) && extId.GetString() == "issue-123", "ExternalId missing or wrong");
        Assert.True(root.TryGetProperty("Reason", out var reason) && reason.GetString() == "rate-limited", "Reason missing or wrong");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("http://localhost:5341", true)]
    public void ShouldEnableSeq_requires_non_blank_url(string? url, bool expected)
    {
        Assert.Equal(expected, LoggingWiring.ShouldEnableSeq(url));
    }

    [Fact]
    public void Async_wrapper_flushes_buffered_events_when_logger_is_disposed()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"github-sync-async-flush-{Guid.NewGuid():N}.log");
        try
        {
            var logger = new LoggerConfiguration()
                .WriteTo.Async(a => a.File(
                    formatter: new CompactJsonFormatter(),
                    path: tempPath,
                    shared: true),
                    bufferSize: LoggingWiring.FileSinkAsyncBufferSize,
                    blockWhenFull: false)
                .CreateLogger();

            logger.Information("buffered-event-{Marker}", "alpha");
            logger.Information("buffered-event-{Marker}", "beta");

            // Disposing the wrapper sink drains the queue to the inner file sink,
            // which is what Log.CloseAndFlush() triggers on host shutdown.
            logger.Dispose();

            var content = File.ReadAllText(tempPath);
            Assert.Contains("alpha", content);
            Assert.Contains("beta", content);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static (ILogger<LoggingWiringTests> logger, CapturingSink sink) BuildTestLogger(string envName)
    {
        var env = new TestHostEnvironment(envName);
        var sink = new CapturingSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose();
        LoggingWiring.ApplyEnrichers(serilog, env);
        serilog.WriteTo.Sink(sink);

        var factory = new SerilogLoggerFactory(serilog.CreateLogger(), dispose: true);
        return (factory.CreateLogger<LoggingWiringTests>(), sink);
    }
}
