using LoadingCache;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

bool smoke = args.Any(static argument =>
    string.Equals(argument, "--smoke", StringComparison.Ordinal)
);

WebApplicationBuilder builder = WebApplication.CreateBuilder(
    new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }
);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddScoped<TenantConfigRepository>(_ => new TenantConfigRepository("config"));
builder.Services.AddAsyncLoadingCache<TenantKey, TenantConfig>(
    configure: static cache => cache.MaximumSize(128).MaxConcurrentLoads(8),
    loader: static async (provider, key, cancellationToken) =>
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        TenantConfigRepository repository =
            scope.ServiceProvider.GetRequiredService<TenantConfigRepository>();
        return await repository.LoadAsync(key, cancellationToken).ConfigureAwait(false);
    }
);

WebApplication app = builder.Build();
app.MapGet(
    "/config/{tenantId}",
    async (
        string tenantId,
        IAsyncLoadingCache<TenantKey, TenantConfig> cache,
        CancellationToken requestAborted
    ) =>
    {
        TenantConfig value = await cache
            .GetAsync(new TenantKey(tenantId), requestAborted)
            .ConfigureAwait(false);
        return Results.Ok(value);
    }
);

if (!smoke)
{
    await app.RunAsync().ConfigureAwait(false);
    return;
}

using CancellationTokenSource smokeTimeout = new(TimeSpan.FromSeconds(10));
await app.StartAsync(smokeTimeout.Token).ConfigureAwait(false);
try
{
    IServerAddressesFeature addresses =
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
        ?? throw new InvalidOperationException(
            "ASP.NET Core did not expose a server address feature."
        );
    string address = addresses.Addresses.Single();
    using HttpClient client = new();
    client.BaseAddress = new Uri(address, UriKind.Absolute);
    TenantConfig? response = await client
        .GetFromJsonAsync<TenantConfig>("config/tenant-a", smokeTimeout.Token)
        .ConfigureAwait(false);
    if (response is null || response.TenantId != "tenant-a" || response.Value != "config:tenant-a")
    {
        throw new InvalidOperationException(
            "ASP.NET Core smoke response did not match the expected tenant value."
        );
    }

    Console.WriteLine($"ASP.NET Core smoke passed at {address} for tenant {response.TenantId}.");
}
finally
{
    await app.StopAsync().ConfigureAwait(false);
    await app.DisposeAsync().ConfigureAwait(false);
}

internal readonly record struct TenantKey(string TenantId);

internal sealed record TenantConfig(string TenantId, string Value);

internal sealed class TenantConfigRepository
{
    internal TenantConfigRepository(string valuePrefix)
    {
        ValuePrefix = valuePrefix;
    }

    private string ValuePrefix { get; }

    internal Task<TenantConfig> LoadAsync(TenantKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TenantConfig(key.TenantId, $"{ValuePrefix}:{key.TenantId}"));
    }
}
