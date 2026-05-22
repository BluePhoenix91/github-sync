using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class IdentityMappingConfiguration : IEntityTypeConfiguration<IdentityMapping>
{
    public void Configure(EntityTypeBuilder<IdentityMapping> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.CanonicalActorId, x.TargetSystem }).IsUnique();

        builder.HasOne(x => x.CanonicalActor)
            .WithMany()
            .HasForeignKey(x => x.CanonicalActorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
