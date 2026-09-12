using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace LoadingCache.Extensions.DependencyInjection;

/// <summary>
/// Registers LoadingCache personalities as DI-owned singleton services.
/// </summary>
public static class LoadingCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers a synchronous manual cache.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The cache builder configuration.</param>
    /// <param name="name">
    /// An optional non-empty name. Named registrations are resolved with
    /// <see cref="ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService{T}(IServiceProvider, object?)" />.
    /// </param>
    /// <returns>The same service collection.</returns>
    [PublicAPI]
    public static IServiceCollection AddCache<TKey, TValue>(
        this IServiceCollection services,
        Action<CacheBuilder<TKey, TValue>> configure,
        string? name = null
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        Register(
            services,
            typeof(ICache<TKey, TValue>),
            name,
            static (provider, state) => BuildCache(provider, state),
            configure
        );
        return services;
    }

    /// <summary>
    /// Registers a synchronous loading cache whose loader can resolve services.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The cache builder configuration.</param>
    /// <param name="loader">The loader invoked with the DI provider and key.</param>
    /// <param name="name">An optional non-empty registration name.</param>
    /// <returns>The same service collection.</returns>
    [PublicAPI]
    public static IServiceCollection AddLoadingCache<TKey, TValue>(
        this IServiceCollection services,
        Action<CacheBuilder<TKey, TValue>> configure,
        Func<IServiceProvider, TKey, TValue> loader,
        string? name = null
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(loader);
        Register(
            services,
            typeof(ILoadingCache<TKey, TValue>),
            name,
            static (provider, state) => BuildLoadingCache(provider, state),
            new LoadingRegistration<TKey, TValue>(configure, loader)
        );
        return services;
    }

    /// <summary>
    /// Registers a manually populated asynchronous cache.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The cache builder configuration.</param>
    /// <param name="name">An optional non-empty registration name.</param>
    /// <returns>The same service collection.</returns>
    [PublicAPI]
    public static IServiceCollection AddAsyncCache<TKey, TValue>(
        this IServiceCollection services,
        Action<CacheBuilder<TKey, TValue>> configure,
        string? name = null
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        Register(
            services,
            typeof(IAsyncCache<TKey, TValue>),
            name,
            static (provider, state) => BuildAsyncCache(provider, state),
            configure
        );
        return services;
    }

    /// <summary>
    /// Registers an asynchronous loading cache whose loader can resolve services.
    /// </summary>
    /// <typeparam name="TKey">The cache key type.</typeparam>
    /// <typeparam name="TValue">The cached value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The cache builder configuration.</param>
    /// <param name="loader">
    /// The asynchronous loader invoked with the DI provider, key, and shared
    /// cache-owned cancellation token.
    /// </param>
    /// <param name="name">An optional non-empty registration name.</param>
    /// <returns>The same service collection.</returns>
    [PublicAPI]
    public static IServiceCollection AddAsyncLoadingCache<TKey, TValue>(
        this IServiceCollection services,
        Action<CacheBuilder<TKey, TValue>> configure,
        Func<IServiceProvider, TKey, CancellationToken, Task<TValue>> loader,
        string? name = null
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(loader);
        Register(
            services,
            typeof(IAsyncLoadingCache<TKey, TValue>),
            name,
            static (provider, state) => BuildAsyncLoadingCache(provider, state),
            new AsyncLoadingRegistration<TKey, TValue>(configure, loader)
        );
        return services;
    }

    private static void Register<TService, TState>(
        IServiceCollection services,
        Type serviceType,
        string? name,
        Func<IServiceProvider, TState, TService> factory,
        TState state
    )
        where TService : class
    {
        ValidateName(name);
        EnsureUnique(services, serviceType, name);
        if (name is null)
        {
            services.AddSingleton(provider => factory(provider, state));
        }
        else
        {
            services.AddKeyedSingleton(name, (provider, _) => factory(provider, state));
        }
    }

    private static ICache<TKey, TValue> BuildCache<TKey, TValue>(
        IServiceProvider _,
        Action<CacheBuilder<TKey, TValue>> configure
    )
        where TKey : notnull
        where TValue : notnull
    {
        CacheBuilder<TKey, TValue> builder = CacheBuilder.Create<TKey, TValue>();
        configure(builder);
        return builder.Build();
    }

    private static ILoadingCache<TKey, TValue> BuildLoadingCache<TKey, TValue>(
        IServiceProvider provider,
        LoadingRegistration<TKey, TValue> registration
    )
        where TKey : notnull
        where TValue : notnull
    {
        CacheBuilder<TKey, TValue> builder = CacheBuilder.Create<TKey, TValue>();
        registration.Configure(builder);
        return builder.BuildLoading(key => registration.Loader(provider, key));
    }

    private static IAsyncCache<TKey, TValue> BuildAsyncCache<TKey, TValue>(
        IServiceProvider _,
        Action<CacheBuilder<TKey, TValue>> configure
    )
        where TKey : notnull
        where TValue : notnull
    {
        CacheBuilder<TKey, TValue> builder = CacheBuilder.Create<TKey, TValue>();
        configure(builder);
        return builder.BuildAsync();
    }

    private static IAsyncLoadingCache<TKey, TValue> BuildAsyncLoadingCache<TKey, TValue>(
        IServiceProvider provider,
        AsyncLoadingRegistration<TKey, TValue> registration
    )
        where TKey : notnull
        where TValue : notnull
    {
        CacheBuilder<TKey, TValue> builder = CacheBuilder.Create<TKey, TValue>();
        registration.Configure(builder);
        return builder.BuildAsyncLoading((key, token) => registration.Loader(provider, key, token));
    }

    private static void EnsureUnique(IServiceCollection services, Type serviceType, string? name)
    {
        bool sameRegistration = services.Any(descriptor =>
            descriptor.ServiceType == serviceType
            && (
                name is null
                    ? !descriptor.IsKeyedService
                    : descriptor.IsKeyedService && Equals(descriptor.ServiceKey, name)
            )
        );
        if (!sameRegistration)
        {
            return;
        }

        string registrationName = name ?? "<unnamed>";
        throw new InvalidOperationException(
            $"A LoadingCache service is already registered for {serviceType} with name '{registrationName}'."
        );
    }

    private static void ValidateName(string? name)
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "A cache name must contain non-whitespace characters.",
                nameof(name)
            );
        }
    }

    private sealed record LoadingRegistration<TKey, TValue>(
        Action<CacheBuilder<TKey, TValue>> Configure,
        Func<IServiceProvider, TKey, TValue> Loader
    )
        where TKey : notnull
        where TValue : notnull;

    private sealed record AsyncLoadingRegistration<TKey, TValue>(
        Action<CacheBuilder<TKey, TValue>> Configure,
        Func<IServiceProvider, TKey, CancellationToken, Task<TValue>> Loader
    )
        where TKey : notnull
        where TValue : notnull;
}
