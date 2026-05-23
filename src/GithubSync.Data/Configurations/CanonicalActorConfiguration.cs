using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class CanonicalActorConfiguration : IEntityTypeConfiguration<CanonicalActor>
{
    public void Configure(EntityTypeBuilder<CanonicalActor> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.Source, x.SourceActorId }).IsUnique();
    }
}
