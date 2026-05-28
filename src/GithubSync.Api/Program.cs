using GithubSync.Api.Startup;
using GithubSync.Data;
using GithubSync.Sources.GitHub;
using Microsoft.EntityFrameworkCore;

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

app.Run();

public partial class Program;
