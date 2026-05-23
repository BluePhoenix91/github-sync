using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetter>
{
    public void Configure(EntityTypeBuilder<DeadLetter> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RawResponse).HasColumnType("jsonb");

        // Non-unique index for triage queries filtering unresolved dead letters per event.
        builder.HasIndex(x => new { x.CanonicalEventId, x.Resolved });

        builder.HasOne(x => x.CanonicalEvent)
            .WithMany()
            .HasForeignKey(x => x.CanonicalEventId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
