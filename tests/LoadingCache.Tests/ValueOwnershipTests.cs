using LoadingCache.Ownership;

namespace LoadingCache.Tests;

public sealed class ValueOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedDisposalRemainsBoundedAndCanBeRetriedAfterShutdown(bool throws)
    {
        var reject = new System.Runtime.CompilerServices.StrongBox<bool>(true);
        Action? scheduled = null;
        var value = new DisposableValue();
        var ownership = new ValueOwnership<DisposableValue>(
            1,
            static item => item.Dispose(),
            scheduleDisposal: work =>
            {
                if (reject.Value)
                {
                    return throws
                        ? throw new InvalidOperationException("controlled rejection")
                        : false;
                }

                scheduled = work;
                return true;
            }
        );
        try
        {
            var token = ownership.Publish(value);
            ownership.Retire(token);
            await Assert.That(ownership.GetStatistics().ActiveValueCount).IsEqualTo(1);
            await Assert.That(ownership.GetStatistics().PendingDisposals).IsEqualTo(1);
            await Assert
                .That<object>(ownership.GetStatistics().LastDisposalError!)
                .IsTypeOf<InvalidOperationException>();
            await Assert.That(value.DisposeCount).IsEqualTo(0);
            await Assert
                .That(() => ownership.Publish(new DisposableValue()))
                .Throws<ValueOwnershipCapacityException>();
            ownership.Dispose();
            reject.Value = false;
            await Assert.That(ownership.RetryPendingDisposals()).IsEqualTo(1);
            await Assert.That(ownership.RetryPendingDisposals()).IsEqualTo(0);
            Assert.NotNull(scheduled);
            scheduled!();
            await Assert.That(value.DisposeCount).IsEqualTo(1);
            await Assert.That(ownership.GetStatistics().PendingDisposals).IsEqualTo(0);
            await Assert.That(ownership.GetStatistics().ActiveValueCount).IsEqualTo(0);
            await Assert.That(ownership.RetryPendingDisposals()).IsEqualTo(0);
        }
        finally
        {
            ownership.Dispose();
        }
    }

    [Test]
    public async Task RetiringEntryDefersDisposalUntilLastLeaseIsReleased()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);
        ownership.Retire(token);
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        await Assert.That(ReferenceEquals(lease.Value, value)).IsTrue();
        lease.Dispose();
        lease.Dispose();
        WaitForDisposal(value);
    }

    [Test]
    public async Task AliasedEntriesShareOneDisposal()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token first = ownership.Publish(value);
        ValueOwnership<DisposableValue>.Token second = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(first);
        ownership.Retire(first);
        ownership.Retire(second);
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        lease.Dispose();
        WaitForDisposal(value);
    }

    [Test]
    public async Task RetiringAnOldTokenDoesNotRetireANewAlias()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token oldToken = ownership.Publish(value);
        ValueOwnership<DisposableValue>.Token newToken = ownership.Publish(value);
        ownership.Retire(oldToken);
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        ownership.Retire(newToken);
        WaitForDisposal(value);
    }

    [Test]
    public async Task DisposalCallbackRunsOutsideOwnershipLock()
    {
        var value = new DisposableValue();
        var callbackEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCallback = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var ownership = new ValueOwnership<DisposableValue>(
            1,
            item =>
            {
                callbackEntered.SetResult(null);
                releaseCallback.Task.GetAwaiter().GetResult();
                item.Dispose();
            }
        );
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        Task retire = Task.Factory.StartNew(
            static state =>
            {
                (
                    ValueOwnership<DisposableValue> current,
                    ValueOwnership<DisposableValue>.Token retired
                ) = ((ValueOwnership<DisposableValue>, ValueOwnership<DisposableValue>.Token))
                    state!;
                current.Retire(retired);
            },
            (ownership, token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await retire.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.That(value.DisposeCount).IsEqualTo(0);
            releaseCallback.SetResult(null);
            await Assert
                .That(SpinWait.SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2)))
                .IsTrue();
            await Assert.That(value.DisposeCount).IsEqualTo(1);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
            await retire.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert
                .That(SpinWait.SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2)))
                .IsTrue();
        }
    }

    [Test]
    public async Task RegistryDisposalDefersValuesWithLiveLeases()
    {
        var value = new DisposableValue();
        var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);
        ownership.Dispose();
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        lease.Dispose();
        WaitForDisposal(value);
        ownership.Dispose();
    }

    [Test]
    public async Task AsyncDisposalIsObservedWithoutBlockingRetirement()
    {
        var value = new DisposableValue();
        var started = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var ownership = new ValueOwnership<DisposableValue>(
            1,
            async item =>
            {
                started.SetResult(null);
                await release.Task.ConfigureAwait(false);
                item.Dispose();
                throw new InvalidOperationException("dispose failed");
            },
            exception => observed.TrySetResult(exception)
        );
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        ownership.Retire(token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        release.SetResult(null);
        await Assert
            .That<object>((await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)))!)
            .IsTypeOf<InvalidOperationException>();
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentLeaseDisposalReleasesOnlyOnce()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);
        ownership.Retire(token);
        Parallel.For(0, 64, _ => lease.Dispose());
        await Assert
            .That(SpinWait.SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2)))
            .IsTrue();
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposedValueCannotBeRepublished()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var ownership = new ValueOwnership<DisposableValue>(
            1,
            item =>
            {
                item.Dispose();
                disposed.SetResult(null);
            }
        );
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        ownership.Retire(token);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Action republish = () =>
        {
            ownership.Publish(value);
        };
        await Assert.That(republish).Throws<ValueOwnershipCapacityException>();
    }

    private sealed class DisposableValue : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private static void WaitForDisposal(DisposableValue value)
    {
        if (!(SpinWait.SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2))))
            Assert.Fail(
                "Expected SpinWait .SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2)) to be true ()."
            );
    }
}
