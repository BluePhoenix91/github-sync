using GithubSync.Api.Startup;
using GithubSync.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);
LoggingWiring.Configure(builder);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));

builder.Services.AddAppHealthChecks();

var app = builder.Build();

RequiredSecrets.Validate(
    app.Configuration,
    app.Environment,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Secrets"));

app.MapAppHealthEndpoints();

try
{
    app.Run();
}
finally
{
    // Drains the Serilog.Sinks.Async buffer so events queued at shutdown
    // reach the file sink before the process exits.
    Log.CloseAndFlush();
}

// Exposed for WebApplicationFactory in integration tests; top-level statements would otherwise
// keep the generated Program class internal.
public partial class Program;
