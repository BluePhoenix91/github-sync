using System.Text.Json;
using GithubSync.Api.Sync;
using GithubSync.Data.Enums;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace GithubSync.Tests.Sync;

public class SyncRunMetricsEmitterTests
{
    [Fact]
    public void Emit_writes_exactly_one_log_record_at_information_level()
    {
        var (emitter, sink) = BuildEmitter();
        var metrics = BuildMetricsWithMixedCounts();

        emitter.Emit(metrics);

        var captured = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Information, captured.Level);
    }

    [Fact]
    public void Emit_attaches_every_metric_field_as_a_discrete_property()
    {
        var (emitter, sink) = BuildEmitter();
        var metrics = BuildMetricsWithMixedCounts();
        metrics.Complete();

        emitter.Emit(metrics);

        var captured = Assert.Single(sink.Events);
        Assert.Equal(metrics.RunId.ToString(), captured.Properties["RunId"].ToString());
        Assert.Equal("GitHub", ScalarText(captured.Properties["Source"]));
        Assert.Equal("6", captured.Properties["Fetched"].ToString());
        Assert.Equal("5", captured.Properties["Mapped"].ToString());
        Assert.Equal("3", captured.Properties["Persisted"].ToString());
        Assert.Equal("2", captured.Properties["Deduped"].ToString());
        Assert.Equal("1", captured.Properties["Skipped"].ToString());
        Assert.Equal("1", captured.Properties["Failed"].ToString());
        Assert.True(captured.Properties.ContainsKey("DurationMs"));
    }

    [Fact]
    public void CompactJsonFormatter_renders_every_metric_field_as_a_top_level_key()
    {
        var (emitter, sink) = BuildEmitter();
        var metrics = BuildMetricsWithMixedCounts();
        metrics.Complete();

        emitter.Emit(metrics);

        var captured = Assert.Single(sink.Events);
        var formatter = new CompactJsonFormatter();
        var writer = new StringWriter();
        formatter.Format(captured, writer);

        using var doc = JsonDocument.Parse(writer.ToString());
        var root = doc.RootElement;

        foreach (var key in new[]
                 {
                     "RunId", "Source", "Fetched", "Mapped", "Persisted",
                     "Deduped", "Skipped", "Failed", "DurationMs",
                 })
        {
            Assert.True(root.TryGetProperty(key, out _), $"{key} missing from CLEF output");
        }
    }

    private static SyncRunMetrics BuildMetricsWithMixedCounts()
    {
        var m = new SyncRunMetrics(Source.GitHub);
        m.IncrementFetched(6);
        m.IncrementMapped(5);
        m.IncrementPersisted(3);
        m.IncrementDeduped(2);
        m.IncrementSkipped();
        m.IncrementFailed();
        return m;
    }

    private static (SyncRunMetricsEmitter emitter, CapturingSink sink) BuildEmitter()
    {
        var sink = new CapturingSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var factory = new SerilogLoggerFactory(serilog, dispose: true);
        var emitter = new SyncRunMetricsEmitter(factory.CreateLogger<SyncRunMetricsEmitter>());
        return (emitter, sink);
    }

    private static string ScalarText(LogEventPropertyValue value) =>
        ((ScalarValue)value).Value?.ToString() ?? "";
}
