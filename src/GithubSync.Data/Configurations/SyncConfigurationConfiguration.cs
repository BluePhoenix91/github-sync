using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class SyncConfigurationConfiguration : IEntityTypeConfiguration<SyncConfiguration>
{
    public void Configure(EntityTypeBuilder<SyncConfiguration> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TargetTypeMapping).HasColumnType("jsonb");

        builder.HasIndex(x => new
        {
            x.Source,
            x.SourceOwner,
            x.SourceRepo,
            x.TargetSystem,
            x.TargetOrganization,
            x.TargetProject,
        }).IsUnique();

        builder.HasOne(x => x.Cursor)
            .WithOne(x => x.SyncConfiguration)
            .HasForeignKey<SyncCursor>(x => x.SyncConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Events)
            .WithOne(x => x.SyncConfiguration)
            .HasForeignKey(x => x.SyncConfigurationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.WorkItemMappings)
            .WithOne(x => x.SyncConfiguration)
            .HasForeignKey(x => x.SyncConfigurationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
