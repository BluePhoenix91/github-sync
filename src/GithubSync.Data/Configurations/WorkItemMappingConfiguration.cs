using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class WorkItemMappingConfiguration : IEntityTypeConfiguration<WorkItemMapping>
{
    public void Configure(EntityTypeBuilder<WorkItemMapping> builder)
    {
        builder.HasKey(x => x.Id);

        // Source side: one source entity maps to exactly one target within a config.
        builder.HasIndex(x => new
        {
            x.SyncConfigurationId,
            x.Source,
            x.SourceEntityType,
            x.SourceEntityId,
        }).IsUnique();

        // Target side: the same target ID cannot be claimed by two different source entities.
        builder.HasIndex(x => new
        {
            x.SyncConfigurationId,
            x.TargetSystem,
            x.TargetEntityId,
        }).IsUnique();
    }
}
