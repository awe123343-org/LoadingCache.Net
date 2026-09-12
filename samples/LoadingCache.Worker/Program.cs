using System.Collections.Concurrent;
using LoadingCache;
using LoadingCache.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

bool smoke = args.Any(static argument =>
    string.Equals(argument, "--smoke", StringComparison.Ordinal)
);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
);
builder.Services.AddSingleton(new WorkerOptions(smoke));
builder.Services.AddSingleton<TenantLoadMonitor>();
builder.Services.AddScoped<TenantConfigRepository>();
builder.Services.AddAsyncLoadingCache<TenantKey, TenantSnapshot>(
    configure: static cache => cache.MaximumSize(128).MaxConcurrentLoads(8),
    loader: static async (provider, key, cancellationToken) =>
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        TenantConfigRepository repository =
            scope.ServiceProvider.GetRequiredService<TenantConfigRepository>();
        return await repository.LoadAsync(key, cancellationToken).ConfigureAwait(false);
    }
);
builder.Services.AddHostedService<TenantWorker>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

internal readonly record struct TenantKey(string TenantId);

internal sealed record TenantSnapshot(string TenantId, string Value);

internal sealed record WorkerOptions(bool Smoke);

internal sealed class TenantLoadMonitor
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    internal void Record(TenantKey key) =>
        _counts.AddOrUpdate(key.TenantId, 1, static (_, count) => count + 1);

    internal int CountFor(string tenantId) => _counts.GetValueOrDefault(tenantId);
}

internal sealed class TenantConfigRepository(TenantLoadMonitor monitor)
{
    private const string ValuePrefix = "config";

    internal Task<TenantSnapshot> LoadAsync(TenantKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        monitor.Record(key);
        return Task.FromResult(new TenantSnapshot(key.TenantId, $"{ValuePrefix}:{key.TenantId}"));
    }
}

internal sealed class TenantWorker(
    IAsyncLoadingCache<TenantKey, TenantSnapshot> cache,
    IHostApplicationLifetime lifetime,
    WorkerOptions options,
    TenantLoadMonitor monitor
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (options.Smoke)
            {
                using CancellationTokenSource smokeTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                smokeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await RunFiniteSampleAsync(smokeTimeout.Token).ConfigureAwait(false);
                if (monitor.CountFor("tenant-a") != 1 || monitor.CountFor("tenant-b") != 1)
                {
                    throw new InvalidOperationException(
                        "Worker smoke expected one loader invocation per tenant after the repeated tenant-a read."
                    );
                }

                Console.WriteLine(
                    "Worker smoke passed: tenant isolation and cache reuse verified."
                );
                lifetime.StopApplication();
                return;
            }

            using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                TenantSnapshot value = await cache
                    .GetAsync(new TenantKey("background"), stoppingToken)
                    .ConfigureAwait(false);
                await Console
                    .Out.WriteLineAsync($"Worker refresh sample: {value.Value}")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            await Console
                .Error.WriteLineAsync($"Worker smoke failed: {exception.Message}")
                .ConfigureAwait(false);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private async Task RunFiniteSampleAsync(CancellationToken cancellationToken)
    {
        foreach (string tenantId in new[] { "tenant-a", "tenant-b", "tenant-a" })
        {
            TenantSnapshot value = await cache
                .GetAsync(new TenantKey(tenantId), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"Worker smoke: {value.TenantId} => {value.Value}");
        }
    }
}
