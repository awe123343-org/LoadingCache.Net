using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LoadingCache;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

#if NET10_0_OR_GREATER
const string targetFramework = "net10.0";
#else
const string targetFramework = "net8.0";
#endif

bool expectNative = args.Contains("--expect-native", StringComparer.Ordinal);
Console.WriteLine($"TargetFramework: {targetFramework}");
Console.WriteLine($"FrameworkDescription: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"RuntimeVersion: {Environment.Version}");
Console.WriteLine($"RuntimeIdentifier: {RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"DynamicCodeSupported: {RuntimeFeature.IsDynamicCodeSupported}");

if (expectNative)
{
    Require(!RuntimeFeature.IsDynamicCodeSupported, "Native AOT execution");
}

await RunSmokeAsync().ConfigureAwait(false);
Console.WriteLine("DI/AOT consumer smoke passed.");
return;

static async Task RunSmokeAsync()
{
    LoadMonitor monitor = new();
    LoadCoordinator coordinator = new();
    ServiceCollection services = new();
    services.AddSingleton(monitor);
    services.AddSingleton(coordinator);
    services.AddScoped<ScopedLoadValue>();

    services.AddAsyncLoadingCache<TenantKey, string>(
        static builder => builder.MaximumSize(16).MaxConcurrentLoads(4),
        static (provider, key, cancellationToken) =>
            provider
                .GetRequiredService<LoadCoordinator>()
                .LoadAsync(provider, key, "default", cancellationToken)
    );
    services.AddAsyncLoadingCache<TenantKey, string>(
        static builder => builder.MaximumSize(16).MaxConcurrentLoads(4),
        static (provider, key, cancellationToken) =>
            provider
                .GetRequiredService<LoadCoordinator>()
                .LoadAsync(provider, key, "named", cancellationToken),
        "named"
    );
    services.AddCache<int, string>(static builder => builder.MaximumSize(4).MaxConcurrentLoads(1));

    ServiceProvider provider = services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateScopes = true }
    );
    IAsyncLoadingCache<TenantKey, string> defaultCache = provider.GetRequiredService<
        IAsyncLoadingCache<TenantKey, string>
    >();
    IAsyncLoadingCache<TenantKey, string> namedCache = provider.GetRequiredKeyedService<
        IAsyncLoadingCache<TenantKey, string>
    >("named");
    ICache<int, string> typedCache = provider.GetRequiredService<ICache<int, string>>();

    Require(
        ReferenceEquals(
            defaultCache,
            provider.GetRequiredService<IAsyncLoadingCache<TenantKey, string>>()
        ),
        "default registration is a singleton"
    );
    Require(!ReferenceEquals(defaultCache, namedCache), "named registration is isolated");

    typedCache.Put(7, "typed-7");
    Require(
        typedCache.TryGet(7, out string? typedValue) && typedValue == "typed-7",
        "closed generic registration"
    );

    using CancellationTokenSource callerCancellation = new();
    TenantKey sharedKey = new("shared");
    Task<string> canceledWaiter = defaultCache
        .GetAsync(sharedKey, callerCancellation.Token)
        .AsTask();
    Task startSignal = coordinator.DefaultLoadStarted.Task;
    Task firstCompletion = await Task.WhenAny(startSignal, canceledWaiter)
        .WaitAsync(TimeSpan.FromSeconds(10))
        .ConfigureAwait(false);
    if (!ReferenceEquals(firstCompletion, startSignal))
    {
        await canceledWaiter.ConfigureAwait(false);
    }

    await startSignal.ConfigureAwait(false);

    Task<string> survivingWaiter = defaultCache.GetAsync(sharedKey).AsTask();
    callerCancellation.Cancel();
    await AssertCanceledAsync(canceledWaiter).ConfigureAwait(false);
    Require(!survivingWaiter.IsCompleted, "caller cancellation does not cancel shared load");

    coordinator.ReleaseDefaultLoad();
    string sharedValue = await survivingWaiter.ConfigureAwait(false);
    Require(sharedValue.StartsWith("default:shared:", StringComparison.Ordinal), "shared result");
    Require(coordinator.DefaultLoads == 1, "same-key load is shared");

    string namedValue = await namedCache.GetAsync(new TenantKey("named")).ConfigureAwait(false);
    Require(namedValue.StartsWith("named:named:", StringComparison.Ordinal), "named result");
    Require(monitor is { Created: 2, Disposed: 2 }, "each load scope is disposed");

    await provider.DisposeAsync().ConfigureAwait(false);
    await AssertDisposedAsync(() => defaultCache.GetAsync(new TenantKey("after-dispose")).AsTask())
        .ConfigureAwait(false);
}

static async Task AssertCanceledAsync(Task task)
{
    try
    {
        await task.ConfigureAwait(false);
        throw new InvalidOperationException("The canceled waiter completed successfully.");
    }
    catch (OperationCanceledException) { }
}

static async Task AssertDisposedAsync(Func<Task> operation)
{
    try
    {
        await operation().ConfigureAwait(false);
        throw new InvalidOperationException("The disposed cache accepted a new operation.");
    }
    catch (ObjectDisposedException) { }
}

static void Require(bool condition, string operation)
{
    if (!condition)
    {
        throw new InvalidOperationException($"DI/AOT smoke failed: {operation}");
    }
}

internal readonly record struct TenantKey(string Value);

internal sealed class LoadCoordinator
{
    private readonly TaskCompletionSource<object?> _defaultLoadRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    internal TaskCompletionSource<object?> DefaultLoadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _defaultLoads;

    internal int DefaultLoads => Volatile.Read(ref _defaultLoads);

    internal async Task<string> LoadAsync(
        IServiceProvider provider,
        TenantKey key,
        string cacheName,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ScopedLoadValue value = scope.ServiceProvider.GetRequiredService<ScopedLoadValue>();

        if (cacheName == "default" && key.Value == "shared")
        {
            Interlocked.Increment(ref _defaultLoads);
            DefaultLoadStarted.TrySetResult(null);
            await _defaultLoadRelease.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return $"{cacheName}:{key.Value}:{value.Id}";
    }

    internal void ReleaseDefaultLoad() => _defaultLoadRelease.TrySetResult(null);
}

internal sealed class LoadMonitor
{
    private int _created;
    private int _disposed;

    internal int Created => Volatile.Read(ref _created);
    internal int Disposed => Volatile.Read(ref _disposed);

    internal int NextId() => Interlocked.Increment(ref _created);

    internal void MarkDisposed() => Interlocked.Increment(ref _disposed);
}

internal sealed class ScopedLoadValue(LoadMonitor monitor) : IAsyncDisposable
{
    internal int Id { get; } = monitor.NextId();

    public ValueTask DisposeAsync()
    {
        monitor.MarkDisposed();
        return ValueTask.CompletedTask;
    }
}
