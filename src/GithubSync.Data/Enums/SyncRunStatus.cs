namespace GithubSync.Data.Enums;

public enum SyncRunStatus
{
    Success = 1,

    // Reserved for a future state where a run completes some work and aborts the rest.
    // v1 orchestrators (#70 import, #72 export) never write Partial — they pick Success or Failed
    // because today's PersistResult doesn't expose interim counts on throw.
    Partial = 2,

    Failed = 3,
}
