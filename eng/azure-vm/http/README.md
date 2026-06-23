# HTTP walkthroughs

REST Client (`.http`) files demonstrating the API surface for each environment.
Tested with the VS Code `humao.rest-client` extension (also works in Visual Studio
and JetBrains IDEs).

The `.http` files are pre-filled with the deployed env's FQDN and the live review-app
credentials (also recorded in `docs/infrastructure.md` — **keep this repo private**).
Because some secrets contain characters REST Client treats as variables (`{{ }}`), the
`Basic` auth header is pre-encoded as base64(`key:secret`).

> Self-signed certificate: in VS Code set `"rest-client.verifySsl": false` (or trust the
> cert) before running these.

- `single-tenant.http` — Discovery, token, and data requests against `/st-dms`.
- `multi-tenant.http` — token + tenant/route-qualifier data request against `/mt-dms`,
  plus a Configuration Service call using the `Tenant` header (tenant2 `@basic` in a comment).
- `sample-all.sh` — curl-based smoke sampler: tokens each environment (ST + both tenants)
  and reads a spread of resources. Run `./sample-all.sh` (override host with `FQDN=...`).
