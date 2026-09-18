using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class LoadingCacheContractTests
{
    [Test]
    public async Task GetAsyncCachesValueAndTryGetAcceptsDefaultValue()
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(0);
            }
        );

        (await Get(cache, 1)).Should().Be(0);
        (await Get(cache, 1)).Should().Be(0);
        cache.TryGet(1, out int value).Should().BeTrue();
        value.Should().Be(0);
        calls.Should().Be(1);
    }

    [Test]
    public async Task ComparerEqualKeysShareAFlightAndResidentValue()
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
            Get(cache, "Key").AsTask(),
            Get(cache, "kEy").AsTask()
        );

        foreach (string value in values)
        {
            value.Should().Be("KEY");
        }
        calls.Should().Be(1);
        cache.TryGet("KEY", out string? resident).Should().BeTrue();
        resident.Should().Be("KEY");
    }

    [Test]
    public void InvalidOptionsFailFast()
    {
        FluentActions
            .Invoking(() => Create<int, int>(CompletedLoader<int, int>, Options(maximumSize: 0)))
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() => Create<int, int>(CompletedLoader<int, int>, Options(maximumSize: -1)))
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() =>
                Create<int, int>(CompletedLoader<int, int>, Options(maxConcurrentLoads: 0))
            )
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() =>
                Create<int, int>(CompletedLoader<int, int>, Options(maxConcurrentLoads: -1))
            )
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() =>
                Create<int, int>(
                    CompletedLoader<int, int>,
                    Options(expireAfterWrite: TimeSpan.Zero)
                )
            )
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
        FluentActions
            .Invoking(() =>
                Create<int, int>(
                    CompletedLoader<int, int>,
                    Options(expireAfterAccess: TimeSpan.FromTicks(-1))
                )
            )
            .Should()
            .ThrowExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ImmutableOptionsAreAppliedAtConstruction()
    {
        var clock = new ManualTimeProvider();
        var options = Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock);
        await using var cache = Create<int, int>((_, _) => Task.FromResult(42), options);

        await Get(cache, 1);
        clock.Advance(TimeSpan.FromSeconds(2));

        cache.TryGet(1, out int value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Test]
    public async Task SameKeyHasOneLoaderForMoreThanOneHundredParallelCallers()
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
            await AwaitWithTestTimeout(Task.WhenAll(ready.Select(signal => signal.Task)));
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task);
            releaseLoader.TrySetResult(true);

            int[] values = await AwaitWithTestTimeout(Task.WhenAll(waiters));
            values.Length.Should().Be(callerCount);
            foreach (int value in values)
            {
                value.Should().Be(7);
            }
            calls.Should().Be(1);
        }
        finally
        {
            go.TrySetResult(true);
            releaseLoader.TrySetResult(true);
            await Task.WhenAll(waiters).WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task DifferentKeysCanEnterLoadersConcurrently()
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

        var first = Get(cache, 1).AsTask();
        var second = Get(cache, 2).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task);
            maximumActive.Value.Should().Be(2);
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task WaiterJoiningAfterFlightInstallationCannotLeavePromiseUnstarted()
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

        var first = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(loaderEntered.Task);
        var second = Get(cache, 1).AsTask();
        release.TrySetResult(true);

        int[] values = await AwaitWithTestTimeout(Task.WhenAll(first, second));
        values[0].Should().Be(9);
        values[1].Should().Be(9);
        calls.Should().Be(1);
    }

    [Test]
    public async Task JoiningCallerStartsFlightWhenInstallerIsPausedAfterInstallation()
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
                static state => Get((IAsyncLoadingCache<int, int>)state!, 1).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<int>? joining = null;
        try
        {
            await AwaitWithTestTimeout(installation.Entered);
            joining = Get(cache, 1).AsTask();
            await AwaitWithTestTimeout(loaderEntered.Task);
            installation.Release();
            releaseLoader.TrySetResult(true);

            (await AwaitWithTestTimeout(installer)).Should().Be(9);
            (await AwaitWithTestTimeout(joining)).Should().Be(9);
            calls.Should().Be(1);
            installation.TimedOut.Should().BeFalse();
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
    public async Task BeforePublishHookIsOutsideCacheLockAndSetWinsTheGeneration()
    {
        await using var publication = new BlockingTestHook(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforePublish = publication.Invoke };

        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(100),
            Options(testHooks: hooks)
        );
        var pending = Task
            .Factory.StartNew(
                static state => Get((IAsyncLoadingCache<int, int>)state!, 1).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task? set = null;
        try
        {
            await AwaitWithTestTimeout(publication.Entered);
            set = Task.Factory.StartNew(
                static state => ((IAsyncLoadingCache<int, int>)state!).Set(1, 101),
                cache,
                TestContext.CurrentContext.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await AwaitWithTestTimeout(set);
            publication.Release();

            (await AwaitWithTestTimeout(pending)).Should().Be(100);
            (await Get(cache, 1)).Should().Be(101);
            publication.TimedOut.Should().BeFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(pending, set ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task BeforeCompletionHookIsOutsideCacheLockAndSetSurvivesCompletion()
    {
        await using var completion = new BlockingTestHook(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke };

        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(110),
            Options(testHooks: hooks)
        );
        var pending = Task
            .Factory.StartNew(
                static state => Get((IAsyncLoadingCache<int, int>)state!, 1).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task? set = null;
        try
        {
            await AwaitWithTestTimeout(completion.Entered);
            set = Task.Factory.StartNew(
                static state => ((IAsyncLoadingCache<int, int>)state!).Set(1, 111),
                cache,
                TestContext.CurrentContext.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await AwaitWithTestTimeout(set);
            completion.Release();

            (await AwaitWithTestTimeout(pending)).Should().Be(110);
            (await Get(cache, 1)).Should().Be(111);
            completion.TimedOut.Should().BeFalse();
        }
        finally
        {
            completion.Release();
            await Task.WhenAll(pending, set ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task TryGetDoesNotWaitForOrStartPendingLoad()
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

        var pending = Get(cache, 1).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task);
            cache.TryGet(1, out _).Should().BeFalse();
            calls.Should().Be(1);
            release.TrySetResult(true);
            (await AwaitWithTestTimeout(pending)).Should().Be(10);
        }
        finally
        {
            release.TrySetResult(true);
            await pending.WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task SynchronousThrowIsNotCachedAndCanRetry()
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new InvalidOperationException("first attempt")
                    : Task.FromResult(11)
        );

        Task firstAttempt = Get(cache, 1).AsTask();
        await FluentActions
            .Awaiting(() => firstAttempt)
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();
        (await Get(cache, 1)).Should().Be(11);
        calls.Should().Be(2);
    }

    [Test]
    public async Task AsyncFaultIsNotCachedAndCanRetry()
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromException<int>(new InvalidOperationException("async fault"))
                    : Task.FromResult(12)
        );

        Task firstAttempt = Get(cache, 1).AsTask();
        await FluentActions
            .Awaiting(() => firstAttempt)
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();
        (await Get(cache, 1)).Should().Be(12);
        calls.Should().Be(2);
    }

    [Test]
    public async Task LoaderCancellationIsNotCachedAndCanRetry()
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

        Task firstAttempt = Get(cache, 1, TestContext.CurrentContext.CancellationToken).AsTask();
        await FluentActions
            .Awaiting(() => firstAttempt)
            .Should()
            .ThrowAsync<OperationCanceledException>();
        (await Get(cache, 1, TestContext.CurrentContext.CancellationToken)).Should().Be(13);
        calls.Should().Be(2);
    }

    [Test]
    public async Task NullTaskIsAContractFailureAndCanRetry()
    {
        int calls = 0;
        await using var cache = Create<int, int>(
            (_, _) => Interlocked.Increment(ref calls) == 1 ? null! : Task.FromResult(14)
        );

        Task firstAttempt = Get(cache, 1).AsTask();
        await FluentActions.Awaiting(() => firstAttempt).Should().ThrowAsync<Exception>();
        (await Get(cache, 1)).Should().Be(14);
        calls.Should().Be(2);
    }

    [Test]
    public async Task NullReferenceValueIsAContractFailureAndCanRetry()
    {
        int calls = 0;
        await using var cache = Create<int, string>(
            (_, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? Task.FromResult<string>(null!)
                    : Task.FromResult("value")
        );

        Task firstAttempt = Get(cache, 1).AsTask();
        await FluentActions.Awaiting(() => firstAttempt).Should().ThrowAsync<Exception>();
        (await Get(cache, 1)).Should().Be("value");
        calls.Should().Be(2);
    }

    [Test]
    public async Task NullKeyIsRejectedBeforeLoaderStarts()
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
            await Get(cache, null!);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        rejection.Should().BeOfType<ArgumentNullException>();
        calls.Should().Be(0);
    }

    [Test]
    public async Task CallerCancellationOnlyCancelsThatWaiter()
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
            await AwaitWithTestTimeout(loaderEntered.Task);
            second = Get(cache, 1, CancellationToken.None).AsTask();
            await canceled.CancelAsync();

            await FluentActions
                .Awaiting(() => first)
                .Should()
                .ThrowAsync<OperationCanceledException>();
            release.TrySetResult(true);
            (await AwaitWithTestTimeout(second)).Should().Be(21);
            (await Get(cache, 1, CancellationToken.None)).Should().Be(21);
            calls.Should().Be(1);
        }
        finally
        {
            await canceled.CancelAsync();
            release.TrySetResult(true);
            await ObserveForCleanup(Task.WhenAll(first, second ?? Task.CompletedTask));
        }
    }

    [Test]
    public async Task AllCanceledWaitersDoNotCancelSharedLoad()
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
            await AwaitWithTestTimeout(loaderEntered.Task);
            second = Get(cache, 1, secondCancellation.Token).AsTask();
            await firstCancellation.CancelAsync();
            await secondCancellation.CancelAsync();
            await FluentActions
                .Awaiting(() => first)
                .Should()
                .ThrowAsync<OperationCanceledException>();
            await FluentActions
                .Awaiting(() => second)
                .Should()
                .ThrowAsync<OperationCanceledException>();
            release.TrySetResult(true);

            (await Get(cache, 1, CancellationToken.None)).Should().Be(22);
            calls.Should().Be(1);
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
        await FluentActions
            .Awaiting(() => firstAttempt)
            .Should()
            .ThrowAsync<OperationCanceledException>();
        calls.Should().Be(0);

        cache.Set(1, 24);
        Task secondAttempt = Get(cache, 1, cancellation.Token).AsTask();
        await FluentActions
            .Awaiting(() => secondAttempt)
            .Should()
            .ThrowAsync<OperationCanceledException>();
        calls.Should().Be(0);
    }

    [Test]
    public async Task InvalidateFencesLateSuccessFromEarlierLoad()
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

        var old = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task);
        cache.Invalidate(1).Should().BeTrue();
        var current = Get(cache, 1).AsTask();
        secondLoad.TrySetResult(32);
        (await AwaitWithTestTimeout(current)).Should().Be(32);
        firstLoad.TrySetResult(31);
        (await AwaitWithTestTimeout(old)).Should().Be(31);
        (await Get(cache, 1)).Should().Be(32);
    }

    [Test]
    public async Task InvalidateFencesLateFailureWithoutDeletingNewValue()
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

        var old = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task);
        cache.Invalidate(1).Should().BeTrue();
        var current = Get(cache, 1).AsTask();
        secondLoad.TrySetResult(34);
        (await AwaitWithTestTimeout(current)).Should().Be(34);
        firstLoad.TrySetException(new InvalidOperationException("late failure"));
        await ((Func<Task>)(() => AwaitWithTestTimeout(old)))
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();
        (await Get(cache, 1)).Should().Be(34);
    }

    [Test]
    public async Task SetFencesLateLoadCompletion()
    {
        var entered = NewSignal();
        var load = NewSignal<int>();
        await using var cache = Create<int, int>((_, _) => FirstLoad(entered, load));
        var pending = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(entered.Task);

        cache.Set(1, 35);
        load.TrySetResult(36);
        (await AwaitWithTestTimeout(pending)).Should().Be(36);
        (await Get(cache, 1)).Should().Be(35);
    }

    [Test]
    public async Task ClearFencesOldEpochCompletion()
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

        var old = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(oldEntered.Task);
        cache.Clear();
        var current = Get(cache, 1).AsTask();
        newLoad.TrySetResult(38);
        (await AwaitWithTestTimeout(current)).Should().Be(38);
        oldLoad.TrySetResult(37);
        (await AwaitWithTestTimeout(old)).Should().Be(37);
        (await Get(cache, 1)).Should().Be(38);
    }

    [Test]
    public async Task MaximumConcurrentLoadsRejectsDistinctKeyButAllowsJoining()
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

        var first = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(firstEntered.Task);
        var joining = Get(cache, 1).AsTask();
        await AssertRejectedWithoutHangingAsync(() => Get(cache, 2).AsTask());
        release.TrySetResult(true);
        int[] values = await AwaitWithTestTimeout(Task.WhenAll(first, joining));
        values[0].Should().Be(41);
        values[1].Should().Be(41);
        calls.Should().Be(1);
    }

    [Test]
    public async Task InvalidateAndClearDoNotReleasePermitForRunningLoader()
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

        var pending = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(entered.Task);
        cache.Invalidate(1);
        await AssertRejectedWithoutHangingAsync(() => Get(cache, 2).AsTask());
        cache.Clear();
        await AssertRejectedWithoutHangingAsync(() => Get(cache, 3).AsTask());
        release.TrySetResult(true);
        await AwaitWithTestTimeout(pending);
    }

    [Test]
    public async Task ExpireAfterWriteUsesPublishTimestampAndExactBoundary()
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

        (await Get(cache, 1)).Should().Be(51);
        clock.Advance(TimeSpan.FromSeconds(9));
        cache.TryGet(1, out int beforeBoundary).Should().BeTrue();
        beforeBoundary.Should().Be(51);
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.TryGet(1, out _).Should().BeFalse();
        (await Get(cache, 1)).Should().Be(52);
        calls.Should().Be(2);
    }

    [Test]
    public async Task ExpireAfterWriteStartsAtSuccessfulPublishNotLoadStart()
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

        var load = Get(cache, 1).AsTask();
        await AwaitWithTestTimeout(entered.Task);
        clock.Advance(TimeSpan.FromSeconds(5));
        release.TrySetResult(true);
        (await AwaitWithTestTimeout(load)).Should().Be(60);
        cache.TryGet(1, out int published).Should().BeTrue();
        published.Should().Be(60);
        clock.Advance(TimeSpan.FromSeconds(5));
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Test]
    public async Task TimeProviderTimestampRemainsMonotonicWhenWallClockMovesBack()
    {
        var clock = new ManualTimeProvider();
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(61),
            Options(expireAfterWrite: TimeSpan.FromSeconds(10), timeProvider: clock)
        );
        await Get(cache, 1);
        clock.MoveWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(10));
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Test]
    public async Task ExpireAfterAccessAndWriteUseTheEarlierDeadline()
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

        await Get(cache, 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        cache.TryGet(1, out _).Should().BeFalse();
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
        cache.EstimatedCount.Should().BeInRange(0, 8);
    }

    [Test]
    public async Task DisposeRejectsNewOperationsAndCooperativeLoaderDoesNotHangShutdown()
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

        var pending = Get(cache, 1).AsTask();
        try
        {
            await AwaitWithTestTimeout(entered.Task);
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask());
            var completion = await CaptureExceptionAsync(() => AwaitWithTestTimeout(pending));
            (completion is OperationCanceledException or ObjectDisposedException)
                .Should()
                .BeTrue($"started load completed with unexpected exception: {completion}");
            await cache
                .Awaiting(static current => Get(current, 2).AsTask())
                .Should()
                .ThrowExactlyAsync<ObjectDisposedException>();
        }
        finally
        {
            await cache.DisposeAsync();
            await ObserveForCleanup(pending);
        }
    }

    [Test]
    public async Task DisposeDoesNotPublishLateCompletionFromNonCooperativeLoader()
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
            pending = Get(cache, 1).AsTask();
            await AwaitWithTestTimeout(entered.Task);
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask());
            release.TrySetResult(71);
            var lateCompletion = await CaptureExceptionAsync(() => AwaitWithTestTimeout(pending));
            (lateCompletion is TimeoutException)
                .Should()
                .BeFalse($"non-cooperative completion hung: {lateCompletion}");
            Exception? rejection = null;
            try
            {
                await Get(cache, 1);
            }
            catch (Exception exception)
            {
                rejection = exception;
            }

            rejection.Should().BeOfType<ObjectDisposedException>();
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
    public async Task DisposeAccountsForFlightsDetachedByInvalidateAndClear()
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
            first = Get(cache, 1).AsTask();
            await AwaitWithTestTimeout(firstInstalled.Task);
            cache.Invalidate(1).Should().BeTrue();
            cache.Clear();

            second = Get(cache, 1).AsTask();
            await AwaitWithTestTimeout(secondInstalled.Task);
            cache.Clear();
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask());

            firstLoad.TrySetResult(120);
            secondLoad.TrySetResult(121);
            var firstCompletion = await CaptureExceptionAsync(() => AwaitWithTestTimeout(first));
            var secondCompletion = await CaptureExceptionAsync(() => AwaitWithTestTimeout(second));
            (firstCompletion is TimeoutException || secondCompletion is TimeoutException)
                .Should()
                .BeFalse();
            calls.Value.Should().Be(2);
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
    public async Task DisposeDoesNotWaitForAStuckLoaderCancellationCallback()
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

        var pending = Get(cache, 1).AsTask();
        try
        {
            await AwaitWithTestTimeout(cache.DisposeAsync().AsTask());
            await AwaitWithTestTimeout(callback.Entered);
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
                (completion is TimeoutException).Should().BeFalse();
            }
            finally
            {
                await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout, CancellationToken.None);
            }
        }

        await callback.Returned.WaitAsync(TestTimeout, CancellationToken.None);
        callback.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task SameKeyReentrancyFailsFastAndDoesNotLeaveScopePoisoned()
    {
        var loader = new SameKeyReentrantLoader();
        await using var cache = Create<int, int>(loader.LoadAsync);
        loader.Cache = cache;
        var failure = await CaptureExceptionAsync(
            cache.Awaiting(static current => AwaitWithTestTimeout(Get(current, 1).AsTask()))
        );
        failure.Should().NotBeNull();
        failure.Should().NotBeOfType<TimeoutException>();
        (await Get(cache, 1)).Should().Be(80);
    }

    [Test]
    public async Task ReentrantKToJToKCycleFailsInsteadOfDeadlocking()
    {
        var loader = new CyclicReentrantLoader();
        await using var cache = Create<string, int>(loader.LoadAsync);
        loader.Cache = cache;
        var failure = await CaptureExceptionAsync(
            cache.Awaiting(static current => AwaitWithTestTimeout(Get(current, "K").AsTask()))
        );
        failure.Should().NotBeNull();
        failure.Should().NotBeOfType<TimeoutException>();
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
                    cache.Invalidate(key).Should().Be(expectedRemoved);
                    break;
                case 2:
                    trace.Add($"{step}: TryGet({key})");
                    bool actualFound = cache.TryGet(key, out int actualValue);
                    bool expectedFound = expected.TryGetValue(key, out int expectedValue);
                    (actualFound == expectedFound).Should().BeTrue(Failure(seed, trace));
                    if (expectedFound)
                    {
                        actualValue.Should().Be(expectedValue);
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

    private static async Task AssertRejectedWithoutHangingAsync(Func<Task> operation)
    {
        var failure = await CaptureExceptionAsync(() => AwaitWithTestTimeout(operation()));
        failure.Should().NotBeNull();
        (failure is TimeoutException)
            .Should()
            .BeFalse("Rejected operation hung instead of reporting overload.");
    }

    private static Task AwaitWithTestTimeout(Task task) =>
        task.WaitAsync(TestTimeout, TestContext.CurrentContext.CancellationToken);

    private static Task<T> AwaitWithTestTimeout<T>(Task<T> task) =>
        task.WaitAsync(TestTimeout, TestContext.CurrentContext.CancellationToken);

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
        LoadingCacheOptions? options = null,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull => LoadingCache.Create(loader, options ?? Options(), comparer);

    private static void AssertInvariants<TKey, TValue>(IAsyncLoadingCache<TKey, TValue> cache)
        where TKey : notnull
        where TValue : notnull => ((LoadingCacheImpl<TKey, TValue>)cache).AssertInvariants();

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, TestContext.CurrentContext.CancellationToken);

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key,
        CancellationToken cancellationToken
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, cancellationToken);

    private static LoadingCacheOptions Options(
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
