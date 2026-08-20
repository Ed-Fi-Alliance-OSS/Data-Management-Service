<!--
SPDX-License-Identifier: Apache-2.0
Licensed to the Ed-Fi Alliance under one or more agreements.
The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
See the LICENSE and NOTICES files in the project root for more information.
-->

# Northridge dataset workflow

Tooling and recipes for producing and consuming the published Northridge DMS datasets.

Northridge is a ~10.58M document dataset used for performance and storage work at realistic
volume. There are two published artifacts, one per engine, and they are only useful as a matched
pair: comparing DMS on PostgreSQL against DMS on SQL Server is meaningful only when both datasets
hold the same documents on the same effective schema.

There is **no in-place migration path for the DMS document store**. Provisioning is create-only:
`ddl provision` emits full DDL, journaled versioned deploy scripts exist only for the Configuration
Service, and `dms.EffectiveSchema` is a deployment invariant that returns 503 on mismatch. Bringing a
published dataset onto a newer schema therefore means deploying a fresh database at the current schema
and copying the data across -- never patching the published artifact.

## Contents

| Script | Purpose |
| --- | --- |
| [`Copy-NorthridgeDataForward.ps1`](./Copy-NorthridgeDataForward.ps1) | Copy dataset tables from a published dump into a freshly provisioned database, derive `dms.Descriptor.ResourceKeyId`, and assert the post-copy and checkpoint invariants |
| [`Compare-DmsSchemaSnapshot.ps1`](./Compare-DmsSchemaSnapshot.ps1) | Capture a normalized catalog snapshot of a database and diff two snapshots |
| [`Get-DmsResourceCount.ps1`](./Get-DmsResourceCount.ps1) | Per-resource document counts for PostgreSQL or SQL Server, and the both-direction reconciliation between two count sets |
| [`Add-NorthridgeGapDocument.ps1`](./Add-NorthridgeGapDocument.ps1) | POST documents through the DMS API from a manifest and verify each with a GET-by-id |

All four scripts run PostgreSQL and SQL Server client tools **inside containers**, so no host `psql`,
`pg_restore`, `pg_dump`, or `sqlcmd` installation is required. Every script supports `-WhatIf` and
prints its plan without touching a database.

## Why the client tools run in containers

The published restore recipe has to work for a consumer who has Docker and a checkout and nothing
else. Assuming a host PostgreSQL client installation is the most common reason a documented recipe
fails for its reader, so the scripts and the recipe below use the same containerized invocation.

## Artifact identities

| Item | Value |
| --- | --- |
| PostgreSQL artifact | `EdFi_DMS_Northridge_v80_20260819_PG.7z`, 869,019,055 bytes, sha256 `49129363581eab342146e8dd9a4da95dd6f7b035f0c39ee39c9691176cd856a0` |
| — inner dump | `EdFi_DMS_Northridge_v80_20260819_PG.dump`, 888,167,269 bytes, sha256 `08c03fe279e7ab10516f3e29c8009dca76b154fe67b5b0aaaad31409035d4167` |
| — contents | 10,576,801 documents across 210 resources; `dms` 10 tables, `edfi` 467, `tracked_changes_edfi` 139, `dmscs` 24, `auth` 1, `public` 1 |
| SQL Server artifact | `EdFi_DMS_Northridge_v80_20260808_MSSQL.7z`, sha256 `2b7f1318bdbd5bcead90e6b74bfc3918ff12d31391a88f35f46f3199b6171d71` |
| Source ODS | `EdFi_Ods_Northridge_v73_20241230_PG13.7z`, sha256 `b5e1acf0ea82f226ea44a7ce6bc2c97bc2cd78342ebdd6923a98b54bc34f137e` |
| ApiSchema | `EdFi.DataStandard52.ApiSchema` v1.0.333, **core only, no TPDM** |
| Effective schema hash | `816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b` — identical on both engines |

> The repository default for Data Standard 5.2 stages core **and** TPDM, which computes a different
> `EffectiveSchemaHash` and makes DMS answer 503 for every request. Pinning to core only is required,
> not advisory — see step 3 of the recipe for the path that actually works, and why editing
> `SCHEMA_PACKAGES` or setting it in the shell does not.

## Restore recipe -- PostgreSQL

Verified by executing this exact text from a clean slate on 2026-08-19, on a stack deliberately
configured with a non-default PostgreSQL password, a non-default `DMS_CONFIG_DATABASE_ENCRYPTION_KEY`
and a non-default `DMS_CONFIG_IDENTITY_ENCRYPTION_KEY`, so that nothing could pass by sharing a
default with the machine that produced the artifact.

Three steps are **mandatory for every consumer**, not conditional on your configuration differing
from the producer's. Each one is explained where it appears, and skipping any of them leaves a stack
that cannot serve the dataset:

1. pinning the schema set to core only (step 3),
2. installing your own OpenIddict signing key (step 7),
3. rotating `dms.DataStoreIdentity.SourceIdentity` (step 8).

```shell
DC=~/src/Data-Management-Service/eng/docker-compose   # your checkout
ART=~/northridge-artifact                             # scratch dir, needs ~12 GB
ARTIFACT=EdFi_DMS_Northridge_v80_20260819_PG

# 1. Download and verify. A checksum mismatch means stop -- do not restore a partial download.
mkdir -p "$ART" && cd "$ART"
curl -O "https://odsassets.blob.core.windows.net/public/Northridge/${ARTIFACT}.7z"
echo "49129363581eab342146e8dd9a4da95dd6f7b035f0c39ee39c9691176cd856a0  ${ARTIFACT}.7z" | sha256sum -c -

# 2. Extract, in a container so no host 7-Zip is needed. The inner dump is 888,167,269 bytes with
#    sha256 08c03fe279e7ab10516f3e29c8009dca76b154fe67b5b0aaaad31409035d4167.
docker run --rm -v "$PWD:/w" alpine sh -c \
  "apk add --no-cache p7zip >/dev/null 2>&1 && cd /w && 7z x -y ${ARTIFACT}.7z >/dev/null"
DUMP="$ART/${ARTIFACT}.dump"

# 3. REQUIRED: stage the schema set as CORE ONLY, before bootstrap.
#    The artifact is provisioned for EdFi.DataStandard52.ApiSchema 1.0.333, core only, effective
#    schema hash 816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b. The repository
#    default for Data Standard 5.2 stages core PLUS TPDM, which computes a different hash and makes
#    DMS answer 503 for every request.
#
#    Use the expert ApiSchemaPath path rather than editing SCHEMA_PACKAGES: the bootstrap wrapper
#    reads SCHEMA_PACKAGES from the environment FILE, so an ambient shell variable is ignored, and
#    the DS 5.2 overlay would overwrite a value set in your own base env file.
cd "$DC"
mkdir -p "$ART/apischema-core-only"
#    Take the core ApiSchema.json from the NuGet package EdFi.DataStandard52.ApiSchema 1.0.333,
#    at contentFiles/any/any/ApiSchema/ApiSchema.json, and place it in that directory ALONE.
cp <path-to-package>/contentFiles/any/any/ApiSchema/ApiSchema.json "$ART/apischema-core-only/"

dotnet publish ../../src/dms/clis/EdFi.DataManagementService.SchemaTools \
  -c Release -p:UseAppHost=true -o .bootstrap/tools/api-schema-tools
#    No DMS_SCHEMA_TOOL_PATH is needed: `.bootstrap/tools/api-schema-tools` is the documented publish
#    location and is probed first, with the platform executable suffix handled for you.
pwsh -NoProfile -File ./prepare-dms-schema.ps1 -ApiSchemaPath "$ART/apischema-core-only"
#    Expect: "Effective schema hash: 816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b"
#    If that hash differs, STOP: the restored database will answer 503 and no later step will fix it.

# 4. Bootstrap a normal PostgreSQL stack. The staged core-only workspace is reused as-is.
pwsh -NoProfile -File ./bootstrap-local-dms.ps1 -DatabaseEngine postgresql -IdentityProvider self-contained

# 5. Stop the applications, then restore. Nothing may hold a connection during the restore.
#    Read the live values rather than assuming defaults.
DB=$(docker exec dms-postgresql printenv POSTGRES_DB_NAME)
docker stop ed-fi-api ed-fi-api-config-service
docker cp "$DUMP" dms-postgresql:/tmp/nr.dump
docker exec dms-postgresql psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"$DB\";" -c "CREATE DATABASE \"$DB\";"
docker exec dms-postgresql pg_restore -U postgres -d "$DB" --no-owner --no-privileges /tmp/nr.dump
docker exec -u 0 dms-postgresql rm -f /tmp/nr.dump

# 6. Verify the restore by content, not by exit code. pg_restore continues past a failed COPY and
#    still exits 0, reporting the count only as a warning, so the count below is the real check.
docker exec dms-postgresql psql -U postgres -d "$DB" -tAc 'SELECT COUNT(*) FROM dms."Document";'
#    Expect exactly: 10576801

# 7. REQUIRED: install your own OpenIddict signing key.
#    The artifact carries the producer's dmscs."OpenIddictKey" row, whose private key is encrypted
#    with the PRODUCER's DMS_CONFIG_IDENTITY_ENCRYPTION_KEY. Yours differs, so CMS cannot decrypt it
#    and POST /connect/token answers 500 with "No active private key or key id found". Without a
#    token no CMS API call succeeds, so step 9 becomes impossible -- this step must precede it.
#
#    Note this is a DIFFERENT key from DMS_CONFIG_DATABASE_ENCRYPTION_KEY, which protects the data
#    store connection string in step 9. The two are configured separately and both matter here.
#    Read the key with `docker inspect`, not `docker exec`: the CMS container is stopped at this
#    point in the recipe and `docker exec` refuses a stopped container.
IDK=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' ed-fi-api-config-service |
      sed -n 's/^IdentitySettings__EncryptionKey=//p')
test -n "$IDK" || { echo "could not read the CMS identity encryption key"; exit 1; }
pwsh -NoProfile -File ./Generate-OpenIddictKey-Insert.ps1 -EncryptionKey "$IDK" > "$ART/newkey.sql"
docker exec dms-postgresql psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 \
  -c 'UPDATE dmscs."OpenIddictKey" SET "IsActive" = FALSE;'
docker cp "$ART/newkey.sql" dms-postgresql:/tmp/newkey.sql
docker exec dms-postgresql psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 -f /tmp/newkey.sql
docker exec -u 0 dms-postgresql rm -f /tmp/newkey.sql && rm -f "$ART/newkey.sql"

# 8. REQUIRED: rotate dms.DataStoreIdentity.SourceIdentity.
#    Restoring this artifact creates an independent writable data store from a copied backup, and the
#    data-model contract assigns a new source identity in exactly that case, before the data store
#    becomes available. Rotation is never part of DDL rerun or DMS startup, so it must happen here.
#
#    If you are REPLACING an existing CDC-enabled source rather than standing up a new one, do NOT
#    use this UPDATE. Rotate through the CDC recovery workflow instead, which also requires a new
#    binding generation, topics and consumer state namespace.
docker exec dms-postgresql psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 -c \
  'UPDATE dms."DataStoreIdentity" SET "SourceIdentity" = gen_random_uuid()
   WHERE "DataStoreIdentitySingletonId" = 1;'
docker exec dms-postgresql psql -U postgres -d "$DB" -tAc \
  'SELECT "SourceIdentity" FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;'
#    Expect a non-zero UUID that differs from 8b962de6-b979-49aa-bce0-ca59e0a1ad51, the value the
#    artifact ships with. Sharing that value with the producer is what this step exists to prevent.

# 9. Start the Configuration Service ONLY, then re-save the data store.
#    The local PostgreSQL stack is single-database, so the artifact carries dmscs.* alongside dms.*.
#    The restored dmscs rows are PRODUCER-LOCAL: the stored DataStore.ConnectionString describes the
#    machine that produced the artifact and is encrypted with that machine's
#    DMS_CONFIG_DATABASE_ENCRYPTION_KEY. Re-saving re-encrypts it with yours. The restore also
#    replaced dmscs.OpenIddict*, so register the admin client AFTER the restore, not before.
#    Skip this and DMS restart-loops with "Failed to decrypt the connection string".
docker start ed-fi-api-config-service
until [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8081/health)" = "200" ]; do sleep 3; done

CMS=http://localhost:8081
curl -s -X POST "$CMS/connect/register" -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "ClientId=restore-admin" \
  --data-urlencode "ClientSecret=ValidClientSecret1234567890!Abcd" \
  --data-urlencode "DisplayName=Restore Admin" > /dev/null
T=$(curl -s -X POST "$CMS/connect/token" -H "Content-Type: application/x-www-form-urlencoded" \
  --data-urlencode "client_id=restore-admin" \
  --data-urlencode "client_secret=ValidClientSecret1234567890!Abcd" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "scope=edfi_admin_api/full_access" \
  | sed -n 's/.*"access_token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
test -n "$T" || { echo "no token: step 7 did not take effect"; exit 1; }

PW=$(docker exec dms-postgresql printenv POSTGRES_PASSWORD)
curl -s -X PUT "$CMS/v3/dataStores/1" -H "Authorization: Bearer $T" \
  -H "Content-Type: application/json" \
  -d "{\"id\":1,\"dataStoreType\":\"Development\",\"name\":\"Local Development Data Store\",
       \"connectionString\":\"host=dms-postgresql;port=5432;username=postgres;password=${PW};database=${DB};\"}" \
  -w 'data store re-saved -> HTTP %{http_code}\n'

# 10. Now start DMS. A cached first-use validation failure is NOT cleared by re-provisioning, so if
#     DMS was started too early, restart the container rather than re-running any provisioning step.
docker start ed-fi-api
until [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health)" = "200" ]; do sleep 3; done
echo "DMS healthy"

# 11. Create your own DMS API client before reading data.
#     A healthy DMS is not yet a readable one. The artifact carries the PRODUCER's vendor,
#     application and client rows in dmscs, whose secrets you do not have, so create your own.
#     `EdFiAPIPublisherWriter` is the claim set to ask for: API Publisher loaded this dataset
#     originally, so that claim set already covers every resource present.
VID=$(curl -s -X POST "$CMS/v3/vendors" -H "Authorization: Bearer $T" \
  -H "Content-Type: application/json" -o /dev/null -w '%{header_json}' \
  -d '{"company":"Local Consumer","contactName":"Consumer","contactEmailAddress":"consumer@example.com",
       "namespacePrefixes":"uri://ed-fi.org"}' | sed -n 's|.*/v3/vendors/\([0-9]*\).*|\1|p')
#     POST /v3/vendors answers 201 with an EMPTY body; the new id is in the Location header only.
curl -s -X POST "$CMS/v3/applications" -H "Authorization: Bearer $T" \
  -H "Content-Type: application/json" \
  -d "{\"applicationName\":\"Local Consumer Read\",\"vendorId\":${VID},
       \"claimSetName\":\"EdFiAPIPublisherWriter\",\"educationOrganizationIds\":[255901],
       \"dataStoreIds\":[1]}"
#     The application response carries the "key" and "secret" -- the only time the secret is shown.
#     Mint a DMS token with them exactly as in step 9, then read:
#       curl -H "Authorization: Bearer <dms-token>" \
#            "http://localhost:8080/data/ed-fi/students?limit=1&totalCount=true"
#     Expect HTTP 200 with Total-Count: 21628.
```

> **Do not drop and recreate the database part-way through this recipe and expect a CMS restart to
> recover.** CMS deploys the `dmscs` schema on startup but does not seed an OpenIddict signing key, so
> a recreated database leaves `dmscs."OpenIddictKey"` empty, `POST /connect/token` answers 500, and no
> CMS API call can succeed. Recovering means re-running step 7, or re-running the bootstrap. If you
> need to start over, start over from step 4.

### Consumer checklist

| Step | Required for | Why |
| --- | --- | --- |
| Stage core-only ApiSchema (step 3) | everyone | a different schema set computes a different `EffectiveSchemaHash`, and DMS answers 503 |
| Verify the document count (step 6) | everyone | `pg_restore` exits 0 after skipping a failed COPY, so only content proves the restore |
| Install your own OpenIddict key (step 7) | everyone | the shipped private key is encrypted with the producer's identity key; without this no token mints |
| Rotate `SourceIdentity` (step 8) | everyone standing up a new data store | a restored copied backup is an independent writable data store and must not share a source identity with the producer |
| Register admin client, re-save data store (step 9) | everyone | the shipped `dmscs` connection string is producer-local and encrypted with the producer's database key |
| Create your own DMS API client (step 11) | everyone who reads data | the shipped vendor/application/client rows are the producer's and their secrets are not published |
| Restart DMS rather than re-provisioning | anyone who saw a 503 | first-use validation failures are cached for the process lifetime |

## Provenance record

Every published artifact records the following, on its ticket and in this directory's history:

1. **The artifact** -- URL, file name, `.7z` and inner `.dump` sizes and sha256, document count,
   resource count, engine image, ApiSchema package and version, schema set, `EffectiveSchemaHash`,
   `ResourceKeyCount`, `ResourceKeySeedHash`, and the `SourceIdentity` the artifact ships with, so a
   consumer can prove rotation happened.
2. **Provenance** -- source artifact names and checksums, source ODS artifact, DMS commit and branch,
   date produced, and what changed relative to the artifact it supersedes.
3. **Restore recipe** -- the text above with placeholders filled, validated by execution from a clean
   slate against non-default credentials.
4. **Validation evidence** -- schema compare result, effective schema hash agreement, DMS smoke
   results, the full resource-by-resource reconciliation with both-direction diff counts, per-table
   row-count reconciliation, sequence-position assertions, and the invariant checkpoint table.
5. **Known limitations** -- that the shipped CMS state is producer-local, and anything deferred.

### Record for `EdFi_DMS_Northridge_v80_20260819_PG`

| | |
| --- | --- |
| Supersedes | `EdFi_DMS_Northridge_07_20260708.7z` — 10,576,794 documents on the DMS-1221 schema |
| What changed | brought to the current schema by fresh deployment plus copy-forward (the document store has no migration path, so the old database was never patched in place); added the 7 documents the old artifact was missing |
| The 7 added documents | Staff +2 (Krystal Redd, Lorraine Chen), StaffEducationOrganizationEmploymentAssociation +2, StaffEducationOrganizationAssignmentAssociation +2, AccountabilityRating +1 (EdOrg 255901, 2018, "Accountability Rating", "Recognized") |
| Sourced from | the Northridge ODS artifact named above, added through the DMS API with GET-by-id verification of every field on every document — not via API Publisher, whose exit code can be 0 after silently dropping documents on 4xx |
| Produced from | branch `DMS-1406`, DMS built from source at the branch head |
| `SourceIdentity` as shipped | `8b962de6-b979-49aa-bce0-ca59e0a1ad51` — rotate it (step 8); a consumer whose value still reads this has skipped the step |
| `ResourceKey` rows | 351 |
| Descriptors | 2,968 |
| `ChangeVersionSequence` | 21,553,810, equal to `MAX("IdentityVersion")` |

Validation, all on the restored artifact rather than on the database that produced it: schema compare
against a fresh deployment at the same revision reported no differences; the startup-computed
`EffectiveSchemaHash` equals the value stored in `dms.EffectiveSchema` and equals the SQL Server
artifact's; DMS served authenticated reads with no 503 and no restarts; the resource-by-resource
reconciliation against SQL Server reported zero differences in both directions across 210 resources
and 10,576,801 documents. Each positive result was paired with a negative control run at the same
time, because `pg_restore` exits 0 after skipping a failed `COPY`, `RESTORE VERIFYONLY` passes an
unreadable file, and a restore that never starts reports zero errors — success-shaped signals that
mean nothing on their own.

Known limitations: the `dmscs` rows in the artifact are producer-local throughout — connection string,
OpenIddict signing key, and vendor/application/client rows — which is what steps 7, 9 and 11 exist to
replace. Ownership-token stamping is not implemented at this revision, so `CreatedByOwnershipTokenId`
is null on every document. `tracked_changes_edfi` is present and empty by design. During production a
manifest re-POST was issued against the live database to exercise the field comparison; DMS treated it
as an idempotent upsert, creating no duplicates, and no `ContentVersion` moved on any of the
10,576,794 copied documents.

> The blob upload is the one step not yet exercised end to end: step 1's `curl` becomes valid when the
> artifact is published to the container above. Everything after it in the recipe was run from a clean
> slate against the exact local file whose checksums this document records.

## Never commit

Dataset artifacts and their by-products do not belong in this repository: `*.7z`, `*.dump`, `*.bak`,
schema snapshots, count and reconciliation output, container logs, connection strings, passwords, SAS
tokens, and encryption keys. Point the scripts' `-OutputDirectory` at a scratch location outside the
repository.
