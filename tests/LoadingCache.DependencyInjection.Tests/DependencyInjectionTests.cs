using System.Globalization;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace LoadingCache.DependencyInjection.Tests;

public sealed class DependencyInjectionTests
{
    [Test]
    public async Task RegistersUnnamedAndNamedCachesAsSeparateSingletons()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure, "named");
        services.AddCache<int, string>(Configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        ICache<int, string> unnamed = provider.GetRequiredService<ICache<int, string>>();
        ICache<int, string> named = provider.GetRequiredKeyedService<ICache<int, string>>("named");
        await Assert
            .That(ReferenceEquals(provider.GetRequiredService<ICache<int, string>>(), unnamed))
            .IsTrue();
        await Assert
            .That(
                ReferenceEquals(
                    provider.GetRequiredKeyedService<ICache<int, string>>("named"),
                    named
                )
            )
            .IsTrue();
        await Assert.That(ReferenceEquals(named, unnamed)).IsFalse();
        unnamed.Put(1, "unnamed");
        named.Put(1, "named");
        await Assert.That(unnamed.TryGet(1, out string? unnamedValue)).IsTrue();
        await Assert.That(named.TryGet(1, out string? namedValue)).IsTrue();
        await Assert.That(unnamedValue).IsEqualTo("unnamed");
        await Assert.That(namedValue).IsEqualTo("named");
    }

    [Test]
    public async Task DifferentGenericServiceTypesAreIsolated()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure);
        services.AddCache<string, string>(Configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        await Assert
            .That(
                ReferenceEquals(
                    provider.GetRequiredService<ICache<int, string>>(),
                    provider.GetRequiredService<ICache<string, string>>()
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task BuildsSynchronousLoadingCacheWithTheProviderCallback()
    {
        ServiceCollection services = new();
        services.AddSingleton(_ => new LoaderPrefix("prefix-"));
        services.AddLoadingCache<int, string>(
            Configure,
            static (provider, key) => provider.GetRequiredService<LoaderPrefix>().Value + key
        );
        using ServiceProvider provider = services.BuildServiceProvider();
        ILoadingCache<int, string> cache = provider.GetRequiredService<
            ILoadingCache<int, string>
        >();
        await Assert.That(cache.Get(7)).IsEqualTo("prefix-7");
    }

    [Test]
    public async Task BuildsAsyncManualAndLoadingCaches()
    {
        ServiceCollection services = new();
        services.AddAsyncCache<int, string>(Configure);
        services.AddAsyncLoadingCache<int, string>(
            Configure,
            (provider, key, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(provider.GetRequiredService<LoaderPrefix>().Value + key);
            },
            "async-loading"
        );
        services.AddSingleton(_ => new LoaderPrefix("prefix-"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IAsyncCache<int, string> manual = provider.GetRequiredService<IAsyncCache<int, string>>();
        IAsyncLoadingCache<int, string> loading = provider.GetRequiredKeyedService<
            IAsyncLoadingCache<int, string>
        >("async-loading");
        await Assert
            .That(
                (await manual.GetOrAddAsync(1, static (key, _) => Task.FromResult($"manual-{key}")))
            )
            .IsEqualTo("manual-1");
        await Assert.That((await loading.GetAsync(2))).IsEqualTo("prefix-2");
    }

    [Test]
    public async Task LoaderCanCreateItsOwnScopeWithScopeValidationEnabled()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => new ScopedLoaderValue("scoped-"));
        services.AddAsyncLoadingCache<int, string>(
            Configure,
            async (provider, key, cancellationToken) =>
            {
                await using AsyncServiceScope scope = provider.CreateAsyncScope();
                cancellationToken.ThrowIfCancellationRequested();
                return scope.ServiceProvider.GetRequiredService<ScopedLoaderValue>().Value + key;
            }
        );
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        IAsyncLoadingCache<int, string> cache = provider.GetRequiredService<
            IAsyncLoadingCache<int, string>
        >();
        await Assert.That((await cache.GetAsync(3))).IsEqualTo("scoped-3");
    }

    [Test]
    public async Task DefersBuilderValidationUntilTheSingletonIsResolved()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(_ => { });
        Action resolve = () =>
        {
            using ServiceProvider provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<ICache<int, string>>();
        };
        await Assert
            .That(resolve)
            .Throws<InvalidOperationException>()
            .WithMessageMatching("*MaximumSize*");
    }

    [Test]
    public async Task RejectsInvalidNamesAndDuplicateRegistrationsImmediately()
    {
        ServiceCollection services = new();
        Action blank = () => services.AddCache<int, string>(Configure, " ");
        await Assert.That(blank).Throws<ArgumentException>();
        services.AddCache<int, string>(Configure, "same");
        Action duplicate = () => services.AddCache<int, string>(Configure, "same");
        await Assert
            .That(duplicate)
            .Throws<InvalidOperationException>()
            .WithMessageMatching("*already registered*");
    }

    [Test]
    public async Task RejectsNullConfigurationAndLoaderArguments()
    {
        ServiceCollection services = new();
        Action nullConfiguration = () => services.AddCache<int, string>(null!);
        Action nullSyncLoader = () => services.AddLoadingCache<int, string>(Configure, null!);
        Action nullAsyncLoader = () => services.AddAsyncLoadingCache<int, string>(Configure, null!);
        await Assert.That(nullConfiguration).Throws<ArgumentNullException>();
        await Assert.That(nullSyncLoader).Throws<ArgumentNullException>();
        await Assert.That(nullAsyncLoader).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SyncProviderDisposalStopsTheResolvedCache()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure);
        ServiceProvider provider = services.BuildServiceProvider();
        ICache<int, string> cache = provider.GetRequiredService<ICache<int, string>>();
        cache.Put(1, "one");
        provider.Dispose();
        Action useDisposed = () => cache.Put(2, "two");
        await Assert.That(useDisposed).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task AsyncProviderDisposalStopsTheResolvedCache()
    {
        ServiceCollection services = new();
        services.AddAsyncLoadingCache<int, string>(
            Configure,
            static (_, key, _) => Task.FromResult(key.ToString(CultureInfo.InvariantCulture))
        );
        ServiceProvider provider = services.BuildServiceProvider();
        IAsyncLoadingCache<int, string> cache = provider.GetRequiredService<
            IAsyncLoadingCache<int, string>
        >();
        await Assert.That((await cache.GetAsync(4))).IsEqualTo("4");
        await provider.DisposeAsync();
        Func<Task> useDisposed = async () => await cache.GetAsync(5);
        await Assert.That(useDisposed).Throws<ObjectDisposedException>();
    }

    private static void Configure<TKey, TValue>(CacheBuilder<TKey, TValue> builder)
        where TKey : notnull
        where TValue : notnull => builder.MaximumSize(16).MaxConcurrentLoads(4);

    private sealed class LoaderPrefix
    {
        internal LoaderPrefix(string value)
        {
            Value = value;
        }

        internal string Value { get; }
    }

    private sealed class ScopedLoaderValue
    {
        internal ScopedLoaderValue(string value)
        {
            Value = value;
        }

        internal string Value { get; }
    }
}
