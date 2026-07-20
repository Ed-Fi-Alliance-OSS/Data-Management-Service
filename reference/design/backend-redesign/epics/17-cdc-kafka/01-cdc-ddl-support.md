---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Emit/Provision Two-Table CDC Key and Database Support

## Description

Add provider-specific DDL/provisioning for one Debezium connector to capture
`dms.DocumentCache` upserts and authoritative `dms.Document` deletes with the shared
`DocumentUuid` key.

Both tables are physically keyed by `DocumentId`, so CDC setup must explicitly make
`DocumentUuid` the connector key. PostgreSQL must make that non-primary-key column
available in `dms.Document` delete records.

## Acceptance Criteria

- PostgreSQL provisioning creates or validates a publication scoped to
  `dms.DocumentCache` and `dms.Document`.
- PostgreSQL CDC setup sets `dms.Document` to `REPLICA IDENTITY FULL` and validates that
  `DocumentUuid` is present in delete events.
- SQL Server setup enables CDC on both captured tables and includes `DocumentUuid` in the
  capture instances.
- Connector key configuration can use `DocumentUuid` for both tables on both providers.
- Setup artifacts are opt-in and do not run for ordinary relational provisioning unless
  CDC is enabled.
- Generated SQL/manifests expose both captured tables, key setup, publication/capture
  instance, and replica identity for diagnostics.
- Provider smoke tests prove a `dms.Document` delete can produce a `DocumentUuid`-keyed
  source record.
- Provider smoke tests prove cache deletion is distinguishable and available for the
  connector pipeline to drop; they do not require materialize-then-delete behavior.

## Tasks

1. Identify exact provider table/index identifiers for `dms.Document` and
   `dms.DocumentCache`.
2. Add optional PostgreSQL publication and `REPLICA IDENTITY FULL` setup.
3. Add optional SQL Server database/table CDC setup for both tables.
4. Extend provisioning output/manifests with two-table CDC setup status.
5. Add DB-apply and delete-key smoke tests for both providers.
6. Add negative tests for missing `DocumentUuid`/replica/capture support.

## Out of Scope

- Connector JSON template generation.
- Kafka Connect REST registration.
- Projector implementation.
