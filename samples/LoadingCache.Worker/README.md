# Worker loading-cache sample

This sample hosts a singleton typed async loading cache inside a `BackgroundService`. The worker uses tenant-aware keys and the loader creates an `AsyncServiceScope` for every load before resolving the scoped repository. No request token or ambient request state is captured by the shared loader.

Run the finite worker smoke test with:

```bash
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.Worker -- --smoke
```

The worker performs three bounded reads (`tenant-a`, `tenant-b`, and `tenant-a` again), prints the values, requests host shutdown, and exits 0. Without `--smoke`, it runs a periodic background read until the host is stopped. The sample uses the `Microsoft.AspNetCore.App` framework reference for the hosting stack and does not add another hosting package.
