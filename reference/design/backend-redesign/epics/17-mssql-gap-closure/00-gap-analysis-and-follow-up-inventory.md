---
jira: DMS-873
jira_url: https://edfi.atlassian.net/browse/DMS-873
---

# Spike: Inventory MSSQL Implementation and Parity Gaps

## Outcome

DMS has a functioning SQL Server backend. The remaining work is a bounded set of parity, validation,
deployment, and lifecycle gaps rather than a new backend implementation.

`DMS-1125` is the canonical Jira epic for this gap-closing work. `DMS-872` is the legacy epic under which the
spike and several follow-up stories were originally organized. Non-Done work owned by the legacy epic should
move to `DMS-1125`; this repository structure records the target ownership before Jira cleanup is applied.

## Gap Inventory

| Ticket | Gap | Design ownership |
| --- | --- | --- |
| `DMS-1270` | Optional separate CMS database topology is not consistent across local PostgreSQL and MSSQL workflows | [`01-local-database-topology-parity.md`](01-local-database-topology-parity.md) |
| `DMS-1271` | Bootstrap cannot materialize a datastore from a published database-template package before CMS and DMS startup | [`02-database-template-restore-workflow.md`](02-database-template-restore-workflow.md) |
| `DMS-1279` | Local/CI MSSQL remains on SQL Server 2022, and the optional document-cache column needs a gated adopt/defer decision for SQL Server 2025 native `json` | [`03-sql-server-2025-and-native-json.md`](03-sql-server-2025-and-native-json.md) |
| `DMS-1284` | The standard DMS and Instance Management Docker E2E paths are PostgreSQL-specific | [`04-mssql-docker-e2e.md`](04-mssql-docker-e2e.md) |
| `DMS-1285` | Several critical relational write-path correctness and resilience scenarios lack real-MSSQL execution | [`05-mssql-write-path-coverage.md`](05-mssql-write-path-coverage.md) |
| `DMS-1286` | NamespaceBased CRUD authorization lacks broad real-MSSQL provider integration coverage | [`06-mssql-namespace-authorization-coverage.md`](06-mssql-namespace-authorization-coverage.md) |

## Related Work That Keeps Existing Ownership

| Ticket | Ownership decision |
| --- | --- |
| `DMS-1019` | Keep under operational guardrails; it owns cross-engine performance measurement. |
| `DMS-1023` | Keep under test migration; it owns shared fixtures, canonical scenario names, and parity assertions. |
| `DMS-1065` | Keep under authorization follow-up; it owns measured follow-on performance optimization. |
| `DMS-1127` | Keep under update-tracking follow-up; it validates native-cascade stamping and journaling behavior. |
| `DMS-1255` | Keep under bootstrap ownership; it supplies baseline template packages, the shared local-database default, and the `-DbOnly` prerequisite. `DMS-1271` owns the narrow restore-manifest extension. |
| `DMS-1258` | Keep under its existing SQL Server foreign-key-pruning ownership and normative [`sql-server-pruning.md`](../../design-docs/sql-server-pruning.md) design. It must close the pending implementation gap but is not re-owned by this spike. |

Links between these tickets and `DMS-873` remain useful for provenance, but a Jira relation does not imply
that the related ticket must move into `DMS-1125`.

## Sequencing

1. Finish the `DMS-1255` template and `-DbOnly` prerequisite. Continue `DMS-1258` under its existing pruning
   ownership in parallel; MSSQL parity cannot be declared closed while that implementation remains pending.
2. Close local topology and template-restore workflow gaps (`DMS-1270`, `DMS-1271`).
3. Upgrade the MSSQL runtime and make a deliberate native-JSON adopt/defer decision (`DMS-1279`); deferral does
   not block the runtime upgrade.
4. Establish MSSQL lanes for both the standard DMS and Instance Management Docker E2E suites (`DMS-1284`).
5. Close provider-backed write and NamespaceBased authorization matrices (`DMS-1285`, `DMS-1286`), using
   E2E for representative public-boundary confidence rather than duplicating every integration scenario.

Items may overlap when their prerequisites are satisfied, but each ticket must retain its documented test
boundary and non-goals.

## Spike Exit Criteria

- Every identified non-Done gap has one owning Jira ticket and one discoverable design document.
- `DMS-1125` is the target epic for MSSQL gap-closing work that has no more specific active epic.
- Cross-epic dependencies and exclusions are explicit.
- Jira parent changes, status changes, and description updates are performed only after human approval.
- Follow-up tickets contain enough acceptance criteria to distinguish environment failures from product
  failures and to prevent PostgreSQL regressions.
