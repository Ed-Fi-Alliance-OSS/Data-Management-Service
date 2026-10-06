---
jira: DMS-1447
jira_url: https://edfi.atlassian.net/browse/DMS-1447
epic: DMS-1402
---

# Story: Raise the PostgreSQL Floor and Publish the Descriptor-Collation Upgrade Contract

## Outcome

Establish PostgreSQL 18 and its `pg_c_utf8` collation as supported platform foundations before the
descriptor index and natural-key resolver depend on them.

`pg_c_utf8` itself is available from PostgreSQL 17. The team chose PostgreSQL 18 as the floor for
its performance improvements, so every version guard, image, and client package in this story
targets 18.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- No dependency on another story in this epic.
- May run in parallel with DMS-1443–DMS-1446.
- Blocks DMS-1448.

## Implementation Scope

- Raise DMS CI, compose/developer images, supported-version documentation, and provisioning guards to
  PostgreSQL 18 or later.
- Guard both platform preconditions in SchemaTools before any DDL is emitted:
  `server_version_num >= 180000` **and** a UTF-8 database encoding
  (`pg_encoding_to_char(encoding) = 'UTF8'` for the target row in `pg_database`). Either failure
  produces the documented compatibility message naming both requirements; a non-UTF8 database on
  PostgreSQL 18 must not surface later as `collation "pg_c_utf8" for encoding "…" does not exist`
  on the descriptor index.
- When SchemaTools creates the database itself, emit `CREATE DATABASE … ENCODING 'UTF8'`. Do not
  attempt `TEMPLATE template0` / locale-provider selection to force UTF-8 onto a cluster whose
  `template1` is not UTF-8; that remains an operator precondition.
- Run the server checks before the create. `DdlProvisionCommand` calls `CreateDatabaseIfNotExists`
  before `PreflightSeedValidation`, so a guard that only runs in the preflight comes too late: on a
  non-UTF-8 `template1` the create fails first with the raw
  `22023: new encoding (UTF8) is incompatible with the encoding of the template database`, and on a
  16 or 17 server the create succeeds and leaves an empty database behind before the version guard
  rejects it. When the create path is requested, check `server_version_num` and the `template1`
  encoding over the maintenance connection first, and fail with the documented compatibility
  message without creating anything. Keep the target-database encoding check for databases that
  already exist.
- Sweep every pinned PostgreSQL 16 site. Re-derive the inventory at implementation time rather than
  trusting a fixed list; new pins keep landing. Use both
  `git grep -nE 'postgres:1[67]|postgresql1[67]'` and a digest search for the pinned 16.8 image,
  `git grep -n 951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0`, because some
  sites pin only the digest. The inventory on `main` as of 2026-10-05 is:
  `.github/workflows/on-dms-pullrequest.yml` (`POSTGRES_INTEGRATION_IMAGE`, feeds the three
  integration jobs through `start-postgresql-test-container`), `.github/workflows/on-config-pullrequest.yml`
  (CMS CI service image), `eng/docker-compose/postgresql.yml` (digest-pinned image),
  `eng/azure-vm/compose/docker-compose.yml`, `src/dms/Dockerfile` and `src/config/Dockerfile`
  (`postgresql16-client` → `postgresql18-client`, available in the images' Alpine 3.23 base; a 16
  `pg_dump` refuses an 18 server), `src/dms/Nuget.Dockerfile` and `src/config/Nuget.Dockerfile`
  (`postgresql16-client`), `eng/docker-compose/tests/CmsDatabaseTopology.Tests.ps1` fixtures
  hard-coding `postgres:16`, `eng/docker-compose/tests/CdcRunbookWrappers.Tests.ps1` (default
  `POSTGRES_IMAGE`, DMS-1326), `eng/ci/tests/CdcQualification.Tests.ps1` (`postgres:16` image-pull
  fixture, DMS-1323),
  the performance harness (`Performance.Harness/README.md`, the three
  `Smoke/Given_Postgresql_*Smoke.cs` scenarios, and in `Performance.Harness.Tests.Unit`
  `Configuration/PerfEvidenceRunSettingsTests.cs` and `Results/ResultSamples.cs`; image tag and
  digest), `eng/performance/invoke-final-gate.ps1` and `eng/performance/invoke-traditional-baseline.ps1`
  (digest-only `ExpectedPostgresDigest` defaults; `Assert-ExpectedDigest` refuses any other image, so
  manual performance-evidence runs break until they move), `eng/northridge/README.md` (the
  `nr-pg-fixture` container in the restore recipe), `reference/cdc-documentation/operations-runbook.md`
  (pinned engine image, DMS-1326), the design docs that describe the current pin
  (`epics/14-authorization/22-namespace-auth-index-prefix-like.md` and
  `reference/design/plugins-DMS-1462/design.md`), and `docs/OPERATIONS.md` "Database versions",
  which states "PostgreSQL 15 or later" and must state the PostgreSQL 18 floor.
- Historical records stay as written in any file: measurements and provenance that say which
  PostgreSQL version something was measured on or produced with. Examples are the Northridge README
  "Engine image" row of the published-artifact record (`postgres:16.8-alpine@sha256:951d…`,
  "Dumped from database version: 16.8"), the measurement comment in
  `CmsDatabaseTopology.Tests.ps1`, and design-doc benchmark write-ups. Live pins in the same files
  still move.
- Update the `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` GitHub repository variable, which is outside
  the source tree and currently holds `postgres:16@sha256:e17e8606…`. The CDC lanes in
  `on-dms-pullrequest.yml` and `nightly-cdc-qualification.yml`, `eng/ci/Invoke-CdcQualification.ps1`,
  and `eng/ci/prepare-cdc-api-e2e.ps1` take their PostgreSQL image from it with no in-repo fallback,
  so every in-repo check can pass while CDC still runs on 16. Set it to a digest-pinned
  PostgreSQL 18 image; this needs a repository administrator.
- Handle the official `postgres:18` image's data-directory change. From 18 the image sets
  `PGDATA=/var/lib/postgresql/18/docker` and declares its `VOLUME` at `/var/lib/postgresql`, so
  anything that mounts or reads `/var/lib/postgresql/data` no longer reaches the cluster. Move each
  mount to `/var/lib/postgresql` and resolve data-directory paths through `$PGDATA`; do not set
  `PGDATA` explicitly, since that still forces every mount to move and only adds a variable to keep
  in sync. The sites on `main` as of 2026-10-04
  (`git grep -nE 'var/lib/postgresql|PGDATA'`) are:
  - `eng/docker-compose/postgresql.yml` and `eng/azure-vm/compose/docker-compose.yml` (named
    volumes mounted at `/var/lib/postgresql/data`);
  - `eng/docker-compose/postgresql-tmpfs.yml` and
    `.github/actions/start-postgresql-test-container/action.yml` (tmpfs at
    `/var/lib/postgresql/data`);
  - `eng/docker-compose/postgresql-init.sh`, which appends the logical-replication `pg_hba.conf`
    entry and `wal_level = logical` to files under `/var/lib/postgresql/data`; on 18 those writes
    miss the live configuration and CDC loses logical replication;
  - the `data-path` inputs and `df`/`du` diagnostics in `.github/workflows/on-dms-pullrequest.yml`
    and `.github/workflows/nightly-keycloak-e2e.yml`;
  - `CdcConnectorTelemetryQualificationTests.Runbook.cs` in
    `EdFi.DataManagementService.Backend.Cdc.Tests.Integration`.

  Existing developer volumes created under 16 are not upgraded in place.
- Rebuild and republish the minimal and populated template packages on PostgreSQL 18: their
  PostgreSQL `.sql` dumps are coupled to the major version they were built and restored against
  (`build-minimal-template.yml`, `build-populated-template.yml`), and the version-coupled E2E lane
  in `on-dms-pullrequest.yml` must restore an 18-built dump.
- Publish the PostgreSQL major-upgrade `REINDEX` and collision-detection playbook for every
  `pg_c_utf8` descriptor index.
- Do not add the cross-engine Unicode verdict fixture matrix or the SQL Server `Turkish_100_CS_AS`
  database-default live fixture here. DMS-1455 owns both, because they exercise descriptor probe
  surfaces (`UriLowered` index, write/upsert, resolver, query-filter, and Change Query probes) that
  do not exist until DMS-1448 through DMS-1454 land. This story only documents the expected
  per-engine folding differences in the upgrade playbook.
- Keep legacy descriptor lowercasing and RI resolution in place in this story.

## Acceptance Criteria

- All PostgreSQL lanes run on PostgreSQL 18 or later; no `postgres:16*` or `postgres:17*` image,
  `postgresql16-client` or `postgresql17-client` package, pinned 16.8 digest, or matching fixture
  string remains in workflows, actions, compose files, Dockerfiles (including the `Nuget.Dockerfile`
  variants), CDC qualification and runbook tests, the performance harness and its evidence scripts,
  operator runbooks, design docs that describe the current pin, or test fixtures. Historical
  records are exempt as described above.
- The `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` repository variable holds a digest-pinned
  PostgreSQL 18 image, and the CDC lanes' recorded engine version is 18.
- `docs/OPERATIONS.md` states the PostgreSQL 18 minimum.
- No workflow, action, compose file, init script, or test reads or mounts the pre-18
  `/var/lib/postgresql/data` path; the CDC lanes prove logical replication is enabled on the
  PostgreSQL 18 container (`SHOW wal_level` returns `logical`) and the tmpfs-backed CI containers
  hold their data on tmpfs.
- A UTF-8 PostgreSQL 18 database passes both guards; a PostgreSQL 17 server, a PostgreSQL 16 server,
  and a non-UTF8 PostgreSQL 18 database each fail with the documented compatibility message before
  any DDL runs.
- With database creation requested, a PostgreSQL 16 or 17 server and a cluster whose `template1` is
  not UTF-8 each fail with the documented compatibility message (not a raw `22023` error), and no
  database is left behind.
- Template packages are rebuilt on PostgreSQL 18 and the version-coupled E2E lane restores them.
- Upgrade fixtures exercise pre/post-`REINDEX` validation and collision reporting.
- The upgrade playbook documents expected cross-engine Unicode differences as per-engine verdicts,
  never as parity; the executable pins are DMS-1455 acceptance criteria.
