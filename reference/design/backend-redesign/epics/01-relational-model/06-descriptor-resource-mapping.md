---
jira: DMS-942
jira_url: https://edfi.atlassian.net/browse/DMS-942
---

# Story: Map Descriptor Resources to `dms.Descriptor` (No Per-Descriptor Tables)

## Description

Treat descriptor resources as a special-case storage shape:

- Descriptor resources are persisted in `dms.Descriptor` (keyed by `dms.Document.DocumentId`).
- There are **no** per-descriptor resource tables (no `{schema}.{DescriptorName}` marker tables).
- Descriptor resource read/write/query plans operate over `dms.Descriptor` joined to `dms.Document` (for `DocumentUuid`, update-tracking stamps, and resource-type discrimination).

This aligns DMS descriptor storage with the Ed-Fi ODS pattern of a single descriptor table while keeping the redesign’s “tables per resource” approach for non-descriptor resources.

## Acceptance Criteria

- The derived relational model identifies descriptor resources from the effective schema and produces a dedicated model/plan shape that targets:
  - `dms.Document` (for `ResourceKeyId`, `DocumentUuid`, stamps), and
  - `dms.Descriptor` (for descriptor fields).
- The model emits no per-project schema tables for descriptor resources.
- In the unified in-memory model (`DerivedRelationalModelSet`; see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`), descriptor resources are represented as concrete resources with storage kind `SharedDescriptorTable`.
- The model defines a deterministic discriminator strategy for descriptor type (must be stable across dialects), using either:
  - `dms.Document.ResourceKeyId` as the primary resource-type discriminator (preferred), and/or
  - `dms.Descriptor.Discriminator` as a secondary/diagnostic discriminator.
- Query compilation for descriptor endpoints maps descriptor queryable fields (e.g., `namespace`, `codeValue`, `effectiveBeginDate`, `effectiveEndDate`) to `dms.Descriptor` columns with root-table-only semantics.
- Model compilation fails fast if the effective schema contains descriptor resources whose JSON shape cannot be represented by the `dms.Descriptor` column contract (missing required columns, unexpected required fields, incompatible types).

## Tasks

1. Add “descriptor resource” detection to schema/model compilation (shared with read/write plan compilation).
2. Define the canonical descriptor column contract derived from the effective schema (and validate compatibility across all descriptor resources).
3. Implement model/plan generation for:
   - GET by id for descriptor resources,
   - query paging/filtering for descriptor resources.
4. Add unit tests for:
   - “no per-descriptor tables” invariant,
   - descriptor query field mapping determinism,
   - fail-fast on incompatible descriptor schema shapes.
