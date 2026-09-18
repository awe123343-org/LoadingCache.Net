using JetBrains.Annotations;
using LoadingCache.Ownership;

namespace LoadingCache;

/// <summary>
/// A reference counted handle for a value that is owned by an opt-in cache
/// adapter. The handle keeps a value alive after its cache entry is retired.
/// </summary>
/// <typeparam name="TValue">The reference type held by the lease.</typeparam>
/// <remarks>Do not release this lease concurrently with using its Value.
/// Concurrent readers must acquire independent leases, and values must not
/// escape their lease lifetime. Repeated concurrent Dispose calls are safe.</remarks>
[PublicAPI]
public sealed class CacheLease<TValue> : IDisposable, IAsyncDisposable
    where TValue : class
{
    private ValueOwnership<TValue>.LeaseRegistration? _registration;

    internal CacheLease(ValueOwnership<TValue>.LeaseRegistration registration)
    {
        _registration = registration;
    }

    /// <summary>Gets the leased value.</summary>
    public TValue Value
    {
        get
        {
            var registration =
                Volatile.Read(ref _registration)
                ?? throw new ObjectDisposedException(nameof(CacheLease<TValue>));
            return registration.State.Value
                ?? throw new ObjectDisposedException(nameof(CacheLease<TValue>));
        }
    }

    /// <summary>Gets whether this lease has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _registration) is null;

    /// <summary>Releases this lease.</summary>
    public void Dispose()
    {
        ValueOwnership<TValue>.LeaseRegistration? registration = Interlocked.Exchange(
            ref _registration,
            null
        );
        registration?.Owner.ReleaseLease(registration.State);
    }

    /// <summary>Releases this lease without waiting for user disposal code.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
