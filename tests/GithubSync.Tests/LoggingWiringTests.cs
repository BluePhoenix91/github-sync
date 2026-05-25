using GithubSync.Api.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

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
