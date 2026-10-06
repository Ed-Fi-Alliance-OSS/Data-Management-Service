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
  rejects it. Put the checks inside the PostgreSQL provisioner's `CreateDatabaseIfNotExists` so
  every create path gets them: `DdlProvisionCommand`'s own create and the CDC managed-provisioning
  path (`ManagedDatabaseProvisioner.CreateDatabase`). Over the maintenance connection, check
  `server_version_num`, and when the database does not exist yet the `template1` encoding, before
  issuing `CREATE DATABASE`; fail with the documented compatibility message without creating
  anything. Keep the target-database encoding check for databases that already exist.
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
  the performance harness, image tag and digest:
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness/README.md`;
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness/Smoke/Given_Postgresql_BaselineRunSmoke.cs`,
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness/Smoke/Given_Postgresql_FinalGateDescriptorPipelineSmoke.cs`
  and
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness/Smoke/Given_Postgresql_FinalGatePrimaryPipelineSmoke.cs`;
  and
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/Configuration/PerfEvidenceRunSettingsTests.cs`
  and
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/Results/ResultSamples.cs`;
  `eng/performance/invoke-final-gate.ps1` and `eng/performance/invoke-traditional-baseline.ps1`
  (digest-only `ExpectedPostgresDigest` defaults; `Assert-ExpectedDigest` refuses any other image, so
  manual performance-evidence runs break until they move), `eng/northridge/README.md` (the
  `nr-pg-fixture` container used to run `eng/northridge/tests/SchemaSnapshotSecurity.Tests.ps1`
  locally), `reference/cdc-documentation/operations-runbook.md` (pinned engine image, DMS-1326), the
  design docs that describe the current pin
  (`reference/design/backend-redesign/epics/14-authorization/22-namespace-auth-index-prefix-like.md` and
  `reference/design/plugins-DMS-1462/design.md`), and `docs/OPERATIONS.md` "Database versions":
  keep the Configuration Service's "PostgreSQL 15 or later" statement and add the DMS requirement
  beside it: DMS requires PostgreSQL 18 or later with a UTF-8 database encoding, and a server shared
  by DMS and the Configuration Service must meet the DMS floor.
- Historical records stay as written in any file: measurements and provenance that say which
  PostgreSQL version something was measured on or produced with. Examples are the Northridge README
  "Engine image" row of the published-artifact record (`postgres:16.8-alpine@sha256:951d…`,
  "Dumped from database version: 16.8"), the measurement comment in
  `eng/docker-compose/tests/CmsDatabaseTopology.Tests.ps1`, and design-doc benchmark write-ups. Live pins in the same files
  still move.
- Update the `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` GitHub repository variable, which is outside
  the source tree and currently holds `postgres:16@sha256:e17e8606…`. The CDC lanes in
  `.github/workflows/on-dms-pullrequest.yml` and `.github/workflows/nightly-cdc-qualification.yml`,
  `eng/ci/Invoke-CdcQualification.ps1`,
  and `eng/ci/prepare-cdc-api-e2e.ps1` take their PostgreSQL image from it with no in-repo fallback,
  so every in-repo check can pass while CDC still runs on 16. This is a manual step: a repository
  administrator sets the variable to a digest-pinned PostgreSQL 18 image, and a second person
  verifies the new value (for example with `gh variable get CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE`)
  and records the verification in the pull request. No automated test checks the version.
- Handle the official `postgres:18` image's data-directory change. From 18 the image sets
  `PGDATA=/var/lib/postgresql/18/docker` and declares its `VOLUME` at `/var/lib/postgresql`, so
  anything that mounts or reads `/var/lib/postgresql/data` no longer reaches the cluster. Move each
  mount to `/var/lib/postgresql` and resolve data-directory paths through `$PGDATA`, following the
  image's new default layout. Do not keep the old path by setting `PGDATA=/var/lib/postgresql/data`
  explicitly: that works, but every container start would have to set it, and one that misses it
  silently falls back to the new default and loses its data mount, tmpfs, or init-script
  configuration. The sites on `main` as of 2026-10-05
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
  - `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTelemetryQualificationTests.Runbook.cs`.

  Existing developer volumes created under 16 are not upgraded in place.
- Rebuild and republish the minimal and populated template packages on PostgreSQL 18: their
  PostgreSQL `.sql` dumps are coupled to the major version they were built and restored against
  (`.github/workflows/build-minimal-template.yml`, `.github/workflows/build-populated-template.yml`),
  and `.github/workflows/scheduled-smoke-test.yml`, which builds the populated template and restores it (`verify_restore: true`), must build and
  restore it on PostgreSQL 18.
- Update the existing "DMS requires PostgreSQL 15 or later" breaking-change entry in
  `docs/changelog/8.1.0.md` to the new floor: PostgreSQL 18 or later with a UTF-8 database
  encoding. Say which deployments it affects (any PostgreSQL 15–17 server, and any non-UTF-8
  database) and that existing databases must be upgraded or re-created before upgrading DMS.
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
- A repository administrator has manually set the `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE`
  repository variable to a digest-pinned PostgreSQL 18 image, and a second person has manually
  verified the value and recorded that in the pull request.
- `docs/OPERATIONS.md` states the DMS PostgreSQL 18 + UTF-8 requirement alongside the Configuration
  Service's own floor.
- No workflow, action, compose file, init script, or test reads or mounts the pre-18
  `/var/lib/postgresql/data` path, and the tmpfs-backed CI containers hold their data on tmpfs.
- A person has manually verified, on the compose stack (`eng/docker-compose/postgresql.yml`)
  running PostgreSQL 18, that `eng/docker-compose/postgresql-init.sh` took effect: `SHOW wal_level` returns `logical`,
  and `pg_hba_file_rules` contains the replication entry for `kafka-postgresql-source`. The result
  is recorded in the pull request. (The CI action and the CDC test fixture pass
  `-c wal_level=logical` themselves, so their lanes would pass even with a broken init script.)
- A UTF-8 PostgreSQL 18 database passes both guards; a PostgreSQL 17 server, a PostgreSQL 16 server,
  and a non-UTF8 PostgreSQL 18 database each fail with the documented compatibility message before
  any DDL runs.
- With database creation requested, a PostgreSQL 16 or 17 server and a cluster whose `template1` is
  not UTF-8 each fail with the documented compatibility message (not a raw `22023` error), and no
  database is left behind, on both the direct and the CDC managed-provisioning paths.
- Template packages are rebuilt on PostgreSQL 18 and `.github/workflows/scheduled-smoke-test.yml`
  restores them on
  PostgreSQL 18.
- The 8.1.0 changelog's PostgreSQL breaking-change entry states the PostgreSQL 18 + UTF-8 floor; no
  release note still says "15 or later".
- Upgrade fixtures exercise pre/post-`REINDEX` validation and collision reporting.
- The upgrade playbook documents expected cross-engine Unicode differences as per-engine verdicts,
  never as parity; the executable pins are DMS-1455 acceptance criteria.
