using System.Globalization;
using FluentAssertions;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace LoadingCache.DependencyInjection.Tests;

[TestFixture]
public sealed class DependencyInjectionTests
{
    [Test]
    public void RegistersUnnamedAndNamedCachesAsSeparateSingletons()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure, "named");
        services.AddCache<int, string>(Configure);

        using ServiceProvider provider = services.BuildServiceProvider();
        ICache<int, string> unnamed = provider.GetRequiredService<ICache<int, string>>();
        ICache<int, string> named = provider.GetRequiredKeyedService<ICache<int, string>>("named");

        provider.GetRequiredService<ICache<int, string>>().Should().BeSameAs(unnamed);
        provider.GetRequiredKeyedService<ICache<int, string>>("named").Should().BeSameAs(named);
        named.Should().NotBeSameAs(unnamed);

        unnamed.Put(1, "unnamed");
        named.Put(1, "named");
        unnamed.TryGet(1, out string? unnamedValue).Should().BeTrue();
        named.TryGet(1, out string? namedValue).Should().BeTrue();
        unnamedValue.Should().Be("unnamed");
        namedValue.Should().Be("named");
    }

    [Test]
    public void DifferentGenericServiceTypesAreIsolated()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure);
        services.AddCache<string, string>(Configure);

        using ServiceProvider provider = services.BuildServiceProvider();
        provider
            .GetRequiredService<ICache<int, string>>()
            .Should()
            .NotBeSameAs(provider.GetRequiredService<ICache<string, string>>());
    }

    [Test]
    public void BuildsSynchronousLoadingCacheWithTheProviderCallback()
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

        cache.Get(7).Should().Be("prefix-7");
    }

    [Test]
    public async Task BuildsAsyncManualAndLoadingCaches()
    {
        ServiceCollection services = new();
        services.AddAsyncCache<int, string>(Configure);
        services.AddAsyncLoadingCache<int, string>(
            Configure,
            static (provider, key, cancellationToken) =>
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

        (await manual.GetOrAddAsync(1, static (key, _) => Task.FromResult($"manual-{key}")))
            .Should()
            .Be("manual-1");
        (await loading.GetAsync(2)).Should().Be("prefix-2");
    }

    [Test]
    public async Task LoaderCanCreateItsOwnScopeWithScopeValidationEnabled()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => new ScopedLoaderValue("scoped-"));
        services.AddAsyncLoadingCache<int, string>(
            Configure,
            static async (provider, key, cancellationToken) =>
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

        (await cache.GetAsync(3)).Should().Be("scoped-3");
    }

    [Test]
    public void DefersBuilderValidationUntilTheSingletonIsResolved()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(_ => { });

        Action resolve = () =>
        {
            using ServiceProvider provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<ICache<int, string>>();
        };

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*MaximumSize*");
    }

    [Test]
    public void RejectsInvalidNamesAndDuplicateRegistrationsImmediately()
    {
        ServiceCollection services = new();
        Action blank = () => services.AddCache<int, string>(Configure, " ");
        blank.Should().Throw<ArgumentException>();

        services.AddCache<int, string>(Configure, "same");
        Action duplicate = () => services.AddCache<int, string>(Configure, "same");
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Test]
    public void RejectsNullConfigurationAndLoaderArguments()
    {
        ServiceCollection services = new();
        Action nullConfiguration = () => services.AddCache<int, string>(null!);
        Action nullSyncLoader = () => services.AddLoadingCache<int, string>(Configure, null!);
        Action nullAsyncLoader = () => services.AddAsyncLoadingCache<int, string>(Configure, null!);

        nullConfiguration.Should().Throw<ArgumentNullException>();
        nullSyncLoader.Should().Throw<ArgumentNullException>();
        nullAsyncLoader.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void SyncProviderDisposalStopsTheResolvedCache()
    {
        ServiceCollection services = new();
        services.AddCache<int, string>(Configure);

        ServiceProvider provider = services.BuildServiceProvider();
        ICache<int, string> cache = provider.GetRequiredService<ICache<int, string>>();
        cache.Put(1, "one");

        provider.Dispose();

        Action useDisposed = () => cache.Put(2, "two");
        useDisposed.Should().Throw<ObjectDisposedException>();
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
        (await cache.GetAsync(4)).Should().Be("4");

        await provider.DisposeAsync();

        Func<Task> useDisposed = async () => await cache.GetAsync(5);
        await useDisposed.Should().ThrowAsync<ObjectDisposedException>();
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
