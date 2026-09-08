---
jira: DMS-956
jira_url: https://edfi.atlassian.net/browse/DMS-956
---

# Epic: Verification Harness (Determinism + DB-Apply)

## Description

Implement the fixture-driven verification harness described in `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`:

- Determinism tests (hashing, naming, type mapping, ordering).
- Snapshot tests for “small” fixtures (no DB).
- Authoritative “golden” comparisons for real schemas (no DB).
- DB-apply smoke tests using docker compose (pgsql + mssql), including journaling trigger checks.
- Runtime compatibility gate tests (seed hash/count validation; mismatch diagnostics).

Authorization objects remain out of scope.

## Stories

- `DMS-957` — `00-fixture-runner.md` — Fixture layout + runner producing `actual/` outputs
- `DMS-958` — `01-contract-tests.md` — Unit/contract tests for determinism + negative paths
- `DMS-959` — `02-snapshot-tests.md` — Snapshot tests for small fixtures (`*.sql` + manifests)
- `DMS-960` — `03-authoritative-goldens.md` — Authoritative directory diff tests + “bless” mode
- `DMS-961` — `04-db-apply-smoke.md` — Docker compose DB-apply + introspection manifests + trigger smoke
- `DMS-962` — `05-runtime-compatibility-gate.md` — Pack/mapping-set ↔ DB validation gate tests
