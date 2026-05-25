using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GithubSync.Tests;

internal sealed class TestHostEnvironment(string envName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = envName;
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}
