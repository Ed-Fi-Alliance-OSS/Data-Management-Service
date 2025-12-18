# Backend Redesign 1 vs Relational Flattening Design

This document summarizes how `BACKEND-REDESIGN-1.md` differs from the earlier projection-oriented approach described in `reference/design/flattening-metadata-design.md`.

## Executive Summary

- **Flattening design**: relational tables are an *auxiliary projection* alongside the canonical JSON document store (`dms.Document`), primarily to enable relational analytics/joins while keeping documents as the source of truth.
- **Backend Redesign 1**: relational tables are the **canonical** store; JSON is **optional** (cache/materialization only). Keys and relationships are redesigned to remove the “document store indirection” from most relational relationships.

## Table Shape and Relationships

### Canonical storage

- **Flattening design**
  - Canonical data lives in `dms.Document(EdfiDoc JSONB)` (and `dms.Alias`, `dms.Reference`).
  - Flattened tables include `Document_Id` (+ `Document_PartitionKey`) as a backlink to the canonical document row.
- **Backend Redesign 1**
  - Canonical data lives in relational tables keyed by `DocumentId`.
  - Full JSON is optional via `dms.DocumentCache` (optimization/integration), not required for correctness.

### Root table primary key strategy

- **Flattening design**
  - Resource root tables have a *resource-local surrogate* primary key (`Id BIGSERIAL` / `BIGINT IDENTITY`).
  - They also store `Document_Id` (and partition key) to link back to `dms.Document`.
  - Natural keys typically have `UNIQUE(...)` constraints.
- **Backend Redesign 1**
  - Resource root tables use `DocumentId BIGINT` as the **primary key** and **FK** to `dms.Document(DocumentId)`.
  - There is no separate resource-local surrogate for the root row; `DocumentId` is the shared surrogate across the model.
  - Natural keys are still enforced with `UNIQUE(...)`.

### Child tables for collections (arrays)

- **Both approaches**
  - Use separate child tables per collection, recursively for nested arrays.
- **Flattening design**
  - Child tables typically FK to the parent table’s surrogate key (e.g., `StudentAddress.Student_Id → Student.Id`).
  - Ordering is not explicitly called out as a required mechanism (often implied/ignored unless stored).
- **Backend Redesign 1**
  - First-level collection tables FK to the root `DocumentId` (e.g., `ParentDocumentId → Student.DocumentId`) and include `Ordinal INT NOT NULL` to preserve array order for lossless reconstitution.
  - Deeper nesting uses a child-table surrogate `Id` to support stable joins for nested collections.

## References and Reference Validation

### How references are represented

- **Flattening design**
  - References in flattened tables are typically FKs to the target table’s surrogate key (`..._Id BIGINT REFERENCES edfi.School(Id)`), matching ODS/API style.
  - Resolving an Ed-Fi reference (natural key/URI) into that surrogate requires multi-step lookup (conceptually: `ReferentialId → Alias/Document → target table via Document_Id → surrogate Id`).
- **Backend Redesign 1**
  - References are stored as `..._DocumentId BIGINT` FKs to the **target resource table’s `DocumentId`** (e.g., `SSA.Student_DocumentId → edfi.Student.DocumentId`).
  - Reference resolution is direct: `ReferentialId → dms.Identity → DocumentId`, then insert the FK.
  - This eliminates the extra “target surrogate Id” hop for most relationships.

### What enforces referential integrity

- **Flattening design**
  - Core referential integrity is handled by the three-table system: `dms.Reference` plus a foreign key to `dms.Alias` (and the Alias uniqueness constraint).
  - Flattened-table FKs enforce integrity only if you also resolve and store surrogate IDs correctly.
- **Backend Redesign 1**
  - Referential integrity is enforced by **real FK constraints** between resource tables (and `dms.Descriptor` for descriptor refs).
  - Write-time validation still occurs (because references must be resolved from `ReferentialId`), but the DB becomes the ultimate enforcement mechanism.

### Identity updates (natural key changes)

- **Flattening design**
  - Because the canonical representation is JSON and references are identity-based, identity changes can trigger cascade update behavior to keep embedded reference identities consistent.
- **Backend Redesign 1**
  - References are stored by `DocumentId`, so identity updates only require updating `dms.Identity` (and any unique natural-key columns); referencing rows remain valid without rewriting.
  - Reconstitution always uses *current* referenced natural keys to rebuild reference objects.

### Polymorphic references

- **Flattening design**
  - Uses `dms.Alias` + `dms.Document` to determine the concrete type then join to the appropriate concrete table to get the surrogate key.
- **Backend Redesign 1**
  - Baseline: FK to `dms.Document(DocumentId)` for existence + application-level membership enforcement.
  - Optional: add discriminator or membership tables/triggers for DB-level enforcement of “allowed concrete types”.

## Descriptors

- **Flattening design**
  - Proposes a unified `dms.Descriptor` table with its own surrogate `Id`, and flattened tables FK to `dms.Descriptor(Id)`.
- **Backend Redesign 1**
  - Descriptors are still unified, but the recommended key is the descriptor document’s `DocumentId` (i.e., `dms.Descriptor(DocumentId)`), aligning with the “DocumentId everywhere” model.
  - Descriptor refs can be enforced as:
    - FK to the specific descriptor resource table (type-safe), or
    - FK to `dms.Descriptor(DocumentId)` (descriptor-only).

## Queries

- **Flattening design**
  - Focuses on DDL generation and relational projection; query mechanics are not the primary driver.
  - Separate `DocumentIndex` proposal exists for JSON-based pagination/filtering.
- **Backend Redesign 1**
  - Introduces an engine-neutral option: `dms.QueryIndex` (typed EAV index for declared `queryFieldMapping` fields) to keep querying metadata-driven without relying on PostgreSQL-only JSON operators.

## Migration Model

- **Flattening design**
  - Assumes a stable canonical document store; flattening can be layered on.
- **Backend Redesign 1**
  - Schema changes require database migration (explicitly acceptable).
  - DDL is derived from `ApiSchema` + conventions + minimal overrides (no per-resource code generation).

## Implications

- **Backend Redesign 1 simplifies relationships** (one consistent key, fewer lookup hops) and **removes the need for `dms.Reference`** as a referential integrity mechanism.
- It **increases the importance of reconstitution performance** when JSON cache is disabled, because GET/query responses must be assembled from relational rows.

