using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using LoadingCache;
using LoadingCache.Extensions.DependencyInjection;
using LoadingCache.Grpc.Sample;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

bool smoke = args.Any(static argument =>
    string.Equals(argument, "--smoke", StringComparison.Ordinal)
);

if (smoke)
{
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(
    new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }
);
builder.WebHost.ConfigureKestrel(options =>
    options.Listen(
        IPAddress.Loopback,
        0,
        listenOptions => listenOptions.Protocols = HttpProtocols.Http2
    )
);
builder.Services.AddGrpc();
builder.Services.AddScoped<TenantConfigRepository>(_ => new TenantConfigRepository("config"));
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

WebApplication app = builder.Build();
app.MapGrpcService<TenantConfigService>();

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
        ?? throw new InvalidOperationException("gRPC did not expose a server address feature.");
    string address = addresses.Addresses.Single();
    using GrpcChannel channel = GrpcChannel.ForAddress(address);
    TenantConfig.TenantConfigClient client = new(channel);
    TenantConfigReply response = await client
        .GetConfigAsync(
            new TenantConfigRequest { TenantId = "tenant-a" },
            cancellationToken: smokeTimeout.Token
        )
        .ResponseAsync.ConfigureAwait(false);
    if (response.TenantId != "tenant-a" || response.Value != "config:tenant-a")
    {
        throw new InvalidOperationException(
            "gRPC smoke response did not match the expected tenant value."
        );
    }

    Console.WriteLine($"gRPC smoke passed at {address} for tenant {response.TenantId}.");
}
finally
{
    await app.StopAsync().ConfigureAwait(false);
    await app.DisposeAsync().ConfigureAwait(false);
}

internal readonly record struct TenantKey(string TenantId);

internal sealed record TenantSnapshot(string TenantId, string Value);

internal sealed class TenantConfigRepository
{
    internal TenantConfigRepository(string valuePrefix)
    {
        ValuePrefix = valuePrefix;
    }

    private string ValuePrefix { get; }

    internal Task<TenantSnapshot> LoadAsync(TenantKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TenantSnapshot(key.TenantId, $"{ValuePrefix}:{key.TenantId}"));
    }
}

internal sealed class TenantConfigService(IAsyncLoadingCache<TenantKey, TenantSnapshot> cache)
    : TenantConfig.TenantConfigBase
{
    public override async Task<TenantConfigReply> GetConfig(
        TenantConfigRequest request,
        ServerCallContext context
    )
    {
        TenantSnapshot value = await cache
            .GetAsync(new TenantKey(request.TenantId), context.CancellationToken)
            .ConfigureAwait(false);
        return new TenantConfigReply { TenantId = value.TenantId, Value = value.Value };
    }
}
