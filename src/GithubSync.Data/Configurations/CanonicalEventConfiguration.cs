using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GithubSync.Data.Configurations;

public class CanonicalEventConfiguration : IEntityTypeConfiguration<CanonicalEvent>
{
    public void Configure(EntityTypeBuilder<CanonicalEvent> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PayloadJson).HasColumnType("jsonb");

        // Composite uniqueness with NULLS NOT DISTINCT so two rows with an identical
        // (Source, SourceEntityType, SourceEntityId, EventKind, EventTime) tuple and
        // both-null SourceEventId still collide. See docs/idempotency.md.
        builder.HasIndex(x => new
        {
            x.Source,
            x.SourceEntityType,
            x.SourceEntityId,
            x.EventKind,
            x.EventTime,
            x.SourceEventId,
        })
        .IsUnique()
        .AreNullsDistinct(false);

        builder.HasOne(x => x.Actor)
            .WithMany()
            .HasForeignKey(x => x.ActorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
