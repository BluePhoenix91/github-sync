using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class SyncConfigurationConfiguration : IEntityTypeConfiguration<SyncConfiguration>
{
    public void Configure(EntityTypeBuilder<SyncConfiguration> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceLocator).HasColumnType("jsonb");
        builder.Property(x => x.TargetLocator).HasColumnType("jsonb");
        builder.Property(x => x.TargetTypeMapping).HasColumnType("jsonb");

        // Postgres jsonb equality is canonicalised (sorted keys, no insignificant whitespace),
        // so identical content with different key order still collides. Casing of keys is *not*
        // canonicalised — LocatorJsonOptions pins serialisation to camelCase so writes are
        // deterministic.
        builder.HasIndex(x => new
        {
            x.Source,
            x.SourceLocator,
            x.TargetSystem,
            x.TargetLocator,
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

        builder.HasMany(x => x.Runs)
            .WithOne(x => x.SyncConfiguration)
            .HasForeignKey(x => x.SyncConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
