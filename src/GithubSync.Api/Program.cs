using GithubSync.Api.Startup;
using GithubSync.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));

builder.Services.AddAppHealthChecks();

var app = builder.Build();

RequiredSecrets.Validate(
    app.Configuration,
    app.Environment,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Secrets"));

app.MapAppHealthEndpoints();

app.Run();

// Exposed for WebApplicationFactory in integration tests; top-level statements would otherwise
// keep the generated Program class internal.
public partial class Program;
