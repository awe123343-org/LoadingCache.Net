# Security

This is an experimental project without a stable release or a support SLA.
A private security contact and disclosure channel have not yet been confirmed;
establish them before publishing a release. Do not place secrets, real production
keys, private traces or exploitable security details in a public issue.

The cache does not isolate tenants automatically. Include necessary authorization
dimensions in typed keys, and use trusted loaders and stable comparers. Resource
limits cover cache-managed residents and flights, not arbitrary caller or loader
memory. Hosts remain responsible for request admission and backend limits.

Supported framework intent and unverified platforms are recorded in
`docs/compatibility.md` and `docs/release-readiness.md`.
