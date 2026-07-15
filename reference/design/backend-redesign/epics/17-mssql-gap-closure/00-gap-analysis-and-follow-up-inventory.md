---
jira: DMS-873
jira_url: https://edfi.atlassian.net/browse/DMS-873
---

# Inventory MSSQL Implementation and Parity Gaps

## Outcome

DMS and CMS both support PostgreSQL and SQL Server. The remaining work is a bounded set of MSSQL parity,
validation, deployment, and lifecycle gaps rather than a new backend implementation. This spike inventories
those gaps and records their owning tickets, dependencies, and scope boundaries.

`DMS-1125` is the canonical Jira epic for MSSQL gap-closing work that does not have a more specific active home.
All seven work items originally listed in this design—`DMS-873`, `DMS-1270`, `DMS-1271`, `DMS-1279`,
`DMS-1284`, `DMS-1285`, and `DMS-1286`—are already children of `DMS-1125`. The parent cleanup is complete;
no reparenting remains pending for those items.

## Gap Inventory

| Ticket | Gap | Design ownership |
| --- | --- | --- |
| `DMS-1270` | Optional separate CMS database topology is not consistent across local PostgreSQL and MSSQL workflows | [`01-local-database-topology-parity.md`](01-local-database-topology-parity.md) |
| `DMS-1271` | Bootstrap cannot materialize a datastore from a published database-template package before CMS and DMS startup | [`02-database-template-restore-workflow.md`](02-database-template-restore-workflow.md) |
| `DMS-1279` | Local/CI MSSQL remains on SQL Server 2022, and the optional document-cache column needs a gated adopt/defer decision for SQL Server 2025 native `json` | [`03-sql-server-2025-and-native-json.md`](03-sql-server-2025-and-native-json.md) |
| `DMS-1284` | The standard DMS and Instance Management Docker E2E paths are PostgreSQL-specific | [`04-mssql-docker-e2e.md`](04-mssql-docker-e2e.md) |
| `DMS-1285` | Several critical relational write-path correctness and resilience scenarios lack real-MSSQL execution | [`05-mssql-write-path-coverage.md`](05-mssql-write-path-coverage.md) |
| `DMS-1286` | NamespaceBased CRUD authorization lacks broad real-MSSQL provider integration coverage | [`06-mssql-namespace-authorization-coverage.md`](06-mssql-namespace-authorization-coverage.md) |
| `DMS-1289` | Scheduled smoke tests do not exercise the complete MSSQL template build, restore, registration, and API/SDK smoke path | [Detailed Jira scope and acceptance criteria](https://edfi.atlassian.net/browse/DMS-1289) |

`DMS-1289` belongs in this inventory because scheduled release-confidence coverage is distinct from the Docker
E2E boundary in `DMS-1284` and the provider-integration matrices in `DMS-1285` and `DMS-1286`.

Each implementation item retains its own acceptance criteria and non-goals. `DMS-873` owns only this inventory
and its scope boundaries; it does not re-own implementation assigned to the follow-up tickets.

## Delivered Prerequisite

`DMS-1255` is complete. It delivered the baseline MSSQL database-template packages, shared local-database
default, `-DbOnly` startup slice, and engine-aware template build/restore foundations consumed by later work.
Treat it as a delivered prerequisite and provenance link for `DMS-1270`, `DMS-1271`, `DMS-1279`, and
`DMS-1289`, not as an active blocker.

## Related Work That Keeps Existing Ownership

| Ticket | Ownership decision |
| --- | --- |
| `DMS-1019` | Keep under operational guardrails; it owns cross-engine performance measurement. |
| `DMS-1023` | Keep under test migration; it owns shared fixtures, canonical scenario names, and parity assertions. |
| `DMS-1065` | Keep under authorization follow-up; it owns measured follow-on performance optimization. |
| `DMS-1127` | Keep under update-tracking follow-up; it validates native-cascade stamping and journaling behavior. |
| `DMS-1258` | Keep under its existing SQL Server foreign-key-pruning ownership and normative [`sql-server-pruning.md`](../../design-docs/sql-server-pruning.md) design. It must close the pending implementation gap but is not re-owned by this spike. |

Links between these tickets and `DMS-873` remain useful for provenance but do not transfer implementation
ownership.

## Active Dependencies

| Blocker | Blocked ticket | Reason |
| --- | --- | --- |
| `DMS-1258` | `DMS-1127` | Native-cascade update-tracking validation requires the finalized SQL Server foreign-key-pruning behavior. |
| `DMS-1270` | `DMS-1271` | Restore cannot close its shared/separate topology acceptance matrix until the separate-CMS contract exists. |
| `DMS-1023` | `DMS-1285` | The write-path matrix consumes the canonical scenario catalog, fixtures, and assertion contract from `DMS-1023`. |

These are completion dependencies, not a requirement to serialize all implementation work. `DMS-1284`,
`DMS-1286`, and `DMS-1289` have no open hard blocker in this inventory. `DMS-1258` and `DMS-1127` retain their
existing epic ownership while remaining on the MSSQL parity-closure path.

## Sequencing

1. Continue `DMS-1258` under its existing pruning ownership; complete `DMS-1127` after the finalized pruning
   behavior is available.
2. Close local topology and template-restore workflow gaps (`DMS-1270`, `DMS-1271`) using the foundation
   delivered by `DMS-1255`.
3. Upgrade the MSSQL runtime and make a deliberate native-JSON adopt/defer decision (`DMS-1279`); deferral does
   not block the runtime upgrade.
4. Establish MSSQL lanes for both the standard DMS and Instance Management Docker E2E suites (`DMS-1284`).
5. Close provider-backed write and NamespaceBased authorization matrices (`DMS-1285`, `DMS-1286`), using
   E2E for representative public-boundary confidence rather than duplicating every integration scenario.
6. Add the full MSSQL scheduled smoke matrix (`DMS-1289`) using the template and engine-selection foundations
   delivered by `DMS-1255`.

Items may overlap when their prerequisites are satisfied, but each ticket must retain its documented test
boundary and non-goals.

## Scope Guardrails and Exit Criteria

- Do not describe this work as adding SQL Server support; both DMS and CMS already support SQL Server.
- Preserve PostgreSQL behavior unless a shared defect requires an intentional cross-engine change.
- Validate provider-specific claims against a real SQL Server; SQL-shape tests alone are not sufficient for
  provider behavior.
- Keep CMS and DMS persistence responsibilities separate while allowing bootstrap and topology work to
  coordinate both services.
- Keep performance measurement and speculative optimization in their existing owning tickets.
- Every identified non-Done gap has one owning Jira ticket, explicit dependencies and exclusions, and enough
  acceptance criteria to distinguish environment failures from product failures. Detailed implementation scope
  is discoverable through either the linked repository design or the owning Jira ticket.
