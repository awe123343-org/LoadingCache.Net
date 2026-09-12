# gRPC loading-cache sample

This sample runs a real ASP.NET Core gRPC server and client over loopback HTTP/2. The `.proto` contract generates both the server base class and client during the build; the smoke test invokes the generated client, so it does not replace gRPC with an HTTP or fake endpoint.

The RPC request contains a tenant identifier. The server turns it into a typed cache key and uses a singleton async loading cache. Each load creates an `AsyncServiceScope` and resolves the scoped repository there. The gRPC request cancellation token is used only for that caller's wait; the cache owns the shared loader lifetime.

Run the finite loopback smoke test with:

```bash
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.Grpc -- --smoke
```

The smoke path binds Kestrel to an ephemeral loopback HTTP/2 port, calls the generated `TenantConfigClient`, validates the response, disposes the channel and host, and exits 0. Failures exit non-zero. Without `--smoke`, the sample runs as a normal gRPC server.
