# Primary-source evidence

Accessed on 12 September 2026. [manifest.json](manifest.json) records immutable upstream URLs, revisions, byte counts, SHA-256 hashes and inspection scopes. `retrieved-only` does not mean audited. Upstream source was downloaded for research, not added to this repository or executed in this research pass.

Versions were resolved through GitHub's `releases/latest` and `commits/{tag}` APIs, then files were fetched by commit SHA. These are frozen observations; querying latest again need not return the same version.

To verify a source, retrieve its immutable URL and compare its digest, for example:

```sh
curl --fail --location 'https://raw.githubusercontent.com/bitfaster/BitFaster.Caching/a71bec32a6b7f621af7a9d2b1d7c4edd0209b289/BitFaster.Caching/Atomic/AsyncAtomicFactory.cs' -o /tmp/AsyncAtomicFactory.research.cs
shasum -a 256 /tmp/AsyncAtomicFactory.research.cs
bat --paging=never /tmp/AsyncAtomicFactory.research.cs
```

Mutable documentation and conclusions are indexed in [research](../research.md). Downloading or reading a file is neither a test pass nor a performance result.
