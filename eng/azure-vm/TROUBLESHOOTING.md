# Troubleshooting

SSH into the Linux VM (`ssh edfi@<FQDN>`), then `cd ~/dms-src/eng/azure-vm/compose`.

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
| **401 / invalid token** from DMS | Keycloak issuer mismatch behind the proxy. Decode the token; its `iss` must equal `JwtAuthentication__Authority` (`https://<FQDN>/auth/realms/edfi`). Check `KC_HOSTNAME` and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` in `keycloak.yml`. |
| **403** on a resource with an EdOrg-scoped client | Expected — the client's claim set (`E2E-RelationshipsWithEdOrgsOnlyClaimSet`) only authorizes its `educationOrganizationIds`. Use the full-access client or add the EdOrg. |
| Bootstrap fails **"claim set not found"** | The `claimSetName` doesn't exist. `GET /<config>/v3/claimSets` for valid names; pass `-ClaimSetName` to `bootstrap.ps1`. |
| Grand Bend restore **skipped** | `grandbend.sh` only loads into a fresh DB. If `dms` schema already exists (startup deploy ran), `./reset.sh`, set `DMS_DEPLOY_DATABASE_ON_STARTUP=false`, then re-run `seed/grandbend.sh`. |
| **404** on a data store | The data store / route context isn't configured for that tenant+qualifier. Check `GET /<config>/v3/dataStores` (with `Tenant` header for multi-tenant). |
| Gateway **502/504** | Upstream container not healthy yet. `docker compose ... ps`; tail that service's logs. |
| Cert / TLS errors during setup | Self-signed locally (use `-k` / `-Insecure`); Let's Encrypt on the VM needs port 80 reachable and DNS resolving to the VM. |
| Container won't start after `Stop`→`Start` | `restart: unless-stopped` should resume them; if not, `./up.sh`. |
| **DMS crash-loops** with `Realm does not exist` (or never binds `/health`) | DMS loads CMS data stores at startup and fails fast if identity/CMS aren't ready (`DMS-1093`/`DMS-1109`). Run `bootstrap/bootstrap.ps1` **before** starting `st-dms`/`mt-dms`. |
| **Bulk-load can't fetch XSDs** against `/mt-dms` (404) | `DMS-1230`, fixed in `:pre` ≥ 2026-06-24 (#1048). If you still see 404s, your image predates the fix — `docker compose … pull`. (`seed/clone-data.sh` remains a faster MT seeding path.) |
| Bulk-load **`invalid_client` / 401** with a fresh key | `DMS-1231`, fixed in `:pre` ≥ 2026-06-24 (#1047) — generated secrets are now Basic-safe. On an older image the secret may contain `+`/`%`; re-mint until `+`/`%`/space-free, or URL-encode it in the Basic header. |
| Bulk-load **429 / "circuit is now open"** mid-run | Rate limiter + circuit breaker (`FAILURE_RATIO=0.01`) tripped by parallel load. Load descriptors first, raise the rate limit, then resources; or use `seed/clone-data.sh`. |

## Reset (keep the Keycloak realm)

```bash
./reset.sh
pwsh ./bootstrap/bootstrap.ps1 -BaseUrl https://localhost -Insecure
```
