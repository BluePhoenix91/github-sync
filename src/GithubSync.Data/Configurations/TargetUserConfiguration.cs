using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class TargetUserConfiguration : IEntityTypeConfiguration<TargetUser>
{
    public void Configure(EntityTypeBuilder<TargetUser> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.TargetSystem, x.TargetUserId }).IsUnique();
    }
}
