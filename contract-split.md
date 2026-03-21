# Contract Split for Collection Semantic Identity Validation

## Decision

Keep collection semantic-identity compilation in the shared relational-model pipeline, but move collection semantic-identity validation out of the universal default pipeline and into an explicit strict pipeline.

This is not a "make tests pass" exception. It is the intended contract split:

- `CreateDefault()` remains the permissive shared relational-model build used by DDL, manifests, snapshots, and generic hand-authored fixtures.
- `CreateStrict()` (or equivalent explicit strict builder mode) adds `ValidateCollectionSemanticIdentityPass`.
- Strict mode is used only at boundaries that require non-empty compiled semantic identity for correct execution.

## Why

The current implementation wires `ValidateCollectionSemanticIdentityPass` into `RelationalModelSetPasses.CreateDefault()`, and that default pipeline is consumed by both:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/DdlPipelineHelpers.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RuntimeMappingSetCompiler.cs`

That makes semantic-identity validation global.

The design does require fail-fast when a persisted multi-item collection scope cannot compile a non-empty semantic identity, but the design places that requirement at the runtime write / merge boundary, not at every shared compilation path. Relevant design references:

- `reference/design/backend-redesign/epics/01-relational-model/11-stable-collection-row-identity.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/epics/15-plan-compilation/04b-stable-collection-merge-plans.md`

Today there is no provenance or strictness flag on `EffectiveSchemaSet` or `RelationalModelSetBuilderContext` that distinguishes validated MetaEd-backed input from generic fixture input. Because of that, placing the validation pass in `CreateDefault()` leaks the supported-model boundary into DDL fixtures and other generic compile paths.

## Immediate Change

Implement the smallest permanent contract fix now:

1. Remove `ValidateCollectionSemanticIdentityPass` from `RelationalModelSetPasses.CreateDefault()`.
2. Add `RelationalModelSetPasses.CreateStrict()` or an equivalent explicit strict-builder option that appends `ValidateCollectionSemanticIdentityPass` after `SemanticIdentityCompilationPass`.
3. Keep validation-focused relational-model tests on the strict path.
4. Leave DDL and generic fixture compilation on the default permissive path.

## Permanent Boundary

The permanent enforcement point should land with `DMS-1108`, not by restoring validation to the shared default pipeline.

`DMS-1108` is the first story where runtime write-plan compilation must consume collection semantic identity as executable merge semantics. At that point, missing semantic identity becomes an actual runtime ambiguity, so strict validation is correct there.

When `DMS-1108` lands:

- runtime write-plan compilation should use the strict relational-model pipeline,
- mapping-pack or runtime startup compilation that depends on executable collection merge semantics should use the strict pipeline,
- DDL and other non-executable compile paths should remain on the default pipeline unless they deliberately opt into strict validation.

## What This Is Not

This is not a temporary waiver that should later be undone by putting `ValidateCollectionSemanticIdentityPass` back into `CreateDefault()`.

The later story should adopt strict mode at the correct boundary. It should not re-globalize the validation pass.

## Optional Follow-up

A later refinement may add explicit schema provenance or strictness metadata to `EffectiveSchemaSet` so the system can choose strict mode automatically for validated supported inputs. That is a useful enhancement, but it is not required for the contract split itself.
