using System.Diagnostics;
using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class LoadingCacheContractTests
{
    [Test]
    public async Task GetAsyncCachesValueAndTryGetAcceptsDefaultValue(
        CancellationToken cancellationToken
    )
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(0);
            }
        );
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(0);
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ComparerEqualKeysShareAFlightAndResidentValue(
        CancellationToken cancellationToken
    )
    {
        int calls = 0;
        await using var cache = Create<string, string>(
            (key, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(key.ToUpperInvariant());
            },
            comparer: StringComparer.OrdinalIgnoreCase
        );
        string[] values = await Task.WhenAll(
            Get(cache, "Key", cancellationToken).AsTask(),
            Get(cache, "kEy", cancellationToken).AsTask()
        );
        foreach (string value in values)
        {
            await Assert.That(value).IsEqualTo("KEY");
        }

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(cache.TryGet("KEY", out string? resident)).IsTrue();
        await Assert.That(resident).IsEqualTo("KEY");
    }

    [Test]
    public async Task InvalidOptionsFailFast()
    {
        await Assert
            .That(() => Create<int, int>(CompletedLoader<int, int>, Options(maximumSize: 0)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() => Create<int, int>(CompletedLoader<int, int>, Options(maximumSize: -1)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() => Create<int, int>(CompletedLoader<int, int>, Options(maxConcurrentLoads: 0)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() =>
                Create<int, int>(CompletedLoader<int, int>, Options(maxConcurrentLoads: -1))
            )
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() =>
                Create<int, int>(
                    CompletedLoader<int, int>,
                    Options(expireAfterWrite: TimeSpan.Zero)
                )
            )
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert
            .That(() =>
                Create<int, int>(
                    CompletedLoader<int, int>,
                    Options(expireAfterAccess: TimeSpan.FromTicks(-1))
                )
            )
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ImmutableOptionsAreAppliedAtConstruction(CancellationToken cancellationToken)
    {
        var clock = new ManualTimeProvider();
        var options = Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock);
        await using var cache = Create<int, int>((_, _) => Task.FromResult(42), options);
        await Get(cache, 1, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(cache.TryGet(1, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task SameKeyHasOneLoaderForMoreThanOneHundredParallelCallers(
        CancellationToken cancellationToken
    )
    {
        const int callerCount = 128;
        var loaderEntered = NewSignal();
        var releaseLoader = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                {
                    throw new AssertionException(
                        "A same-generation flight started more than one loader."
                    );
                }

                loaderEntered.TrySetResult(true);
                await releaseLoader.Task.ConfigureAwait(false);
                return 7;
            }
        );
        var ready = Enumerable.Range(0, callerCount).Select(_ => NewSignal()).ToArray();
        var go = NewSignal();
        var waiters = new Task<int>[callerCount];
        for (int index = 0; index < callerCount; index++)
        {
            waiters[index] = GetWhenReleasedAsync(cache, ready[index], go.Task);
        }

        try
        {
            await AwaitWithTestTimeout(
                Task.WhenAll(ready.Select(signal => signal.Task)),
                cancellationToken
            );
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            releaseLoader.TrySetResult(true);
            int[] values = await AwaitWithTestTimeout(Task.WhenAll(waiters), cancellationToken);
            await Assert.That(values.Length).IsEqualTo(callerCount);
            foreach (int value in values)
            {
                await Assert.That(value).IsEqualTo(7);
            }

            await Assert.That(calls).IsEqualTo(1);
        }
        finally
        {
            go.TrySetResult(true);
            releaseLoader.TrySetResult(true);
            await Task.WhenAll(waiters).WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task DifferentKeysCanEnterLoadersConcurrently(CancellationToken cancellationToken)
    {
        var entered = NewSignal();
        var release = NewSignal();
        int active = 0;
        var maximumActive = new AtomicCounter();
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive.UpdateMaximum(current);
                if (current == 2)
                {
                    entered.TrySetResult(true);
                }

                await release.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref active);
                return 1;
            },
            Options(maxConcurrentLoads: 2)
        );
        var first = Get(cache, 1, cancellationToken).AsTask();
        var second = Get(cache, 2, cancellationToken).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task, cancellationToken);
            await Assert.That(maximumActive.Value).IsEqualTo(2);
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task WaiterJoiningAfterFlightInstallationCannotLeavePromiseUnstarted(
        CancellationToken cancellationToken
    )
    {
        var loaderEntered = NewSignal();
        var release = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                loaderEntered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 9;
            }
        );
        var first = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
        var second = Get(cache, 1, cancellationToken).AsTask();
        release.TrySetResult(true);
        int[] values = await AwaitWithTestTimeout(Task.WhenAll(first, second), cancellationToken);
        await Assert.That(values[0]).IsEqualTo(9);
        await Assert.That(values[1]).IsEqualTo(9);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task JoiningCallerStartsFlightWhenInstallerIsPausedAfterInstallation(
        CancellationToken cancellationToken
    )
    {
        await using var installation = new BlockingTestHook(TestTimeout);
        var loaderEntered = NewSignal();
        var releaseLoader = NewSignal();
        int calls = 0;
        var hooks = new LoadingCacheTestHooks { AfterFlightInstalled = installation.Invoke };
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                loaderEntered.TrySetResult(true);
                await releaseLoader.Task.ConfigureAwait(false);
                return 9;
            },
            Options(testHooks: hooks)
        );
        var installer = Task
            .Factory.StartNew(
                state => Get((IAsyncLoadingCache<int, int>)state!, 1, cancellationToken).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<int>? joining = null;
        try
        {
            await AwaitWithTestTimeout(installation.Entered, cancellationToken);
            joining = Get(cache, 1, cancellationToken).AsTask();
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            installation.Release();
            releaseLoader.TrySetResult(true);
            await Assert
                .That((await AwaitWithTestTimeout(installer, cancellationToken)))
                .IsEqualTo(9);
            await Assert
                .That((await AwaitWithTestTimeout(joining, cancellationToken)))
                .IsEqualTo(9);
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(installation.TimedOut).IsFalse();
        }
        finally
        {
            installation.Release();
            releaseLoader.TrySetResult(true);
            await Task.WhenAll(installer, joining ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task BeforePublishHookIsOutsideCacheLockAndSetWinsTheGeneration(
        CancellationToken cancellationToken
    )
    {
        await using var publication = new BlockingTestHook(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforePublish = publication.Invoke };
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(100),
            Options(testHooks: hooks)
        );
        var pending = Task
            .Factory.StartNew(
                state => Get((IAsyncLoadingCache<int, int>)state!, 1, cancellationToken).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task? set = null;
        try
        {
            await AwaitWithTestTimeout(publication.Entered, cancellationToken);
            set = Task.Factory.StartNew(
                static state => ((IAsyncLoadingCache<int, int>)state!).Set(1, 101),
                cache,
                cancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await AwaitWithTestTimeout(set, cancellationToken);
            publication.Release();
            await Assert
                .That((await AwaitWithTestTimeout(pending, cancellationToken)))
                .IsEqualTo(100);
            await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(101);
            await Assert.That(publication.TimedOut).IsFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(pending, set ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task BeforeCompletionHookIsOutsideCacheLockAndSetSurvivesCompletion(
        CancellationToken cancellationToken
    )
    {
        await using var completion = new BlockingTestHook(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke };
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(110),
            Options(testHooks: hooks)
        );
        var pending = Task
            .Factory.StartNew(
                state => Get((IAsyncLoadingCache<int, int>)state!, 1, cancellationToken).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task? set = null;
        try
        {
            await AwaitWithTestTimeout(completion.Entered, cancellationToken);
            set = Task.Factory.StartNew(
                static state => ((IAsyncLoadingCache<int, int>)state!).Set(1, 111),
                cache,
                cancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await AwaitWithTestTimeout(set, cancellationToken);
            completion.Release();
            await Assert
                .That((await AwaitWithTestTimeout(pending, cancellationToken)))
                .IsEqualTo(110);
            await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(111);
            await Assert.That(completion.TimedOut).IsFalse();
        }
        finally
        {
            completion.Release();
            await Task.WhenAll(pending, set ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task TryGetDoesNotWaitForOrStartPendingLoad(CancellationToken cancellationToken)
    {
        var entered = NewSignal();
        var release = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 10;
            }
        );
        var pending = Get(cache, 1, cancellationToken).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task, cancellationToken);
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(calls).IsEqualTo(1);
            release.TrySetResult(true);
            await Assert
                .That((await AwaitWithTestTimeout(pending, cancellationToken)))
                .IsEqualTo(10);
        }
        finally
        {
            release.TrySetResult(true);
            await pending.WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task SynchronousThrowIsNotCachedAndCanRetry(CancellationToken cancellationToken)
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new InvalidOperationException("first attempt")
                    : Task.FromResult(11)
        );
        Task firstAttempt = Get(cache, 1, cancellationToken).AsTask();
        await Assert.That(() => firstAttempt).ThrowsExactly<InvalidOperationException>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(11);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task AsyncFaultIsNotCachedAndCanRetry(CancellationToken cancellationToken)
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<int>(new InvalidOperationException("async fault"))
                    : Task.FromResult(12)
        );
        Task firstAttempt = Get(cache, 1, cancellationToken).AsTask();
        await Assert.That(() => firstAttempt).ThrowsExactly<InvalidOperationException>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(12);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task LoaderCancellationIsNotCachedAndCanRetry(CancellationToken cancellationToken)
    {
        int calls = 0;
        using var loaderCancellation = new CancellationTokenSource();
        await loaderCancellation.CancelAsync();
        CancellationToken canceledToken = loaderCancellation.Token;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromCanceled<int>(canceledToken)
                    : Task.FromResult(13)
        );
        Task firstAttempt = Get(cache, 1, cancellationToken).AsTask();
        await Assert.That(() => firstAttempt).Throws<OperationCanceledException>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(13);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task NullTaskIsAContractFailureAndCanRetry(CancellationToken cancellationToken)
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) => Interlocked.Increment(ref calls) == 1 ? null! : Task.FromResult(14)
        );
        Task firstAttempt = Get(cache, 1, cancellationToken).AsTask();
        await Assert.That(() => firstAttempt).Throws<Exception>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(14);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task NullReferenceValueIsAContractFailureAndCanRetry(
        CancellationToken cancellationToken
    )
    {
        int calls = 0;
        await using var cache = Create<int, string>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromResult<string>(null!)
                    : Task.FromResult("value")
        );
        Task firstAttempt = Get(cache, 1, cancellationToken).AsTask();
        await Assert.That(() => firstAttempt).Throws<Exception>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo("value");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task NullKeyIsRejectedBeforeLoaderStarts(CancellationToken cancellationToken)
    {
        int calls = 0;
        await using var cache = Create<string, string>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("unexpected");
            }
        );
        Exception? rejection = null;
        try
        {
            await Get(cache, null!, cancellationToken);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        await Assert.That<object>(rejection!).IsTypeOf<ArgumentNullException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task CallerCancellationOnlyCancelsThatWaiter(CancellationToken cancellationToken)
    {
        var loaderEntered = NewSignal();
        var release = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                loaderEntered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 21;
            }
        );
        using var canceled = new CancellationTokenSource();
        var first = Get(cache, 1, canceled.Token).AsTask();
        Task<int>? second = null;
        try
        {
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            second = Get(cache, 1, CancellationToken.None).AsTask();
            await canceled.CancelAsync();
            await Assert.That(() => first).Throws<OperationCanceledException>();
            release.TrySetResult(true);
            await Assert
                .That((await AwaitWithTestTimeout(second, cancellationToken)))
                .IsEqualTo(21);
            await Assert.That((await Get(cache, 1, CancellationToken.None))).IsEqualTo(21);
            await Assert.That(calls).IsEqualTo(1);
        }
        finally
        {
            await canceled.CancelAsync();
            release.TrySetResult(true);
            await ObserveForCleanup(Task.WhenAll(first, second ?? Task.CompletedTask));
        }
    }

    [Test]
    public async Task AllCanceledWaitersDoNotCancelSharedLoad(CancellationToken cancellationToken)
    {
        var loaderEntered = NewSignal();
        var release = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                loaderEntered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 22;
            }
        );
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = Get(cache, 1, firstCancellation.Token).AsTask();
        Task<int>? second = null;
        try
        {
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            second = Get(cache, 1, secondCancellation.Token).AsTask();
            await firstCancellation.CancelAsync();
            await secondCancellation.CancelAsync();
            await Assert.That(() => first).Throws<OperationCanceledException>();
            await Assert.That(() => second).Throws<OperationCanceledException>();
            release.TrySetResult(true);
            await Assert.That((await Get(cache, 1, CancellationToken.None))).IsEqualTo(22);
            await Assert.That(calls).IsEqualTo(1);
        }
        finally
        {
            await firstCancellation.CancelAsync();
            await secondCancellation.CancelAsync();
            release.TrySetResult(true);
            await ObserveForCleanup(Task.WhenAll(first, second ?? Task.CompletedTask));
        }
    }

    [Test]
    public async Task PreCanceledMissDoesNotStartLoaderAndPreCanceledHitDoesNotReturnValue()
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(23);
            }
        );
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Task firstAttempt = Get(cache, 1, cancellation.Token).AsTask();
        await Assert.That(() => firstAttempt).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(0);
        cache.Set(1, 24);
        Task secondAttempt = Get(cache, 1, cancellation.Token).AsTask();
        await Assert.That(() => secondAttempt).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidateFencesLateSuccessFromEarlierLoad(
        CancellationToken cancellationToken
    )
    {
        var firstEntered = NewSignal();
        var firstLoad = NewSignal<int>();
        var secondLoad = NewSignal<int>();
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
            {
                firstEntered.TrySetResult(true);
                return Interlocked.Increment(ref calls) switch
                {
                    1 => firstLoad.Task,
                    2 => secondLoad.Task,
                    _ => throw new AssertionException("unexpected third load"),
                };
            }
        );
        var old = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task, cancellationToken);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        var current = Get(cache, 1, cancellationToken).AsTask();
        secondLoad.TrySetResult(32);
        await Assert.That((await AwaitWithTestTimeout(current, cancellationToken))).IsEqualTo(32);
        firstLoad.TrySetResult(31);
        await Assert.That((await AwaitWithTestTimeout(old, cancellationToken))).IsEqualTo(31);
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(32);
    }

    [Test]
    public async Task InvalidateFencesLateFailureWithoutDeletingNewValue(
        CancellationToken cancellationToken
    )
    {
        var firstEntered = NewSignal();
        var firstLoad = NewSignal<int>();
        var secondLoad = NewSignal<int>();
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) switch
                {
                    1 => FirstLoad(firstEntered, firstLoad),
                    2 => secondLoad.Task,
                    _ => throw new AssertionException("unexpected third load"),
                }
        );
        var old = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task, cancellationToken);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        var current = Get(cache, 1, cancellationToken).AsTask();
        secondLoad.TrySetResult(34);
        await Assert.That((await AwaitWithTestTimeout(current, cancellationToken))).IsEqualTo(34);
        firstLoad.TrySetException(new InvalidOperationException("late failure"));
        await Assert
            .That(((Func<Task>)(() => AwaitWithTestTimeout(old, cancellationToken))))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(34);
    }

    [Test]
    public async Task SetFencesLateLoadCompletion(CancellationToken cancellationToken)
    {
        var entered = NewSignal();
        var load = NewSignal<int>();
        await using var cache = Create<int, int>((_, _) => FirstLoad(entered, load));
        var pending = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(entered.Task, cancellationToken);
        cache.Set(1, 35);
        load.TrySetResult(36);
        await Assert.That((await AwaitWithTestTimeout(pending, cancellationToken))).IsEqualTo(36);
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(35);
    }

    [Test]
    public async Task ClearFencesOldEpochCompletion(CancellationToken cancellationToken)
    {
        var oldEntered = NewSignal();
        var oldLoad = NewSignal<int>();
        var newLoad = NewSignal<int>();
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) switch
                {
                    1 => FirstLoad(oldEntered, oldLoad),
                    2 => newLoad.Task,
                    _ => throw new AssertionException("unexpected third load"),
                }
        );
        var old = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(oldEntered.Task, cancellationToken);
        cache.Clear();
        var current = Get(cache, 1, cancellationToken).AsTask();
        newLoad.TrySetResult(38);
        await Assert.That((await AwaitWithTestTimeout(current, cancellationToken))).IsEqualTo(38);
        oldLoad.TrySetResult(37);
        await Assert.That((await AwaitWithTestTimeout(old, cancellationToken))).IsEqualTo(37);
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(38);
    }

    [Test]
    public async Task MaximumConcurrentLoadsRejectsDistinctKeyButAllowsJoining(
        CancellationToken cancellationToken
    )
    {
        var firstEntered = NewSignal();
        var release = NewSignal();
        int calls = 0;
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                firstEntered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 41;
            },
            Options(maxConcurrentLoads: 1)
        );
        var first = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task, cancellationToken);
        var joining = Get(cache, 1, cancellationToken).AsTask();
        await AssertRejectedWithoutHangingAsync(
            () => Get(cache, 2, cancellationToken).AsTask(),
            cancellationToken
        );
        release.TrySetResult(true);
        int[] values = await AwaitWithTestTimeout(Task.WhenAll(first, joining), cancellationToken);
        await Assert.That(values[0]).IsEqualTo(41);
        await Assert.That(values[1]).IsEqualTo(41);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidateAndClearDoNotReleasePermitForRunningLoader(
        CancellationToken cancellationToken
    )
    {
        var entered = NewSignal();
        var release = NewSignal();
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 43;
            },
            Options(maxConcurrentLoads: 1)
        );
        var pending = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(entered.Task, cancellationToken);
        cache.Invalidate(1);
        await AssertRejectedWithoutHangingAsync(
            () => Get(cache, 2, cancellationToken).AsTask(),
            cancellationToken
        );
        cache.Clear();
        await AssertRejectedWithoutHangingAsync(
            () => Get(cache, 3, cancellationToken).AsTask(),
            cancellationToken
        );
        release.TrySetResult(true);
        await AwaitWithTestTimeout(pending, cancellationToken);
    }

    [Test]
    public async Task ExpireAfterWriteUsesPublishTimestampAndExactBoundary(
        CancellationToken cancellationToken
    )
    {
        var clock = new ManualTimeProvider();
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(50 + calls);
            },
            Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock)
        );
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(51);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(cache.TryGet(1, out int beforeBoundary)).IsTrue();
        await Assert.That(beforeBoundary).IsEqualTo(51);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(52);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ExpireAfterWriteStartsAtSuccessfulPublishNotLoadStart(
        CancellationToken cancellationToken
    )
    {
        var clock = new ManualTimeProvider();
        var entered = NewSignal();
        var release = NewSignal();
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return 60;
            },
            Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock)
        );
        var load = Get(cache, 1, cancellationToken).AsTask();
        await AwaitWithTestTimeout(entered.Task, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        release.TrySetResult(true);
        await Assert.That((await AwaitWithTestTimeout(load, cancellationToken))).IsEqualTo(60);
        await Assert.That(cache.TryGet(1, out int published)).IsTrue();
        await Assert.That(published).IsEqualTo(60);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task TimeProviderTimestampRemainsMonotonicWhenWallClockMovesBack(
        CancellationToken cancellationToken
    )
    {
        var clock = new ManualTimeProvider();
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(61),
            Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock)
        );
        await Get(cache, 1, cancellationToken);
        clock.MoveWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task ExpireAfterAccessAndWriteUseTheEarlierDeadline(
        CancellationToken cancellationToken
    )
    {
        var clock = new ManualTimeProvider();
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(62),
            Options(
                expireAfterWrite: TimeSpan.FromSeconds(10),
                expireAfterAccess: TimeSpan.FromSeconds(5),
                timeProvider: clock
            )
        );
        await Get(cache, 1, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task CleanUpConvergesResidentCountWithinMaximumSize()
    {
        await using var cache = Create<int, int>(
            (key, _) => Task.FromResult(key),
            Options(maximumSize: 8)
        );
        for (int key = 0; key < 128; key++)
        {
            cache.Set(key, key);
        }

        cache.CleanUp();
        AssertInvariants(cache);
        await Assert.That(cache.EstimatedCount).IsBetween(0, 8);
    }

    [Test]
    public async Task DisposeRejectsNewOperationsAndCooperativeLoaderDoesNotHangShutdown(
        CancellationToken cancellationToken
    )
    {
        var entered = NewSignal();
        var cache = Create<int, int>(
            async (_, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await WaitForeverAsync(cancellationToken).ConfigureAwait(false);
                return 70;
            }
        );
        var pending = Get(cache, 1, cancellationToken).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task, cancellationToken);
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask(), cancellationToken);
            var completion = await CaptureExceptionAsync(() =>
                AwaitWithTestTimeout(pending, cancellationToken)
            );
            await Assert
                .That((completion is OperationCanceledException or ObjectDisposedException))
                .IsTrue()
                .Because($"started load completed with unexpected exception: {completion}");
            await Assert
                .That(() => Get(cache, 2, cancellationToken).AsTask())
                .ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            await cache.DisposeAsync();
            await ObserveForCleanup(pending);
        }
    }

    [Test]
    public async Task DisposeDoesNotPublishLateCompletionFromNonCooperativeLoader(
        CancellationToken cancellationToken
    )
    {
        var entered = NewSignal();
        var release = NewSignal<int>();
        var loaderReturned = NewSignal<bool>();
        var cache = Create<int, int>(
            async (_, _) =>
            {
                entered.TrySetResult(true);
                try
                {
                    return await release.Task.ConfigureAwait(false);
                }
                finally
                {
                    loaderReturned.TrySetResult(true);
                }
            }
        );
        Task<int>? pending = null;
        try
        {
            pending = Get(cache, 1, cancellationToken).AsTask();
            await AwaitWithTestTimeout(entered.Task, cancellationToken);
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask(), cancellationToken);
            release.TrySetResult(71);
            var lateCompletion = await CaptureExceptionAsync(() =>
                AwaitWithTestTimeout(pending, cancellationToken)
            );
            await Assert
                .That((lateCompletion is TimeoutException))
                .IsFalse()
                .Because($"non-cooperative completion hung: {lateCompletion}");
            Exception? rejection = null;
            try
            {
                await Get(cache, 1, cancellationToken);
            }
            catch (Exception exception)
            {
                rejection = exception;
            }

            await Assert.That<object>(rejection!).IsTypeOf<ObjectDisposedException>();
        }
        finally
        {
            release.TrySetResult(71);
            try
            {
                if (entered.Task.IsCompleted)
                {
                    await ObserveForCleanup(loaderReturned.Task);
                }
            }
            finally
            {
                await cache.DisposeAsync();
            }

            if (pending is not null)
            {
                await ObserveForCleanup(pending);
            }
        }
    }

    [Test]
    public async Task DisposeAccountsForFlightsDetachedByInvalidateAndClear(
        CancellationToken cancellationToken
    )
    {
        var firstInstalled = NewSignal();
        var secondInstalled = NewSignal();
        var firstLoad = NewSignal<int>();
        var secondLoad = NewSignal<int>();
        int installs = 0;
        var hooks = new LoadingCacheTestHooks
        {
            AfterFlightInstalled = () =>
            {
                if (Interlocked.Increment(ref installs) == 1)
                {
                    firstInstalled.TrySetResult(true);
                }
                else
                {
                    secondInstalled.TrySetResult(true);
                }
            },
        };
        var calls = new AtomicCounter();
        var cache = Create<int, int>(
            (_, _) =>
                calls.Increment() switch
                {
                    1 => firstLoad.Task,
                    2 => secondLoad.Task,
                    _ => throw new AssertionException("unexpected third load"),
                },
            Options(maxConcurrentLoads: 2, testHooks: hooks)
        );
        Task<int>? first = null;
        Task<int>? second = null;
        try
        {
            first = Get(cache, 1, cancellationToken).AsTask();
            await AwaitWithTestTimeout(firstInstalled.Task, cancellationToken);
            await Assert.That(cache.Invalidate(1)).IsTrue();
            cache.Clear();
            second = Get(cache, 1, cancellationToken).AsTask();
            await AwaitWithTestTimeout(secondInstalled.Task, cancellationToken);
            cache.Clear();
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask(), cancellationToken);
            firstLoad.TrySetResult(120);
            secondLoad.TrySetResult(121);
            var firstCompletion = await CaptureExceptionAsync(() =>
                AwaitWithTestTimeout(first, cancellationToken)
            );
            var secondCompletion = await CaptureExceptionAsync(() =>
                AwaitWithTestTimeout(second, cancellationToken)
            );
            await Assert
                .That((firstCompletion is TimeoutException || secondCompletion is TimeoutException))
                .IsFalse();
            await Assert.That(calls.Value).IsEqualTo(2);
        }
        finally
        {
            firstLoad.TrySetResult(120);
            secondLoad.TrySetResult(121);
            int startedLoads = calls.Value;
            try
            {
                if (startedLoads >= 1)
                {
                    await ObserveForCleanup(firstLoad.Task);
                }

                if (startedLoads >= 2)
                {
                    await ObserveForCleanup(secondLoad.Task);
                }
            }
            finally
            {
                await cache.DisposeAsync();
            }

            if (first is not null)
            {
                await ObserveForCleanup(first);
            }

            if (second is not null)
            {
                await ObserveForCleanup(second);
            }
        }
    }

    [Test]
    public async Task DisposeDoesNotWaitForAStuckLoaderCancellationCallback(
        CancellationToken cancellationToken
    )
    {
        await using var callback = new BlockingTestHook(TestTimeout);
        Action cancellationCallback = callback.Invoke;
        var releaseLoader = NewSignal<int>();
        var loaderReturned = NewSignal<bool>();
        var cache = Create<int, int>(
            async (_, cancellationToken) =>
            {
                try
                {
                    await using CancellationTokenRegistration registration =
                        cancellationToken.Register(cancellationCallback);
                    return await releaseLoader.Task.ConfigureAwait(false);
                }
                finally
                {
                    loaderReturned.TrySetResult(true);
                }
            }
        );
        var pending = Get(cache, 1, cancellationToken).AsTask();
        try
        {
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask(), cancellationToken);
            await AwaitWithTestTimeout(callback.Entered, cancellationToken);
        }
        finally
        {
            callback.Release();
            releaseLoader.TrySetResult(122);
            try
            {
                await loaderReturned.Task.WaitAsync(TestTimeout, CancellationToken.None);
                var completion = await CaptureExceptionAsync(() =>
                    pending.WaitAsync(TestTimeout, CancellationToken.None)
                );
                await Assert.That((completion is TimeoutException)).IsFalse();
            }
            finally
            {
                await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout, CancellationToken.None);
            }
        }

        await callback.Returned.WaitAsync(TestTimeout, CancellationToken.None);
        await Assert.That(callback.TimedOut).IsFalse();
    }

    [Test]
    public async Task SameKeyReentrancyFailsFastAndDoesNotLeaveScopePoisoned(
        CancellationToken cancellationToken
    )
    {
        var loader = new SameKeyReentrantLoader();
        await using var cache = Create<int, int>(loader.LoadAsync);
        loader.Cache = cache;
        var failure = await CaptureExceptionAsync(() =>
            AwaitWithTestTimeout(Get(cache, 1, cancellationToken).AsTask(), cancellationToken)
        );
        Assert.NotNull(failure);
        await Assert.That<object>(failure!).IsNotTypeOf<TimeoutException>();
        await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(80);
    }

    [Test]
    public async Task ReentrantKToJToKCycleFailsInsteadOfDeadlocking(
        CancellationToken cancellationToken
    )
    {
        var loader = new CyclicReentrantLoader();
        await using var cache = Create<string, int>(loader.LoadAsync);
        loader.Cache = cache;
        var failure = await CaptureExceptionAsync(() =>
            AwaitWithTestTimeout(Get(cache, "K", cancellationToken).AsTask(), cancellationToken)
        );
        Assert.NotNull(failure);
        await Assert.That<object>(failure!).IsNotTypeOf<TimeoutException>();
    }

    [Test]
    public async Task FixedSeedReferenceModelMatchesSetInvalidateClearAndTryGet()
    {
        const int seed = 0x5EED;
        var random = new Random(seed);
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        var trace = new List<string>();
        await using var cache = Create<string, int>(
            (_, _) => throw new AssertionException("Reference sequence must not load"),
            Options(maximumSize: 64)
        );
        for (int step = 0; step < 2_000; step++)
        {
            string key = $"k{random.Next(32)}";
            switch (random.Next(4))
            {
                case 0:
                    int value = random.Next();
                    trace.Add($"{step}: Set({key},{value})");
                    expected[key] = value;
                    cache.Set(key, value);
                    break;
                case 1:
                    trace.Add($"{step}: Invalidate({key})");
                    bool expectedRemoved = expected.Remove(key);
                    await Assert.That(cache.Invalidate(key)).IsEqualTo(expectedRemoved);
                    break;
                case 2:
                    trace.Add($"{step}: TryGet({key})");
                    bool actualFound = cache.TryGet(key, out int actualValue);
                    bool expectedFound = expected.TryGetValue(key, out int expectedValue);
                    await Assert
                        .That((actualFound == expectedFound))
                        .IsTrue()
                        .Because(Failure(seed, trace));
                    if (expectedFound)
                    {
                        await Assert.That(actualValue).IsEqualTo(expectedValue);
                    }

                    break;
                default:
                    trace.Add($"{step}: Clear()");
                    expected.Clear();
                    cache.Clear();
                    break;
            }

            AssertInvariants(cache);
        }
    }

    private static string Failure(int seed, List<string> trace) =>
        $"seed={seed}; trace={string.Join(" | ", trace)}";

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task AssertRejectedWithoutHangingAsync(
        Func<Task> operation,
        CancellationToken cancellationToken
    )
    {
        var failure = await CaptureExceptionAsync(() =>
            AwaitWithTestTimeout(operation(), cancellationToken)
        );
        Assert.NotNull(failure);
        await Assert
            .That((failure is TimeoutException))
            .IsFalse()
            .Because("Rejected operation hung instead of reporting overload.");
    }

    private static Task AwaitWithTestTimeout(Task task, CancellationToken cancellationToken) =>
        task.WaitAsync(TestTimeout, cancellationToken);

    private static Task<T> AwaitWithTestTimeout<T>(
        Task<T> task,
        CancellationToken cancellationToken
    ) => task.WaitAsync(TestTimeout, cancellationToken);

    private static async Task ObserveForCleanup(Task task)
    {
        try
        {
            await task.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (task.IsCompleted)
        {
            // The test already checks expected results; cleanup observes faults,
            // but a watchdog expiration must not hide unfinished work.
        }
    }

    private static async Task<int> GetWhenReleasedAsync(
        IAsyncLoadingCache<int, int> cache,
        TaskCompletionSource<bool> ready,
        Task release
    )
    {
        ready.TrySetResult(true);
        // The signal schedules continuations asynchronously. Every requester
        // reaches it before release, without a redundant Task.Run wrapper.
        await release.ConfigureAwait(false);
        return await Get(cache, 1, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WaitForeverAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private static IAsyncLoadingCache<TKey, TValue> Create<TKey, TValue>(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingTestOptions? options = null,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull => (options ?? Options()).Build(loader, comparer);

    private static void AssertInvariants<TKey, TValue>(IAsyncLoadingCache<TKey, TValue> cache)
        where TKey : notnull
        where TValue : notnull => ((AsyncLoadingCache<TKey, TValue>)cache).AssertInvariants();

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key,
        CancellationToken cancellationToken
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, cancellationToken);

    private static LoadingTestOptions Options(
        int maximumSize = 128,
        int maxConcurrentLoads = 128,
        TimeSpan? expireAfterWrite = null,
        TimeSpan? expireAfterAccess = null,
        TimeProvider? timeProvider = null,
        LoadingCacheTestHooks? testHooks = null
    ) =>
        new()
        {
            MaximumSize = maximumSize,
            MaxConcurrentLoads = maxConcurrentLoads,
            ExpireAfterWrite = expireAfterWrite,
            ExpireAfterAccess = expireAfterAccess,
            TimeProvider = timeProvider ?? TimeProvider.System,
            TestHooks = testHooks,
        };

    private static Task<TValue> CompletedLoader<TKey, TValue>(TKey _, CancellationToken __) =>
        Task.FromResult(default(TValue)!);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<T> FirstLoad<T>(
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<T> load
    )
    {
        entered.TrySetResult(true);
        return load.Task;
    }

    private sealed class SameKeyReentrantLoader
    {
        private int _attempts;
        internal IAsyncLoadingCache<int, int> Cache { private get; set; } = null!;

        internal async Task<int> LoadAsync(int key, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                return await Get(Cache, key, CancellationToken.None).ConfigureAwait(false);
            }

            return 80;
        }
    }

    private sealed class CyclicReentrantLoader
    {
        internal IAsyncLoadingCache<string, int> Cache { private get; set; } = null!;

        internal async Task<int> LoadAsync(string key, CancellationToken cancellationToken) =>
            await Get(Cache, key == "K" ? "J" : "K", CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class AtomicCounter
    {
        private int _value;
        internal int Value => Volatile.Read(ref _value);

        internal int Increment() => Interlocked.Increment(ref _value);

        internal void UpdateMaximum(int value)
        {
            while (true)
            {
                int current = Value;
                if (
                    current >= value
                    || Interlocked.CompareExchange(ref _value, value, current) == current
                )
                {
                    return;
                }
            }
        }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _wallClock = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => Stopwatch.Frequency;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
        {
            lock (this)
            {
                return _wallClock;
            }
        }

        public void Advance(TimeSpan duration)
        {
            long ticks = checked((long)(duration.TotalSeconds * TimestampFrequency));
            Interlocked.Add(ref _timestamp, ticks);
            lock (this)
            {
                _wallClock += duration;
            }
        }

        public void MoveWallClock(TimeSpan duration)
        {
            lock (this)
            {
                _wallClock += duration;
            }
        }
    }
}
