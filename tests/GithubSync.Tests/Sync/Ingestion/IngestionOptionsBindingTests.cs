using GithubSync.Api.Sync.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GithubSync.Tests.Sync.Ingestion;

public class IngestionOptionsBindingTests
{
    [Fact]
    public void Binds_CronExpression_from_Ingestion_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:CronExpression"] = "*/5 * * * *",
            })
            .Build();

        var services = new ServiceCollection().AddIngestion(config);
        using var sp = services.BuildServiceProvider();

        var opt = sp.GetRequiredService<IOptions<IngestionOptions>>();
        Assert.Equal("*/5 * * * *", opt.Value.CronExpression);
    }
}
