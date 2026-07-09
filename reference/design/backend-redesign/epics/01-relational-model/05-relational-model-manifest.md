---
jira: DMS-934
jira_url: https://edfi.atlassian.net/browse/DMS-934
---

# Story: Emit `relational-model.manifest.json`

## Description

Emit a deterministic, comparable `relational-model.manifest.json` describing the derived relational model inventory used for:

- DDL generation,
- compiled-plan generation (future),
- and verification harness comparisons.

The manifest must be emitted from the unified success artifact (`DerivedRelationalModelArtifact`; see
`reference/design/backend-redesign/design-docs/compiled-mapping-set.md`) so all downstream producers/consumers share the
same finalized model and success diagnostics. SQL Server derivation failure throws
`RelationalModelDerivationException`; no success manifest is emitted.

Important: index and trigger inventories are derived as “DDL intent” and embedded in `DerivedRelationalModelSet` (see `07-index-and-trigger-inventory.md`). The manifest emitter must **not** re-derive indexes/triggers independently from tables/constraints; it must serialize the shared inventories so `relational-model.manifest.json` cannot drift from DDL output.

The manifest is a *semantic* representation (not engine introspection) and must be stable across runs.

## Integration (ordered passes)

- Set-level (`DMS-1033`): the manifest is emitted from the final `DerivedRelationalModelArtifact` after all ordered
  passes have completed. The emitter must serialize shared inventories and artifact diagnostics and must not perform
  independent re-derivation that could drift from the builder output.

## Acceptance Criteria

- Manifest includes, at minimum, stable inventories for:
  - schemas,
  - tables (scope + name),
  - columns (name, type metadata, nullability, key participation),
  - each table's ordered `PersistedOccurrenceIdentity`: ancestor-context and semantic-identity parts, stored physical
    columns, materialized or document-reference-target source roles with `ReferenceSiteId`, relative paths where
    applicable, and stable row-locator columns,
  - constraints (PK/UK/FK/CHECK where applicable),
  - indexes (including FK-supporting indexes),
  - views (abstract union views),
  - triggers (derived maintenance-trigger intent inventory including `name`, `table`, `kind`, `key_columns`,
    `identity_projection_columns`, optional `mirror_stamp_target_table`, and parameter-specific payloads such as
    `target_table`). Identity-value propagation uses finalized FK actions and contributes no trigger payload,
  - intrinsic target lineage inventory/storage, each site's demanded `AnchorSetId`, expanded local/target propagation
    vector, stable `PhysicalForeignKeyId`, and finalized full-composite FK actions,
  - provider-neutral `AnchorOmissionProof` values for every omitted `(ReferenceSiteId, IdentityLineageId)` pair, ordered
    by site and lineage and including exhaustive consumer checks plus mutation-case/composition coverage on both dialects,
  - success-only `SameStatementReferenceResolutionRequirement` values from `artifact.ExecutorRequirements`, ordered by
    resource/site/origin/case and including the retained route, row correlation, and complete future-vector sources,
  - SQL Server-only `MssqlForeignKeyDecision` success diagnostics keyed by stable physical FK id. `NoPropagation`
    decisions include ordered typed certificates for complete mutation cases with changed-target and receiver-carrier
    routes, full selected-vector equality, separate origin/receiver row correlation, presence, timing, and any required
    `SubsetCompositionProof`. Missing composition is `UnprovedSubsetComposition`. PostgreSQL emits no classifier
    decisions or certificates.
- Output is byte-for-byte stable for the same inputs (stable ordering + `\n` line endings).
- Small fixture snapshot tests compare the manifest exactly.

## Tasks

1. Define a stable manifest schema for the derived model (properties and ordering).
2. Implement deterministic ordering rules matching `reference/design/backend-redesign/design-docs/ddl-generation.md`.
3. Emit `relational-model.manifest.json` for fixtures via a shared artifact emitter.
4. Ensure indexes/triggers are serialized from `DerivedRelationalModelArtifact.Model`, provider-neutral omission proofs
   plus SQL Server decisions from `.Diagnostics`, and executor-plan semantic requirements from `.ExecutorRequirements`
   (no divergent derivation logic).
5. Add snapshot/golden tests for at least one small fixture validating exact output.
6. Wire manifest emission to consume the final `DerivedRelationalModelArtifact` produced by the `DMS-1033` ordered-pass
   builder. Keep `RelationalMappingVersion = v1`; no physical-model hash or migration metadata is introduced.
