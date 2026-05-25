using GithubSync.Api.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GithubSync.Tests;

public class RequiredSecretsTests
{
    [Fact]
    public void FindMissing_returns_empty_when_all_present()
    {
        var config = BuildConfig(AllSet());

        Assert.Empty(RequiredSecrets.FindMissing(config));
    }

    [Fact]
    public void FindMissing_treats_empty_string_as_missing()
    {
        var values = AllSet();
        values["SENTRY_DSN"] = "";

        var missing = RequiredSecrets.FindMissing(BuildConfig(values));

        Assert.Equal(new[] { "SENTRY_DSN" }, missing);
    }

    [Fact]
    public void FindMissing_lists_each_missing_env_var_name()
    {
        var values = AllSet();
        values.Remove("GITHUB_TOKEN");
        values.Remove("ConnectionStrings:AppDb");

        var missing = RequiredSecrets.FindMissing(BuildConfig(values));

        Assert.Equal(new[] { "GITHUB_TOKEN", "ConnectionStrings__AppDb" }, missing);
    }

    [Fact]
    public void Validate_throws_in_production_when_secret_missing()
    {
        var values = AllSet();
        values.Remove("ADO_PAT");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RequiredSecrets.Validate(BuildConfig(values), Env(Environments.Production), NullLogger.Instance));

        Assert.Contains("ADO_PAT", ex.Message);
    }

    [Fact]
    public void Validate_message_lists_every_missing_secret()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RequiredSecrets.Validate(BuildConfig(new()), Env(Environments.Production), NullLogger.Instance));

        Assert.Contains("SENTRY_DSN", ex.Message);
        Assert.Contains("GITHUB_TOKEN", ex.Message);
        Assert.Contains("ADO_PAT", ex.Message);
        Assert.Contains("ConnectionStrings__AppDb", ex.Message);
    }

    [Fact]
    public void Validate_does_not_throw_in_production_when_all_present()
    {
        RequiredSecrets.Validate(BuildConfig(AllSet()), Env(Environments.Production), NullLogger.Instance);
    }

    [Fact]
    public void Validate_does_not_throw_in_development_when_secrets_missing()
    {
        RequiredSecrets.Validate(BuildConfig(new()), Env(Environments.Development), NullLogger.Instance);
    }

    [Fact]
    public void Validate_throws_in_staging_when_secret_missing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RequiredSecrets.Validate(BuildConfig(new()), Env("Staging"), NullLogger.Instance));
    }

    private static Dictionary<string, string?> AllSet() => new()
    {
        ["SENTRY_DSN"] = "https://example@sentry.io/1",
        ["GITHUB_TOKEN"] = "ghp_placeholder",
        ["ADO_PAT"] = "ado_placeholder",
        ["ConnectionStrings:AppDb"] = "Host=localhost;Database=x;Username=y;Password=z",
    };

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Env(string name) => new TestHostEnvironment(name);
}
