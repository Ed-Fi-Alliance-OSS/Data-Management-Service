---
jira: DMS-1402
jira_url: https://edfi.atlassian.net/browse/DMS-1402
---

# Epic: DMS Storage Reduction

## Description

A bunch of good storage reduction ideas, in particular the first three are high-impact at reasonable
development cost. Along with the planned removal of `dms.ReferentialIdentity`, we should be able to
get to ODS sizing with just the first three without the redesign required by `dms.Document` removal.

This epic tracks the high-impact, reasonable-cost DMS storage reduction work identified in DMS-1398, with the first three ideas split into implementation stories.

## Design References

- [Natural-key resolution and ReferentialIdentity removal](../../design-docs/natural-key-resolution.md)

## Status

The natural-key/`dms.ReferentialIdentity` removal workstream is filed as fourteen DMS-1402 child
stories, DMS-1443 through DMS-1456. T1–T14 remain stable local rollout aliases matching the file
ordering. Jira carries the same direct dependency chain with `blocks` links.

The other DMS-1398 storage-reduction ideas remain outside this local story set until their approved
scope and Jira children are available. Mapping-pack functionality, DMS-1015, DMS-1016, and the closed
DMS-946 are not included in these stories. In particular, mapping packs / AOT
(`mpack-format-v1.md`, `aot-compilation.md`, epic 05) are out of scope even though this epic
changes the compiled `MappingSet` they serialize; the boundary and E05's alignment obligation are
recorded in [natural-key-resolution.md § Out of scope](../../design-docs/natural-key-resolution.md#out-of-scope).

## Dependency Chain

```text
DMS-1443 (T1) -> DMS-1444 (T2) -> DMS-1445 (T3) -> DMS-1446 (T4)
DMS-1443 (T1) + DMS-1444 (T2) + DMS-1447 (T5) -> DMS-1448 (T6)
DMS-1445 (T3) + DMS-1448 (T6) -> DMS-1449 (T7) -> DMS-1450 (T8)
DMS-1446 (T4) + DMS-1450 (T8) -> DMS-1451 (T9)
DMS-1451 (T9) -> DMS-1452 (T10)
DMS-1451 (T9) -> DMS-1453 (T11)
DMS-1452 (T10) + DMS-1453 (T11) -> DMS-1454 (T12)
DMS-1445 (T3) + DMS-1448 (T6) + DMS-1454 (T12) -> DMS-1455 (T13)
DMS-1454 (T12) + DMS-1455 (T13) -> DMS-1456 (T14)
```

DMS-1447 (T5) may run in parallel with DMS-1443–DMS-1446 (T1–T4). DMS-1455 (T13) Change Query
cutover work may start after DMS-1445 (T3) and DMS-1448 (T6), but the story closes only after
DMS-1454 (T12) because it owns the cross-engine Unicode verdict fixture matrix and the SQL Server
`Turkish_100_CS_AS` live fixture across every descriptor probe surface.

## Release Atomicity

All fourteen stories ship in the same release. Intermediate trunk states between stories are
internal checkpoints only and are never deployed. In particular, the transient RI-hash mismatch
recorded in
[`09-natural-key-resolver-and-core-contract-cutover.md`](09-natural-key-resolver-and-core-contract-cutover.md#known-transient-trunk-state)
is accepted on trunk between DMS-1451 and DMS-1452/DMS-1454.

E2E gating is engine-asymmetric: PostgreSQL runs the full suite, SQL Server runs only the
`@MssqlRepresentative` cross-section. Every E2E scenario added by this epic that must gate SQL Server
carries that tag (DMS-1451, DMS-1453, DMS-1454), engine-divergent verdicts use the `@PostgresqlOnly` /
`@MssqlOnly` categories DMS-1443 introduces or stay at the integration level, and the representative
set grows by roughly half a dozen scenarios as an accepted lane-time cost.

## Stories

- **DMS-1443 (T1)** — [`01-sql-server-identity-collation-contract.md`](01-sql-server-identity-collation-contract.md) — Pin the SQL Server identity collation and runtime equality contract.
- **DMS-1444 (T2)** — [`02-document-resource-invariant-and-abstract-resource-key.md`](02-document-resource-invariant-and-abstract-resource-key.md) — Add the document/resource invariant and abstract `ResourceKeyId`.
- **DMS-1445 (T3)** — [`03-natural-key-probe-metadata.md`](03-natural-key-probe-metadata.md) — Compile natural-key probe metadata.
- **DMS-1446 (T4)** — [`04-probe-based-duplicate-identity-and-constraint-diagnostics.md`](04-probe-based-duplicate-identity-and-constraint-diagnostics.md) — Move duplicate-identity and constraint diagnostics to compiled probes.
- **DMS-1447 (T5)** — [`05-postgresql-17-and-descriptor-collation-upgrade.md`](05-postgresql-17-and-descriptor-collation-upgrade.md) — Raise the PostgreSQL floor and publish the descriptor-collation upgrade contract.
- **DMS-1448 (T6)** — [`06-descriptor-validation-index-and-fk-foundations.md`](06-descriptor-validation-index-and-fk-foundations.md) — Add descriptor validation, index, and foreign-key foundations.
- **DMS-1449 (T7)** — [`07-natural-key-sql-builders-and-cardinality-contracts.md`](07-natural-key-sql-builders-and-cardinality-contracts.md) — Implement natural-key SQL builders and cardinality contracts.
- **DMS-1450 (T8)** — [`08-natural-key-resolver-internal-seam.md`](08-natural-key-resolver-internal-seam.md) — Implement the natural-key resolver behind an internal seam.
- **DMS-1451 (T9)** — [`09-natural-key-resolver-and-core-contract-cutover.md`](09-natural-key-resolver-and-core-contract-cutover.md) — Cut over the resolver, Core contracts, and raw descriptor URI handling.
- **DMS-1452 (T10)** — [`10-post-upsert-natural-key-cutover-and-stored-identity-rebind.md`](10-post-upsert-natural-key-cutover-and-stored-identity-rebind.md) — Cut over POST upsert detection and rebind SQL Server stored identity.
- **DMS-1453 (T11)** — [`11-collection-duplicate-detection-and-conflict-fallback.md`](11-collection-duplicate-detection-and-conflict-fallback.md) — Extend collection duplicate detection and add the generic conflict fallback.
- **DMS-1454 (T12)** — [`12-descriptor-write-cutover-and-uuidv5-cleanup.md`](12-descriptor-write-cutover-and-uuidv5-cleanup.md) — Cut over descriptor writes and remove Core UUIDv5 contracts.
- **DMS-1455 (T13)** — [`13-change-query-descriptor-identity-cutover.md`](13-change-query-descriptor-identity-cutover.md) — Cut over Change Query descriptor identity resolution and own the cross-engine Unicode verdict fixture matrix and `Turkish_100_CS_AS` live fixture.
- **DMS-1456 (T14)** — [`14-remove-referential-identity-infrastructure.md`](14-remove-referential-identity-infrastructure.md) — Remove ReferentialIdentity fixtures, maintenance, and infrastructure.
