---
jira: DMS-1127
jira_url: https://edfi.atlassian.net/browse/DMS-1127
---

# Story: Validate SQL Server Fallback-Managed Propagation Preserves Update Tracking

## Description

Per `reference/design/backend-redesign/design-docs/mssql-cascading.md`, SQL Server fallback-managed identity propagation in v1 depends on downstream `AFTER` triggers remaining enabled so referrer-table stamping and journaling still fire after fallback updates copied identity columns.

This story adds the explicit E10 update-tracking contract and SQL Server verification for that behavior:

- fallback-managed identity propagation must still bump `dms.Document.ContentVersion` and `dms.Document.ContentLastModifiedAt` for impacted referrers,
- the resulting `ContentVersion` changes must still emit `dms.DocumentChangeEvent` rows,
- coverage must include at least one non-root fallback referrer, and
- SQL Server environments that disable `nested triggers` are unsupported for this path and must fail verification with a clear diagnostic.

This story is the update-tracking slice of the broader MSSQL cascade revision; it does not own the final hybrid cascade classification algorithm. That ownership sits with `reference/design/backend-redesign/epics/09-identity-concurrency/03a-mssql-hybrid-cascade-classification.md` (`DMS-1128`).

## Acceptance Criteria

- In a SQL Server fixture that requires fallback-managed propagation, an upstream identity update updates the impacted referrer documents' stored representation stamps and journal rows in the same transaction.
- Coverage includes at least one non-root fallback referrer so downstream `AFTER` trigger fan-out is proven beyond root tables.
- The SQL Server smoke or integration workflow asserts the server `nested triggers` option is enabled before running fallback-managed propagation scenarios and fails with an actionable diagnostic when it is disabled.
- Update-tracking docs and runbooks explicitly state that SQL Server fallback-managed propagation depends on nested `AFTER` triggers for downstream stamping and journaling, and that this prerequisite does not make uncovered cyclic fallback safe.
- For the covered scenario, PostgreSQL and SQL Server produce equivalent update-tracking outcomes for the affected documents.

## Tasks

1. Add a SQL Server fallback-propagation fixture that exercises a fallback-required edge and at least one non-root impacted referrer.
2. Extend smoke or integration helpers to assert the SQL Server `nested triggers` server option before executing the fallback scenario.
3. Add assertions that fallback-managed propagation updates `dms.Document.ContentVersion` and `dms.Document.ContentLastModifiedAt` and emits `dms.DocumentChangeEvent` rows for each impacted referrer document.
4. Add a negative-path verification or harness diagnostic for disabled `nested triggers`.
5. Update update-tracking-related docs to record the SQL Server runtime prerequisite and the scope of that guarantee.
