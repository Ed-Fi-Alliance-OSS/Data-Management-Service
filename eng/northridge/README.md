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
| [`Compare-DmsSchemaSnapshot.ps1`](./Compare-DmsSchemaSnapshot.ps1) | Capture a normalized catalog snapshot of a database -- structure, trigger state, ownership, privileges and routine security attributes -- and diff two snapshots |
| [`Get-DmsResourceCount.ps1`](./Get-DmsResourceCount.ps1) | Per-resource document counts for PostgreSQL or SQL Server, and the both-direction reconciliation between two count sets |
| [`Add-NorthridgeGapDocument.ps1`](./Add-NorthridgeGapDocument.ps1) | POST documents through the DMS API from a manifest and verify each with a GET-by-id |

The database-oriented scripts run PostgreSQL and SQL Server client tools **inside containers**, so no
host `psql`, `pg_restore`, `pg_dump`, or `sqlcmd` installation is required.
`Add-NorthridgeGapDocument.ps1`, `Compare-DmsSchemaSnapshot.ps1` and `Copy-NorthridgeDataForward.ps1`
support `-WhatIf` and print their plan without contacting a database. `Get-DmsResourceCount.ps1` does
not: it has no `-WhatIf`, and never writes to a database -- only to its own output file.

[`tests/SchemaSnapshotSecurity.Tests.ps1`](./tests/SchemaSnapshotSecurity.Tests.ps1) covers the part of
`Compare-DmsSchemaSnapshot.ps1` that a restore can silently break, and the recipe step that puts it
back. `pg_restore --no-owner --no-privileges` drops object ownership and every `GRANT`, and the DMS
enqueue functions are `SECURITY DEFINER` owned by a dedicated role with `EXECUTE` revoked from
`PUBLIC` -- `pg_get_functiondef` carries none of that, so a definition-only snapshot would report a
restored database equivalent after it had lost the privilege boundary. Step 5b of the recipe
re-applies the DDL's security block, and the suite checks that block statement by statement against
the emitter's authoritative fixture, so a change to the DDL's grants fails a pull request before it
can fail a restore. Its query-map and recipe coverage runs in the DMS pull-request Pester lane. Its
live scenarios build and drop their own databases, dump and restore one of them with the recipe's
flags to prove the compare fails before step 5b, passes after it and still fails a real constraint
change, and use cluster roles, so they
run only when `DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER` names a **disposable** PostgreSQL container
(with `DMS_NORTHRIDGE_PG_FIXTURE_USER` if its superuser is not `postgres`), and self-skip otherwise.
The stack's own `dms-postgresql` is not that container: the fixtures create and drop databases and
cluster roles, and the `edfi_dms_enqueue_owner` role a bootstrapped stack owns objects through is
exactly what they must not borrow. Start a throwaway one from the stack's own image instead, wait
for it to accept connections, and remove it afterwards:

```powershell
docker run --rm -d --name nr-pg-fixture -e POSTGRES_PASSWORD=fixture postgres:16.8-alpine
$deadline = (Get-Date).AddSeconds(60)
while ($(docker exec nr-pg-fixture pg_isready -q -h localhost -U postgres 2>$null; $LASTEXITCODE) -ne 0) {
    if ((Get-Date) -gt $deadline) { throw "nr-pg-fixture did not accept connections within 60 s" }
    Start-Sleep -Seconds 1
}
$env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER = "nr-pg-fixture"
Invoke-Pester -Path ./tests/SchemaSnapshotSecurity.Tests.ps1 -Output Detailed
docker rm -f nr-pg-fixture
```

[`tests/NorthridgeFailClosedGuards.Tests.ps1`](./tests/NorthridgeFailClosedGuards.Tests.ps1) holds
the four scripts and the recipe to the guards that stop a run from passing on nothing: two database
names that differ only in case cannot collapse to one snapshot, one count file cannot be reconciled
against itself, a `dms` base table on none of the copy tool's lists stops the copy before it loads, a
target cannot be its own source or reference, a measured checkpoint value cannot lack an
expectation, a deferred read is recorded with its final status, a date-time field is compared rather
than thrown on, a count row that does not parse stops the count instead of being skipped, two
projects' resources of one name stay two resources, the descriptor load re-points only the COPY header
and only inside the container, the recipe reads the service ports from the containers and bounds
every wait, a failed restore is recovered from without touching the reference deployment, every
secret and token the recipe sends travels as one process's environment rather than as an argument,
and the DMS-to-CMS client is deleted and recreated in the one database the Configuration Service
reads. It needs no database and runs in the same pull-request lane.

## Why the client tools run in containers

The published restore recipe has to work for a consumer who has Docker and a checkout and nothing
else. Assuming a host PostgreSQL client installation is the most common reason a documented recipe
fails for its reader, so the scripts and the recipe below use the same containerized invocation.

## Artifact identities

| Item | Value |
| --- | --- |
| PostgreSQL artifact | `EdFi_DMS_Northridge_v80_20260819_PG.7z`, 869,019,055 bytes, sha256 `49129363581eab342146e8dd9a4da95dd6f7b035f0c39ee39c9691176cd856a0` |
| — published at | <https://odsassets.blob.core.windows.net/public/Northridge/EdFi_DMS_Northridge_v80_20260819_PG.7z> |
| — as served | `Content-Length: 869019055`, `Content-Type: application/x-7z-compressed`, access tier `Cool` |
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

Two different things are on record about this recipe, and they must not be read as one. The recipe
text **as it stood at publication** -- eleven steps, branch head `b303450ab5fd689526696cb088a76a90b7ef6c14`,
recorded unchanged in `a0eecb58a5e4051730f82ec4c12288a443307250`: no step 5b, no step 5c, and DMS started
as its step 10 -- was executed from a clean slate on 2026-08-19, on a stack deliberately configured with
a non-default PostgreSQL password, a non-default `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` and a non-default
`DMS_CONFIG_IDENTITY_ENCRYPTION_KEY`, so that nothing could pass by sharing a default with the machine
that produced the artifact. The fourteen-step text below is that recipe plus the review hardening added
after publication -- steps 5b and 5c, the fail-closed status checks, the bounded waits, the in-memory
secret handling and the recovery helper -- and the current text has **not** been executed end to end
against the published artifact as one run. Its added gates are covered piecewise by the two suites
named above; until a run of the current text is performed and recorded with its date and commit (see
*Recipe execution record* at the end of this document), the clean-slate claim is a claim about the
publication-time text only.

The three producer-identity and data-model steps called out on DMS-1406 are mandatory rather than
conditional on your configuration differing from the producer's:

1. pinning the schema set to core only (step 3),
2. installing your own OpenIddict signing key (step 7),
3. rotating `dms.DataStoreIdentity.SourceIdentity` for a new writable restore (step 8).

The checklist below includes the other required restore gates, including the producer-local
Configuration Service rows that must be recreated before DMS can serve the restored database, and
step 5b, which is mandatory for every consumer: the restore flags that let the archive land on your
stack also drop the ownership and privilege metadata the DMS DDL sets on purpose, and step 5c is the
proof that it was put back.

> **Run this recipe from the DMS revision the artifact records** -- *DMS revision built*,
> `087eaa013df22a88d0046ac6f0e211bf47ec79e4` in the Record at the end of this document -- or from a
> checkout whose `src/` matches it. The hash step 3 expects and the DDL step 5c compares against both
> come from `src/` at that revision; step 3 checks this and stops otherwise. To bring the dataset onto a
> newer checkout, use `Copy-NorthridgeDataForward.ps1` instead of this recipe.

```shell
DC=~/src/Data-Management-Service/eng/docker-compose   # your checkout
ART=~/northridge-artifact                             # scratch dir, needs ~12 GB
ARTIFACT=EdFi_DMS_Northridge_v80_20260819_PG

# 1. Download and verify. A checksum mismatch means stop -- do not restore a partial download.
mkdir -p "$ART" && cd "$ART"
curl -O "https://odsassets.blob.core.windows.net/public/Northridge/${ARTIFACT}.7z"
#    The status is checked rather than printed. This recipe carries no `set -e`, so on its own
#    sha256sum reports FAILED and a pasted run carries straight on into the extraction and the
#    restore with a partial, wrong, or left-over-from-last-time archive.
echo "49129363581eab342146e8dd9a4da95dd6f7b035f0c39ee39c9691176cd856a0  ${ARTIFACT}.7z" | sha256sum -c - || \
  { echo "archive checksum failed -- delete ${ARTIFACT}.7z and download it again; do NOT continue"; exit 1; }

# 2. Extract, in a container so no host 7-Zip is needed. The inner dump is 888,167,269 bytes.
docker run --rm -v "$PWD:/w" alpine sh -c \
  "apk add --no-cache p7zip >/dev/null 2>&1 && cd /w && 7z x -y ${ARTIFACT}.7z >/dev/null"
#    Checked for the same reason as the archive, and because this is the file the restore
#    actually reads: a failed extraction leaves whatever dump was already in $ART, and a stale
#    one satisfies every later step while restoring the wrong dataset.
echo "08c03fe279e7ab10516f3e29c8009dca76b154fe67b5b0aaaad31409035d4167  ${ARTIFACT}.dump" | sha256sum -c - || \
  { echo "extracted dump checksum failed -- delete ${ARTIFACT}.dump and extract it again; do NOT continue"; exit 1; }
DUMP="$ART/${ARTIFACT}.dump"

# 3. REQUIRED: stage the schema set as CORE ONLY, before bootstrap.
#    The artifact is provisioned for EdFi.DataStandard52.ApiSchema 1.0.333, core only, effective
#    schema hash 816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b. The repository
#    default for Data Standard 5.2 stages core PLUS TPDM, which computes a different hash and makes
#    DMS answer 503 for every request.
#
cd "$DC"
#    The recipe is for the DMS revision the artifact records: the hash below and the DDL step 5c
#    compares against both come from src/ at that revision. Compared by content, commit to commit, so
#    an equivalent commit passes; uncommitted src/ edits are not seen, and the commit has to be in the
#    clone. A path that git cannot find compares as unchanged, hence the directory test in front.
ARTIFACT_DMS_REV=087eaa013df22a88d0046ac6f0e211bf47ec79e4   # DMS revision built, from the Record
git cat-file -e "${ARTIFACT_DMS_REV}^{commit}" 2>/dev/null || \
  { echo "commit ${ARTIFACT_DMS_REV} is not in this clone -- fetch the full history (git fetch --unshallow) and retry; do NOT continue"; exit 1; }
test -d ../../src && git diff --quiet "$ARTIFACT_DMS_REV" HEAD -- ../../src || \
  { echo "this checkout's src/ differs from the DMS revision the artifact records -- for this recipe check out $ARTIFACT_DMS_REV, or a checkout whose src/ matches it; to bring the dataset onto a newer checkout run Copy-NorthridgeDataForward.ps1 instead of this recipe; do NOT continue"; exit 1; }
APISCHEMA_VER=1.0.333       # the version THIS artifact was provisioned from

#    Materialize the core package. The repo pins it centrally and references it with
#    GeneratePathProperty, so restoring the project that consumes it puts the package in the NuGet
#    global-packages folder at a path you can compute.
dotnet restore ../../src/dms/core/EdFi.DataManagementService.Core.Tests.Unit
PKGROOT=$(dotnet nuget locals global-packages --list | sed 's/^[^:]*: *//' | tr -d '\r')
CORE_DIR="${PKGROOT}/edfi.datastandard52.apischema/${APISCHEMA_VER}/contentFiles/any/any/ApiSchema"
test -f "$CORE_DIR/ApiSchema.json" || { echo "core ApiSchema ${APISCHEMA_VER} is not in the package cache -- check out the
  DMS commit this artifact was produced from, whose src/Directory.Packages.props pins that version"; exit 1; }
test -f "$CORE_DIR/discovery-spec.json" || { echo "core ApiSchema ${APISCHEMA_VER} is missing discovery-spec.json"; exit 1; }
test -d "$CORE_DIR/xsd" || { echo "core ApiSchema ${APISCHEMA_VER} is missing xsd/"; exit 1; }

#    Copy the whole core package ApiSchema asset directory, ALONE as the only project in the staging
#    directory. ApiSchema.json determines the effective schema hash; discovery-spec.json and xsd/ are
#    the metadata assets DMS serves from the same manifest-backed workspace.
mkdir -p "$ART/apischema-core-only"
cp -R "$CORE_DIR/." "$ART/apischema-core-only/"

dotnet publish ../../src/dms/clis/EdFi.DataManagementService.SchemaTools \
  -c Release -p:UseAppHost=true -o .bootstrap/tools/api-schema-tools
#    No DMS_SCHEMA_TOOL_PATH is needed: `.bootstrap/tools/api-schema-tools` is the documented publish
#    location and is probed first, with the platform executable suffix handled for you.
pwsh -NoProfile -File ./prepare-dms-schema.ps1 -ApiSchemaPath "$ART/apischema-core-only"
#    Expect: "Effective schema hash: 816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b"
#    If that hash differs, STOP: the restored database will answer 503 and no later step will fix it.
#
#    -ApiSchemaPath (expert mode) is required here, and the tempting shortcuts do not work:
#      * Running prepare-dms-schema.ps1 with no arguments does stage catalog-pinned core-only, and
#        it prints the right hash -- but step 4 then refuses to start, because the wrapper compares
#        the staged package identity against the effective env's SCHEMA_PACKAGES and reports
#        "staged packages [core] vs effective packages [core, TPDM]". Expert workspaces are exempt
#        from that comparison and are reused as-is, which is why this recipe uses one.
#      * Editing SCHEMA_PACKAGES in your own env file does not survive, because the DS 5.2 overlay
#        is composed on top of it; setting it in your shell does nothing at all, because it is read
#        from the environment FILE rather than the ambient environment.
#
#    Staging must run against a clean workspace: prepare-dms-schema.ps1 refuses to overwrite one
#    staged from different inputs, failing with "workspace fingerprint mismatch". If you have staged
#    before, clear it first:  pwsh -NoProfile -File ./bootstrap-local-dms.ps1 -d -v

# 4. Bootstrap a normal PostgreSQL stack. The staged core-only workspace is reused as-is.
#    Guarded like everything else here: with no `set -e`, a bootstrap that failed part-way would be
#    followed by step 5 setting the incomplete deployment aside as the reference step 5c compares to.
pwsh -NoProfile -File ./bootstrap-local-dms.ps1 -DatabaseEngine postgresql -IdentityProvider self-contained || \
  { echo "bootstrap failed -- the reference deployment may be incomplete; fix the cause and re-run step 4; do NOT run step 5"; exit 1; }

# 5. Stop the applications, set the deployment aside, then restore. Nothing may hold a connection
#    during the rename or the restore.
#    Read the live values rather than assuming defaults. The superuser name is one of them:
#    postgresql.yml sets POSTGRES_USER: ${POSTGRES_USER:-postgres}, so the container accepts an
#    override, and this recipe reads whatever the running container was given rather than adding a
#    hard-coded copy of its own -- here, and in the connection string built in step 9, where a
#    wrong name registers a data store that saves cleanly and cannot connect. This is not a claim
#    that the stack supports another superuser end to end: .env.example and the compose defaults
#    still build the DMS and CMS connection strings with `username=postgres`, so the bootstrap in
#    step 4 works only with the default today. Reading the value keeps this recipe correct for the
#    stack it actually finds, and keeps it from becoming one more place that has to change.
DB=$(docker exec dms-postgresql printenv POSTGRES_DB_NAME)
DBUSER=$(docker exec dms-postgresql printenv POSTGRES_USER)
test -n "$DB" -a -n "$DBUSER" || \
  { echo "could not read the database name and superuser from the dms-postgresql container"; exit 1; }
docker stop ed-fi-api ed-fi-api-config-service

#    Keep the database step 4 deployed, under another name, rather than dropping it. Step 5c compares
#    the restored artifact against it: the bootstrap deployed the current DDL as $DBUSER on this
#    cluster, so it is the reference a restore has to match -- structure, trigger state, ownership,
#    privileges and routine security attributes -- and a rename keeps every one of those attributes
#    exactly as the deployment left them, which a dump and restore of it would not. The compare is
#    meaningful only when the checkout is at the DMS revision the artifact records (see the Record
#    below): a newer DDL differs on purpose, and the answer to that is the copy-forward, not a
#    restore.
#
#    No database name is pasted into SQL text: a supported POSTGRES_DB_NAME is not required to be a
#    well-behaved SQL identifier. dropdb and createdb take the name as a command argument, with `--`
#    protecting one that begins with a dash -- the pattern the repository's own
#    eng/docker-compose/postgresql-init.sh uses to create this database, for the same reason. There
#    is no client tool for ALTER DATABASE, so the rename is generated server-side: :'db' and :'ref'
#    are psql variables read as string literals, format('%I') quotes each as an identifier, and
#    \gexec runs the statement it produced. A reference left by an earlier attempt is refused, not
#    dropped: if $REF exists, that attempt set the deployment aside and did not finish, and $REF IS
#    the intact deployment -- see the recovery helper below.
REF="${DB}_reference"
#    PostgreSQL truncates an identifier longer than 63 bytes with a NOTICE, not an error, so a long
#    POSTGRES_DB_NAME would have the deployment renamed to a name that no later lookup of $REF finds --
#    the recovery would then report that the deployment was never set aside. Refused here, before the
#    rename, by byte length: ${#REF} counts characters, and printf rather than echo so a name is never
#    interpreted as options or escapes.
test "$(printf '%s' "$REF" | wc -c | tr -d ' ')" -le 63 || \
  { echo "$REF is longer than PostgreSQL's 63-byte identifier limit, so the deployment cannot be set aside under that name; nothing was changed"; exit 1; }

#    Recovery. A failure anywhere from createdb below through step 6 leaves $DB holding an empty,
#    partial or unproven restore and the deployment intact as $REF. Do NOT go back to step 4: the
#    bootstrap does not reset volumes, so that $DB survives it, and a second pass through this step
#    would set it aside as $REF -- over the intact deployment, which is the reference step 5c compares
#    against and cannot be rebuilt short of a wipe. Every failure guard from createdb to the end of
#    step 6 therefore runs RECOVER_FROM_REF itself before it stops -- all but one: the 5b apply, which
#    psql rolled back whole, is re-run in place, and its message says when to run the helper instead.
#    The helper refuses to touch anything unless $REF exists, drops the $DB the attempt left, renames
#    $REF back to $DB and reports what it did -- so by the time the shell stops, the cluster is as
#    step 4 left it and the next attempt resumes at step 5. The evidence of the failure outlives the
#    recovery: restore.log, the 5c diff file and the printed step 6 mismatches are all outside the
#    database that is dropped.
#    The helper reads the database name and superuser from the container rather than from this
#    shell, so the same text works pasted alone into a fresh shell -- which is what "Recovery after a
#    failed restore", after the recipe, is for: the helper itself reported a failure (a connection
#    still held), or the run was interrupted before a guard could call it. $REF is dropped only once
#    step 6 has passed, because step 6 is the last gate that needs it. The helper returns rather than
#    exits, so it never ends the shell it is pasted into.
RECOVER_FROM_REF() { # puts the deployment back under its own name after a failed restore; reads its inputs from the container
  _db=$(docker exec dms-postgresql printenv POSTGRES_DB_NAME)
  _dbuser=$(docker exec dms-postgresql printenv POSTGRES_USER)
  _ref="${_db}_reference"
  test -n "$_db" -a -n "$_dbuser" || \
    { echo "recovery: could not read the database name and superuser from dms-postgresql; nothing was changed"; return 1; }
  _ref_exists=$(docker exec -i dms-postgresql psql -U "$_dbuser" -d postgres -v ON_ERROR_STOP=1 -tA -v ref="$_ref" -f - <<'SQL'
SELECT 1 FROM pg_database WHERE datname = :'ref';
SQL
  ) || { echo "recovery: could not ask the cluster whether $_ref exists; nothing was changed"; return 1; }
  test -n "$_ref_exists" || \
    { echo "recovery: there is no $_ref, so the deployment was never set aside and $_db is still the deployment; nothing was changed"; return 1; }
  docker exec dms-postgresql dropdb -U "$_dbuser" --maintenance-db=postgres --if-exists -- "$_db" || \
    { echo "recovery: could not drop $_db -- something still holds a connection to it; the deployment is intact as $_ref; close that connection and run the recovery again (see Recovery after a failed restore)"; return 1; }
  docker exec -i dms-postgresql psql -U "$_dbuser" -d postgres -v ON_ERROR_STOP=1 -q \
    -v ref="$_ref" -v db="$_db" -f - <<'SQL' || \
    { echo "recovery: could not rename $_ref back to $_db; the deployment is intact as $_ref -- run the recovery again (see Recovery after a failed restore)"; return 1; }
SELECT format('ALTER DATABASE %I RENAME TO %I', :'ref', :'db') \gexec
SQL
  echo "recovery: the deployment is back as $_db; fix the cause, then resume at step 5"
}

#    An existing $REF is the deployment an earlier attempt set aside and did not put back. It is
#    refused, never dropped: use the paste-alone "Recovery after a failed restore" block below if you
#    mean to redo the restore, then resume at step 5.
_ref_exists=$(docker exec -i dms-postgresql psql -U "$DBUSER" -d postgres -v ON_ERROR_STOP=1 -tA -v ref="$REF" -f - <<'SQL'
SELECT 1 FROM pg_database WHERE datname = :'ref';
SQL
) || { echo "could not ask the cluster whether $REF exists; nothing was changed"; exit 1; }
test -z "$_ref_exists" || \
  { echo "$REF already exists: an earlier attempt set the deployment aside and did not put it back. Use the \"Recovery after a failed restore\" block below if you mean to redo the restore, then resume at step 5. Do NOT drop $REF -- it is the intact deployment"; exit 1; }
docker exec -i dms-postgresql psql -U "$DBUSER" -d postgres -v ON_ERROR_STOP=1 -q \
  -v db="$DB" -v ref="$REF" -f - <<'SQL' || \
  { echo "could not rename $DB to $REF -- something still holds a connection to it; nothing was restored"; exit 1; }
SELECT format('ALTER DATABASE %I RENAME TO %I', :'db', :'ref') \gexec
SQL
docker exec dms-postgresql createdb -U "$DBUSER" --maintenance-db=postgres -- "$DB" || \
  { echo "could not create database $DB; putting the deployment back under its own name"; RECOVER_FROM_REF; exit 1; }

docker cp "$DUMP" dms-postgresql:/tmp/nr.dump || \
  { echo "could not copy the dump into the container; nothing was restored, but $DB already exists empty -- putting the deployment back under its own name"; RECOVER_FROM_REF; exit 1; }

#    --exit-on-error stops at the first failed archive entry. Without it pg_restore skips the entry,
#    carries on to the end of the archive, and summarises what it swallowed as
#    "errors ignored on restore: N" -- leaving a database that is missing tables you never saw named.
docker exec dms-postgresql pg_restore -U "$DBUSER" -d "$DB" --no-owner --no-privileges \
  --exit-on-error /tmp/nr.dump > "$ART/restore.log" 2>&1
RC=$?
docker exec -u 0 dms-postgresql rm -f /tmp/nr.dump

#    Both signals are checked, because neither is sufficient alone: a non-zero status means the
#    database holds a partial restore, and an "errors ignored" line names entries that were skipped.
if [ "$RC" -ne 0 ]; then
  tail -20 "$ART/restore.log"
  echo "pg_restore exited $RC. $DB holds a PARTIAL restore -- putting the deployment back under its own name; the log is $ART/restore.log"
  RECOVER_FROM_REF; exit 1
fi
if grep -Eiq 'errors ignored on restore|pg_restore: error' "$ART/restore.log"; then
  grep -Ei 'errors ignored on restore|pg_restore: error' "$ART/restore.log"
  echo "pg_restore reported errors, so entries were skipped -- putting the deployment back under its own name; the log is $ART/restore.log"
  RECOVER_FROM_REF; exit 1
fi
echo "pg_restore finished with no reported errors"

# 5b. REQUIRED: repair the PostgreSQL security metadata that --no-owner --no-privileges dropped.
#     Those two flags are what let the archive land on YOUR stack: without --no-owner pg_restore
#     issues ALTER ... OWNER TO the producer's superuser for every object, and without
#     --no-privileges it replays the producer cluster's GRANT and REVOKE statements -- both name
#     roles your cluster need not have, and under --exit-on-error either stops the restore. Every
#     table, sequence, view and routine therefore comes back owned by $DBUSER with default
#     privileges -- which is exactly what a fresh deployment by $DBUSER produces, for every object
#     but the ones the DDL locks down on purpose. The document-projection enqueue functions are
#     SECURITY DEFINER, owned by the dedicated edfi_dms_enqueue_owner role, with EXECUTE revoked
#     from PUBLIC and from the deploying user; that role holds USAGE (not CREATE) on the dms schema,
#     SELECT on dms."DocumentCacheState" and SELECT, INSERT, UPDATE on dms."DocumentProjectionWork".
#     After a bare restore the same functions run as SECURITY DEFINER *as the superuser*, and every
#     role in the cluster may execute them. That is a privilege boundary lost, and it is invisible
#     to everything but the schema compare in 5c, which reads exactly these attributes.
#
#     The statements are Phase 9 ("Security and Grants") of the DMS PostgreSQL DDL, as emitted by
#     CoreDdlEmitter.EmitPgsqlDocumentProjectionEnqueueSecurity, in the same order and run the same
#     way -- the REVOKEs execute as the owner role, so the recorded grantor matches a fresh
#     deployment byte for byte. The role is cluster-level, so the rename and the new database above
#     leave it in place: the bootstrap in step 4 created it, and the guard refuses to run if it is
#     missing rather than create a differently shaped one. tests/SchemaSnapshotSecurity.Tests.ps1
#     checks this block statement by statement against the emitter's authoritative fixture on every
#     pull request, and runs dump -> bare restore -> this block -> compare live, so a change to the
#     DDL's security block fails there before it can fail here. Written to a file so the statement
#     that applies it runs the block from disk as one transaction, and a re-run after a fix applies
#     the same text. Guarded like every other write here: a file that could not be written must not
#     leave a stale one from an earlier attempt to be applied in its place -- and, like the guards
#     around it, the failure puts the deployment back under its own name before it stops.
cat > "$ART/repair.sql" <<'REPAIR_SQL' || { echo "could not write $ART/repair.sql, so step 5b did not run -- putting the deployment back under its own name"; RECOVER_FROM_REF; exit 1; }
DO $$
BEGIN
    IF pg_catalog.to_regrole('edfi_dms_enqueue_owner') IS NULL THEN
        RAISE EXCEPTION 'role edfi_dms_enqueue_owner does not exist in this cluster, so step 4 never deployed to it -- tear the stack down with volumes (./bootstrap-local-dms.ps1 -d -v) and start over from step 3; do not re-run step 4 over a database that holds a restore';
    END IF;
END $$;
GRANT CREATE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner";
ALTER FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() OWNER TO "edfi_dms_enqueue_owner";
ALTER FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() OWNER TO "edfi_dms_enqueue_owner";
REVOKE CREATE ON SCHEMA "dms" FROM "edfi_dms_enqueue_owner";
GRANT USAGE ON SCHEMA "dms" TO "edfi_dms_enqueue_owner";
SET ROLE "edfi_dms_enqueue_owner";
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() FROM SESSION_USER;
REVOKE EXECUTE ON FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() FROM SESSION_USER;
RESET ROLE;
REVOKE INSERT, UPDATE, DELETE ON TABLE "dms"."DocumentProjectionWork" FROM PUBLIC;
GRANT SELECT ON TABLE "dms"."DocumentCacheState" TO "edfi_dms_enqueue_owner";
GRANT SELECT, INSERT, UPDATE ON TABLE "dms"."DocumentProjectionWork" TO "edfi_dms_enqueue_owner";
REPAIR_SQL
#     -1 runs the file as one transaction and ON_ERROR_STOP aborts it at the first error, so a
#     failure applies nothing and a re-run after a fix starts from the same state -- which is why a
#     failure here is re-run in place, and only a cause that cannot be fixed sends you to the recovery.
docker exec -i dms-postgresql psql -U "$DBUSER" -d "$DB" -v ON_ERROR_STOP=1 -q -1 -f - < "$ART/repair.sql" || \
  { echo "step 5b failed: the security metadata was not repaired, so this database is NOT the deployed schema -- fix the cause and re-run step 5b; if it cannot be fixed, run RECOVER_FROM_REF (here, or the Recovery block after the recipe from a fresh shell) and resume at step 5"; exit 1; }
echo "security metadata repaired"

# 5c. Prove it. A restore of this artifact must be indistinguishable from the same-revision deployment
#     it stands in for, ownership and privileges included. The reference is the database step 4
#     deployed, kept as $REF by the rename in step 5 -- the actual fresh deployment, not a dump of it
#     restored beside the artifact -- and the compare runs the script's two-database mode against
#     both live databases. A restored copy would have made a weaker reference: put through the same
#     flags it needs the same repair, so a repair block incomplete in the same way on both sides
#     would have passed. Against the deployment itself, an artifact restore without 5b cannot pass:
#     the compare fails in sections 10, 14, 15 and 16 and names the objects. One thing is
#     normalized, and only one. pg_dump writes a CHECK constraint as pg_get_constraintdef renders
#     it, and PostgreSQL re-parses (ARRAY[...])::text[] as ARRAY[(...)::text, ...] -- the same
#     predicate, spelled differently -- so dms.CK_DocumentCacheState_Lifecycle reads as the second
#     form in the restored artifact and as the first in the deployment. Compare-DmsSchemaSnapshot.ps1
#     rewrites the second spelling to the first when, and only when, every element carries the same
#     cast; a different value list, element count, cast type or column still fails, and
#     tests/SchemaSnapshotSecurity.Tests.ps1 holds it to that with a negative control on every pull
#     request. Runs before the content checks in step 6: a database holding every published row on
#     a schema that is not the deployed one is still not a restore of this artifact.
#     The names travel as environment variables, never pasted into the PowerShell command text --
#     the same handoff step 9 uses, for the same reason. A failing compare writes
#     $ART/schema/schema-diff.<db>-vs-<db>_reference.txt and names the section of every difference.
if ! DB="$DB" REF="$REF" DBUSER="$DBUSER" ART="$ART" pwsh -NoProfile -Command \
     '& ../northridge/Compare-DmsSchemaSnapshot.ps1 -Database $env:DB, $env:REF -OutputDirectory "$env:ART/schema" -PostgresUser $env:DBUSER'; then
  echo "step 5c failed: the restored artifact is not the deployed schema -- see the diff named above; putting the deployment back under its own name"
  RECOVER_FROM_REF; exit 1
fi
echo "schema compare PASS: the restored artifact is the deployed schema, ownership and privileges included"
#     $REF stays until step 6 has passed: a content failure there is recovered from through
#     RECOVER_FROM_REF like every failure before it, and that needs the deployment to still exist.

# 6. Verify the restore by content as well as by status. A clean exit says the archive was applied;
#    it does not say the database holds the published dataset. This block compares 22 values across
#    nine dms tables, the three sequences the copied data draws from -- with the CollectionItemId
#    high-water mark taken over every collection table that holds the column -- the table inventory
#    of all four DMS-owned schemas and the referential closure, and raises on the first
#    disagreement, so it either passes or stops the recipe. A value that is missing rather than wrong
#    reads as NULL and is reported as a mismatch, not skipped, and the count of checks that ran is
#    itself asserted.
#
#    The table counts are what catch a restore that dropped a projection table: every other value
#    here can be right while one of the 467 edfi tables is simply absent.
#
#    Foreign keys are not re-checked here on purpose. This is a full restore: pg_restore adds the
#    foreign keys after the table data, and PostgreSQL validates the existing rows as each one is
#    created, so --exit-on-error above already refused a load that violated any of them. That is not
#    true of the producer-side copy-forward, which loads with --disable-triggers and therefore has to
#    validate every constraint itself.
#
#    Run this BEFORE step 8. The source identity assertion checks the value the artifact ships with,
#    and step 8 exists to replace it.
#    The status is checked explicitly. ON_ERROR_STOP=1 turns the RAISE below into a non-zero psql
#    exit (3), but this recipe carries no `set -e`, so nothing stops on its own: without the check a
#    failed content verification prints its ERROR and a pasted recipe carries straight on into step 7,
#    which is the one outcome this block exists to prevent.
if ! docker exec -i dms-postgresql psql -U "$DBUSER" -d "$DB" -v ON_ERROR_STOP=1 -q <<'SQL'
DO $$
DECLARE
    checked        int;
    mismatch       text;
    collection_sql text;
    collection_max bigint;
BEGIN
    -- Every projection collection table draws CollectionItemId from one sequence, so its high-water
    -- mark is the maximum over all of them. The table list is read from the catalog rather than
    -- written here, with the same predicate Copy-NorthridgeDataForward.ps1 uses, so the two cannot
    -- disagree on which tables count; format('%I') quotes each name as an identifier.
    SELECT string_agg(format('SELECT COALESCE(MAX(%I), 0) AS v FROM %I.%I', column_name, table_schema, table_name), ' UNION ALL ')
      INTO collection_sql
      FROM information_schema.columns
     WHERE column_name = 'CollectionItemId' AND table_schema IN ('edfi', 'tracked_changes_edfi');
    IF collection_sql IS NULL THEN
        RAISE EXCEPTION 'no CollectionItemId columns were found, so CollectionItemIdSequence cannot be checked; this is not the published schema';
    END IF;
    EXECUTE format('SELECT COALESCE(MAX(v), 0) FROM (%s) AS m', collection_sql) INTO collection_max;

    SELECT COUNT(*),
           string_agg(format('%s: expected %s, found %s', item, want, got), E'\n  ' ORDER BY item)
               FILTER (WHERE got IS DISTINCT FROM want)
      INTO checked, mismatch
      FROM (
        SELECT 'dms."Document" rows' AS item, '10576801' AS want,
               (SELECT COUNT(*)::text FROM dms."Document") AS got
        UNION ALL SELECT 'dms."ResourceKey" rows', '351',
               (SELECT COUNT(*)::text FROM dms."ResourceKey")
        UNION ALL SELECT 'dms."EffectiveSchema"."ResourceKeyCount"', '351',
               (SELECT "ResourceKeyCount"::text FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1)
        UNION ALL SELECT 'dms."EffectiveSchema"."EffectiveSchemaHash"',
               '816fe17af8c06994204f5c73903a616a16ca43a4be98a3716c07ae7b7b58587b',
               (SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1)
        UNION ALL SELECT 'dms."DataStoreIdentity"."SourceIdentity" as shipped',
               '8b962de6-b979-49aa-bce0-ca59e0a1ad51',
               (SELECT "SourceIdentity"::text FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1)
        UNION ALL SELECT 'dms."DocumentProjectionWork" rows', '0',
               (SELECT COUNT(*)::text FROM dms."DocumentProjectionWork")
        UNION ALL SELECT 'dms."DocumentCache" rows', '0',
               (SELECT COUNT(*)::text FROM dms."DocumentCache")
        -- The projection lifecycle singleton, at the values provisioning seeds and the copy tool
        -- asserts at every checkpoint: exactly one row, Disabled, with no cache-ahead recovery
        -- pending. Empty queues above say nothing about it, and a restore carrying a producer
        -- mid-rebuild, or a hand-edited state, would pass every row count and start DMS into a
        -- projection it must not run.
        UNION ALL SELECT 'dms."DocumentCacheState" rows', '1',
               (SELECT COUNT(*)::text FROM dms."DocumentCacheState")
        UNION ALL SELECT 'dms."DocumentCacheState"."ProjectionLifecycleState"', 'Disabled',
               (SELECT "ProjectionLifecycleState" FROM dms."DocumentCacheState" WHERE "StateId" = 1)
        UNION ALL SELECT 'dms."DocumentCacheState"."CacheAheadRecoveryRequired"', 'false',
               (SELECT "CacheAheadRecoveryRequired"::text FROM dms."DocumentCacheState" WHERE "StateId" = 1)
        UNION ALL SELECT 'dms."Descriptor" rows', '2968',
               (SELECT COUNT(*)::text FROM dms."Descriptor")
        UNION ALL SELECT 'dms base tables', '10',
               (SELECT COUNT(*)::text FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE' AND table_schema = 'dms')
        UNION ALL SELECT 'edfi base tables', '467',
               (SELECT COUNT(*)::text FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE' AND table_schema = 'edfi')
        UNION ALL SELECT 'tracked_changes_edfi base tables', '139',
               (SELECT COUNT(*)::text FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE' AND table_schema = 'tracked_changes_edfi')
        UNION ALL SELECT 'auth base tables', '1',
               (SELECT COUNT(*)::text FROM information_schema.tables
                 WHERE table_type = 'BASE TABLE' AND table_schema = 'auth')
        UNION ALL SELECT 'documents with no resource key', '0',
               (SELECT COUNT(*)::text FROM dms."Document" d
                 WHERE NOT EXISTS (SELECT 1 FROM dms."ResourceKey" k WHERE k."ResourceKeyId" = d."ResourceKeyId"))
        UNION ALL SELECT 'descriptors with no document', '0',
               (SELECT COUNT(*)::text FROM dms."Descriptor" x
                 WHERE NOT EXISTS (SELECT 1 FROM dms."Document" d WHERE d."DocumentId" = x."DocumentId"))
        UNION ALL SELECT 'descriptors whose ResourceKeyId disagrees with their document', '0',
               (SELECT COUNT(*)::text FROM dms."Descriptor" x
                  JOIN dms."Document" d ON d."DocumentId" = x."DocumentId"
                 WHERE x."ResourceKeyId" <> d."ResourceKeyId")
        UNION ALL SELECT 'referential identities with no document', '0',
               (SELECT COUNT(*)::text FROM dms."ReferentialIdentity" r
                 WHERE NOT EXISTS (SELECT 1 FROM dms."Document" d WHERE d."DocumentId" = r."DocumentId"))
        -- The value the next write would receive, not the recorded position: nextval() returns
        -- last_value + increment once is_called is true and last_value itself while it is false, so a
        -- sequence sitting exactly on the maximum with is_called false reports a position that looks
        -- high enough and still hands the first writer a value the data already holds. is_called is on
        -- the sequence relation and nowhere else; the increment is in pg_sequence. Nothing here calls
        -- nextval(), which would move the sequence being checked.
        --
        -- The maximum spans dms."Descriptor" as well as dms."Document", matching the copy script.
        -- The descriptor stamping trigger draws from this same sequence, so a descriptor
        -- ContentVersion above every document version is on its own enough to make the next value
        -- collide, and a Document-only maximum would report exactly that case as safe.
        UNION ALL SELECT 'ChangeVersionSequence next value beyond the restored data', 'true',
               (SELECT ((s.last_value + CASE WHEN s.is_called THEN q.seqincrement ELSE 0 END)
                        > GREATEST(
                              COALESCE((SELECT MAX("ContentVersion") FROM dms."Document"), 0),
                              COALESCE((SELECT MAX("ContentVersion") FROM dms."Descriptor"), 0)
                          ))::text
                  FROM dms."ChangeVersionSequence" s, pg_sequence q
                 WHERE q.seqrelid = 'dms."ChangeVersionSequence"'::regclass)
        -- The other two sequences the copy tool restores from the archive and asserts, read the same
        -- way: the identity sequence behind dms."Document"."DocumentId", and the sequence every
        -- collection table's CollectionItemId draws from, measured against the maximum gathered
        -- above. A sequence left at its fresh-database position is invisible to every row count in
        -- this block and surfaces as a primary-key collision on the first write.
        UNION ALL SELECT 'Document_DocumentId_seq next value beyond the restored data', 'true',
               (SELECT ((s.last_value + CASE WHEN s.is_called THEN q.seqincrement ELSE 0 END)
                        > COALESCE((SELECT MAX("DocumentId") FROM dms."Document"), 0))::text
                  FROM dms."Document_DocumentId_seq" s, pg_sequence q
                 WHERE q.seqrelid = 'dms."Document_DocumentId_seq"'::regclass)
        UNION ALL SELECT 'CollectionItemIdSequence next value beyond the restored data', 'true',
               (SELECT ((s.last_value + CASE WHEN s.is_called THEN q.seqincrement ELSE 0 END)
                        > collection_max)::text
                  FROM dms."CollectionItemIdSequence" s, pg_sequence q
                 WHERE q.seqrelid = 'dms."CollectionItemIdSequence"'::regclass)
      ) t;

    IF checked <> 22 THEN
        RAISE EXCEPTION 'only % of 22 checks ran, so this proves nothing; the block itself is broken', checked;
    END IF;

    IF mismatch IS NOT NULL THEN
        RAISE EXCEPTION E'the restored database does not match the published artifact:\n  %', mismatch;
    END IF;

    RAISE NOTICE 'restore verified: all 22 published values and invariants match';
END $$;
SQL
then
  echo "step 6 failed: the restored database is not the published dataset -- putting the deployment back under its own name"
  RECOVER_FROM_REF; exit 1
fi
#    Expect: NOTICE: restore verified: all 22 published values and invariants match

#    Step 6 was the last gate whose failure recovers through RECOVER_FROM_REF, so the reference is
#    scratch from here on and is dropped now rather than in 5c, where a step 6 failure would have found
#    it gone.
#    Nothing after this point is recovered by re-restoring; steps 7 to 12 each say how they are re-run.
docker exec dms-postgresql dropdb -U "$DBUSER" --maintenance-db=postgres -- "$REF" || \
  { echo "could not drop the reference database $REF; every restore gate passed, so drop it by hand before continuing"; exit 1; }
echo "reference database $REF dropped"

# 7. REQUIRED: install your own OpenIddict signing key.
#    The artifact carries the producer's dmscs."OpenIddictKey" row, whose private key is encrypted
#    with the PRODUCER's DMS_CONFIG_IDENTITY_ENCRYPTION_KEY. Yours differs, so CMS cannot decrypt it
#    and POST /connect/token answers 500 with "No active private key or key id found". Without a
#    token no CMS API call succeeds, so step 9 becomes impossible -- this step must precede it.
#
#    The key is replaced in the CMS database: CMS mints tokens from the dmscs."OpenIddictKey" row of
#    the database it reads its dmscs rows from, taken from the running Configuration Service rather
#    than assumed to be $DB -- DMS_CONFIG_DATABASE_NAME is a supported override, and local-config.yml
#    builds the CMS connection string from it -- so a key replaced anywhere else leaves CMS reading
#    the producer's. The rule for the rest of the recipe: dmscs operations target $CMSDB (step 10
#    reuses this value), dms data-store operations target $DB. The connection string carries the
#    database password, so it travels to pwsh as CMSCS in that one process's environment, the channel
#    step 9 uses, and DbConnectionStringBuilder reads the database keyword back by the rules Npgsql
#    parses rather than by a pattern over the text. ENVOF is `docker inspect`, which reads the
#    stopped container.
ENVOF() { docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$1"; }
CMSCS=$(ENVOF ed-fi-api-config-service | sed -n 's/^DatabaseSettings__DatabaseConnection=//p')
CMSDB=$(CMSCS="$CMSCS" pwsh -NoProfile -Command '
  $csb = [System.Data.Common.DbConnectionStringBuilder]::new()
  $csb.PSBase.ConnectionString = $env:CMSCS
  [Console]::Out.Write([string]$csb["database"])
')
unset CMSCS
test -n "$CMSDB" || \
  { echo "could not read the database name from the Configuration Service connection string; nothing was changed"; exit 1; }

#    Note this is a DIFFERENT key from DMS_CONFIG_DATABASE_ENCRYPTION_KEY, which protects the data
#    store connection string in step 9. The two are configured separately and both matter here.
#    Read the key with `docker inspect`, not `docker exec`: the CMS container is stopped at this
#    point in the recipe and `docker exec` refuses a stopped container.
IDK=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' ed-fi-api-config-service |
      sed -n 's/^IdentitySettings__EncryptionKey=//p')
test -n "$IDK" || { echo "could not read the CMS identity encryption key"; exit 1; }

#    Deactivating the producer's key and inserting yours are one operation or none. Run as two
#    statements, a failed insert leaves NO active key, and a failed deactivate leaves the producer's
#    key active and trusted beside yours -- and neither shows until step 9 asks for a token. So the
#    generator's INSERT is assembled between the deactivate and an assertion into one stream, and
#    that stream runs under -1 (one transaction) and ON_ERROR_STOP: a failure anywhere, the
#    assertion included, rolls the whole thing back and the recipe stops here. The token check in
#    step 9 stays as a second proof, not the only one. Every command is guarded because this recipe
#    carries no `set -e`.
#    The key material never touches disk or an argument list. The identity encryption key reaches the
#    generator as IDK in that one pwsh process's environment -- the channel the client secrets use in
#    step 9 -- not as an argument, which every process on the host could read for as long as the
#    generator ran. The generated INSERT carries the private key and the encryption key in clear, so
#    it is held in this shell's memory as KEY_SQL and fed to psql through a pipe, and both variables
#    are unset on every path out of this step. Nothing is written under $ART, so there is nothing to
#    remove and nothing left behind under the scratch directory's permissions.
KEY_SQL=$(IDK="$IDK" pwsh -NoProfile -Command '& ./Generate-OpenIddictKey-Insert.ps1 -EncryptionKey $env:IDK') || \
  { unset KEY_SQL IDK; echo "could not generate the OpenIddict key; nothing was changed"; exit 1; }
printf '%s\n' "$KEY_SQL" | grep -q '^INSERT INTO "dmscs"."OpenIddictKey" ' || \
  { unset KEY_SQL IDK; echo "the generator wrote no INSERT for dmscs.OpenIddictKey; nothing was changed"; exit 1; }
{
  echo 'UPDATE dmscs."OpenIddictKey" SET "IsActive" = FALSE;'
  printf '%s\n' "$KEY_SQL"
  cat <<'KEY_ASSERT_SQL'
DO $$
DECLARE
    active int;
BEGIN
    SELECT COUNT(*) INTO active FROM dmscs."OpenIddictKey" WHERE "IsActive";
    IF active <> 1 THEN
        RAISE EXCEPTION 'expected exactly one active dmscs."OpenIddictKey" row after the replacement, found %', active;
    END IF;
END $$;
KEY_ASSERT_SQL
} | docker exec -i dms-postgresql psql -U "$DBUSER" -d "$CMSDB" -v ON_ERROR_STOP=1 -q -1 -f - || \
  { unset KEY_SQL IDK; echo "step 7 failed: the key replacement was rolled back, so the producer's key is still the active one -- fix the cause and re-run step 7"; exit 1; }
unset KEY_SQL IDK
echo "OpenIddict signing key replaced: exactly one active key, and it is yours"

# 8. REQUIRED: rotate dms.DataStoreIdentity.SourceIdentity.
#    Restoring this artifact creates an independent writable data store from a copied backup, and the
#    data-model contract assigns a new source identity in exactly that case, before the data store
#    becomes available. Rotation is never part of DDL rerun or DMS startup, so it must happen here.
#
#    If you are REPLACING an existing CDC-enabled source rather than standing up a new one, do NOT
#    use this UPDATE. Rotate through the CDC recovery workflow instead, which also requires a new
#    binding generation, topics and consumer state namespace.
SHIPPED_SOURCE_ID=8b962de6-b979-49aa-bce0-ca59e0a1ad51
NEW_SOURCE_ID=$(docker exec dms-postgresql psql -U "$DBUSER" -d "$DB" -v ON_ERROR_STOP=1 -tAc \
  'UPDATE dms."DataStoreIdentity" SET "SourceIdentity" = gen_random_uuid()
   WHERE "DataStoreIdentitySingletonId" = 1
   RETURNING "SourceIdentity";')
ROTATE_STATUS=$?
if [ "$ROTATE_STATUS" -ne 0 ] || [ -z "$NEW_SOURCE_ID" ] || \
   [ "$NEW_SOURCE_ID" = "$SHIPPED_SOURCE_ID" ] || \
   [ "$NEW_SOURCE_ID" = "00000000-0000-0000-0000-000000000000" ]; then
  echo "SourceIdentity rotation failed; got '$NEW_SOURCE_ID'"
  exit 1
fi
echo "SourceIdentity rotated to $NEW_SOURCE_ID"

# 9. Start the Configuration Service ONLY, then re-save the data store.
#    The local PostgreSQL stack is single-database, so the artifact carries dmscs.* alongside dms.*.
#    The restored dmscs rows are PRODUCER-LOCAL: the stored DataStore.ConnectionString describes the
#    machine that produced the artifact and is encrypted with that machine's
#    DMS_CONFIG_DATABASE_ENCRYPTION_KEY. Re-saving re-encrypts it with yours. The restore also
#    replaced dmscs.OpenIddict*, so register the admin client AFTER the restore, not before.
#    Skip this and DMS restart-loops with "Failed to decrypt the connection string".
#
#    The ports are read from the containers, not assumed. local-config.yml and local-dms.yml set each
#    container's ASPNETCORE_HTTP_PORTS from DMS_CONFIG_ASPNETCORE_HTTP_PORTS and DMS_HTTP_PORTS
#    respectively and publish that same number on 127.0.0.1, so the value inside the container is the
#    host port -- the same two overrides eng/docker-compose/env-utility.psm1 resolves in
#    Resolve-CmsBaseUrl and Resolve-DockerLocalDmsBaseUrl. A stack on other ports would otherwise meet
#    a hard-coded 8081 here and wait on it forever. `docker inspect` (ENVOF, from step 7) reads a
#    stopped container, which both still are.
CMS_PORT=$(ENVOF ed-fi-api-config-service | sed -n 's/^ASPNETCORE_HTTP_PORTS=//p')
DMS_PORT=$(ENVOF ed-fi-api | sed -n 's/^ASPNETCORE_HTTP_PORTS=//p')
case "$CMS_PORT" in ''|*[!0-9]*) echo "could not read one numeric ASPNETCORE_HTTP_PORTS from ed-fi-api-config-service (got '$CMS_PORT')"; exit 1;; esac
case "$DMS_PORT" in ''|*[!0-9]*) echo "could not read one numeric ASPNETCORE_HTTP_PORTS from ed-fi-api (got '$DMS_PORT')"; exit 1;; esac
CMS="http://localhost:$CMS_PORT"
DMS="http://localhost:$DMS_PORT"

#    Every wait in this recipe is bounded. An open-ended `until` against a wrong port, a container that
#    exited, or a service that never becomes healthy hangs a pasted recipe with nothing on the screen;
#    this gives up after the stated number of seconds, shows the container's last log lines and stops.
#    Each probe is capped as well (2 s to connect, 5 s in total), so a port that accepts the connection
#    and never answers cannot hold one curl open past the counter.
WAIT200() { # WAIT200 <url> <seconds> <container>
  _waited=0
  until [ "$(curl -s --connect-timeout 2 --max-time 5 -o /dev/null -w '%{http_code}' "$1")" = "200" ]; do
    if [ "$_waited" -ge "$2" ]; then
      echo "$3 did not answer 200 at $1 within $2 s -- check the port above and the container's logs:"
      docker logs --tail 20 "$3"
      return 1
    fi
    sleep 3; _waited=$((_waited + 3))
  done
}
docker start ed-fi-api-config-service
WAIT200 "$CMS/health" 300 ed-fi-api-config-service || exit 1
echo "CMS healthy at $CMS"

#    `restore-admin` is registered against YOUR CMS, so its secret has to satisfy that instance's
#    IdentitySettings:ClientSecretValidation bounds -- which are configurable, so no literal secret
#    written here can be known to fit. Reuse the CMS identity client secret instead: CMS validates
#    that secret against those same bounds itself, so whatever they are, it is in range. The bounds
#    are read here as well; step 10 hands them to setup-openiddict.ps1, which otherwise falls back to
#    its own 32/128 defaults and would reject a secret a differently configured stack accepts.
CMSENV=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' ed-fi-api-config-service)
ADMIN_SECRET=$(printf '%s\n' "$CMSENV" | sed -n 's/^IdentitySettings__ClientSecret=//p')
CLIENT_SECRET_MIN=$(printf '%s\n' "$CMSENV" | sed -n 's/^IdentitySettings__ClientSecretValidation__MinimumLength=//p')
CLIENT_SECRET_MAX=$(printf '%s\n' "$CMSENV" | sed -n 's/^IdentitySettings__ClientSecretValidation__MaximumLength=//p')
test -n "$ADMIN_SECRET" -a -n "$CLIENT_SECRET_MIN" -a -n "$CLIENT_SECRET_MAX" || \
  { echo "could not read the CMS client secret and its validation bounds from ed-fi-api-config-service"; exit 1; }

#    The registration and every token request carry a client secret. A curl argument list is visible
#    to every process on the host for as long as curl runs, so those calls are made by pwsh instead:
#    the secret travels in the environment of that one process, under CMS_SECRET, and
#    Invoke-WebRequest encodes the form itself. Both helpers assert the status -- registration
#    answers 200 with a title, a token request 200 with access_token -- and print the status and
#    body to stderr on anything else, so a failed registration is reported as one instead of
#    surfacing a step later as a token that never came. Each request is capped at 60 s, so a service
#    that accepts the connection and never answers stops the recipe rather than hanging it. The token
#    is written without a trailing newline so a Windows shell cannot hand the header a stray carriage
#    return.
CMS_REGISTER() { # CMS_REGISTER <client-id> <display-name>; the secret is read from CMS_SECRET
  CMS="$CMS" CLIENT_ID="$1" DISPLAY_NAME="$2" CMS_SECRET="$CMS_SECRET" pwsh -NoProfile -Command '
    $response = Invoke-WebRequest -Method Post -Uri "$env:CMS/connect/register" -SkipHttpErrorCheck -TimeoutSec 60 `
      -ContentType "application/x-www-form-urlencoded" `
      -Body @{ ClientId = $env:CLIENT_ID; ClientSecret = $env:CMS_SECRET; DisplayName = $env:DISPLAY_NAME }
    if ([int]$response.StatusCode -ne 200) {
      [Console]::Error.WriteLine("registering $env:CLIENT_ID answered HTTP $([int]$response.StatusCode): $($response.Content)")
      exit 1
    }
  '
}
CMS_TOKEN() { # CMS_TOKEN <client-id> <scope>; the secret is read from CMS_SECRET; prints the token
  CMS="$CMS" CLIENT_ID="$1" SCOPE="$2" CMS_SECRET="$CMS_SECRET" pwsh -NoProfile -Command '
    $response = Invoke-WebRequest -Method Post -Uri "$env:CMS/connect/token" -SkipHttpErrorCheck -TimeoutSec 60 `
      -ContentType "application/x-www-form-urlencoded" `
      -Body @{ grant_type = "client_credentials"; client_id = $env:CLIENT_ID; client_secret = $env:CMS_SECRET; scope = $env:SCOPE }
    $token = if ([int]$response.StatusCode -eq 200) { ($response.Content | ConvertFrom-Json).access_token } else { "" }
    if ([string]::IsNullOrWhiteSpace($token)) {
      [Console]::Error.WriteLine("token for $env:CLIENT_ID answered HTTP $([int]$response.StatusCode): $($response.Content)")
      exit 1
    }
    [Console]::Out.Write($token)
  '
}
#    The bearer tokens those requests mint are under the same rule. Every call that carries one -- the
#    data store re-save below, and the vendor creation, application creation and smoke read in step 12
#    -- goes through AUTH_HTTP, which reads the token from TOKEN and the JSON body, if any, from BODY
#    in its own environment, so neither is ever an argument. It prints the status on its first line
#    and the body after it, and writes the response headers to the file named, so every caller keeps
#    the status check and the body diagnostics it had; a request that could not be made at all exits
#    non-zero, which each caller guards.
AUTH_HTTP() { # AUTH_HTTP <method> <url> <headers-file>; token from TOKEN, JSON body (if any) from BODY; prints the status, then the body
  METHOD="$1" URL="$2" HEADERS_FILE="$3" TOKEN="$TOKEN" BODY="${BODY:-}" pwsh -NoProfile -Command '
    $request = @{ Method = $env:METHOD; Uri = $env:URL; Headers = @{ Authorization = "Bearer $env:TOKEN" }; SkipHttpErrorCheck = $true; TimeoutSec = 60 }
    if (-not [string]::IsNullOrEmpty($env:BODY)) { $request.ContentType = "application/json"; $request.Body = $env:BODY }
    $response = Invoke-WebRequest @request
    $headerLine = foreach ($header in $response.Headers.GetEnumerator()) { "$($header.Key): $($header.Value -join ", ")" }
    Set-Content -Path $env:HEADERS_FILE -Encoding utf8NoBOM -Value ($headerLine -join "`n")
    [Console]::Out.WriteLine([int]$response.StatusCode)
    [Console]::Out.Write([string]$response.Content)
  '
}
CMS_SECRET="$ADMIN_SECRET"
CMS_REGISTER restore-admin "Restore Admin" || { echo "restore-admin was not registered, so nothing else in step 9 can succeed"; exit 1; }
T=$(CMS_TOKEN restore-admin edfi_admin_api/full_access) || \
  { echo "no token for restore-admin -- an HTTP 500 naming the private key above means step 7 did not take effect"; exit 1; }

PW=$(docker exec dms-postgresql printenv POSTGRES_PASSWORD)
test -n "$PW" -a -n "$DB" -a -n "$DBUSER" || { echo "could not read the database password, name and user from the container"; exit 1; }

#    None of these three values is pasted into the request by hand. All come from the running
#    container and may hold characters that are special in JSON (") or in a connection string
#    (; ' " =) -- POSTGRES_USER no less than the password, since it is an override like the others. Raw
#    interpolation either breaks the JSON outright or -- worse -- produces a connection string that
#    CMS stores happily and Npgsql then reads differently, which is a data store that saves cleanly
#    and cannot connect. So DbConnectionStringBuilder assembles the connection string, applying the
#    same quoting rules Npgsql parses; ConvertTo-Json writes the body; and the assembled string is
#    re-parsed and compared to its inputs before anything is sent.
#
#    `.PSBase` is required when SETTING ConnectionString: without it PowerShell's dictionary adapter
#    stores a keyword literally named ConnectionString instead of parsing the string, and the check
#    below would pass on an empty builder.
DS_BODY=$(PW="$PW" DB="$DB" DBUSER="$DBUSER" pwsh -NoProfile -Command '
  $csb = [System.Data.Common.DbConnectionStringBuilder]::new()
  $csb.Add("host", "dms-postgresql")
  $csb.Add("port", "5432")
  $csb.Add("username", $env:DBUSER)
  $csb.Add("password", $env:PW)
  $csb.Add("database", $env:DB)
  $connectionString = $csb.PSBase.ConnectionString

  $check = [System.Data.Common.DbConnectionStringBuilder]::new()
  $check.PSBase.ConnectionString = $connectionString
  if ($check["password"] -cne $env:PW -or $check["database"] -cne $env:DB -or
      $check["username"] -cne $env:DBUSER) {
      throw "the assembled connection string does not read back as the values it was built from"
  }

  $body = [ordered]@{
      id               = 1
      dataStoreType    = "Development"
      name             = "Local Development Data Store"
      connectionString = $connectionString
  }
  [Console]::Out.Write(($body | ConvertTo-Json -Compress))
') || { echo "the data store request body could not be built; nothing was sent"; exit 1; }

#    The status is asserted rather than printed. CMS answers this PUT with 204 No Content on success;
#    on anything else the stored connection string is still the producer's, and DMS restart-loops on
#    it in step 11 rather than failing here where you can see why. The body carries the database
#    password, so it travels to AUTH_HTTP as BODY in that process's environment and is never written
#    to disk; the token travels the same way as TOKEN.
TOKEN="$T"
BODY="$DS_BODY"
DS_RESPONSE=$(AUTH_HTTP PUT "$CMS/v3/dataStores/1" "$ART/datastore-put.headers") || \
  { unset BODY DS_BODY; echo "the data store re-save request could not be made"; exit 1; }
unset BODY DS_BODY
DS=$(printf '%s\n' "$DS_RESPONSE" | sed -n 1p)
if [ "$DS" != "204" ]; then
  echo "the data store re-save answered HTTP ${DS:-none}, expected 204"
  printf '%s\n' "$DS_RESPONSE" | sed 1d
  exit 1
fi
echo "data store re-saved -> HTTP 204"

# 10. REQUIRED: recreate the client DMS uses to reach the Configuration Service.
#     Registering `restore-admin` above is not this step. DMS authenticates to CMS as its own client,
#     `CMSReadOnlyAccess` by default, and the restore replaced `dmscs.OpenIddict*` with the PRODUCER's
#     rows -- so the `CMSReadOnlyAccess` row now in the database stores a hash of the producer's
#     secret, which is not published. DMS presents your `CONFIG_SERVICE_CLIENT_SECRET` on every call
#     to CMS, so unless your secret is byte-identical to one you have never seen, CMS answers 401 and
#     DMS cannot read claim sets or authorization metadata. There is no way to check whether you got
#     lucky, so this step is unconditional.
#
#     Read the credentials from the DMS container rather than from your env file: what has to exist in
#     CMS is what DMS will actually send. `docker inspect` (ENVOF, from step 7) rather than
#     `docker exec`, for the same reason as step 7 -- ed-fi-api is stopped until step 11.
CID=$(ENVOF ed-fi-api | sed -n 's/^ConfigurationServiceSettings__ClientId=//p')
CSEC=$(ENVOF ed-fi-api | sed -n 's/^ConfigurationServiceSettings__ClientSecret=//p')
CSCOPE=$(ENVOF ed-fi-api | sed -n 's/^ConfigurationServiceSettings__Scope=//p')
test -n "$CID" -a -n "$CSEC" -a -n "$CSCOPE" || \
  { echo "could not read the DMS-to-CMS client id, secret and scope from ed-fi-api"; exit 1; }

#     The two role names are read as well, and from CMS rather than from DMS, because CMS is the side
#     that enforces them: its service policy requires the presented token to carry
#     IdentitySettings:ConfigServiceRole. Both are supported overrides -- local-config.yml sets them
#     from DMS_CONFIG_IDENTITY_SERVICE_ROLE and DMS_CONFIG_IDENTITY_CLIENT_ROLE -- and left unpassed,
#     setup-openiddict.ps1 falls back to its own cms-client/dms-client defaults and grants a role your
#     CMS does not require. The token check below would still pass, because minting a token does not
#     exercise the role; the failure would surface in step 11 as DMS unable to read claim sets.
CMSROLE=$(ENVOF ed-fi-api-config-service | sed -n 's/^IdentitySettings__ConfigServiceRole=//p')
DMSROLE=$(ENVOF ed-fi-api-config-service | sed -n 's/^IdentitySettings__ClientRole=//p')
test -n "$CMSROLE" -a -n "$DMSROLE" || \
  { echo "could not read the identity role names from ed-fi-api-config-service"; exit 1; }

#     Both commands below run against $CMSDB, the database the running Configuration Service reads its
#     dmscs rows from, resolved once in step 7 and reused here rather than resolved again (a fresh
#     shell re-runs that resolution first): the DELETE and the insert after it must target the same
#     database, or the delete clears one while the insert skips the producer's row in the other.

#     Delete before recreating. setup-openiddict.ps1 inserts ON CONFLICT DO NOTHING, so on its own it
#     leaves the producer's secret hash exactly where it is and reports success. The two dependent
#     tables, OpenIddictApplicationScope and OpenIddictClientRole, are ON DELETE CASCADE.
#     The client id is a configured value as well, so it reaches psql as a variable and the
#     statement quotes it with :'cid' rather than having it pasted into the SQL text. Guarded, like
#     step 7: a delete that fails leaves the producer's row for the insert below to skip.
docker exec -i dms-postgresql psql -U "$DBUSER" -d "$CMSDB" -v ON_ERROR_STOP=1 -v cid="$CID" -f - <<'SQL' || \
  { echo "could not remove the producer's client row for $CID; the DMS-to-CMS client was not recreated"; exit 1; }
DELETE FROM dmscs."OpenIddictApplication" WHERE "ClientId" = :'cid';
SQL
#     Nothing below has to be escaped for SQL. Every value passed here is a configured one, and
#     setup-openiddict.ps1 builds each PostgreSQL literal with the shared quoting helper, so a client
#     id, scope or role name containing a single quote is inserted as data rather than ending the
#     statement -- the same property the DELETE above gets from :'cid'.
#     The secret is the one value here that must not be an argument: setup-openiddict.ps1 is a new
#     process, and its argument list is readable by every process on the host while it runs. So the
#     secret travels as CSEC in that one process's environment, and -NewClientSecretEnvironmentVariable
#     names the variable; the script reads it with the same Compose-precedence resolver its ENV:
#     parameters use, the ambient environment first. -NewClientSecret is always a literal -- a secret
#     may itself begin with "ENV:" -- which is why the variable has a parameter of its own.
#     -DbName is the same $CMSDB the DELETE above targeted, passed as a literal rather than as an ENV:
#     indirection the script would resolve on its own, so the two cannot name different databases.
CSEC="$CSEC" pwsh -NoProfile -File ./setup-openiddict.ps1 -InsertData \
  -NewClientId "$CID" -NewClientName "CMS ReadOnly Access" -ClientScopeName "$CSCOPE" \
  -NewClientSecretEnvironmentVariable CSEC -ConfigServiceRole "$CMSROLE" -DmsClientRole "$DMSROLE" \
  -EnvironmentFile ./.env -DbName "$CMSDB" -DbUser "$DBUSER" \
  -ClientSecretMinimumLength "$CLIENT_SECRET_MIN" -ClientSecretMaximumLength "$CLIENT_SECRET_MAX" || \
  { echo "setup-openiddict.ps1 failed; the DMS-to-CMS client was not recreated"; exit 1; }
#     Those are the arguments the bootstrap uses for this client, with the role names taken from the
#     running Configuration Service rather than left to defaults, so the roles, scope, permissions and
#     namespace claim come back identical rather than approximately.

#     Prove it before starting DMS: the credentials DMS will present must actually mint a token.
CMS_SECRET="$CSEC"
CT=$(CMS_TOKEN "$CID" "$CSCOPE") || \
  { echo "the DMS-to-CMS client cannot mint a token; DMS would start and fail to read claim sets"; exit 1; }
echo "DMS-to-CMS client '$CID' recreated with your secret and verified"

# 11. Now start DMS. A cached first-use validation failure is NOT cleared by re-provisioning, so if
#     DMS was started too early, restart the container rather than re-running any provisioning step.
docker start ed-fi-api
WAIT200 "$DMS/health" 300 ed-fi-api || exit 1
echo "DMS healthy at $DMS"

# 12. Create your own DMS API client before reading data.
#     A healthy DMS is not yet a readable one. The artifact carries the PRODUCER's vendor,
#     application and client rows in dmscs, whose secrets you do not have, so create your own.
#     `EdFiAPIPublisherWriter` is the claim set to ask for: API Publisher loaded this dataset
#     originally, so that claim set already covers every resource present.
#     POST /v3/vendors answers 201 with an EMPTY body, so read the new id from the Location header,
#     which AUTH_HTTP writes to the headers file named. Both calls carry the admin token as TOKEN.
#     Each status is asserted before the response is trusted, as the data store PUT's is: a 4xx or
#     5xx comes with no Location and no credentials, and unasserted it would surface one line later
#     as "no Location" or "no credentials" with the cause gone. CMS creates vendors by company name
#     (VendorModule.InsertVendor): a new company answers 201; one it already holds answers 200 with
#     Location set and the row updated, which is what a re-run of this step meets. Nothing else may
#     continue.
TOKEN="$T"
BODY='{"company":"Local Consumer","contactName":"Consumer","contactEmailAddress":"consumer@example.com","namespacePrefixes":"uri://ed-fi.org"}'
VENDOR_RESPONSE=$(AUTH_HTTP POST "$CMS/v3/vendors" "$ART/vendor-post.headers") || \
  { echo "the vendor request could not be made"; exit 1; }
VS=$(printf '%s\n' "$VENDOR_RESPONSE" | sed -n 1p)
case "$VS" in
  201) echo "vendor created -> HTTP 201" ;;
  200) echo "vendor 'Local Consumer' already existed; CMS updated it and answered HTTP 200, so its id is reused" ;;
  *) echo "the vendor was not created: HTTP ${VS:-none}, expected 201"; printf '%s\n' "$VENDOR_RESPONSE" | sed 1d; exit 1 ;;
esac
VID=$(sed -n 's|^[Ll]ocation:.*/v3/vendors/\([0-9]*\).*|\1|p' "$ART/vendor-post.headers" | tr -d '\r')
test -n "$VID" || \
  { echo "HTTP $VS, but no Location header names the vendor id"; cat "$ART/vendor-post.headers"; exit 1; }

BODY="{\"applicationName\":\"Local Consumer Read\",\"vendorId\":${VID},\"claimSetName\":\"EdFiAPIPublisherWriter\",\"educationOrganizationIds\":[255901],\"dataStoreIds\":[1]}"
APP_RESPONSE=$(AUTH_HTTP POST "$CMS/v3/applications" "$ART/application-post.headers") || \
  { echo "the application request could not be made"; exit 1; }
unset BODY
#     POST /v3/applications answers 201 with the credentials as its body (ApplicationModule), and that
#     response is the only place the secret is ever shown -- keep it if you want the client again.
AS=$(printf '%s\n' "$APP_RESPONSE" | sed -n 1p)
if [ "$AS" != "201" ]; then
  echo "the application was not created: HTTP ${AS:-none}, expected 201"
  printf '%s\n' "$APP_RESPONSE" | sed 1d
  exit 1
fi
APP=$(printf '%s\n' "$APP_RESPONSE" | sed 1d)
KEY=$(printf '%s\n' "$APP" | sed -n 's/.*"key"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
SEC=$(printf '%s\n' "$APP" | sed -n 's/.*"secret"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
test -n "$KEY" -a -n "$SEC" || { echo "HTTP 201, but no key and secret in the body: $APP"; exit 1; }

CMS_SECRET="$SEC"
DT=$(CMS_TOKEN "$KEY" edfi_admin_api/full_access) || { echo "no DMS token minted"; exit 1; }

#     The read that proves the dataset is being served. Send a real token: an absent or malformed
#     one answers 401, which tells you nothing about the data.
#     A 401, 403, 500 or 503 all match "HTTP/" and every one of them leaves the dataset unproven, so
#     the status and the count are captured and compared to exact values rather than grepped for.
#     BODY is cleared first: a GET must not inherit the application body above.
TOKEN="$DT"
BODY=
SMOKE_RESPONSE=$(AUTH_HTTP GET "$DMS/data/ed-fi/students?limit=1&totalCount=true" "$ART/smoke-headers.txt") || \
  { echo "the smoke read could not be made"; exit 1; }
SC=$(printf '%s\n' "$SMOKE_RESPONSE" | sed -n 1p)
printf '%s\n' "$SMOKE_RESPONSE" | sed 1d > "$ART/smoke-body.json"
TC=$(grep -i '^total-count:' "$ART/smoke-headers.txt" | tail -1 | tr -d '\r' |
     sed 's/^[^:]*:[[:space:]]*//')
if [ "$SC" != "200" ] || [ "$TC" != "21628" ]; then
  echo "DMS did not serve the restored dataset: HTTP ${SC:-none}, Total-Count ${TC:-absent}, expected 200 and 21628"
  cat "$ART/smoke-headers.txt"
  head -c 2000 "$ART/smoke-body.json"; echo
  exit 1
fi
echo "DMS served the restored dataset: HTTP 200, Total-Count 21628"
```

> **Do not drop and recreate the database part-way through this recipe and expect a CMS restart to
> recover.** CMS deploys the `dmscs` schema on startup but does not seed an OpenIddict signing key, so
> a recreated database leaves `dmscs."OpenIddictKey"` empty, `POST /connect/token` answers 500, and no
> CMS API call can succeed. Recovering means re-running step 7. Before step 6 has passed, a failed
> attempt puts the deployment back under its own name itself -- its guards run `RECOVER_FROM_REF`, and
> *Recovery after a failed restore* below covers a helper that could not -- and the next attempt
> resumes at step 5; to start over after that, tear the stack down with volumes
> (`./bootstrap-local-dms.ps1 -d -v`) and begin again at step 3 -- never at step 4 over a database that
> already holds a restore, which is how a partial restore ends up set aside as the reference.

### Recovery after a failed restore

Every failure guard from `createdb` in step 5 through step 6 runs `RECOVER_FROM_REF` before it stops --
all but the 5b apply, which psql rolled back whole and which is re-run in place -- so in the normal case
the deployment is already back under its own name when the shell exits, and the next attempt simply
resumes at step 5. The block below is for the cases where that did not happen: the helper itself
reported a failure (a connection was still held on one of the databases), the run was interrupted
before a guard could call it, or a 5b apply failed for a cause that cannot be fixed in place. It is the
same function as in step 5 -- the pull-request lane holds the two copies to be identical -- and it
reads everything it needs from the running container, so it can be pasted into a fresh shell on its
own. It returns rather than exits.

```shell
RECOVER_FROM_REF() { # puts the deployment back under its own name after a failed restore; reads its inputs from the container
  _db=$(docker exec dms-postgresql printenv POSTGRES_DB_NAME)
  _dbuser=$(docker exec dms-postgresql printenv POSTGRES_USER)
  _ref="${_db}_reference"
  test -n "$_db" -a -n "$_dbuser" || \
    { echo "recovery: could not read the database name and superuser from dms-postgresql; nothing was changed"; return 1; }
  _ref_exists=$(docker exec -i dms-postgresql psql -U "$_dbuser" -d postgres -v ON_ERROR_STOP=1 -tA -v ref="$_ref" -f - <<'SQL'
SELECT 1 FROM pg_database WHERE datname = :'ref';
SQL
  ) || { echo "recovery: could not ask the cluster whether $_ref exists; nothing was changed"; return 1; }
  test -n "$_ref_exists" || \
    { echo "recovery: there is no $_ref, so the deployment was never set aside and $_db is still the deployment; nothing was changed"; return 1; }
  docker exec dms-postgresql dropdb -U "$_dbuser" --maintenance-db=postgres --if-exists -- "$_db" || \
    { echo "recovery: could not drop $_db -- something still holds a connection to it; the deployment is intact as $_ref; close that connection and run the recovery again (see Recovery after a failed restore)"; return 1; }
  docker exec -i dms-postgresql psql -U "$_dbuser" -d postgres -v ON_ERROR_STOP=1 -q \
    -v ref="$_ref" -v db="$_db" -f - <<'SQL' || \
    { echo "recovery: could not rename $_ref back to $_db; the deployment is intact as $_ref -- run the recovery again (see Recovery after a failed restore)"; return 1; }
SELECT format('ALTER DATABASE %I RENAME TO %I', :'ref', :'db') \gexec
SQL
  echo "recovery: the deployment is back as $_db; fix the cause, then resume at step 5"
}
RECOVER_FROM_REF
```

Then resume at step 5. In a fresh shell, first re-run the three assignments at the top of the recipe,
`DUMP="$ART/${ARTIFACT}.dump"` from step 2, and `cd "$DC"`.

### Consumer checklist

| Step | Required for | Why |
| --- | --- | --- |
| Stage core-only ApiSchema (step 3) | everyone | a different schema set computes a different `EffectiveSchemaHash`, and DMS answers 503 |
| Repair the security metadata (step 5b) | everyone | `--no-owner --no-privileges` leaves the enqueue functions owned by the superuser and executable by `PUBLIC`; the DDL's ownership, `REVOKE`s and `GRANT`s are re-applied |
| Compare the restored schema (step 5c) | everyone | proves the restored artifact is indistinguishable from the same-revision deployment step 4 made, ownership and privileges included; a restore without step 5b fails it |
| Verify the restore (step 6) | everyone | `--exit-on-error` and the status check prove the archive applied; only the content check proves it is the published dataset |
| Install your own OpenIddict key (step 7) | everyone | the shipped private key is encrypted with the producer's identity key; without this no token mints |
| Rotate `SourceIdentity` (step 8) | everyone standing up a new data store | a restored copied backup is an independent writable data store and must not share a source identity with the producer |
| Register admin client, re-save data store (step 9) | everyone | the shipped `dmscs` connection string is producer-local and encrypted with the producer's database key |
| Recreate the DMS-to-CMS client (step 10) | everyone | the shipped `CMSReadOnlyAccess` row hashes the producer's secret, so DMS gets 401 from CMS with yours |
| Create your own DMS API client (step 12) | everyone who reads data | the shipped vendor/application/client rows are the producer's and their secrets are not published |
| Restart DMS rather than re-provisioning | anyone who saw a 503 | first-use validation failures are cached for the process lifetime |

## Provenance record

Every published artifact records the following, on its ticket and in this directory's history:

1. **The artifact** -- URL, file name, `.7z` and inner `.dump` sizes and sha256, document count,
   resource count, engine image, ApiSchema package and version, schema set, `EffectiveSchemaHash`,
   `ResourceKeyCount`, `ResourceKeySeedHash`, and the `SourceIdentity` the artifact ships with, so a
   consumer can prove rotation happened.
2. **Provenance** -- source artifact names and checksums, source ODS artifact, the DMS commit as a
   full immutable SHA rather than a branch name, the branch, date produced, and what changed relative
   to the artifact it supersedes.
3. **Restore recipe** -- the text above with placeholders filled, validated by execution from a clean
   slate against non-default credentials, recorded with the date of the run and the commit whose text
   was run. Text changed after that run is not covered by it: the record has to name the revision
   that was executed and say that later revisions were not, until a run of the later text is
   recorded the same way.
4. **Validation evidence** -- schema compare result, effective schema hash agreement, DMS smoke
   results, the full resource-by-resource reconciliation with both-direction diff counts, per-table
   row-count reconciliation, sequence-position assertions, and the invariant checkpoint table. The
   reconciliation is recorded as named script outputs and captured transcripts, not only as a summary
   sentence: a summary cannot be re-read to find which resource moved.
5. **Known limitations** -- that the shipped CMS state is producer-local, and anything deferred.

### Record for `EdFi_DMS_Northridge_v80_20260819_PG`

| | |
| --- | --- |
| Published | 2026-08-20, to `odsassets` / container `public` / prefix `Northridge/` |
| Supersedes | `EdFi_DMS_Northridge_07_20260708.7z` — 10,576,794 documents on the DMS-1221 schema. **Left in place, not deleted or renamed**; it is superseded, so prefer this artifact for any PostgreSQL-versus-SQL-Server comparison |
| What changed | brought to the current schema by fresh deployment plus copy-forward (the document store has no migration path, so the old database was never patched in place); added the 7 documents the old artifact was missing |
| The 7 added documents | Staff +2 (Krystal Redd, Lorraine Chen), StaffEducationOrganizationEmploymentAssociation +2, StaffEducationOrganizationAssignmentAssociation +2, AccountabilityRating +1 (EdOrg 255901, 2018, "Accountability Rating", "Recognized") |
| Sourced from | the Northridge ODS artifact named above, added through the DMS API with GET-by-id verification of every field on every document — not via API Publisher, whose exit code can be 0 after silently dropping documents on 4xx |
| DMS revision built | `087eaa013df22a88d0046ac6f0e211bf47ec79e4` — the branch's merge base with `main` at publication. At the publication head `b303450ab5fd689526696cb088a76a90b7ef6c14` the branch's changes were confined to `eng/northridge/` (`git diff --name-only 087eaa013df22a88d0046ac6f0e211bf47ec79e4...b303450ab5fd689526696cb088a76a90b7ef6c14`), so the DMS that produced and served the dataset carried exactly that revision's `src/` source. Review hardening after publication added restore and OpenIddict tooling changes under `eng/docker-compose/`, but no DMS production `src/` code changed on this branch (`git diff --name-only origin/main...DMS-1406 -- src` is empty) |
| Branch head at publication | `b303450ab5fd689526696cb088a76a90b7ef6c14` on `DMS-1406` — the workflow tooling and restore recipe as they stood when the artifact was uploaded |
| Recipe text executed | the eleven-step text at `b303450ab5fd689526696cb088a76a90b7ef6c14` (no 5b, no 5c, DMS started as step 10), from a clean slate with non-default credentials on 2026-08-19. The current fourteen-step text has **not** been executed end to end against the published artifact — see *Recipe execution record* below |
| `SourceIdentity` as shipped | `8b962de6-b979-49aa-bce0-ca59e0a1ad51` — rotate it (step 8); a consumer whose value still reads this has skipped the step |
| Engine image | `postgres:16.8-alpine@sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0` — `eng/docker-compose/postgresql.yml` at the DMS revision built, unchanged at the publication head; the archive header reads `Dumped from database version: 16.8` and `Dumped by pg_dump version: 16.8` (`pg_restore -l`) |
| `ResourceKeySeedHash` | `fae376b7b81722efe1878226a49200d74ae68febac7d21f5121a0824236e981b` — `dms."EffectiveSchema"` of the restored artifact and of the fresh same-revision deployment step 5c compares it against (section `11-fingerprint` of both snapshots), and asserted against the reference in every checkpoint record |
| `ResourceKey` rows | 351 |
| `dms."Descriptor"` rows | 2,968 |
| `ChangeVersionSequence` | 21,553,810, equal to `MAX("IdentityVersion")` |
| Reconciliation evidence | The full per-resource output is **not** summarised in this file, and is not committed (see **Never commit**). The evidence set is the files written by the scripts plus the transcripts captured by the run: the two per-resource count CSVs from `Get-DmsResourceCount.ps1` count mode, one per engine; the captured transcript from that script's reconcile mode; the gap-document CSV passed to `Add-NorthridgeGapDocument.ps1 -OutputPath`; `rowcount.<source>-vs-<target>.tsv`; `restore-list.<target>.txt`; `restore-list.descriptor.txt`; `restore-output.<target>.txt`; the `checkpoint.<name>.<target>.txt` records for C1 through C4 -- C5, the checkpoint taken on the consumer-side restore, was measured inside the captured clean-slate transcript, `phase12-g12-clean-slate.txt`, and exists there rather than as a `checkpoint.C5.*` file, so that transcript is part of this set; and `schema-snapshot.<database>.txt` -- or `schema-snapshot.<position>.<database>.txt`, the form `Compare-DmsSchemaSnapshot.ps1` writes when the two compared names differ only in case, so the files stay distinct on a case-insensitive file system. `schema-diff.<left>-vs-<right>.txt` exists only for a failing schema compare; a passing compare is represented by the PASS transcript and matching snapshots. That set belongs with the ticket as attachments rather than in this repository. DMS-1406 carries the summary comment for this artifact; the file set named here is what has to accompany it, and a reader who cannot find these files has not been given the reconciliation |

Validation, all on the restored artifact rather than on the database that produced it: the
startup-computed `EffectiveSchemaHash` equals the value stored in `dms.EffectiveSchema` and equals the
SQL Server artifact's; DMS served authenticated reads with no 503 and no restarts; the
resource-by-resource reconciliation against SQL Server reported zero differences in both directions
across 210 resources and 10,576,801 documents. The schema compare is a gate of the recipe itself
(step 5c) rather than a result recorded once here: a database restored with the recipe's flags is not
equivalent to a deployment until step 5b has re-applied the ownership and privileges those flags
drop, and the snapshot -- ownership, routine security attributes and ACLs included -- reports exactly
that drift when the step is skipped. Each positive result was paired with a negative control run at the same
time, because a `pg_restore` run without `--exit-on-error` skips a failed `COPY` and keeps going to
the end of the archive, `RESTORE VERIFYONLY` passes an unreadable file, and a restore that never
starts reports zero errors — success-shaped signals that mean nothing on their own.

Known limitations: the `dmscs` rows in the artifact are producer-local throughout — connection string,
OpenIddict signing key, DMS-to-CMS client row, and vendor/application/client rows — which is what
steps 7, 9, 10 and 12 exist to replace. Ownership-token stamping is not implemented at this revision,
so `CreatedByOwnershipTokenId` is null on every document. `tracked_changes_edfi` is present and empty
by design. A restored database's catalog respells exactly one row of the deployment's: PostgreSQL
re-parses the dumped `CK_DocumentCacheState_Lifecycle` expression `(ARRAY[...])::text[]` as
`ARRAY[(...)::text, ...]`, the same predicate spelled differently. `Compare-DmsSchemaSnapshot.ps1`
rewrites the second spelling to the first -- only when every element carries the same cast, so a
changed predicate still fails -- which is what lets step 5c compare the restored artifact against
the deployment itself rather than against a restored copy of it. During production a manifest
re-POST was issued against the live database to exercise the field comparison; DMS treated it as an
idempotent upsert, creating no duplicates, and no `ContentVersion` moved on any of the 10,576,794
copied documents.

Publication was verified from the consumer's side, not the uploader's. After the upload, an anonymous
`HEAD` reported `Content-Length: 869019055`, `Content-Type: application/x-7z-compressed` and access
tier `Cool`; the blob was then re-downloaded from the public URL and hashed to
`49129363581eab342146e8dd9a4da95dd6f7b035f0c39ee39c9691176cd856a0`, and `cmp` confirmed it
byte-for-byte identical to the file that was uploaded rather than merely equal in digest. Finally,
**step 1 of the recipe above was executed verbatim against the published URL** — `curl -O` followed by
`sha256sum -c` — and reported `EdFi_DMS_Northridge_v80_20260819_PG.7z: OK`.

### Recipe execution record

The clean-slate, non-default-credential run of 2026-08-19 executed the eleven-step recipe text of the
publication head, `b303450ab5fd689526696cb088a76a90b7ef6c14`, recorded unchanged in
`a0eecb58a5e4051730f82ec4c12288a443307250`: steps 1 to 11, with no step 5b, no step 5c, and DMS
started as step 10. Every change to the recipe text since then is post-publication review hardening
(`git log a0eecb58a..HEAD -- eng/northridge/README.md`), and the fourteen-step text above has **not**
been executed end to end against the published artifact. Its added gates are covered piecewise
instead -- the repair block statement by statement against the emitter's fixture, dump → bare restore
→ 5b → compare as a live scenario, and the fail-closed guards database-free -- by the two suites named
at the top of this document. A future end-to-end run of the current text belongs here, with its date
and the commit whose text was run; nothing above may claim it before then.

## Never commit

Dataset artifacts and their by-products do not belong in this repository: `*.7z`, `*.dump`, `*.bak`,
schema snapshots, count and reconciliation output, container logs, connection strings, passwords, SAS
tokens, and encryption keys. Point the scripts' `-OutputDirectory` at a scratch location outside the
repository.
