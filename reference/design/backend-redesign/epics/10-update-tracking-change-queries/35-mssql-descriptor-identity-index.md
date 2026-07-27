---
jira: TBD
jira_url: TBD
---

# Story: Finalize the SQL Server Descriptor Identity-Index Disposition

## Description

DMS-1185 re-deferred the live `(Discriminator, Namespace, CodeValue)` descriptor identity index on PostgreSQL after an isolated comparison found no observable benefit.
Story 33's evidence phase owns the equivalent SQL Server comparison while holding its tracked Tier-1 candidate fixed.
This story consumes that result so the SQL Server decision has an explicit implementation owner without expanding the tracked-index story.

## Acceptance Criteria

- Consume the reviewed Story 33 result for the two required SQL Server descriptor-probe shapes: descriptor `/deletes` and a regular resource `/deletes` whose identity includes a descriptor.
- If Story 33 blocks Tier-1 emission, rerun the descriptor comparison while holding the actual unchanged production DDL fixed; a comparison made only with rejected candidate Tier-1 DDL is not dispositive.
- If neither median elapsed time nor logical reads improves by at least 20%, record the SQL Server deferral in `change-queries.md` and close with no production change.
- If either probe improves by at least 20% and no measured read shape exceeds the 1.20 regression ceiling, emit the live `(Discriminator, Namespace, CodeValue)` index for SQL Server only through the dialect-aware index inventory.
- When emission is selected, cover inventory derivation, SQL Server DDL and manifest output, upgrade behavior, deterministic naming, a `RelationalMappingVersion` bump, golden regeneration, and an integration plan assertion that the descriptor identity probe uses the index.
- PostgreSQL behavior is unchanged.
