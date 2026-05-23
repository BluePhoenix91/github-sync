using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class WorkItemMappingConfiguration : IEntityTypeConfiguration<WorkItemMapping>
{
    public void Configure(EntityTypeBuilder<WorkItemMapping> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new
        {
            x.SyncConfigurationId,
            x.Source,
            x.SourceEntityType,
            x.SourceEntityId,
        }).IsUnique();

        builder.HasIndex(x => new
        {
            x.SyncConfigurationId,
            x.TargetSystem,
            x.TargetEntityId,
        }).IsUnique();
    }
}
