# Epic: Test Strategy & Migration (Runtime + E2E)

## Description

Update tests and developer workflow to support the relational backend redesign:

- E2E tests provision separate databases/containers per schema (no hot-reload/swap-in-place).
- Add runtime integration tests for write/read/delete correctness using the new relational backend.
- Maintain pgsql + mssql parity testing where applicable.

This epic complements the DDL generator harness epic (`reference/design/backend-redesign/epics/04-verification-harness/EPIC.md`) by covering runtime behavior.

Authorization testing remains out of scope.

## Stories

- `00-e2e-environment-updates.md` — Update docker/E2E workflow for per-schema provisioning
- `01-backend-integration-tests.md` — CRUD integration tests against provisioned DBs (pgsql + mssql)
- `02-parity-and-fixtures.md` — Shared fixtures and parity assertions across engines
- `03-developer-docs.md` — Update docs/runbooks for the new workflow
- `04-descriptor-tests.md` — Descriptor-specific integration coverage (writes, queries, seeding)
