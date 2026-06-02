using GithubSync.Data;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace GithubSync.Tests.Data;

public class SyncRunEntityShapeTests
{
    [Fact]
    public void SyncRuns_DbSet_is_exposed_on_AppDbContext()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var run = new SyncRun
        {
            Id = Guid.NewGuid(),
            SyncConfigurationId = Guid.NewGuid(),
            Source = Source.GitHub,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Status = SyncRunStatus.Success,
            IssuesCommitted = 3,
            EventsAttempted = 5,
            EventsInserted = 4,
            EventsSkippedUnknownKind = 1,
            DurationMs = 1234,
            Message = null,
        };
        db.SyncRuns.Add(run);
        db.SaveChanges();

        var fetched = db.SyncRuns.Single();
        Assert.Equal(3, fetched.IssuesCommitted);
        Assert.Equal(SyncRunStatus.Success, fetched.Status);
    }
}
