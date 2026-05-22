using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class TargetUserPoolConfiguration : IEntityTypeConfiguration<TargetUserPool>
{
    public void Configure(EntityTypeBuilder<TargetUserPool> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.TargetSystem, x.TargetUserId }).IsUnique();
    }
}
