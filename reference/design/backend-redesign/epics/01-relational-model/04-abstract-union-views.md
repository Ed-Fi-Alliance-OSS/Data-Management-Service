---
jira: DMS-933
jira_url: https://edfi.atlassian.net/browse/DMS-933
---

# Story: Derive Abstract Identity Table + Union View Models

## Description

Model abstract-resource artifacts per `reference/design/backend-redesign/design-docs/data-model.md` and `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`:

- Required: abstract identity tables (`{schema}.{AbstractResource}Identity`)
- Required: abstract union views (`{schema}.{AbstractResource}_View`)

- Use `projectSchema.abstractResources[*].identityJsonPaths` order as the select-list contract.
- Determine participating concrete resources using `isSubclass`/superclass metadata.
- Handle identity rename cases for subclasses.
- Derive the full representable intrinsic reference-backed identity-lineage inventory for each abstract target,
  independent of current incoming-reference demand.
- Publish one ordered `AbstractIdentityMemberMapping` per participating concrete member. The mapping owns public identity
  expressions, intrinsic-anchor expressions, concrete/abstract `DocumentId` row correlation, discriminator, and the
  actual later maintenance-statement boundary used by both trigger derivation and SQL Server value-flow analysis.
- Choose canonical SQL types for union columns and apply explicit casts per dialect.
- Ensure deterministic `UNION ALL` arm ordering and select-list ordering.

## Integration (ordered passes)

- Set-level (`DMS-1033`): implemented as a whole-schema pass that scans the effective schema set to discover abstract resources, their participating concrete members, and the required identity field contracts. The pass produces abstract identity-table and union-view models that other passes can reference when binding polymorphic document references.

## Acceptance Criteria

- For each abstract resource, the derived model includes a deterministic identity-table model:
  - table name `{schema}.{AbstractResource}Identity`,
  - `DocumentId` (PK; FK to `dms.Document(DocumentId)` ON DELETE CASCADE),
  - identity columns in `identityJsonPaths` order,
  - the full representable intrinsic lineage-anchor inventory in stable `IdentityLineageId` order, whether or not an
    incoming propagation-key variant currently demands each anchor,
  - `Discriminator` column (NOT NULL; last) with value format `ProjectName:ResourceName` (fail fast if value length exceeds 256).
- Each incoming abstract reference's propagation key selects only its demanded `AnchorSetId` subset; the identity table's
  full intrinsic inventory does not widen unrelated incoming foreign keys.
- Every participating concrete member has exactly one table-qualified `AbstractIdentityMemberMapping` that maps all
  abstract public components and intrinsic anchors, proves complete `DocumentId` row correlation, and records the
  DMS-owned AFTER-trigger maintenance statement as a boundary later than the initiating concrete write.
- The view model includes the same select-list contract:
  - `DocumentId`,
  - identity columns in `identityJsonPaths` order,
  - `Discriminator` column (NOT NULL; last) with value format `ProjectName:ResourceName`.
- `UNION ALL` arms are ordered by concrete `ResourceName` ordinal; fail fast if two participating members share the same `ResourceName` across projects.
- Each arm projects the correct concrete identity columns (including subclass rename rules).
- When a subclass declares `superclassIdentityJsonPath`, it must declare exactly one `identityJsonPaths` entry, and `superclassIdentityJsonPath` must match the referenced abstract resource's required identity path.
- Model compilation fails fast if any participating concrete resource cannot supply all abstract identity fields.
- Model compilation fails deterministically if a purported common abstract lineage cannot supply the same non-null
  referenced-row meaning from every participating concrete member.
- A small “polymorphic” fixture produces the expected identity-table and view inventory and select-list shape.

## Tasks

1. Implement abstract-resource hierarchy discovery from effective schema metadata.
2. Implement abstract identity-table model derivation:
   - identity column resolution and ordering,
   - intrinsic lineage discovery, normalization, stable ids, and concrete-member mappings,
   - deterministic naming and constraints.
3. Implement union view model derivation:
   - identity field resolution for each concrete resource arm (direct identity vs superclass rename mapping),
   - canonical type selection rules and model-level cast requirements.
4. Add unit tests for:
   1. arm ordering determinism,
   2. rename mapping correctness,
   3. identity-table shape and naming,
   4. intrinsic-anchor inventory independent of incoming demand,
   5. shared member mapping use by trigger and SQL Server analysis,
   6. fail-fast behavior when identity fields or common referenced-row meaning are missing.
5. Wire this derivation into the `DMS-1033` set-level builder as a whole-schema pass.
