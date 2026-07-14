---
jira: DMS-1284
jira_url: https://edfi.atlassian.net/browse/DMS-1284
---

# Story: Run the DMS Docker E2E Suite Against MSSQL

## Description

Make the containerized DMS E2E path engine-aware so the supported MSSQL deployment is exercised through the
same public HTTP and Docker-stack boundary used for PostgreSQL.

Provider-backed backend and in-process API integration tests do not cover compose wiring, CMS datastore
registration, generated-schema provisioning, container health, or public HTTP behavior. This story closes
that boundary without creating a second MSSQL-specific E2E product.

## Design

- Add an explicit `-DatabaseEngine postgresql|mssql` selection to `build-dms.ps1 E2ETest` and the local E2E
  setup path. PostgreSQL remains the default.
- Reuse the local MSSQL stack delivered by `DMS-1238` rather than introducing an independent compose topology.
- Dispatch database provisioning, connection-string construction, data reset, readiness, diagnostics, and
  teardown by engine.
- Provision generated MSSQL DDL, including `dms.EffectiveSchema`, before DMS starts.
- Configure CMS registration, the DMS container, and the test process with the same selected database name and
  engine. Custom database names must not fall back to PostgreSQL defaults.
- Remove Npgsql and PostgreSQL SQL assumptions from the MSSQL path while preserving the PostgreSQL path.
- Treat an unavailable or unhealthy SQL Server as a failed test environment, not an ignored test result.

## E2E Scenario Matrix

The required MSSQL signal covers representative public-boundary scenarios rather than duplicating every
provider-integration case:

| Area | Representative coverage |
| --- | --- |
| Resources | Create/read/update/delete, query, paging, and total count |
| Descriptors and profiles | Descriptor CRUD plus representative read/write profile behavior |
| Authorization | Relationship and NamespaceBased allow/deny behavior with real token and claim-set wiring |
| Change tracking | Conditional requests, change versions, deletes, and key changes |
| Schema variation | Extensions and supported Data Standard versions |
| Identity | Self-contained and Keycloak identity-provider modes |

Split the matrix between required pull-request shards and scheduled broader coverage when runtime demands it,
but keep the complete supported matrix documented and runnable locally.

## Acceptance Criteria

- `build-dms.ps1 E2ETest -DatabaseEngine mssql` performs teardown/setup, provisions an MSSQL E2E database,
  starts CMS and DMS against it, and runs the requested filter.
- Engine-specific connection and reset helpers execute no PostgreSQL provider or SQL code on the MSSQL path.
- The full suite is locally invokable against MSSQL; unsupported scenarios are explicit and justified rather
  than silently skipped.
- Required CI shards cover the documented representative matrix and fail on infrastructure or scenario errors.
- MSSQL container, DMS, and CMS logs plus TRX/timing artifacts follow existing failure-upload conventions.
- Script-level tests cover engine selection, argument forwarding, database-name propagation, and cleanup safety.
- Existing PostgreSQL commands and CI lanes behave unchanged when the engine is omitted or set to `postgresql`.
- E2E documentation includes local commands, environment variables, direct-test requirements, and
  troubleshooting.

## Non-Goals

- Implementing backend defects found by the E2E suite; file or link those defects separately.
- Database-template production or restore (`DMS-1255`, `DMS-1271`).
- Foreign-key pruning.
- Duplicating the full provider-integration matrices from `DMS-1285` or `DMS-1286`.

## Design References

- [`../13-test-migration/EPIC.md`](../13-test-migration/EPIC.md)
- [`../13-test-migration/02-parity-and-fixtures.md`](../13-test-migration/02-parity-and-fixtures.md)
- [`05-mssql-write-path-coverage.md`](05-mssql-write-path-coverage.md)
- [`06-mssql-namespace-authorization-coverage.md`](06-mssql-namespace-authorization-coverage.md)
