---
jira: DMS-943
jira_url: https://edfi.atlassian.net/browse/DMS-943
---

# Story: Emit ODS-Parity `dms.Descriptor` DDL (Descriptor Resources Stored in `dms`)

## Description

Update core DDL emission for `dms.Descriptor` so that:

- `dms.Descriptor` is the single storage table for descriptor resources (no per-descriptor tables), and
- the column set supports the descriptor resource API shape used by DMS (ODS-parity descriptor fields).

This story is intentionally separate from generic core-table emission so descriptor schema decisions (columns + uniqueness) are explicit and testable.

## Acceptance Criteria

- Generated DDL for `dms.Descriptor` includes the agreed descriptor field set required by descriptor resources, at minimum:
  - `Namespace`, `CodeValue`, `ShortDescription`, `Description`
  - `EffectiveBeginDate`, `EffectiveEndDate`
  - `Discriminator` (descriptor type discriminator / diagnostics)
  - `Uri` (derived from `Namespace` + `#` + `CodeValue`, normalized per Core rules)
- DDL includes deterministic uniqueness/indexing consistent with the chosen ODS-parity rules (e.g., unique key on `(Namespace, CodeValue)` and/or `Uri`), and is identical in intent across PostgreSQL and SQL Server.
- `dms.Descriptor` remains keyed by `DocumentId` (FK to `dms.Document` with `ON DELETE CASCADE`).
- Snapshot tests cover the emitted DDL for `dms.Descriptor` on both dialects.

## Tasks

1. Decide and document the descriptor uniqueness rule(s) (ODS parity vs allowing cross-type URI collisions), and encode them in generator output.
2. Extend the dialect writer/type mapper to support any new descriptor column types (dates, nullable rules) deterministically.
3. Update core DDL emission to emit the updated `dms.Descriptor` definition (and any required indexes).
4. Add snapshot coverage for descriptor DDL changes (pgsql + mssql).
