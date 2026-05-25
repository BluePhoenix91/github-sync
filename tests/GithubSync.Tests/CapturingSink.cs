using Serilog.Core;
using Serilog.Events;

namespace GithubSync.Tests;

internal sealed class CapturingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}
