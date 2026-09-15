namespace LoadingCache;

internal sealed class LoadingCacheTestHooks
{
    internal Action? AfterFlightInstalled { get; init; }

    internal Action? BeforePublish { get; init; }

    // Internal race-test seam. The entry snapshot is complete at this point,
    // but IsReady has not yet been release-published.
    internal Action? BeforeReadyPublish { get; init; }

    // A read observed expiry and released entry.Sync, before physical cleanup
    // acquires the engine gate. No hook is invoked on a resident hit.
    internal Action? BeforeExpiredReadCleanup { get; init; }

    // Internal race-test seam. The variable read-expiry revision has been
    // committed before this callback is invoked.
    internal Action? AfterReadExpiryUpdate { get; init; }

    // Internal race-test seam. The timer gate is held and the current arm
    // revision has been captured, immediately before ITimer.Change.
    internal Action? BeforeExpirationTimerArm { get; init; }

    // Internal race-test seam. The engine gate and policy gate are held while
    // policy maintenance is paused, so a ready hit can be checked independently.
    internal Action? BeforePolicyMaintenance { get; init; }

    // Internal race-test seam. The first maintenance pass found no queued read
    // event and is paused immediately before the signal handoff re-check.
    internal Action? BeforeMaintenanceSignalClear { get; init; }

    // The pass has released all cache locks but the coordinator still owns
    // the worker, allowing an exact final-write/re-arm interleaving.
    internal Action? AfterPolicyMaintenance { get; init; }

    internal Action? BeforeCompletion { get; init; }

    // Refresh-only seam after value publication and lock release, before timer
    // arming and promise completion. Tests can exercise claimed-failure rollback.
    internal Action? AfterRefreshPublished { get; init; }
}
