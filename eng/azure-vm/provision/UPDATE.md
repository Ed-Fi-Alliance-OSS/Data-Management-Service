<!-- SPDX-License-Identifier: Apache-2.0 -->
# In-place update runbook (keeps the API keys)

Refresh a running deployment without re-issuing the API keys already shared with reviewers.
The keys are Keycloak clients in the `dms-sec-keycloak` volume plus application rows in the `edfi_st_config` and `edfi_mt_config` databases.
Every part below leaves both in place.
[`REDEPLOY.md`](REDEPLOY.md), `reset.sh` and `down.sh -v` drop them and mint new keys.

| Part | API keys | Data databases (`edfi_st`, `edfi_mt`, `edfi_mt_t2`) |
|------|----------|------------------------------------------------------|
| A. Pre-checks and backup | unchanged | unchanged |
| B. Update to a newer build | kept | kept |
| C. Switch the Data Standard (5.2 to 6.1) | kept | **recreated**: data written through the API is lost |
| D. Add the review-variant keys | kept, 12 added | kept |

Run A first, then any of B, C and D in that order.
Commands are labeled **[bash]** (WSL shell) or **[pwsh]** (`pwsh` inside WSL) and assume the repository at `~/dms-src`.

## Part A: Pre-checks and backup  [bash]

1. Confirm the bootstrap admin credentials in `.env` work; Parts C and D, and fixing a blocked migration, need them.

   ```bash
   cd ~/dms-src/eng/azure-vm/compose
   ADMIN_ID=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_ID=' .env | cut -d= -f2-)
   ADMIN_SECRET=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_SECRET=' .env | cut -d= -f2-)
   curl -sk -o /dev/null -w '%{http_code}\n' https://localhost/st-config/connect/token \
     -d grant_type=client_credentials -d scope=edfi_admin_api/full_access \
     --data-urlencode "client_id=$ADMIN_ID" --data-urlencode "client_secret=$ADMIN_SECRET"   # expect 200
   ```

2. Builds from October 2026 add Configuration Service migrations `0035` and `0036`, and each stops startup when the database holds duplicates it would make invalid.
   Check for both before updating, identifiers first: the name cleanup below uses the API, and the API must not touch a row that shares an identifier.

   Migration `0036` makes the API client key (`ClientId`) and the Keycloak client id (`ClientUuid`) unique across the whole `ApiClient` table.
   Startup stops with `API client identifier uniqueness upgrade blocked` if two rows share either value.
   Both queries must return zero rows:

   ```bash
   for db in edfi_st_config edfi_mt_config; do
     docker exec dms-sec-postgres psql -U postgres -d "$db" \
       -c 'SELECT "ClientId", count(*) FROM dmscs."ApiClient" GROUP BY 1 HAVING count(*) > 1;' \
       -c 'SELECT "ClientUuid", count(*) FROM dmscs."ApiClient" GROUP BY 1 HAVING count(*) > 1;'
   done
   ```

   If either returns rows, do not fix them through the API: `DELETE /v3/apiClients/{id}` also deletes the Keycloak client that rows sharing a `ClientUuid` have in common.
   Stop `st-config` and `mt-config`, follow [Configuration Service database upgrades](../../../docs/OPERATIONS.md#configuration-service-database-upgrades) against that database (enumerate every row, back up, review ownership, delete the reviewed rows directly, re-check), and only then go on to the name check.

   Migration `0035` makes data store names unique per tenant and API client names unique per application.
   Startup stops with `Name uniqueness upgrade blocked` if the database already has duplicates.
   Every query must return zero rows:

   ```bash
   for db in edfi_st_config edfi_mt_config; do
     docker exec dms-sec-postgres psql -U postgres -d "$db" \
       -c 'SELECT "TenantId", "Name", count(*) FROM dmscs."DataStore" GROUP BY 1, 2 HAVING count(*) > 1;' \
       -c 'SELECT "ApplicationId", "Name", count(*) FROM dmscs."ApiClient" GROUP BY 1, 2 HAVING count(*) > 1;'
   done
   ```

   Rename or delete any duplicate through `PUT`/`DELETE /v3/dataStores/{id}` or `/v3/apiClients/{id}` on the current build first, but only once the identifier check above is clean.
   An API client that shares a name and also a `ClientId` or `ClientUuid` with another row belongs to the procedure above; deleting it through the API would delete the Keycloak client the surviving row uses.
   Then continue with step 3.

3. Check for claim sets created at runtime.
   Part C's claims reload deletes every claim set that is not system-reserved, in every tenant.
   Applications bound to one keep its name, so their keys still get tokens, but every API request they make answers `500` until the claim set is back.
   Part B alone keeps them.

   ```bash
   for db in edfi_st_config edfi_mt_config; do
     docker exec dms-sec-postgres psql -U postgres -d "$db" \
       -c 'SELECT c."Id", c."ClaimSetName", t."Name" AS "Tenant" FROM dmscs."ClaimSet" c LEFT JOIN dmscs."Tenant" t ON t."Id" = c."TenantId" WHERE NOT c."IsSystemReserved";' \
       -c 'SELECT "Id", "ApplicationName", "ClaimSetName" FROM dmscs."Application" WHERE "ClaimSetName" IN (SELECT "ClaimSetName" FROM dmscs."ClaimSet" WHERE NOT "IsSystemReserved");'
   done
   ```

   If either query returns rows and you will run Part C, export those claim sets now; Part C step 4 imports them back after the reload:

   ```bash
   mkdir -p ~/backup/claimsets
   for cms in st-config mt-config; do
     docker exec dms-sec-postgres psql -U postgres -d "edfi_${cms%-config}_config" -At -F ' ' \
       -c 'SELECT c."Id", coalesce(t."Name", $$none$$) FROM dmscs."ClaimSet" c LEFT JOIN dmscs."Tenant" t ON t."Id" = c."TenantId" WHERE NOT c."IsSystemReserved";' |
     while read -r id tenant; do
       HDR=(); [ "$cms" = mt-config ] && HDR=(-H "Tenant: $tenant")
       TOKEN=$(curl -sk "https://localhost/$cms/connect/token" -d grant_type=client_credentials -d scope=edfi_admin_api/full_access \
         --data-urlencode "client_id=$ADMIN_ID" --data-urlencode "client_secret=$ADMIN_SECRET" \
         | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
       out=~/backup/claimsets/"$cms.$tenant.$id.json"
       if curl -skf "https://localhost/$cms/v3/claimSets/$id/export" -H "Authorization: Bearer $TOKEN" "${HDR[@]}" > "$out"; then
         echo "exported $cms $tenant $id"
       else rm -f "$out"; echo "FAILED $cms $tenant $id"; fi
     done
   done
   ```

   `ADMIN_ID` and `ADMIN_SECRET` come from step 1.
   A claim set defined in a `*-claims.json` file under `compose/claims` is not at risk: the reload reads those files again.
   Builds before the fragment rename named these files `*-claimset.json`, which newer builds do not read; Part B renames them before updating.

4. Back up the credential state.
   Stop Keycloak briefly so its H2 files are copied consistently.

   ```bash
   mkdir -p ~/backup && cd ~/backup
   for db in edfi_st_config edfi_mt_config; do
     docker exec dms-sec-postgres pg_dump -U postgres -Fc "$db" > "$db.dump"
   done
   docker stop dms-sec-keycloak
   docker run --rm -v dms-security-review_dms-sec-keycloak:/v:ro -v "$PWD":/b alpine tar czf /b/keycloak-volume.tgz -C /v .
   docker start dms-sec-keycloak
   ```

5. Record what is running, for the credentials doc and for rollback:

   ```bash
   for c in dms-sec-st-dms dms-sec-st-config dms-sec-keycloak; do
     docker inspect "$c" --format '{{.Name}} {{.Config.Image}} {{index .Config.Labels "org.opencontainers.image.version"}} {{.Image}}'
   done
   git -C ~/dms-src log -1 --oneline
   ```

## Part B: Update to a newer build  [bash]

```bash
cd ~/dms-src && git status --short --untracked-files=no | grep -v 'packages\.lock\.json$'   # expect no output
```

A `.env` created before the Configuration Service variable rename still uses `DMS_CONFIG_*` and `CONFIG_SERVICE_*` names, which Compose no longer reads.
`update.sh` names each one it finds, but rename them before running it below, or the stack fails to start (see [Migrating from the earlier variable names](../../../docs/CONFIGURATION.md#migrating-from-the-earlier-variable-names)).
This renames each assignment in place, including `export` and indented spellings, and changes only the name before `=`: every value stays as it is, including the database encryption key and the client secrets.
It first deletes the assignments of settings this stack has no replacement for, so the rename cannot turn them into names nothing reads: the claims, reset-endpoint, and scope settings the Configuration Service no longer reads, `DMS_CONFIG_IDENTITY_ENCRYPTION_KEY`, whose replacement only self-contained OpenIddict reads, and `DMS_ENABLE_CLAIMSET_RELOAD`, because `DMS_ENABLE_MANAGEMENT_ENDPOINTS` now controls claimset reload and view as well.
The `grep` should print nothing; any line it prints is an assignment or a `$` reference the rename did not cover, so fix that line by hand before updating:

```bash
cd ~/dms-src/eng/azure-vm/compose && cp -p .env .env.before-rename &&
  sed -i -E \
    -e '/^[[:space:]]*(export[[:space:]]+)?DMS_CONFIG_(IDENTITY_ENCRYPTION_KEY|USE_EMBEDDED_BASE_CLAIMS|CLAIMS_PATH|USE_CLAIMS_PATH|DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING|ENABLE_APPLICATION_RESET_ENDPOINT|IDENTITY_SCOPE)[[:space:]]*=/d' \
    -e '/^[[:space:]]*(export[[:space:]]+)?DMS_ENABLE_CLAIMSET_RELOAD[[:space:]]*=/d' \
    -e 's/^([[:space:]]*(export[[:space:]]+)?)DMS_CONFIG_ENABLE_CLAIMS_RELOAD([[:space:]]*=)/\1CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING\3/' \
    -e 's/^([[:space:]]*(export[[:space:]]+)?)DMS_CONFIG_([A-Za-z0-9_]+[[:space:]]*=)/\1CONFIGURATION_SERVICE_\3/' \
    -e 's/^([[:space:]]*(export[[:space:]]+)?)CONFIG_SERVICE_([A-Za-z0-9_]+[[:space:]]*=)/\1DMS_CONFIGURATION_SERVICE_\3/' \
    .env
grep -nE '^[[:space:]]*(export[[:space:]]+)?(DMS_CONFIG_|CONFIG_SERVICE_)[A-Za-z0-9_]*[[:space:]]*=|\$\{?(DMS_CONFIG_|CONFIG_SERVICE_)' \
  ~/dms-src/eng/azure-vm/compose/.env
```

Comments are left unchanged.
`cp -p` keeps the owner-only mode, because `.env.before-rename` holds the secrets too; delete it once the stack is healthy.
If the update below fails, copy it back over `.env` before running the previous build again.

Claims fragment files under `compose/claims` must now end in `-claims.json`; the Configuration Service no longer reads `*-claimset.json` files and only logs a warning for each, and only when it reads fragments (an empty database at startup, a claims reload, or the first `GET /management/current-claims`), so an in-place upgrade does not log it at startup.
A fragment without a top-level `name` takes its claim set name from the file name, so renaming it also renames the claim set: first add `"name": "<old file name without .json>"` to any such file.
Then rename the files, including any in subdirectories; `find` lists nothing once they are all renamed:

```bash
cd ~/dms-src/eng/azure-vm/compose/claims &&
  find . -type f -name '*-claimset.json' -exec sh -c 'mv -n -- "$1" "${1%-claimset.json}-claims.json"' _ {} \; &&
  find . -type f -name '*-claimset.json'
```

`mv -n` never overwrites: if a file is still listed, a `-claims.json` file with the same stem already exists, so merge or remove one of them by hand.

```bash
cd ~/dms-src && git restore -- '*packages.lock.json' &&
  git fetch --force --tags origin && git switch --detach origin/main && git log -1 --oneline &&
  cd eng/azure-vm/compose && SKIP_GIT=1 ./update.sh
```

Replace `origin/main` with a release tag to deploy a release.
The single `&&` chain matters: if the fetch or switch fails, `update.sh` must not pull new images against the old checkout.
The schema tool build ([`REDEPLOY.md`](REDEPLOY.md) Part C step 1) adds `linux-x64` entries to the tracked `packages.lock.json` files, and `git switch` refuses to overwrite them; `git restore` discards only those files.
If the first command lists anything else, it was edited on the VM: keep or discard it before switching.
`--force` lets the fetch move a tag that was re-pointed upstream; without it the fetch fails with `would clobber existing tag`.

`update.sh` pulls the images for the current `DMS_IMAGE_TAG` (`pre` by default) and recreates the changed containers.
It then restarts the gateway and Swagger UI, because both read their mounted files only at container start; that is how a pulled route such as `/swagger/` takes effect.
The Configuration Service applies its database migrations at startup.
If a Configuration Service stays unhealthy, `./logs.sh st-config` (or `mt-config`) names the migration that failed.

Keycloak: `keycloak.compose.yml` pins one image by tag and digest (`26.8.1@sha256:…`), the same image as `eng/docker-compose/keycloak.compose.yml`.
To protect the persisted H2 realm volume, `update.sh` refuses any change to that reference, including a new digest under the same tag; such an update follows [`REDEPLOY.md`](REDEPLOY.md), which issues new API keys.
An `update.sh` from before `keycloak.yml` was renamed to `keycloak.compose.yml` keeps running after its own `git pull`, cannot find `keycloak.yml`, and stops with `cannot read the layout of the PostgreSQL volume`; nothing is changed.
Run it again: the pulled `update.sh` then reports the Keycloak image change, and the deployment follows [`REDEPLOY.md`](REDEPLOY.md).
Later versions of `update.sh` rerun the pulled copy of themselves after `git pull`.

PostgreSQL: builds from October 2026 run PostgreSQL 18, which keeps its cluster in a versioned directory inside the `dms-security-review_dms-sec-postgres` volume.
A deployment created before that holds a PostgreSQL 16 cluster at the volume root, which PostgreSQL 18 does not read: it would start an empty cluster beside it, and the databases and API keys would not carry over.
Part B cannot upgrade that layout in place.
Before it refreshes the deployment images or recreates any container, `update.sh` reads the volume's root `PG_VERSION` through a read-only helper container, and stops when it finds the old layout or cannot read the volume; the volume is left unchanged.
The helper container may download the configured PostgreSQL image.
To move such a deployment to this build, back up every database (`edfi_st_config`, `edfi_mt_config`, `edfi_st`, `edfi_mt`, `edfi_mt_t2`) and the Keycloak volume as in Part A step 4, then either migrate the cluster to PostgreSQL 18 deliberately (`pg_upgrade`, or a dump restored into the new layout) or follow [`REDEPLOY.md`](REDEPLOY.md), which issues new API keys.
Deleting the volume does not keep the keys.
See [Database versions](../../../docs/OPERATIONS.md#database-versions) for the PostgreSQL requirements.
Only `update.sh` makes this check: running `up.sh` or `docker compose up` directly against an old volume skips it.

Keycloak clients created before the Configuration Service gave each client its own audience need that audience added once.
On Keycloak 26.6.2 or later, which the `26.8.1` pin runs, `/connect/introspect` answers `{"active": false}` to a client introspecting its own token unless the client is in the token's `aud` (see [Token introspection](../../../reference/design/configuration-service/CS-AUTH.md#token-introspection)).
`update.sh` keeps existing clients as they are, `bootstrap.ps1` refuses to run again once complete, and `add-review-variants.ps1` skips applications that exist, so none of them adds it.
Run steps 1 to 4 after updating such a deployment, without rerunning `bootstrap.ps1` or deleting its `.bootstrap` markers.
Both repairs are idempotent: they add the own-audience mapper only where it is missing and keep every client id, secret, role, scope and other mapper.

1. The three clients `bootstrap.ps1` creates in Keycloak **[pwsh]**.
   This reruns `setup-keycloak.ps1` for each with the `.env` values and `-SkipRealmAdmin` choices `bootstrap.ps1` uses; for a client that exists it changes only the own-audience mapper, and never reads or writes the secret.
   `AdminPassword` must be Keycloak's current `admin` password, which is the `.env` value unless it was changed in Keycloak.

   ```powershell
   cd ~/dms-src/eng/azure-vm/compose
   $ErrorActionPreference = 'Stop'
   $envValues = @{}
   foreach ($line in Get-Content .env) {
       $t = $line.Trim()
       if ($t -eq '' -or $t.StartsWith('#') -or $t.IndexOf('=') -lt 1) { continue }
       $v = $t.Substring($t.IndexOf('=') + 1).Trim()
       if ($v.Length -ge 2 -and $v[0] -eq $v[-1] -and ($v[0] -eq '"' -or $v[0] -eq "'")) { $v = $v.Substring(1, $v.Length - 2) }
       $envValues[$t.Substring(0, $t.IndexOf('=')).Trim()] = $v
   }
   function EnvVal([string]$key, [string]$default = '') { if ($envValues[$key]) { $envValues[$key] } else { $default } }
   $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
   $PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
   $kc = @{ KeycloakServer = 'https://localhost/auth'; Realm = (EnvVal KEYCLOAK_REALM edfi)
       AdminUsername = (EnvVal KEYCLOAK_ADMIN admin); AdminPassword = (EnvVal KEYCLOAK_ADMIN_PASSWORD) }
   $setup = '../../docker-compose/setup-keycloak.ps1'
   & $setup @kc -NewClientId (EnvVal CONFIGURATION_SERVICE_IDENTITY_CLIENT_ID DmsConfigurationService) -NewClientName 'DMS Configuration Service' `
       -ClientScopeName 'edfi_admin_api/full_access' -NewClientSecret (EnvVal CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET)
   & $setup @kc -NewClientId (EnvVal DMS_CONFIGURATION_SERVICE_CLIENT_ID CMSReadOnlyAccess) -NewClientName 'CMS ReadOnly Access' `
       -ClientScopeName (EnvVal DMS_CONFIGURATION_SERVICE_CLIENT_SCOPE 'edfi_admin_api/readonly_access') `
       -NewClientSecret (EnvVal DMS_CONFIGURATION_SERVICE_CLIENT_SECRET) -SkipRealmAdmin
   & $setup @kc -NewClientId (EnvVal BOOTSTRAP_ADMIN_CLIENT_ID dms-bootstrap-admin) -NewClientName 'DMS Bootstrap Admin' `
       -ClientScopeName 'edfi_admin_api/full_access' -NewClientSecret (EnvVal BOOTSTRAP_ADMIN_CLIENT_SECRET) -SkipRealmAdmin
   ```

   Each client prints `already exists; checking its own audience`, then either `Own audience added` or `already puts itself into its access tokens' audience`.
   A `WARNING: ... was NOT repaired` names a client with a conflicting mapper; see step 3.

2. The API clients the Configuration Service manages **[bash]**.
   An unchanged `PUT /v3/vendors/{id}` adds the mapper to every API client of every application of that vendor.
   An unchanged `PUT /v3/applications/{id}` schedules no new client synchronization: it only retries clients already pending, so it is not a repair; a credential reset changes only the secret.
   The block sends every vendor back exactly as `GET` returned it, a page at a time, on `st-config` and in every tenant `mt-config` lists:

   ```bash
   cd ~/dms-src/eng/azure-vm/compose
   until [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost/st-config/health)" = 200 ] &&
     [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost/mt-config/health)" = 200 ]; do sleep 5; done
   ADMIN_ID=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_ID=' .env | cut -d= -f2-)
   ADMIN_SECRET=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_SECRET=' .env | cut -d= -f2-)
   cms_token() {
     curl -sk "https://localhost/$1/connect/token" -d grant_type=client_credentials -d scope=edfi_admin_api/full_access \
       --data-urlencode "client_id=$ADMIN_ID" --data-urlencode "client_secret=$ADMIN_SECRET" \
       | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])'
   }
   failed=0; LIMIT=50
   for cms in st-config mt-config; do
     TENANTS=('')
     if [ "$cms" = mt-config ]; then
       list=$(curl -skf "https://localhost/$cms/v3/tenants/" -H "Authorization: Bearer $(cms_token "$cms")") ||
         { echo "FAILED $cms: tenant list"; failed=1; continue; }
       mapfile -t TENANTS < <(python3 -c 'import json,sys; [print(t["name"]) for t in json.load(sys.stdin)]' <<< "$list")
       [ "${#TENANTS[@]}" -gt 0 ] || { echo "FAILED $cms: no tenants"; failed=1; continue; }
     fi
     for tenant in "${TENANTS[@]}"; do
       HDR=(); [ -n "$tenant" ] && HDR=(-H "Tenant: $tenant")
       offset=0
       while :; do
         TOKEN=$(cms_token "$cms")
         page=$(curl -skf "https://localhost/$cms/v3/vendors/?orderBy=id&offset=$offset&limit=$LIMIT" \
           -H "Authorization: Bearer $TOKEN" "${HDR[@]}") ||
           { echo "FAILED $cms ${tenant:-(single)}: vendor list at offset $offset"; failed=1; break; }
         count=0
         while IFS=$'\t' read -r id body; do
           count=$((count + 1))
           code=$(curl -sk -o /dev/null -w '%{http_code}' -X PUT "https://localhost/$cms/v3/vendors/$id" \
             -H "Authorization: Bearer $TOKEN" "${HDR[@]}" -H 'Content-Type: application/json' --data-binary "$body")
           if [ "$code" = 204 ]; then echo "ok     $cms ${tenant:-(single)} vendor $id"
           else echo "FAILED $cms ${tenant:-(single)} vendor $id: HTTP $code"; failed=1; fi
         done < <(python3 -c 'import json,sys; [print(v["id"], json.dumps(v, separators=(",", ":")), sep="\t") for v in json.load(sys.stdin)]' <<< "$page")
         [ "$count" -lt "$LIMIT" ] && break
         offset=$((offset + LIMIT))
       done
     done
   done
   [ "$failed" = 0 ] && echo "All vendors updated." || echo "Some vendor updates FAILED; see above."
   ```

   The block must end with `All vendors updated.`
   A `FAILED` vendor was not updated: the Configuration Service restores the clients that request changed, so rerun the block, which is safe; `./logs.sh st-config` (or `mt-config`) names the cause.

3. Conflicting mappers **[bash]**.
   A client that already has an OpenID Connect mapper named `Client's own audience` that does not give it its own audience, for example one edited in Keycloak, is left as it is: step 1 prints the `NOT repaired` warning for it, and the vendor update in step 2 still answers `204` and only logs a Warning.
   A `204` therefore does not prove the client was repaired. This must print nothing:

   ```bash
   for c in dms-sec-st-config dms-sec-mt-config; do
     docker logs --since 1h "$c" 2>&1 | grep -F "does not put the client into its own access tokens' audience"
   done
   ```

   For each client either step names, follow the operator remedy in [Token introspection](../../../reference/design/configuration-service/CS-AUTH.md#token-introspection): add an *Audience* mapper under another name in the client's dedicated scope, with *Included Client Audience* set to the client itself and *Add to access token* and *Add to token introspection* on.

4. Owner introspection with fresh tokens **[bash]**, in the shell from step 2.
   Tokens issued before the repair keep their old audience and stay `{"active": false}` to their owner until they expire, so request new ones:

   ```bash
   REALM=$(grep -E '^KEYCLOAK_REALM=' .env | cut -d= -f2-); REALM=${REALM:-edfi}
   own() {   # $1 = key, $2 = secret: that client introspects a fresh token of its own
     T=$(curl -sk "https://localhost/auth/realms/$REALM/protocol/openid-connect/token" -d grant_type=client_credentials \
       --data-urlencode "client_id=$1" --data-urlencode "client_secret=$2" \
       | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
     curl -sk https://localhost/st-config/connect/introspect \
       --data-urlencode "client_id=$1" --data-urlencode "client_secret=$2" --data-urlencode "token=$T"; echo
   }
   own "$ADMIN_ID" "$ADMIN_SECRET"
   own '<key>' '<secret>'   # each API key in the private credentials doc
   ```

   Each answer must be `"active":true` with the client's `client_id`, `scope`, `exp`, `iat`, `sub`, `aud` and `iss`; `{"active":false}` for a fresh token means that client still lacks its own audience.

Verify with the existing keys once the DMS services report healthy (`update.sh` returns while they are still starting):

```bash
until [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost/st-dms/health)" = 200 ] &&
  [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost/mt-dms/health)" = 200 ]; do sleep 5; done
for p in st-dms st-config mt-dms mt-config; do echo -n "$p: "; curl -sk -o /dev/null -w "%{http_code}\n" "https://localhost/$p/health"; done
cd ~/dms-src/eng/azure-vm/http
FQDN=<FQDN> ST_CREDS='key:secret' T1_CREDS='key:secret' T2_CREDS='key:secret' ./sample-all.sh
```

## Part C: Switch the Data Standard (5.2 to 6.1)

The DMS DS 6.1 populated template leaves out the educator-preparation sample data, so it carries the same 960 students in 3 schools as DS 5.2.
Step 6 loads that data, which gives the deployment the ODS/API DS 6.1 populated template's Grand Bend set: 1959 students in 9 schools.
The API changes to the DS 6.1 surface; requests written for DS 5.2 may need updating.
**Only the three data databases are recreated: anything reviewers wrote through the API is lost. The keys are kept.**
Claim sets created at runtime are deleted by step 4's claims reload; Part A step 3 exports them, and step 4 imports them back.
The DS 6.1 template must exist for the running build; builds before 8.0.1-alpha.0.196 have none, so run Part B first on an older deployment.

1. Stage the DS 6.1 ApiSchema **[bash]**, as in [`REDEPLOY.md`](REDEPLOY.md) Part C but with `.env.template.ds61`:

   ```bash
   cd ~/dms-src/eng/docker-compose
   rm -rf .bootstrap/ApiSchema
   pwsh ./prepare-dms-schema.ps1 -EnvironmentFile ./.env.template.ds61 -SchemaToolPath ./.bootstrap/tools/api-schema-tools/api-schema-tools
   rm -rf ~/dms-src/eng/azure-vm/compose/.bootstrap/ApiSchema
   cp -r ./.bootstrap/ApiSchema ~/dms-src/eng/azure-vm/compose/.bootstrap/
   ```

   Rebuild the schema tool first (REDEPLOY Part C step 1) if the checkout moved since it was built.

2. Point `.env` at DS 6.1 and open the claims reload **[bash]**.
   Pin the template to the version of the running DMS image so the two carry the same effective schema:

   ```bash
   cd ~/dms-src/eng/azure-vm/compose
   VERSION=$(docker inspect dms-sec-st-dms --format '{{index .Config.Labels "org.opencontainers.image.version"}}' | sed 's/^dms-pre-//; s/^dms-v//')
   sed -i \
     -e 's/^CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=.*/CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=6.1/' \
     -e 's/^CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=.*/CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true/' \
     -e 's/^DATABASE_TEMPLATE_PACKAGE_ID=.*/DATABASE_TEMPLATE_PACKAGE_ID=EdFi.Api.Populated.Template.PostgreSql.6.1.0/' \
     -e "s/^DATABASE_TEMPLATE_PACKAGE_VERSION=.*/DATABASE_TEMPLATE_PACKAGE_VERSION=$VERSION/" .env
   grep -q '^CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=' .env || echo 'CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=6.1' >> .env
   grep -E '^(CONFIGURATION_SERVICE_DATA_STANDARD_VERSION|CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING|DATABASE_TEMPLATE_PACKAGE_ID|DATABASE_TEMPLATE_PACKAGE_VERSION)=' .env
   ```

3. Recreate the data databases from the DS 6.1 template **[bash]**:

   ```bash
   docker stop dms-sec-st-dms dms-sec-mt-dms
   for db in edfi_st edfi_mt edfi_mt_t2; do
     docker exec dms-sec-postgres psql -U postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE $db WITH (FORCE);" -c "CREATE DATABASE $db;"
   done
   bash ./seed/grandbend.sh edfi_st edfi_mt edfi_mt_t2
   ```

4. Switch the Configuration Service claims to DS 6.1 **[bash]**.
   It loads claims only into an empty database, so the recreated services need one reload:

   ```bash
   docker compose -f docker-compose.yml -f keycloak.compose.yml --env-file .env up -d --no-deps st-config mt-config
   until [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost/st-config/health)" = 200 ] &&
     curl -sk -o /dev/null -w '%{http_code}' https://localhost/mt-config/health | grep -qE '^(200|400)$'; do sleep 5; done
   ADMIN_ID=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_ID=' .env | cut -d= -f2-)
   ADMIN_SECRET=$(grep -E '^BOOTSTRAP_ADMIN_CLIENT_SECRET=' .env | cut -d= -f2-)
   TENANT1=$(grep -E '^MT_TENANT_1=' .env | cut -d= -f2-); TENANT1=${TENANT1:-tenant1}
   for cms in st-config mt-config; do
     HDR=(); [ "$cms" = mt-config ] && HDR=(-H "Tenant: $TENANT1")
     TOKEN=$(curl -sk "https://localhost/$cms/connect/token" -d grant_type=client_credentials -d scope=edfi_admin_api/full_access \
       --data-urlencode "client_id=$ADMIN_ID" --data-urlencode "client_secret=$ADMIN_SECRET" \
       | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
     curl -sk -X POST "https://localhost/$cms/management/reload-claims" -H "Authorization: Bearer $TOKEN" "${HDR[@]}"; echo
   done
   ```

   Each call must answer `"success": true`.
   The claims are shared by every tenant, so one multi-tenant reload covers both tenants.
   If Part A step 3 exported claim sets, import them back now; each import must answer `201` with an empty `warnings` list:

   ```bash
   for f in ~/backup/claimsets/*.json; do
     [ -e "$f" ] || continue
     IFS=. read -r cms tenant id _ <<< "$(basename "$f")"
     HDR=(); [ "$cms" = mt-config ] && HDR=(-H "Tenant: $tenant")
     TOKEN=$(curl -sk "https://localhost/$cms/connect/token" -d grant_type=client_credentials -d scope=edfi_admin_api/full_access \
       --data-urlencode "client_id=$ADMIN_ID" --data-urlencode "client_secret=$ADMIN_SECRET" \
       | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')
     echo "$cms $tenant $id: $(curl -sk -X POST "https://localhost/$cms/v3/claimSets/import" -H "Authorization: Bearer $TOKEN" "${HDR[@]}" \
       -H 'Content-Type: application/json' --data-binary @"$f" -w ' %{http_code}')"
   done
   ```

   A resource claim the new Data Standard lacks is left out and named in `warnings`: rename it in that file to its new equivalent, delete the imported claim set (`DELETE /v3/claimSets/{id}` with the `id` the import answered), and import the file again.
   The applications bound to a claim set pick it up again by name.
   Then close the reload endpoint again:

   ```bash
   sed -i 's/^CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=.*/CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=false/' .env
   docker compose -f docker-compose.yml -f keycloak.compose.yml --env-file .env up -d --no-deps st-config mt-config
   ```

5. Start the DMS services and check the existing keys **[bash]**:

   ```bash
   ./up.sh st-dms mt-dms
   # then the health loop and sample-all.sh from Part B
   ```

6. Load the educator-preparation data **[bash + pwsh]**.
   Fetch the DS 6.1 sample data once, then load it into all three deployments (about two to three minutes each):

   ```bash
   git clone --depth 1 --branch v6.1.0 --filter=blob:none --sparse https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Data-Standard.git ~/ds61
   git -C ~/ds61 sparse-checkout set "Samples/Sample XML"
   cd ~/dms-src/eng/azure-vm/compose
   pwsh ./seed/load-educator-prep.ps1 -SampleDataDirectory "$HOME/ds61/Samples/Sample XML" -BaseUrl https://localhost -Insecure
   ```

   The script loads the files the template excluded, then re-posts the template files whose records reference educator-prep students.
   Each pass uses a temporary loader application that it deletes by id afterwards.
   A re-run is safe, also after the databases are recreated: each pass clears the BulkLoadClient's record cache, so it sends every record again, and existing records are upserted.

7. Compare with the ODS/API DS 6.1 populated template **[bash]**:

   ```bash
   python3 ./seed/check-ods-parity.py --package-version 7.3.20067 \
     --database edfi_st --database edfi_mt --database edfi_mt_t2 --allow-diff schoolyeartype
   ```

   Expect every populated table to match (1959 students, 9 schools, 2958 enrollments, 999 candidates, 4012 descriptors).
   The one allowed difference is `schoolyeartype`: the DMS template seeds school years 1991 to 2037 (47 rows), the ODS template 1991 to 2050 (60 rows).

## Part D: Add the review-variant keys  [pwsh]

Adds four applications to each deployment, 12 key/secret pairs in all:

| Claim set | Education organization | Vendor namespace |
|-----------|------------------------|------------------|
| `SISVendor` | 255901 (district) | `uri://ed-fi.org` |
| `SISVendor` | 255901107 (school) | `uri://ed-fi.org` |
| `AssessmentVendor` | 255901 (district) | `uri://one.example.com` |
| `EdFiSandbox` | 255901 (district) | `uri://ed-fi.org` |

```bash
cd ~/dms-src/eng/azure-vm/compose
pwsh ./bootstrap/add-review-variants.ps1 -BaseUrl https://localhost -Insecure -OutFile ~/review-variants.json
FQDN=<FQDN> python3 ../http/sample-variants.py ~/review-variants.json
```

A fresh deploy already creates these (`bootstrap.ps1`); the script skips any that exist, and an existing key's secret cannot be read back.
The script refuses an `-OutFile` that already exists, because a re-run would rewrite it with only that run's pairs: keep the file until its pairs are in the private credentials doc, and give any re-run a new path.
If the script fails part-way, it prints the pairs it already created before the error; copy them before re-running, because a re-run skips those applications.
A secret lost anyway can be replaced with `PUT /v3/apiClients/{id}/reset-credential` on that deployment's Configuration Service (with the `Tenant` header on the multi-tenant one).
Expected access: the district keys see every school and student, the school key sees only that school's students (400), and the AssessmentVendor key gets 403 on schools and sees no `uri://ed-fi.org` assessments.
Copy the pairs into the private credentials doc, then delete `~/review-variants.json`.

## Rollback

- **Part B:** pin the previous images (the versions recorded in Part A) by setting `DMS_IMAGE_TAG`, or by re-tagging the recorded image ids as `pre`, then `./up.sh`.
  First run `git -C ~/dms-src switch --detach <commit recorded in Part A>`, so the Compose files match those images: an image built before a configuration key rename ignores the new key, and an older DMS image then loads its bundled ApiSchema instead of `/app/ApiSchema` and answers `503` on the `EffectiveSchemaHash` mismatch.
  A Configuration Service migration does not roll back, so a downgrade across one needs the Part A config database backup.
  A build from before the fragment rename reads only `*-claimset.json`, and ignores `*-claims.json` without a warning, so rename the files under `compose/claims` back first.
- **Part C:** the keys were never touched; repeat from the failed step.
  Going back to DS 5.2 is the same procedure with `.env.template`, `CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=5.2` and the `5.2.0` template.
- **Lost keys:** stop the stack, restore `keycloak-volume.tgz` into the volume and `pg_restore` the config dumps, then start it again.
