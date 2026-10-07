# Troubleshooting

On Linux, SSH into the VM (`ssh edfi@<FQDN>`). On the canonical Windows host, RDP in and open
Ubuntu/WSL. In either case, run `cd ~/dms-src/eng/azure-vm/compose` before the commands below.

## Status, logs, health

```bash
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env ps   # container state
./logs.sh                 # all logs (follow)
./logs.sh mt-dms          # one service
docker logs dms-sec-keycloak --tail 100

# health through the gateway (-k for self-signed):
for p in st-dms st-config mt-dms mt-config; do
  curl -sk -o /dev/null -w "$p %{http_code}\n" "https://localhost/$p/health"
done
curl -sk -o /dev/null -w "keycloak %{http_code}\n" https://localhost/auth/realms/master   # Keycloak up? (health is on KC's mgmt port, not the /auth route)
```

## Get inside a container / the database

```bash
docker exec -it dms-sec-st-dms sh
# PostgreSQL (or use pgAdmin at /pgadmin):
docker exec -it dms-sec-postgres psql -U postgres -d edfi_st -c "\dt dms.*"
docker exec -it dms-sec-postgres psql -U postgres -l    # list databases
```

## Common issues

| Symptom | Likely cause / fix |
|---------|--------------------|
| **401 / invalid token** from DMS | The token was not issued by the configured realm. Decode it; its `iss` must equal `JwtAuthentication__Authority` (`https://<FQDN>/auth/realms/edfi`). |
| **DMS crash-loops** with `OIDC metadata issuer '…' does not match the configured JwtAuthentication:Authority '…'` | Keycloak's metadata `issuer` differs from `JwtAuthentication__Authority`. `KC_HOSTNAME` in `keycloak.yml` must be `${PUBLIC_BASE_URL}/auth`, the same public URL the DMS `Authority` is built from. |
| **DMS crash-loops** with `OIDC document address '…' is not on the JwtAuthentication:MetadataAddress origin '…'` | Keycloak's `jwks_uri` names the public host instead of `http://dms-keycloak:8080`. `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` in `keycloak.yml` must be `true`. |
| **403** on relationship-based data (students, sections, ...) | Expected when the record is outside the client's `educationOrganizationIds` — `EdFiSandbox` scopes relationship-based data to them. Add the EdOrg to the Application. |
| Config service **won't start after an update**: `Name uniqueness upgrade blocked` | Migration `0035` refuses duplicate data store names in a tenant or duplicate API client names in an application. On the previous build, rename or delete the listed ids (`PUT`/`DELETE /v3/dataStores/{id}` or `/v3/apiClients/{id}`), then update again. [`provision/UPDATE.md`](provision/UPDATE.md) Part A checks for this first. |
| **403** on DS 6.1-only resources (e.g. `candidates`) after a Data Standard switch | The CMS still serves DS 5.2 claims: it loads claims only into an empty config DB. Check that `ClaimsOptions__DataStandardVersion` is `6.1` in the config containers, then run the one-time reload in [`provision/UPDATE.md`](provision/UPDATE.md) Part C step 4. |
| **DMS 503** / `EffectiveSchemaHash mismatch` after a Data Standard switch | The staged ApiSchema and the restored template disagree. Restage with `.env.template.ds61` and pin `DATABASE_TEMPLATE_PACKAGE_VERSION` to the running DMS image's version ([`provision/UPDATE.md`](provision/UPDATE.md) Part C). |
| Swagger UI **"Try it out" gets 401** | Authorize first. DMS definitions: client credentials in the "Authorization header". Configuration Service definitions: "Request body" plus a scope; the multi-tenant one sends the `Tenant` header of the definition you picked. |
| **`/swagger/` returns 404 or 502** after an update | The gateway renders its route table only at start; `update.sh` restarts it, but after a manual change run `docker compose -f docker-compose.yml -f keycloak.yml --env-file .env restart gateway swagger-ui`. |
| Bootstrap fails **"claim set not found"** | The `claimSetName` doesn't exist. `GET /<config>/v3/claimSets` for valid names; pass `-ClaimSetName` to `bootstrap.ps1`. |
| Grand Bend restore **skipped** | `grandbend.sh` only loads into a fresh DB. If the `dms` schema already exists (the DB was already provisioned with `api-schema-tools` or previously seeded), reset the data volumes (`./reset.sh`) and re-run `seed/grandbend.sh` against the fresh, empty DB. |
| **404** on a data store | The data store / route context isn't configured for that tenant+qualifier. Check `GET /<config>/v3/dataStores` (with `Tenant` header for multi-tenant). |
| Gateway **502/504** | Upstream container not healthy yet. `docker compose ... ps`; tail that service's logs. |
| Cert / TLS errors during setup | Self-signed locally (use `-k` / `-Insecure`); Let's Encrypt on the VM needs port 80 reachable and DNS resolving to the VM. |
| Container won't start after `Stop`→`Start` | `restart: unless-stopped` should resume them; if not, `./up.sh`. |
| **DMS crash-loops** with `Realm does not exist` (or never binds `/health`) | DMS loads CMS data stores at startup and fails fast if identity/CMS aren't ready (`DMS-1093`/`DMS-1109`). Run `bootstrap/bootstrap.ps1` **before** starting `st-dms`/`mt-dms`. |
| **Bulk-load can't fetch XSDs** against `/mt-dms` (404) | `DMS-1230`, fixed in `:pre` ≥ 2026-06-24 (#1048). If you still see 404s, your image predates the fix — `docker compose … pull`. (`seed/clone-data.sh` remains a faster MT seeding path.) |
| Bulk-load **`invalid_client` / 401** with a fresh key | `DMS-1231`, fixed in `:pre` ≥ 2026-06-24 (#1047) — generated secrets are now Basic-safe. On an older image the secret may contain `+`/`%`; re-mint until `+`/`%`/space-free, or URL-encode it in the Basic header. |
| Bulk-load **429 / 503 "service is temporarily unable"** mid-run | Rate limiter + circuit breaker tripped by parallel load. Load descriptors first, raise the rate limit, then resources; or use `seed/clone-data.sh`. The breaker no longer opens on a single anomaly (`FAILURE_RATIO=0.1`, `MINIMUM_THROUGHPUT=20`), and a break now answers 503 with `Retry-After` rather than a 400, so a client that retries 5xx no longer drops the documents it was refused. |

## Reset deployment state

A reset empties the data DBs and the Keycloak realm, so old review credentials are revoked. Like a
first stand-up, bootstrap, schema provisioning, and the DMS start all have to run again
(`reset.sh` prints these same steps on exit):

```bash
./reset.sh
# wait for the freshly recreated identity/CMS services before bootstrap writes its attempted marker:
until curl -skf https://localhost/auth/realms/master >/dev/null \
  && curl -skf https://localhost/st-config/health >/dev/null \
  && curl -skf https://localhost/mt-config/health >/dev/null; do sleep 5; done
pwsh ./bootstrap/bootstrap.ps1 -BaseUrl https://localhost -Insecure
# provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2
# (api-schema-tools, or restore the populated template; see ../docs/infrastructure.md), then:
./up.sh st-dms mt-dms
```
