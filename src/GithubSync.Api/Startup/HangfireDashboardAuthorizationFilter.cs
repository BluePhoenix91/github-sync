using Hangfire.Dashboard;
using Microsoft.Extensions.Hosting;

namespace GithubSync.Api.Startup;

// Per CLAUDE.md gotcha: Hangfire's dashboard is unauthenticated by default. In v1 the dashboard
// is intentionally restricted to Development only. Production-grade auth is a separate concern
// (filed under epic #29 when needed).
public sealed class HangfireDashboardAuthorizationFilter(IHostEnvironment environment)
    : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => environment.IsDevelopment();
}
