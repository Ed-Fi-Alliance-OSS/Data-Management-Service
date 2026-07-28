---
jira: TBD
jira_url: TBD
---

# Story: Emit the SQL Server Descriptor Identity Index

## Description

DMS-1185 keeps the live `(Discriminator, Namespace, CodeValue)` descriptor identity index deferred on PostgreSQL from its provider-specific evidence and selects it for SQL Server from the spike's isolated comparison.
The final SQL Server Grade probe recorded a `0.0206` median paired elapsed ratio and `0.1044` logical-read ratio; the shared-descriptor probe recorded `0.0405` and `0.0357`, respectively.
Both comparisons matched counts and passed the spike's noise and regression gates.

This story implements that SQL-only decision without expanding the unrelated tracked-index story.

## Acceptance Criteria

- Consume the reviewed DMS-1185 result for the two required SQL Server descriptor-probe shapes: descriptor `/deletes` and regular-resource Grade `/deletes`.
- Emit the live `(Discriminator, Namespace, CodeValue)` index for SQL Server only through the dialect-aware index inventory.
- Cover inventory derivation, SQL Server DDL and manifest output, upgrade behavior, deterministic naming, a `RelationalMappingVersion` bump, and golden regeneration.
- Add a SQL Server integration plan assertion proving that each descriptor identity probe uses the new index without key lookups.
- Rerun the isolated comparison against the exact implemented DDL with the spike's warm-up, repetition, ordering, count, and noise controls; both shapes must remain at or below the `1.20` elapsed regression ceiling and at least one must retain a 20% elapsed or logical-read improvement.
- A failed exact-DDL gate returns the implementation for design review; it does not silently defer or waive the spike's decision.
- PostgreSQL behavior is unchanged.
