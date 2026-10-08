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
- When SchemaTools creates a PostgreSQL database itself, emit `CREATE DATABASE … ENCODING 'UTF8'`.
  Do not attempt `TEMPLATE template0` / locale-provider selection to force UTF-8 onto a cluster
  whose `template1` is not UTF-8; that remains an operator precondition.
- Run the server checks before the create. `DdlProvisionCommand` calls `CreateDatabaseIfNotExists`
  before `PreflightSeedValidation`, so a guard that only runs in the preflight comes too late: once
  the create carries `ENCODING 'UTF8'`, a non-UTF-8 `template1` makes it fail first with the raw
  `22023: new encoding (UTF8) is incompatible with the encoding of the template database`, and on a
  16 or 17 server the create succeeds and leaves an empty database behind before the version guard
  rejects it. Run the checks as a provisioner precondition step that both create paths call before
  any create is attempted or recorded: `DdlProvisionCommand` before its own
  `CreateDatabaseIfNotExists`, and the CDC managed-provisioning controller
  (`CdcManagedDatabaseProvisioning`) before it records the `CreateDatabase` intent, so a failed check
  leaves no unfinished operation that would turn the retry after upgrading PostgreSQL into a
  `CdcManagedProvisioningRecoveryException`. Give the failure its own exception type and catch it
  explicitly on the managed path, which otherwise reports only the exception type name, so the
  documented compatibility message reaches the operator on both paths. Over the maintenance
  connection, check `server_version_num`, and when the database does not exist yet the `template1`
  encoding, before issuing `CREATE DATABASE`; fail with the documented compatibility message without
  creating anything. Keep the target-database encoding check for databases that already exist.
- Sweep every pinned PostgreSQL 16 site. Re-derive the inventory at implementation time rather than
  trusting a fixed list; new pins keep landing. Use
  `git grep -nE 'postgres:1[67]|postgresql1[67]'`, a digest search for the pinned 16.8 image,
  `git grep -n 951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0`, because some
  sites pin only the digest, and `git grep -nE 'PostgreSQL 1[3-7]\b|PG ?1[3-7]\b'` for prose statements
  of the floor; of its hits, DMS floor statements move, while Configuration Service floor
  statements, historical records, version-tolerance or feature-availability notes, and this story's
  own scope description in `natural-key-resolution.md` stay, and fixture server-version strings in
  inventory files move with their tag and digest. The
  inventory on `main` as of 2026-10-07 is:
  `.github/workflows/on-dms-pullrequest.yml` (`POSTGRES_INTEGRATION_IMAGE`, feeds the three
  integration jobs through `start-postgresql-test-container`), `.github/workflows/on-config-pullrequest.yml`
  (CMS CI service image), `eng/docker-compose/postgresql.yml` (digest-pinned image),
  `eng/azure-vm/compose/docker-compose.yml`, `src/dms/Dockerfile` and `src/config/Dockerfile`
  (`postgresql16-client` → `postgresql18-client`, available in the images' Alpine 3.23 base),
  `src/dms/Nuget.Dockerfile` and `src/config/Nuget.Dockerfile`
  (`postgresql16-client`), `eng/docker-compose/tests/CmsDatabaseTopology.Tests.ps1` fixtures
  hard-coding `postgres:16`, `eng/docker-compose/tests/CdcRunbookWrappers.Tests.ps1` (default
  `POSTGRES_IMAGE`, DMS-1326), `eng/ci/tests/CdcQualification.Tests.ps1` (`postgres:16` image-pull
  fixture, #1282),
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
  `eng/docker-compose/tests/CmsDatabaseTopology.Tests.ps1`, design-doc benchmark write-ups, and
  measurement and baseline-assumption records such as those in
  `reference/design/configuration-service/TOKEN-CLEANUP.md`,
  `reference/design/configuration-service/DMS-1327-cms-token-revocation.md`, and
  `reference/design/jobs-DMS-1437/` that name the PostgreSQL 16 image they ran on; present-tense pin
  phrases inside such records stay with them. In a file on the live-pin inventory whose sentence
  fuses a pin with a measurement, as in
  `reference/design/backend-redesign/epics/14-authorization/22-namespace-auth-index-prefix-like.md`,
  the pin reference moves and the measured figure keeps its 16.8 provenance. Image tags and
  commands elsewhere in that inventory file still move.
- Update the `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` GitHub repository variable, which is outside
  the source tree and currently holds `postgres:16@sha256:e17e8606…`. The CDC connector-template
  smoke lane in `.github/workflows/on-dms-pullrequest.yml`, the CDC lanes in
  `.github/workflows/nightly-cdc-qualification.yml`, `eng/ci/prepare-cdc-api-e2e.ps1`,
  `eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1`, and the
  `CdcConnectorTemplatePinnedImageFixture` take their PostgreSQL image from it with no in-repo
  fallback (`eng/ci/Invoke-CdcQualification.ps1` requires it and pulls or inspects the image it
  names, but starts no container from it), so every in-repo check can pass while CDC still runs on
  16 (the pull-request workflow's CDC lane is dispatch-only, so the pull request itself gives no
  evidence for the variable). This is a manual step: a repository administrator sets the variable to
  a digest-pinned PostgreSQL 18 image, and a second person verifies the new value (for example with
  `gh variable get CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE`) and records the verification in the pull
  request. No automated test checks the version. Make the change right after this story merges and
  before the next scheduled run of `.github/workflows/nightly-cdc-qualification.yml`, which reads
  the variable on every run; set earlier it breaks the nightly on `main`'s pre-18 layout, left later
  it fails the new version guard. Confirm with a `workflow_dispatch` run of that workflow.
- Handle the official `postgres:18` image's data-directory change. From 18 the image sets
  `PGDATA=/var/lib/postgresql/18/docker` and declares its `VOLUME` at `/var/lib/postgresql`, so
  anything that mounts or reads `/var/lib/postgresql/data` no longer reaches the cluster. Move each
  named volume to `/var/lib/postgresql`, mount each tmpfs at the data directory itself,
  `/var/lib/postgresql/18/docker`, and resolve data-directory paths through `$PGDATA`, following the
  image's new default layout. Do not keep the old path by setting `PGDATA=/var/lib/postgresql/data`
  explicitly: that works, but every container start would have to set it. The sites on `main` as
  of 2026-10-07 (`git grep -nE 'var/lib/postgresql|PGDATA'`) are:
  - `eng/docker-compose/postgresql.yml` and `eng/azure-vm/compose/docker-compose.yml` (named
    volumes mounted at `/var/lib/postgresql/data`);
  - `eng/docker-compose/postgresql-tmpfs.yml` and
    `.github/actions/start-postgresql-test-container/action.yml` (tmpfs at
    `/var/lib/postgresql/data`);
  - `eng/docker-compose/postgresql-init.sh`, which appends the logical-replication `pg_hba.conf`
    entry and `wal_level = logical` to files under `/var/lib/postgresql/data`; on 18 those writes
    land outside `$PGDATA` or fail, and the stack never gets `wal_level = logical`;
  - the `data-path` inputs in `.github/workflows/on-dms-pullrequest.yml`, and, reached through them
    rather than by the grep, `.github/actions/export-database-container-diagnostics/action.yml`,
    which runs `df`/`du` on the given path without a container shell (SQL Server lanes pass
    `/var/opt/mssql`), so `$PGDATA` would not expand there and the action must resolve the
    PostgreSQL path itself; and the inline `df`/`du` diagnostics in
    `.github/workflows/on-dms-pullrequest.yml` and `.github/workflows/nightly-keycloak-e2e.yml`;
  - `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTelemetryQualificationTests.Runbook.cs`,
    which passes the data directory as the runbook's `<data-path>` input to a shell-less
    `docker exec`, so it must obtain the path from the running container rather than name it;
  - `reference/design/backend-redesign/design-docs/natural-key-resolution.md`, whose hit already
    describes the new layout and needs no change.

  Existing developer volumes created under 16 are not upgraded in place: remounted at
  `/var/lib/postgresql`, they yield a fresh empty cluster beside the orphaned 16 files. State in
  `docs/OPERATIONS.md` "Database versions" that such a volume must be removed, or upgraded with
  `pg_upgrade`, before the first start on 18.
- Rebuild and republish the minimal and populated template packages on PostgreSQL 18: their
  PostgreSQL `.sql` dumps are coupled to the major version they were built and restored against
  (`.github/workflows/build-minimal-template.yml`, `.github/workflows/build-populated-template.yml`),
  and `.github/workflows/scheduled-smoke-test.yml`, which builds the populated template and restores
  it (`verify_restore: true`), must build and restore it on PostgreSQL 18.
- Record the DMS floor in `docs/changelog/8.1.0.md`. The existing "DMS requires PostgreSQL 15 or
  later" breaking-change entry is the Configuration Service's floor (its `NULLS NOT DISTINCT`
  rationale and upgrade instruction are CMS), so attribute it to the Configuration Service and add
  a DMS entry beside it: PostgreSQL 18 or later with a UTF-8 database encoding, which deployments
  it affects (any PostgreSQL server below 18, and any non-UTF-8 database), that existing databases
  must be upgraded or re-created before upgrading DMS, and that a server shared with the
  Configuration Service must meet the DMS floor.
- Publish the PostgreSQL major-upgrade `REINDEX` and collision-detection playbook for every
  `pg_c_utf8` descriptor index.
- Do not add the cross-engine Unicode verdict fixture matrix or the SQL Server `Turkish_100_CS_AS`
  database-default live fixture here. DMS-1455 owns both, because they exercise descriptor probe
  surfaces (`UriLowered` index, write/upsert, resolver, query-filter, and Change Query probes) that
  do not exist until DMS-1448 through DMS-1454 land. This story only documents the expected
  per-engine folding differences in the upgrade playbook.
- Keep legacy descriptor lowercasing and RI resolution in place in this story.

## Acceptance Criteria

- Every PostgreSQL lane whose image is pinned in the repository runs on PostgreSQL 18 or later (the
  CDC lanes follow the repository variable, covered below); no `postgres:16*` or `postgres:17*` image,
  `postgresql16-client` or `postgresql17-client` package, pinned 16.8 digest, or matching fixture
  string remains in workflows, actions, compose files, Dockerfiles (including the `Nuget.Dockerfile`
  variants), CDC qualification and runbook tests, the performance harness and its evidence scripts,
  operator runbooks, design docs that describe the current pin, or test fixtures. Historical
  records are exempt as described above.
- After the merge, a repository administrator has manually set the
  `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` repository variable to a digest-pinned PostgreSQL 18
  image, a second person has manually verified the value, and a `workflow_dispatch` run of
  `nightly-cdc-qualification.yml` with the new value has passed; all three are recorded on the
  merged pull request as closure evidence rather than as a merge gate.
- `docs/OPERATIONS.md` states the DMS PostgreSQL 18 + UTF-8 requirement alongside the Configuration
  Service's own floor, that a server shared by both must meet the DMS floor, and that a PostgreSQL
  16 data volume must be removed or upgraded with `pg_upgrade` before the first start on 18.
- No workflow, action, compose file, init script, or test reads or mounts the pre-18
  `/var/lib/postgresql/data` path; outside the tmpfs mount specifications and their input
  descriptions, none names the versioned data directory literally either (diagnostics and the init
  script resolve it through `$PGDATA`, and the Runbook test obtains it from the container);
  and every tmpfs-backed PostgreSQL container (the CI action and the compose override) holds its
  data on tmpfs.
- A person has manually verified, on the compose stack (`eng/docker-compose/postgresql.yml`)
  running PostgreSQL 18, that `eng/docker-compose/postgresql-init.sh` took effect: `SHOW wal_level`
  returns `logical`, and `pg_hba_file_rules` contains the replication entry for
  `kafka-postgresql-source`. The result is recorded in the pull request. (The CI action and the CDC
  test fixture pass `-c wal_level=logical` themselves, so their lanes would pass even with a broken
  init script.)
- A UTF-8 PostgreSQL 18 database passes both guards; a server reporting a PostgreSQL 17 or 16
  `server_version_num` and a non-UTF8 PostgreSQL 18 database each fail with the documented
  compatibility message, naming both the PostgreSQL 18 and the UTF-8 requirement, before any DDL
  runs. The version cases are pinned by feeding the guard those values through the provisioner's
  connection seam, which the maintenance connection the pre-create checks use must also open
  through, not by running 16 or 17 images; the encoding cases run against a cluster initialized
  with a non-UTF-8 encoding.
- The `CREATE DATABASE` statement the PostgreSQL provisioner emits carries `ENCODING 'UTF8'`.
- With database creation requested, a server reporting a 16 or 17 `server_version_num` and a
  cluster whose `template1` is not UTF-8 each fail with the documented compatibility message (not a
  raw `22023` error), no database is left behind, on both the direct and the CDC
  managed-provisioning paths, and a later managed-provisioning retry on a compliant server succeeds
  without `CdcManagedProvisioningRecoveryException`.
- Template packages are rebuilt on PostgreSQL 18, proven before merge by the pull-request runs of
  `.github/workflows/EdFi.Api.Minimal.Template.PostgreSQL.yml`,
  `.github/workflows/EdFi.Api.Populated.Template.PostgreSQL.yml`, and
  `.github/workflows/scheduled-smoke-test.yml`, which all trigger on the files this story edits and
  build and restore the templates without publishing. The first post-merge `main` runs of the two
  template workflows publishing 18-built packages, and the first scheduled run of the smoke test,
  are closure evidence recorded on the merged pull request.
- The 8.1.0 changelog carries both floors as breaking changes, the Configuration Service's
  PostgreSQL 15 and DMS's PostgreSQL 18 + UTF-8, with the DMS entry naming the affected deployments,
  the upgrade-or-re-create-before-DMS requirement, and the shared-server rule; no release note
  attributes a 15 floor to DMS.
- The upgrade playbook contains the collision-detection query, run under the target major before
  `REINDEX` (against a restored copy or after the binary upgrade), and the post-`REINDEX` validation
  steps for the `pg_c_utf8` descriptor index, with expected output. Executable
  upgrade fixtures are not part of this story; the index arrives with DMS-1448.
- The upgrade playbook documents expected cross-engine Unicode differences as per-engine verdicts,
  never as parity; the executable pins are DMS-1455 acceptance criteria.
