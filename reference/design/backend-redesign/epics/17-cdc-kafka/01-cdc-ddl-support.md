---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Emit/Provision CDC Key and Database Setup Support for `dms.DocumentCache`

## Description

Add provider-specific DDL/provisioning support needed for Debezium to capture `dms.DocumentCache` and publish
delete tombstones keyed by `DocumentUuid`.

`dms.DocumentCache` is physically keyed by `DocumentId`, but the public Kafka key is `DocumentUuid`. CDC setup
must therefore make `DocumentUuid` available in the Debezium key path for create/update/delete records.

## Acceptance Criteria

- PostgreSQL DDL/provisioning supports a `DocumentUuid`-based delete key for `dms.DocumentCache`, preferring
  replica identity on the unique `DocumentUuid` index when viable.
- PostgreSQL setup avoids `REPLICA IDENTITY FULL` unless the unique-index replica identity path is not viable
  for the supported PostgreSQL version or emitted DDL.
- PostgreSQL provisioning can create or validate a publication scoped to `dms.DocumentCache`.
- SQL Server setup can enable CDC for the DMS instance database and for `dms.DocumentCache` only.
- SQL Server setup supports Debezium key-column configuration using `DocumentUuid`.
- Setup artifacts are opt-in for CDC and do not run for ordinary relational provisioning unless CDC is enabled.
- Generated SQL and manifests make CDC setup visible enough for verification and diagnostics.
- Tests verify that delete capture can produce a tombstone key based on `DocumentUuid` for PostgreSQL and SQL
  Server, using provider-specific smoke coverage where available.

## Tasks

1. Identify the exact PostgreSQL table and index names emitted for `dms.DocumentCache`.
2. Add optional PostgreSQL CDC setup SQL for logical replication, publication membership, and replica identity.
3. Add optional SQL Server CDC setup SQL or provisioning commands for database/table CDC enablement.
4. Extend provisioning output/manifests so CDC setup status can be inspected.
5. Add DB-apply smoke tests for the generated CDC setup artifacts.
6. Add negative tests for unsupported or missing `DocumentUuid` key support.

## Out of Scope

- Connector JSON template generation.
- Kafka Connect REST registration.
- Projector implementation.
