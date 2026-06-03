using System.Reflection;
using GithubSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GithubSync.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SyncConfiguration> SyncConfigurations => Set<SyncConfiguration>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<CanonicalEvent> CanonicalEvents => Set<CanonicalEvent>();
    public DbSet<CanonicalActor> CanonicalActors => Set<CanonicalActor>();
    public DbSet<IdentityMapping> IdentityMappings => Set<IdentityMapping>();
    public DbSet<TargetUser> TargetUsers => Set<TargetUser>();
    public DbSet<WorkItemMapping> WorkItemMappings => Set<WorkItemMapping>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
