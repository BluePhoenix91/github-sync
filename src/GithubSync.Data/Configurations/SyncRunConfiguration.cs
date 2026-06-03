using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.HasKey(x => x.Id);

        // Most common read: "the last N runs for config X, newest first". The DESC index
        // serves that query without a sort and supports the SyncConfiguration.Runs navigation.
        builder.HasIndex(x => new { x.SyncConfigurationId, x.StartedAt })
            .IsDescending(false, true);
    }
}
