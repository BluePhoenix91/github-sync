using GithubSync.Api.Sync.Ingestion;
using GithubSync.Data.Entities;
using GithubSync.Data.Enums;
using GithubSync.Sources.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace GithubSync.Tests.Sync.Ingestion;

public class CanonicalEventMapperTests
{
    public static TheoryData<GitHubEventKind, EventKind> KindMappingCases() => new()
    {
        { GitHubEventKind.IssueOpened, EventKind.IssueCreated },
        { GitHubEventKind.BodyEdited, EventKind.IssueEdited },
        { GitHubEventKind.Labeled, EventKind.IssueLabeled },
        { GitHubEventKind.Unlabeled, EventKind.IssueUnlabeled },
        { GitHubEventKind.Assigned, EventKind.IssueAssigned },
        { GitHubEventKind.Unassigned, EventKind.IssueUnassigned },
        { GitHubEventKind.Typed, EventKind.IssueTyped },
        { GitHubEventKind.Untyped, EventKind.IssueUntyped },
        { GitHubEventKind.ParentAdded, EventKind.IssueParentAdded },
        { GitHubEventKind.ParentRemoved, EventKind.IssueParentRemoved },
        { GitHubEventKind.Commented, EventKind.IssueCommented },
        { GitHubEventKind.Closed, EventKind.IssueClosed },
        { GitHubEventKind.Reopened, EventKind.IssueReopened },
    };

    [Theory]
    [MemberData(nameof(KindMappingCases))]
    public async Task Each_known_GitHubEventKind_maps_to_corresponding_canonical_EventKind(
        GitHubEventKind sourceKind, EventKind expectedKind)
    {
        var source = MakeSource(kind: sourceKind);
        var mapper = MakeMapper();

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal(expectedKind, result!.EventKind);
    }

    [Fact]
    public async Task Unknown_GitHubEventKind_value_logs_warning_and_returns_null()
    {
        // Out-of-range enum value — guards against future GitHubEventKind additions
        // landing without a corresponding canonical mapping.
        var source = MakeSource(kind: (GitHubEventKind)999, sourceEventId: "TL_unknown");
        var sink = new CapturingSink();
        var mapper = MakeMapper(logger: BuildLogger<CanonicalEventMapper>(sink));

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.Null(result);
        var warning = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning);
        Assert.Equal("github", ScalarText(warning.Properties["Source"]));
        Assert.Equal(source.SourceEntityId, ScalarText(warning.Properties["ExternalId"]));
        Assert.False(string.IsNullOrWhiteSpace(ScalarText(warning.Properties["Reason"])));
    }

    [Fact]
    public async Task Non_IssueEdited_event_with_null_SourceEventId_throws()
    {
        // Per docs/idempotency.md: state-transition events MUST have a SourceEventId.
        // A null on anything other than BodyEdited is a producer-side bug; mapper fails loud.
        var source = MakeSource(kind: GitHubEventKind.Closed, sourceEventId: null);
        var mapper = MakeMapper();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await mapper.MapAsync(source, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task IssueEdited_event_with_null_SourceEventId_maps_successfully()
    {
        var source = MakeSource(kind: GitHubEventKind.BodyEdited, sourceEventId: null);
        var mapper = MakeMapper();

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal(EventKind.IssueEdited, result!.EventKind);
        Assert.Null(result.SourceEventId);
    }

    [Fact]
    public async Task Null_actor_yields_null_ActorId_and_does_not_call_resolver()
    {
        var source = MakeSource(actor: null);
        var resolver = new FakeActorResolver();
        var mapper = MakeMapper(resolver: resolver);

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Null(result!.ActorId);
        Assert.Empty(resolver.Calls);
    }

    [Fact]
    public async Task Non_null_actor_is_resolved_via_IActorResolver()
    {
        var actor = new GitHubActor("octocat", "1", GitHubActorKind.User);
        var expectedActorId = Guid.NewGuid();
        var source = MakeSource(actor: actor);
        var resolver = new FakeActorResolver { ReturnId = expectedActorId };
        var mapper = MakeMapper(resolver: resolver);

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal(expectedActorId, result!.ActorId);
        Assert.Same(actor, Assert.Single(resolver.Calls));
    }

    [Fact]
    public async Task EventTime_is_normalised_to_UTC_offset()
    {
        // GitHub's GraphQL emits UTC already, but the contract is "every datetime is UTC"
        // — defensive normalisation guards against non-UTC inputs leaking through.
        var nonUtc = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.FromHours(1));
        var source = MakeSource(eventTime: nonUtc);
        var mapper = MakeMapper();

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.Zero, result!.EventTime.Offset);
        Assert.Equal(nonUtc.UtcDateTime, result.EventTime.UtcDateTime);
    }

    [Fact]
    public async Task IngestedAt_is_set_from_TimeProvider()
    {
        var fakeNow = new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(fakeNow);
        var mapper = MakeMapper(time: time);

        var result = await mapper.MapAsync(MakeSource(), Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal(fakeNow, result!.IngestedAt);
    }

    [Fact]
    public async Task Carries_source_SourceEntityType_SourceEntityId_PayloadJson_SyncConfigurationId()
    {
        var syncConfigurationId = Guid.NewGuid();
        var source = MakeSource(
            sourceEntityId: "42",
            payloadJson: "{\"hello\":\"world\"}");
        var mapper = MakeMapper();

        var result = await mapper.MapAsync(source, syncConfigurationId, default);

        Assert.NotNull(result);
        Assert.Equal(Source.GitHub, result!.Source);
        Assert.Equal(SourceEntityType.Issue, result.SourceEntityType);
        Assert.Equal("42", result.SourceEntityId);
        Assert.Equal("{\"hello\":\"world\"}", result.PayloadJson);
        Assert.Equal(syncConfigurationId, result.SyncConfigurationId);
    }

    [Fact]
    public async Task Generates_a_unique_non_empty_Id_per_mapped_event()
    {
        var mapper = MakeMapper();
        var configId = Guid.NewGuid();

        var a = await mapper.MapAsync(MakeSource(sourceEventId: "a"), configId, default);
        var b = await mapper.MapAsync(MakeSource(sourceEventId: "b"), configId, default);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(Guid.Empty, a!.Id);
        Assert.NotEqual(Guid.Empty, b!.Id);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task Copies_SourceEventId_through_for_state_transition_events()
    {
        var source = MakeSource(kind: GitHubEventKind.Closed, sourceEventId: "CE_42");
        var mapper = MakeMapper();

        var result = await mapper.MapAsync(source, Guid.NewGuid(), default);

        Assert.NotNull(result);
        Assert.Equal("CE_42", result!.SourceEventId);
    }

    private static GitHubIssueEvent MakeSource(
        string sourceEntityId = "1",
        string? sourceEventId = "evt_1",
        GitHubEventKind kind = GitHubEventKind.IssueOpened,
        DateTimeOffset? eventTime = null,
        GitHubActor? actor = null,
        string payloadJson = "{}") =>
        new(sourceEntityId,
            sourceEventId,
            kind,
            eventTime ?? new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
            IssueUpdatedAt: new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero),
            actor,
            payloadJson);

    private static CanonicalEventMapper MakeMapper(
        IActorResolver? resolver = null,
        ILogger<CanonicalEventMapper>? logger = null,
        TimeProvider? time = null) =>
        new(resolver ?? new FakeActorResolver(),
            logger ?? NullLogger<CanonicalEventMapper>.Instance,
            time ?? TimeProvider.System);

    private static ILogger<T> BuildLogger<T>(CapturingSink sink)
    {
        var serilog = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true).CreateLogger<T>();
    }

    private static string ScalarText(LogEventPropertyValue value) =>
        ((ScalarValue)value).Value?.ToString() ?? "";

    private sealed class FakeActorResolver : IActorResolver
    {
        public Guid? ReturnId { get; set; } = Guid.NewGuid();
        public List<GitHubActor?> Calls { get; } = new();

        public ValueTask<Guid?> ResolveAsync(GitHubActor? actor, CancellationToken ct)
        {
            if (actor is null) return ValueTask.FromResult<Guid?>(null);
            Calls.Add(actor);
            return ValueTask.FromResult(ReturnId);
        }
    }
}
