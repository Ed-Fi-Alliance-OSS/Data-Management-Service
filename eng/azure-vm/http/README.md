# HTTP walkthroughs

REST Client (`.http`) files demonstrating the API surface for each environment.
Tested with the VS Code `humao.rest-client` extension (also works in Visual Studio
and JetBrains IDEs).

The `.http` files ship with placeholders — fill in the deployed env's FQDN and the review-app
credentials locally from your **private** vault / credentials doc (never commit real values; this
repo stays secret-free). Token requests use the raw `Authorization: Basic key:secret` form, which
VS Code REST Client base64-encodes automatically. Pre-encode it yourself (set
`@basic = <base64(key:secret)>` and use `Authorization: Basic {{basic}}`) if a secret contains
characters REST Client treats as variables (`{{ }}`) or if your client does not auto-encode the
raw form.

> Self-signed certificate: in VS Code set `"rest-client.verifySsl": false` (or trust the
> cert) before running these.

- `single-tenant.http` — Discovery, token, and data requests against `/st-dms`.
- `multi-tenant.http` — token + tenant/route-qualifier data request against `/mt-dms`,
  plus a Configuration Service call using the `Tenant` header (switch to tenant2 via
  `@tenant` and its credentials).
- `sample-all.sh` — curl-based smoke sampler: tokens each environment (ST + both tenants)
  and reads a spread of resources; exits nonzero if any token or read fails, so it doubles
  as a handoff check. Run `./sample-all.sh` (override host with `FQDN=...`).
