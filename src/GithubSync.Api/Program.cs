using GithubSync.Api.Startup;
using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

SentryWiring.Configure(builder);
LoggingWiring.Configure(builder);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));

builder.Services.AddAppHealthChecks();
builder.Services.AddGitHubSource(builder.Configuration);

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

public partial class Program;
