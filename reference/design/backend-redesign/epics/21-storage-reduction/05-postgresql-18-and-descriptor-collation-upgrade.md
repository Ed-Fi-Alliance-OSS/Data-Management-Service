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
  `template1` is not UTF-8; that remains an operator precondition, and the encoding guard reports
  it.
- Sweep every pinned PostgreSQL 16 site. Re-derive the inventory at implementation time with
  `git grep -nE 'postgres:1[67]|postgresql1[67]'` rather than trusting a fixed list; new pins keep
  landing. The inventory on `main` as of 2026-10-04 is:
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
  `src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/Configuration/PerfEvidenceRunSettingsTests.cs`
  (perf image tag), `eng/northridge/README.md` (fixture container and engine-image provenance,
  DMS-1406), `reference/cdc-documentation/operations-runbook.md` (pinned engine image, DMS-1326),
  and `docs/RUNNING-LOCALLY.md` / supported-version documentation. Historical measurement records in
  design documents that cite the PostgreSQL 16 version they were measured on stay as written.
- Handle the official `postgres:18` image's data-directory change. From 18 the image sets
  `PGDATA=/var/lib/postgresql/18/docker` and declares its `VOLUME` at `/var/lib/postgresql`, so
  anything that mounts or reads `/var/lib/postgresql/data` no longer reaches the cluster. Either
  move each mount to `/var/lib/postgresql` and resolve paths through `$PGDATA`, or set `PGDATA`
  explicitly; pick one convention and apply it everywhere. The sites on `main` as of 2026-10-04
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
  `postgresql16-client` or `postgresql17-client` package, or matching fixture string remains in
  workflows, actions, compose files, Dockerfiles (including the `Nuget.Dockerfile` variants), CDC
  qualification and runbook tests, performance harness settings, operator runbooks, or test
  fixtures.
- No workflow, action, compose file, init script, or test reads or mounts the pre-18
  `/var/lib/postgresql/data` path; the CDC lanes prove logical replication is enabled on the
  PostgreSQL 18 container (`SHOW wal_level` returns `logical`) and the tmpfs-backed CI containers
  hold their data on tmpfs.
- A UTF-8 PostgreSQL 18 database passes both guards; a PostgreSQL 17 server, a PostgreSQL 16 server,
  and a non-UTF8 PostgreSQL 18 database each fail with the documented compatibility message before
  any DDL runs.
- Template packages are rebuilt on PostgreSQL 18 and the version-coupled E2E lane restores them.
- Upgrade fixtures exercise pre/post-`REINDEX` validation and collision reporting.
- The upgrade playbook documents expected cross-engine Unicode differences as per-engine verdicts,
  never as parity; the executable pins are DMS-1455 acceptance criteria.
