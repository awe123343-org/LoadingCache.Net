namespace LoadingCache.Tests;

internal sealed class BlockingTestHook(TimeSpan timeout) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource<bool> _entered = Signal();
    private readonly TaskCompletionSource<bool> _returned = Signal();
    private readonly TaskCompletionSource<bool> _idle = Signal();
    private Task? _disposeTask;
    private int _activeCallbacks;
    private int _timedOut;
    private bool _disposed;

    public Task Entered => _entered.Task;

    public Task Returned => _returned.Task;

    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    public void Invoke()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _activeCallbacks++;
            _entered.TrySetResult(true);
        }

        try
        {
            if (!_release.Wait(timeout))
            {
                Volatile.Write(ref _timedOut, 1);
            }
        }
        finally
        {
            _returned.TrySetResult(true);
            lock (_sync)
            {
                _activeCallbacks--;
                if (_disposed && _activeCallbacks == 0)
                {
                    _release.Dispose();
                    _idle.TrySetResult(true);
                }
            }
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _release.Set();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _release.Set();
            if (_activeCallbacks == 0)
            {
                _release.Dispose();
                _idle.TrySetResult(true);
            }

            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _idle.Task.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _timedOut, 1);
            throw;
        }
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
