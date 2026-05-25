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
    [Fact]
    public void ApplyEnrichers_attaches_ApplicationName_property()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogWarning("any message");

        var captured = Assert.Single(sink.Events);
        Assert.Equal("\"github-sync\"", captured.Properties["ApplicationName"].ToString());
    }

    [Fact]
    public void ApplyEnrichers_attaches_Environment_property_from_host()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogInformation("any");

        var captured = Assert.Single(sink.Events);
        Assert.Equal("\"Production\"", captured.Properties["Environment"].ToString());
    }

    [Fact]
    public void ApplyEnrichers_attaches_Release_property_non_empty()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogInformation("any");

        var captured = Assert.Single(sink.Events);
        var release = captured.Properties["Release"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(release));
        Assert.NotEqual("\"\"", release);
    }

    [Fact]
    public void ApplyEnrichers_attaches_MachineName_property_non_empty()
    {
        var (logger, sink) = BuildTestLogger(Environments.Production);

        logger.LogInformation("any");

        var captured = Assert.Single(sink.Events);
        var machineName = captured.Properties["MachineName"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(machineName));
        Assert.NotEqual("\"\"", machineName);
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

    internal sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    internal sealed class TestHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
