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

2. Builds from October 2026 add Configuration Service migration `0035`, which makes data store names unique per tenant and API client names unique per application.
   Startup stops with `Name uniqueness upgrade blocked` if the database already has duplicates.
   Check before updating; every query must return zero rows:

   ```bash
   for db in edfi_st_config edfi_mt_config; do
     docker exec dms-sec-postgres psql -U postgres -d "$db" \
       -c 'SELECT "TenantId", "Name", count(*) FROM dmscs."DataStore" GROUP BY 1, 2 HAVING count(*) > 1;' \
       -c 'SELECT "ApplicationId", "Name", count(*) FROM dmscs."ApiClient" GROUP BY 1, 2 HAVING count(*) > 1;'
   done
   ```

   Rename or delete any duplicate through `PUT`/`DELETE /v3/dataStores/{id}` or `/v3/apiClients/{id}` on the current build first.

3. Back up the credential state.
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

4. Record what is running, for the credentials doc and for rollback:

   ```bash
   for c in dms-sec-st-dms dms-sec-st-config dms-sec-keycloak; do
     docker inspect "$c" --format '{{.Name}} {{.Config.Image}} {{index .Config.Labels "org.opencontainers.image.version"}} {{.Image}}'
   done
   ```

## Part B: Update to a newer build  [bash]

```bash
cd ~/dms-src && git fetch --force --tags origin && git switch --detach origin/main && git log -1 --oneline &&
  cd eng/azure-vm/compose && SKIP_GIT=1 ./update.sh
```

Replace `origin/main` with a release tag to deploy a release.
The single `&&` chain matters: if the fetch or switch fails, `update.sh` must not pull new images against the old checkout.
`--force` lets the fetch move a tag that was re-pointed upstream; without it the fetch fails with `would clobber existing tag`.

`update.sh` pulls the images for the current `DMS_IMAGE_TAG` (`pre` by default) and recreates the changed containers.
The Configuration Service applies its database migrations at startup.
If a config service stays unhealthy, `./logs.sh st-config` (or `mt-config`) names the migration that failed.

Keycloak: `keycloak.yml` pins the floating minor tag `26.7`.
`update.sh` refuses a changed pin, because the H2 realm volume cannot cross minor versions, but it does apply a patch release published under the same tag.
The 26.7.4 to 26.7.5 move has been rehearsed; Keycloak migrated the realm and every key kept working.

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
     -e 's/^DMS_CONFIG_DATA_STANDARD_VERSION=.*/DMS_CONFIG_DATA_STANDARD_VERSION=6.1/' \
     -e 's/^DMS_CONFIG_ENABLE_CLAIMS_RELOAD=.*/DMS_CONFIG_ENABLE_CLAIMS_RELOAD=true/' \
     -e 's/^DATABASE_TEMPLATE_PACKAGE_ID=.*/DATABASE_TEMPLATE_PACKAGE_ID=EdFi.Api.Populated.Template.PostgreSql.6.1.0/' \
     -e "s/^DATABASE_TEMPLATE_PACKAGE_VERSION=.*/DATABASE_TEMPLATE_PACKAGE_VERSION=$VERSION/" .env
   grep -q '^DMS_CONFIG_DATA_STANDARD_VERSION=' .env || echo 'DMS_CONFIG_DATA_STANDARD_VERSION=6.1' >> .env
   grep -E '^(DMS_CONFIG_DATA_STANDARD_VERSION|DMS_CONFIG_ENABLE_CLAIMS_RELOAD|DATABASE_TEMPLATE_PACKAGE_ID|DATABASE_TEMPLATE_PACKAGE_VERSION)=' .env
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
   docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps st-config mt-config
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
   Then close the reload endpoint again:

   ```bash
   sed -i 's/^DMS_CONFIG_ENABLE_CLAIMS_RELOAD=.*/DMS_CONFIG_ENABLE_CLAIMS_RELOAD=false/' .env
   docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps st-config mt-config
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
   A re-run is safe: existing records are upserted.

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
| `SISVendor` | 255901 (district) | `uri://ed-fi.org/` |
| `SISVendor` | 255901107 (school) | `uri://ed-fi.org/` |
| `AssessmentVendor` | 255901 (district) | `uri://one.example.com` |
| `EdFiSandbox` | 255901 (district) | `uri://ed-fi.org/` |

```bash
cd ~/dms-src/eng/azure-vm/compose
pwsh ./bootstrap/add-review-variants.ps1 -BaseUrl https://localhost -Insecure -OutFile ~/review-variants.json
FQDN=<FQDN> python3 ../http/sample-variants.py ~/review-variants.json
```

A fresh deploy already creates these (`bootstrap.ps1`); the script skips any that exist.
Expected access: the district keys see every school and student, the school key sees only that school's students (400), and the AssessmentVendor key gets 403 on schools and sees no `uri://ed-fi.org` assessments.
Copy the pairs into the private credentials doc, then delete `~/review-variants.json`.

## Rollback

- **Part B:** pin the previous images (the versions recorded in Part A) by setting `DMS_IMAGE_TAG`, or by re-tagging the recorded image ids as `pre`, then `./up.sh`.
  A Configuration Service migration does not roll back, so a downgrade across one needs the Part A config database backup.
- **Part C:** the keys were never touched; repeat from the failed step.
  Going back to DS 5.2 is the same procedure with `.env.template`, `DMS_CONFIG_DATA_STANDARD_VERSION=5.2` and the `5.2.0` template.
- **Lost keys:** stop the stack, restore `keycloak-volume.tgz` into the volume and `pg_restore` the config dumps, then start it again.
