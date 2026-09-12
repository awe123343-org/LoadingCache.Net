# Dependency injection integration

`LoadingCache.Extensions.DependencyInjection` provides singleton registrations for the four cache personalities. The integration depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`; the concrete service-provider implementation remains an application or test dependency.

```csharp
using LoadingCache;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

services.AddAsyncLoadingCache<UserId, User>(
    configure: builder => builder
        .MaximumSize(10_000)
        .MaxConcurrentLoads(64),
    loader: static async (provider, key, cancellationToken) =>
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        UserRepository repository = scope.ServiceProvider.GetRequiredService<UserRepository>();
        return await repository.LoadAsync(key, cancellationToken).ConfigureAwait(false);
    });
```

The available registration methods are `AddCache`, `AddLoadingCache`, `AddAsyncCache`, and `AddAsyncLoadingCache`. The first and third register manual caches. The loading overloads receive the root `IServiceProvider`, the typed key, and (for async loading) the cache-owned loader token. A loader callback must be thread-safe and should create an `IServiceScope` or `IAsyncServiceScope` for scoped dependencies inside each load. Do not capture a scoped service in a singleton cache or use request cancellation as the shared loader lifetime; caller cancellation only cancels that caller's wait.

Each registration creates one singleton when its service is first resolved. The `Action<CacheBuilder<TKey, TValue>>` is intentionally executed at that first resolution, so builder validation errors occur when the cache is built. DI host validation does not replace cache builder validation and this module does not claim `ValidateOnStart` behaviour.

`name` is optional. An unnamed cache is resolved with `GetRequiredService<T>()`; a named cache is resolved with the native .NET keyed-service API:

```csharp
IAsyncLoadingCache<UserId, User> cache =
    provider.GetRequiredKeyedService<IAsyncLoadingCache<UserId, User>>("users");
```

Names must contain a non-whitespace character. The same generic service type and name can be registered only once. Different names, or different closed generic key/value types, are separate caches. Registration failures are raised immediately; builder option failures are deferred until singleton construction.

The service provider owns the cache lifetime. Dispose a provider containing synchronous caches with `Dispose`, and use `DisposeAsync` when it contains async cache registrations. After disposal, cache operations fail with `ObjectDisposedException`; cached values are not automatically disposed because callers may still hold references to them.

## Dependency evidence

The production project pins `Microsoft.Extensions.DependencyInjection.Abstractions` `10.0.12`. The package was restored from NuGet and its local `.nuspec` declares Microsoft as author, an MIT license expression, and the dotnet/dotnet repository. The test project uses `Microsoft.Extensions.DependencyInjection` `10.0.12` to obtain the concrete `ServiceProvider`; it is not a production dependency of the extension assembly. Package assets and licenses were inspected from the restored NuGet cache on 2026-09-12. This records package provenance; it is not a legal review or a release approval.
