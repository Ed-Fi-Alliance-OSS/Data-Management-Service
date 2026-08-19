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
| PostgreSQL artifact | published to `https://odsassets.blob.core.windows.net/public/Northridge/` |
| SQL Server artifact | `EdFi_DMS_Northridge_v80_20260808_MSSQL.7z`, sha256 `2b7f1318bdbd5bcead90e6b74bfc3918ff12d31391a88f35f46f3199b6171d71` |
| Source ODS | `EdFi_Ods_Northridge_v73_20241230_PG13.7z` |
| ApiSchema | `EdFi.DataStandard52.ApiSchema` v1.0.333, **core only, no TPDM** |

> The repository default for Data Standard 5.2 (`.env.bootstrap.ds52`) stages core **and** TPDM, which
> computes a different `EffectiveSchemaHash` and makes DMS answer 503 for every request. Pinning to
> core only is required, not advisory.

## Restore recipe -- PostgreSQL

Placeholders in angle brackets are filled in from the published provenance record. Set the first two
variables; everything after that is copy-paste.

```shell
DC=~/src/Data-Management-Service/eng/docker-compose   # your checkout
ART=~/northridge-artifact                             # scratch dir, needs ~12 GB

# 1. Download and verify. A checksum mismatch means stop -- do not restore a partial download.
mkdir -p "$ART" && cd "$ART"
curl -O https://odsassets.blob.core.windows.net/public/Northridge/<artifact>.7z
echo "<sha256>  <artifact>.7z" | sha256sum -c -

# 2. Extract. Runs in a container so no host 7-Zip is needed.
docker run --rm -v "$PWD:/w" alpine sh -c \
  'apk add --no-cache p7zip >/dev/null 2>&1 && cd /w && 7z x -y <artifact>.7z >/dev/null'
DUMP="$ART/<artifact>.dump"

# 3. Pin the schema set to core-only BEFORE bootstrap. Skipping this yields a different
#    EffectiveSchemaHash and DMS answers 503 for every request.
cd "$DC"
#    Edit .env.bootstrap.ds52 so SCHEMA_PACKAGES lists ONLY EdFi.DataStandard52.ApiSchema 1.0.333.

# 4. Bootstrap a normal PostgreSQL stack.
pwsh -NoProfile -File ./bootstrap-local-dms.ps1 -DatabaseEngine postgresql

# 5. Stop the applications, then restore. Nothing may hold a connection during the restore.
docker stop ed-fi-api ed-fi-api-config-service
docker cp "$DUMP" dms-postgresql:/tmp/nr.dump
docker exec dms-postgresql psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
  -c 'DROP DATABASE IF EXISTS edfi_datamanagementservice;' \
  -c 'CREATE DATABASE edfi_datamanagementservice;'
docker exec dms-postgresql pg_restore -U postgres -d edfi_datamanagementservice \
  --no-owner --no-privileges /tmp/nr.dump
docker exec -u 0 dms-postgresql rm -f /tmp/nr.dump

# 6. REQUIRED: rotate dms.DataStoreIdentity.SourceIdentity.
#    Restoring this artifact creates an independent writable data store from a copied backup, and the
#    data-model contract assigns a new source identity in exactly that case, before the data store
#    becomes available. Rotation is never part of DDL rerun or DMS startup, so it must happen here.
#    If you are REPLACING an existing CDC-enabled source rather than standing up a new one, do not use
#    this UPDATE -- rotate through the CDC recovery workflow instead, which also requires a new binding
#    generation, topics, and consumer state namespace.
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -v ON_ERROR_STOP=1 -c \
  'UPDATE dms."DataStoreIdentity" SET "SourceIdentity" = gen_random_uuid()
   WHERE "DataStoreIdentitySingletonId" = 1;'
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tAc \
  'SELECT "SourceIdentity" FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;'
#    Expect a non-zero UUID that differs from the value recorded in the provenance note.

# 7. Start the Configuration Service ONLY. Do not start DMS yet -- see step 8.
docker start ed-fi-api-config-service
until [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8081/health)" = "200" ]; do sleep 3; done

# 8. REQUIRED for every consumer: register a CMS admin client and re-save the data store.
#    The local PostgreSQL stack is single-database, so this artifact carries dmscs.* alongside dms.*.
#    The restored dmscs rows are PRODUCER-LOCAL: the stored DataStore.ConnectionString describes the
#    machine that produced the artifact and is encrypted with that machine's
#    DMS_CONFIG_DATABASE_ENCRYPTION_KEY, and the restore replaced dmscs.OpenIddict*, so any client
#    registered before the restore no longer exists. Re-saving re-encrypts with YOUR key.
#    Skip this and DMS restart-loops with "Failed to decrypt the connection string".
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
curl -s -X PUT "$CMS/v3/dataStores/1" -H "Authorization: Bearer $T" \
  -H "Content-Type: application/json" \
  -d '{"id":1,"dataStoreType":"Development","name":"Local Development Data Store",
       "connectionString":"host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"}' \
  -w 'data store re-saved -> HTTP %{http_code}\n'

# 9. Now start DMS. A cached validation failure is NOT cleared by re-provisioning, so if DMS was
#    started too early, restart it rather than re-running any provisioning step.
docker start ed-fi-api
until [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health)" = "200" ]; do sleep 3; done
echo "DMS healthy"

# 10. Verify content.
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -tAc \
  'SELECT COUNT(*) FROM dms."Document";'
#     Expect: <documentCount>
```

The bootstrap scripts have no restore support of their own; there is no restore-from-backup switch in
`eng/docker-compose`, and provisioning always builds a fresh schema. The recipe above is a manual
restore sequenced around the bootstrap phases.

### Consumer checklist

| Step | Required for | Why |
| --- | --- | --- |
| Pin `SCHEMA_PACKAGES` to core only | everyone | a different schema set computes a different `EffectiveSchemaHash`, and DMS answers 503 |
| Rotate `SourceIdentity` (step 6) | everyone standing up a new data store | a restored copied backup is an independent writable data store and must not share a source identity with the producer |
| Register admin client, re-save data store (step 8) | everyone | the shipped `dmscs` secrets and encrypted connection string are producer-local |
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

## Never commit

Dataset artifacts and their by-products do not belong in this repository: `*.7z`, `*.dump`, `*.bak`,
schema snapshots, count and reconciliation output, container logs, connection strings, passwords, SAS
tokens, and encryption keys. Point the scripts' `-OutputDirectory` at a scratch location outside the
repository.
