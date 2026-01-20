# Epic: Deterministic DDL Emission (PostgreSQL + SQL Server)

## Description

Generate deterministic, cross-engine DDL for the relational primary store, using the derived relational model:

- Emit core `dms.*` objects (tables, sequence, triggers, indexes).
- Emit per-project schemas and per-resource tables (root + collections + `_ext` tables).
- Emit abstract union views.
- Apply the FK index policy and deterministic ordering rules.
- Emit deterministic seed/recording DML (`dms.ResourceKey`, `dms.EffectiveSchema`, `dms.SchemaComponent`).
- Canonicalize SQL text for stable snapshots/goldens.

Authorization objects remain out of scope.

## Stories

- `00-dialect-abstraction.md` — Dialect writer/abstraction (quoting, types, idempotency patterns)
- `01-core-dms-ddl.md` — Generate `dms.*` DDL (incl. update-tracking triggers)
- `02-project-and-resource-ddl.md` — Generate project schemas + resource/extension tables + views
- `03-seed-and-fingerprint-ddl.md` — Seed + fingerprint recording SQL (insert-if-missing + validate)
- `04-sql-canonicalization.md` — SQL canonicalization + deterministic ordering tests
- `05-descriptor-ddl.md` — ODS-parity `dms.Descriptor` DDL (descriptor resources stored in `dms`)
- `06-uuidv5-function.md` — Engine UUIDv5 helper function for deterministic `ReferentialId` recomputation
