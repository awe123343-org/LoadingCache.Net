namespace LoadingCache.Tests;

public sealed class BlockingTestHookTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeReleasesAndJoinsHeldInvocation()
    {
        BlockingTestHook hook = new(TestTimeout);
        Task invocation = Task.Run(hook.Invoke);
        try
        {
            await hook.Entered.WaitAsync(TestTimeout, CancellationToken.None);
            await hook.DisposeAsync();
            await invocation.WaitAsync(TestTimeout, CancellationToken.None);
            await Assert.That(hook.Returned.IsCompleted).IsTrue();
            await Assert.That(hook.TimedOut).IsFalse();
        }
        finally
        {
            await hook.DisposeAsync();
            await invocation.WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task LateInvocationAfterDisposeIsSafe()
    {
        BlockingTestHook hook = new(TestTimeout);
        try
        {
            await hook.DisposeAsync();
            Action invoke = hook.Invoke;
            await Assert.That(invoke).ThrowsNothing();
            await Assert.That(hook.Entered.IsCompleted).IsFalse();
            await Assert.That(hook.Returned.IsCompleted).IsFalse();
        }
        finally
        {
            await hook.DisposeAsync();
        }
    }

    [Test]
    public async Task RepeatedCleanupAndReleaseAreSafe()
    {
        BlockingTestHook hook = new(TestTimeout);
        try
        {
            ValueTask firstDispose = hook.DisposeAsync();
            ValueTask secondDispose = hook.DisposeAsync();
            await firstDispose;
            await secondDispose;
            hook.Release();
            hook.Invoke();
        }
        finally
        {
            await hook.DisposeAsync();
        }
    }

    [Test]
    public async Task FailurePathStillDisposesAndJoinsInvocation()
    {
        BlockingTestHook hook = new(TestTimeout);
        Task invocation = Task.Run(hook.Invoke);
        try
        {
            await hook.Entered.WaitAsync(TestTimeout, CancellationToken.None);
            throw new InvalidOperationException("Intentional failure-path throw.");
        }
        catch (InvalidOperationException) { }
        finally
        {
            await hook.DisposeAsync();
            await invocation.WaitAsync(TestTimeout, CancellationToken.None);
        }

        await Assert.That(hook.Returned.IsCompleted).IsTrue();
        await Assert.That(hook.TimedOut).IsFalse();
    }
}
