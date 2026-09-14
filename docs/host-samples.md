# Host integration samples

The repository contains three net10.0 host samples. Each uses the DI extension to register one singleton typed async loading cache. The cache key includes the tenant identifier, so tenant data is isolated by type and key. Every loader creates its own scope for the scoped repository; no sample captures `HttpContext`, `ServerCallContext`, or a request token as the shared loader lifetime.

| Sample                            | Host boundary                                                          | Smoke command                                                                                       |
| --------------------------------- | ---------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| `samples/LoadingCache.AspNetCore` | Real ASP.NET Core HTTP endpoint on an ephemeral loopback port          | `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.AspNetCore -- --smoke` |
| `samples/LoadingCache.Grpc`       | Real generated gRPC service and `Grpc.Net.Client` over loopback HTTP/2 | `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.Grpc -- --smoke`       |
| `samples/LoadingCache.Worker`     | `BackgroundService` hosted by `Microsoft.AspNetCore.App`               | `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.Worker -- --smoke`     |

The smoke paths are finite, perform real cache reads, and exit 0 only after validating the result. They have no external RPC or database dependency. A startup, transport, cache, or assertion failure is unhandled and exits non-zero. Normal runs omit `--smoke` and keep the host alive.

The samples pin the host content root to `AppContext.BaseDirectory`, so invoking them from the repository root cannot make the whole checkout the configuration root. On the local macOS validation runtime, native `FileSystemWatcher.EnableRaisingEvents` hangs before host construction; run the smoke commands with the explicit process-only prefix `DOTNET_USE_POLLING_FILE_WATCHER=1` to retain `reloadOnChange` semantics. The sample code does not set this variable, and normal runs keep the default native watcher. This does not modify the user's shell or global environment.

## Package evidence

The gRPC sample pins the stable `2.83.0` releases of `Grpc.AspNetCore`, `Grpc.Net.Client`, and `Grpc.Tools`. The package metadata identifies the gRPC authors, Apache-2.0 licensing, and compatible `net10.0` assets (the tooling package is `PrivateAssets="all"`). The packages were restored and their local nuspec/license metadata inspected on 2026-09-12. The ASP.NET Core and Worker samples use the `Microsoft.AspNetCore.App` shared framework rather than adding a separate hosting package. This is package provenance, not a legal review or release approval.

## Lifetime and scope rules

The cache registrations are singleton services owned by the host container. A sync host is disposed normally; hosts containing async caches should be disposed asynchronously when the hosting API permits it. Loader callbacks must remain thread-safe. For scoped dependencies, create an `IServiceScope` or `IAsyncServiceScope` inside each load and resolve the repository from that scope. The request cancellation token passed to `GetAsync` is a waiter token; the DI loader receives the cache-owned token supplied by the loading cache.
