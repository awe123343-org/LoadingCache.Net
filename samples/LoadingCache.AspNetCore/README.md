# ASP.NET Core loading-cache sample

This sample exposes a loopback HTTP endpoint backed by a singleton typed async loading cache:

```text
GET /config/{tenantId}
```

The tenant identifier is part of the typed cache key, so values for different tenants cannot share an entry. The cache loader receives the root `IServiceProvider`, creates an `AsyncServiceScope` for each load, and resolves the scoped repository inside that scope. It does not capture `HttpContext` or the request cancellation token as the shared loader lifetime; the endpoint token only cancels that caller's wait.

Run the finite loopback smoke test with:

```bash
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet run --project samples/LoadingCache.AspNetCore -- --smoke
```

Without `--smoke`, the app runs as a normal ASP.NET Core process. The smoke path binds Kestrel to an ephemeral loopback port, performs one real HTTP request, validates the typed response, and exits with status 0. An exception or failed assertion exits non-zero.
