---
jira: DMS-1289
jira_url: https://edfi.atlassian.net/browse/DMS-1289
---

# Story: Add MSSQL Coverage to Scheduled Smoke Tests

## Description

`DMS-1255` added MSSQL parity to the Minimal and Populated database-template build workflows, including native
`.bak` creation and restore verification. The scheduled smoke workflow still builds and exercises only
PostgreSQL smoke templates for Data Standard 5.2 and 6.1.

Add MSSQL to the existing scheduled smoke workflow while retaining PostgreSQL coverage. Exercise the complete
engine-native path: build a smoke template, restore it, start DMS, register the restored database, and run the
API and SDK smoke tests.

## Dependencies

- Delivered prerequisite: `DMS-1255` supplies the MSSQL template build, backup/restore, and local
  engine-selection foundations. Retain the Jira relationship for provenance rather than as an active blocker.
- Keep this work separate from `DMS-1284`, which owns the standard DMS and Instance Management Docker E2E
  suites rather than the scheduled template and API/SDK smoke workflow.

## Design

- Extend `.github/workflows/scheduled-smoke-test.yml` with a database-engine dimension.
- Cover PostgreSQL and MSSQL for Data Standard 5.2 and 6.1.
- Build an engine-native smoke template for each selected leg and consume that artifact in the matching smoke
  job.
- Keep smoke packages workflow-local with `publish_package: false`.
- Allow correlated MSSQL smoke inputs in `Assert-TemplateWorkflowInputs.ps1` while retaining engine, package,
  environment-file, Data Standard, restore, and non-publishing validation.
- Start DMS through the selected `-DatabaseEngine` path.
- Restore PostgreSQL `.sql` and MSSQL `.bak` artifacts through the engine-aware template restore helper.
- Register the restored smoke database with an engine-correct connection string.
- Run the existing applicable smoke coverage on both engines:
  - NonDestructive API tests.
  - NonDestructive DMS SDK tests.
  - Destructive DMS SDK tests.
  - ODS SDK tests remain limited to Data Standard 5.2 because the consumed ODS SDK is Data Standard
    5.2-specific.
- Preserve strict failure behavior: do not ignore MSSQL failures or convert a nonzero smoke-test result into
  success.
- Include engine and Data Standard in job, artifact, log, and summary names.
- Update result notifications to report PostgreSQL and MSSQL rather than a single backend path.

## Pull-Request Filtering

- Keep the workflow path-filtered so unrelated pull requests do not run the smoke pipeline.
- Include shared smoke/template files and MSSQL-specific environment, Compose, start, build, and restore paths
  that can affect this flow.
- On pull requests, run only the engine/Data Standard legs affected by the change when that can be determined
  safely.
- Shared or ambiguous changes run both engines rather than risking a coverage gap.
- Scheduled and `workflow_dispatch` runs always execute the full PostgreSQL/MSSQL and Data Standard 5.2/6.1
  matrix.

## Acceptance Criteria

- Scheduled and manual runs execute four smoke legs: PostgreSQL 5.2, PostgreSQL 6.1, MSSQL 5.2, and MSSQL 6.1.
- Every leg restores the engine-native smoke artifact built in the same workflow run.
- Smoke artifacts are not published to Azure Artifacts.
- MSSQL registration uses a SQL Server connection string targeting the restored smoke database; PostgreSQL
  behavior remains unchanged.
- Health checks, credential creation, SDK generation, NonDestructive API tests, NonDestructive DMS SDK tests,
  and Destructive DMS SDK tests pass on both engines.
- ODS SDK smoke tests run only on the Data Standard 5.2 legs for both engines.
- Any unexpected smoke-test failure fails its matrix leg.
- Pull requests outside the relevant path set do not trigger the workflow.
- Relevant pull requests run the safely affected legs; shared or ambiguous changes run both engines.
- Scheduled and manual runs are never reduced by pull-request filtering logic.
- Validation tests cover valid MSSQL smoke tuples, invalid engine/package/version combinations, and rejection
  of publishing smoke packages.
- Failure logs and summaries identify engine and Data Standard without exposing credentials or
  connection-string secrets.
- Existing PostgreSQL smoke coverage remains passing.

## Non-Goals

- Publishing smoke-template packages.
- Replacing or rewriting the smoke-test console.
- Adding prerelease-specific execution.
- Broadly suppressing backend-specific failures.

## Design References

- [`../16-bootstrap/EPIC.md`](../16-bootstrap/EPIC.md)
- [`02-database-template-restore-workflow.md`](02-database-template-restore-workflow.md)
- [`04-mssql-docker-e2e.md`](04-mssql-docker-e2e.md)
