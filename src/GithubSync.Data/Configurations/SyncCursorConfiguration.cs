using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class SyncCursorConfiguration : IEntityTypeConfiguration<SyncCursor>
{
    public void Configure(EntityTypeBuilder<SyncCursor> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => x.SyncConfigurationId).IsUnique();
    }
}
