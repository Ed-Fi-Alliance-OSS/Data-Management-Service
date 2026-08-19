---
jira: DMS-1447
jira_url: https://edfi.atlassian.net/browse/DMS-1447
epic: DMS-1402
---

# Story: Raise the PostgreSQL Floor and Publish the Descriptor-Collation Upgrade Contract

## Outcome

Establish PostgreSQL 17 and its `pg_c_utf8` collation as supported platform foundations before the
descriptor index and natural-key resolver depend on them.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- No dependency on another story in this epic.
- May run in parallel with DMS-1443–DMS-1446.
- Blocks DMS-1448.

## Implementation Scope

- Raise DMS CI, compose/developer images, supported-version documentation, and provisioning guards to
  PostgreSQL 17 or later.
- Guard both platform preconditions of `pg_c_utf8` in SchemaTools before any DDL is emitted:
  `server_version_num >= 170000` **and** a UTF-8 database encoding
  (`pg_encoding_to_char(encoding) = 'UTF8'` for the target row in `pg_database`). Either failure
  produces the documented compatibility message naming both requirements; a non-UTF8 database on
  PostgreSQL 17 must not surface later as `collation "pg_c_utf8" for encoding "…" does not exist`
  on the descriptor index.
- When SchemaTools creates the database itself, emit `CREATE DATABASE … ENCODING 'UTF8'`. Do not
  attempt `TEMPLATE template0` / locale-provider selection to force UTF-8 onto a cluster whose
  `template1` is not UTF-8; that remains an operator precondition, and the encoding guard reports
  it.
- Sweep every pinned PostgreSQL 16 site; the inventory on the branch at authoring time is:
  `.github/workflows/on-dms-pullrequest.yml` (`POSTGRES_INTEGRATION_IMAGE`, feeds the three
  integration jobs through `start-postgresql-test-container`), `.github/workflows/on-config-pullrequest.yml`
  (CMS CI service image), `eng/docker-compose/postgresql.yml` (digest-pinned image),
  `eng/azure-vm/compose/docker-compose.yml`, `src/dms/Dockerfile` and `src/config/Dockerfile`
  (`postgresql16-client` → `postgresql17-client`; a 16 `pg_dump` refuses a 17 server),
  `eng/docker-compose/tests/CmsDatabaseTopology.Tests.ps1` fixtures hard-coding `postgres:16`,
  and `docs/RUNNING-LOCALLY.md` / supported-version documentation.
- Rebuild and republish the minimal and populated template packages on PostgreSQL 17: their
  PostgreSQL `.sql` dumps are coupled to the major version they were built and restored against
  (`build-minimal-template.yml`, `build-populated-template.yml`), and the version-coupled E2E lane
  in `on-dms-pullrequest.yml` must restore a 17-built dump.
- Publish the PostgreSQL major-upgrade `REINDEX` and collision-detection playbook for every
  `pg_c_utf8` descriptor index.
- Do not add the cross-engine Unicode verdict fixture matrix or the SQL Server `Turkish_100_CS_AS`
  database-default live fixture here. DMS-1455 owns both, because they exercise descriptor probe
  surfaces (`UriLowered` index, write/upsert, resolver, query-filter, and Change Query probes) that
  do not exist until DMS-1448 through DMS-1454 land. This story only documents the expected
  per-engine folding differences in the upgrade playbook.
- Keep legacy descriptor lowercasing and RI resolution in place in this story.

## Acceptance Criteria

- All PostgreSQL lanes run on PostgreSQL 17 or later; no `postgres:16*` image, `postgresql16-client`
  package, or `postgres:16` fixture string remains in workflows, actions, compose files, Dockerfiles,
  or test fixtures.
- A UTF-8 PostgreSQL 17 database passes both guards; a PostgreSQL 16 server and a non-UTF8
  PostgreSQL 17 database each fail with the documented compatibility message before any DDL runs.
- Template packages are rebuilt on PostgreSQL 17 and the version-coupled E2E lane restores them.
- Upgrade fixtures exercise pre/post-`REINDEX` validation and collision reporting.
- The upgrade playbook documents expected cross-engine Unicode differences as per-engine verdicts,
  never as parity; the executable pins are DMS-1455 acceptance criteria.
