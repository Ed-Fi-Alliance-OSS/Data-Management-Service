---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Verify Provider CDC Delete Source-Row Behavior

## Description

Verify that PostgreSQL and SQL Server can support the CDC-mode delete source-row guarantee.

The implementation must prove that, for each supported provider, a delete transaction that materializes a
missing/stale `dms.DocumentCache` source row before deleting `dms.Document` produces an observable
`dms.DocumentCache` row delete that Debezium can turn into a Kafka tombstone keyed by `DocumentUuid`.

## Dependencies

- Depends on `18-06-cdc-pre-delete-materialization.md` and `18-07-projector-stale-write-fencing.md`.
- Coordinates with `17-cdc-kafka/01-cdc-ddl-support.md` for provider-specific replica identity/key setup.
- Blocks final support for delete scenarios in `17-cdc-kafka/04-message-contract-tests.md` and
  `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- PostgreSQL verification proves that a cache row materialized during the delete path is captured as a
  `dms.DocumentCache` delete with a `DocumentUuid` key under the selected Debezium/replica identity setup.
- SQL Server verification proves equivalent behavior under the selected Debezium SQL Server CDC setup.
- Verification covers:
  - delete with an already fresh cache row,
  - delete with a missing cache row,
  - delete with a stale cache row.
- Verification asserts the public delete key is `DocumentUuid`, not `DocumentId`.
- If a provider suppresses the needed materialize-then-delete logical change, CDC readiness fails for that
  provider with a clear diagnostic.
- The story documents any provider-specific constraints, such as requiring a committed source row before
  accepting CDC-mode deletes or using another durable source-row mechanism.
- Test artifacts are reusable by `17-cdc-kafka/04-message-contract-tests.md` where practical.

## Tasks

1. Build provider smoke tests or integration tests for PostgreSQL logical decoding/Debezium delete capture.
2. Build provider smoke tests or integration tests for SQL Server CDC/Debezium delete capture.
3. Add fixture cases for fresh, missing, and stale cache rows.
4. Assert the delete/tombstone key path uses `DocumentUuid`.
5. Wire provider verification status into DocumentCache CDC readiness.
6. Document supported provider behavior and any limitations.

## Out of Scope

- Full connector template generation.
- Full API E2E Kafka scenario replacement.
- Kafka consumer application behavior.
