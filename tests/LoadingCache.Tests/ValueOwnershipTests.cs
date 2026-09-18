using FluentAssertions;
using LoadingCache.Ownership;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ValueOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RejectedDisposalRemainsBoundedAndCanBeRetriedAfterShutdown(bool throws)
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
            ownership.GetStatistics().ActiveValueCount.Should().Be(1);
            ownership.GetStatistics().PendingDisposals.Should().Be(1);
            ownership
                .GetStatistics()
                .LastDisposalError.Should()
                .BeOfType<InvalidOperationException>();
            value.DisposeCount.Should().Be(0);
            ownership
                .Invoking(static current => current.Publish(new DisposableValue()))
                .Should()
                .Throw<ValueOwnershipCapacityException>();
            ownership.Dispose();

            reject.Value = false;
            ownership.RetryPendingDisposals().Should().Be(1);
            ownership.RetryPendingDisposals().Should().Be(0);
            scheduled.Should().NotBeNull();
            scheduled!();

            value.DisposeCount.Should().Be(1);
            ownership.GetStatistics().PendingDisposals.Should().Be(0);
            ownership.GetStatistics().ActiveValueCount.Should().Be(0);
            ownership.RetryPendingDisposals().Should().Be(0);
        }
        finally
        {
            ownership.Dispose();
        }
    }

    [Test]
    public void RetiringEntryDefersDisposalUntilLastLeaseIsReleased()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);

        ownership.Retire(token);

        value.DisposeCount.Should().Be(0);
        lease.Value.Should().BeSameAs(value);

        lease.Dispose();
        lease.Dispose();

        WaitForDisposal(value);
    }

    [Test]
    public void AliasedEntriesShareOneDisposal()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token first = ownership.Publish(value);
        ValueOwnership<DisposableValue>.Token second = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(first);

        ownership.Retire(first);
        ownership.Retire(second);
        value.DisposeCount.Should().Be(0);

        lease.Dispose();

        WaitForDisposal(value);
    }

    [Test]
    public void RetiringAnOldTokenDoesNotRetireANewAlias()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token oldToken = ownership.Publish(value);
        ValueOwnership<DisposableValue>.Token newToken = ownership.Publish(value);

        ownership.Retire(oldToken);
        value.DisposeCount.Should().Be(0);

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
            value.DisposeCount.Should().Be(0);
            releaseCallback.SetResult(null);
            SpinWait
                .SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2))
                .Should()
                .BeTrue();
            value.DisposeCount.Should().Be(1);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
            await retire.WaitAsync(TimeSpan.FromSeconds(2));
            SpinWait
                .SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2))
                .Should()
                .BeTrue();
        }
    }

    [Test]
    public void RegistryDisposalDefersValuesWithLiveLeases()
    {
        var value = new DisposableValue();
        var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);

        ownership.Dispose();

        value.DisposeCount.Should().Be(0);
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
        value.DisposeCount.Should().Be(0);
        release.SetResult(null);
        (await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .BeOfType<InvalidOperationException>();
        value.DisposeCount.Should().Be(1);
    }

    [Test]
    public void ConcurrentLeaseDisposalReleasesOnlyOnce()
    {
        var value = new DisposableValue();
        using var ownership = new ValueOwnership<DisposableValue>(1, item => item.Dispose());
        ValueOwnership<DisposableValue>.Token token = ownership.Publish(value);
        CacheLease<DisposableValue> lease = ownership.Acquire(token);
        ownership.Retire(token);

        Parallel.For(0, 64, _ => lease.Dispose());

        SpinWait
            .SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue();
        value.DisposeCount.Should().Be(1);
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
        Action republish = ownership.Invoking(current =>
        {
            current.Publish(value);
        });
        republish.Should().Throw<ValueOwnershipCapacityException>();
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
        SpinWait
            .SpinUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2))
            .Should()
            .BeTrue();
    }
}
