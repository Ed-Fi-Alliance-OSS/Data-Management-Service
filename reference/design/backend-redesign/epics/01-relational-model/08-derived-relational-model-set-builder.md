---
jira: DMS-1033
jira_url: https://edfi.atlassian.net/browse/DMS-1033
---

# Story: Build `DerivedRelationalModelSet` from the Effective Schema Set

## Description

Implement the set-level orchestration that derives a single dialect-aware
`DerivedRelationalModelArtifact(Model, Diagnostics, ExecutorRequirements)` (final `DerivedRelationalModelSet`, success
diagnostics, and provider-finalized executor requirements) from the configured
effective `ApiSchema.json` set (core + extensions), per:

- `reference/design/backend-redesign/epics/00-effective-schema-hash/00-schema-loader.md` (explicit multi-file schema set input contract)
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `DerivedRelationalModelSet` shape + ordering rules)
- `reference/design/backend-redesign/design-docs/data-model.md` (project schema normalization + collision rules)
- `reference/design/backend-redesign/design-docs/extensions.md` (`_ext` project-key resolution rules)

Dialect note: “dialect-aware” includes deterministic identifier-length handling and any other engine-specific defaults
that affect the SQL-free derived model inventories (e.g., collision detection and manifests). These “dialect rules”
(identifier length limits, shortening algorithm, type defaults) must be implemented once as a reusable component and
shared between E01 derivation (this epic) and E02 SQL emission, rather than duplicated in both layers.

This story introduces the missing “multi-document glue” between:
- the effective schema set (projects + resources) loaded/normalized by E00, and
- the per-resource derivation steps in this epic (DMS-929/930/931/932/933/942/945).

Key responsibilities:
- Establish canonical project ordering and validate cross-project invariants (e.g., physical schema name uniqueness).
- Invoke the per-resource pipeline for every concrete resource in every project, producing a complete inventory across core + extension projects.
- Provide shared set-level lookup services needed during derivation:
  - `_ext` project-key resolution to a configured project,
  - descriptor/document-reference target validation against the effective schema set,
  - abstract resource metadata lookup for polymorphic identity artifacts.
- Inventory/store every target's intrinsic reference-backed identity lineages, initialize each incoming site's anchor
  demand empty, and compute least-fixed-point demand from receiver full-FK validity/correlation obligations. Demand flows
  only through downstream identity/constraint consumers; omission remains universally proved for every mutation subset
  and simultaneous combination.
- Finalize all FK actions globally before DDL, manifest, runtime plan, or mapping-pack producers consume the model.

Note (current code): `ExtractInputsStep` performs project-wide descriptor-path inference on every per-resource run (`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ExtractInputsStep.cs:108`, `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/ExtractInputsStep.cs:135`). This needs to move into the `DMS-1033` set-level pass context so the inferred descriptor-path map can be computed once (per project/effective schema set) and reused across per-resource steps.

Clarification: the per-resource derivation pipeline builds the base model for **one resource at a time** (selected by
resource endpoint name). This story's builder loops over *all* resources across *all* configured `ApiSchema.json` inputs
(core + extensions), then runs the full-schema anchor, FK, and action passes. Per-resource plan compilation begins only
after the global `DerivedRelationalModelArtifact` is complete.

Implementation note (ordered passes): implement this story as an ordered set-level pipeline of **passes**. Most passes
perform one scan over the full effective schema set and may consult other resources/projects. The anchor-closure pass is
the intentional exception: it iterates canonically to a deterministic fixed point. Cross-resource derivation logic should
live with the pass/story that needs it (e.g., reference/descriptor inference in DMS-930, abstract hierarchy discovery in
DMS-933), rather than being forced into a single shared registry for performance. Every scan/iteration must use canonical
ordinal order and must not depend on dictionary iteration order.

### Proposed pass ordering (current)

This list is intentionally “high level”; exact pass boundaries can be adjusted as the implementations land:

1. **Base traversal + descriptor binding pass** (DMS-929): derive root/collection tables + scalar columns and bind descriptor edges while discovering `_ext` sites.
2. **Descriptor resource mapping pass** (DMS-942): detect/validate descriptor resources and bind descriptor storage/resource mapping metadata.
3. **Extension pass** (DMS-932): derive extension tables for discovered `_ext` sites (including project-key resolution).
4. **Reference binding pass** (DMS-930): bind document references into tables by adding FK/identity columns and emitting `DocumentReferenceBinding` metadata.
5. **Key unification pass**: derive canonical storage, presence-gated aliases, and unification diagnostics before physical FK formation.
6. **Abstract artifact and member-mapping pass** (DMS-933): derive abstract identity tables/views plus one shared,
   table-qualified `AbstractIdentityMemberMapping` inventory. Anchor closure, SQL Server analysis, and abstract trigger
   derivation all consume this inventory; later passes must not reconstruct it privately.
7. **Alias validation + root identity constraint passes** (DMS-930): validate unified storage and derive root natural-key
   constraints. Propagation-key/`RefKey` variants are not knowable until anchor-demand closure and are finalized later.
8. **Stable collection row-identity pass** (DMS-1103): derive `CollectionItemId`, parent/root locator columns,
   nested parent consistency, collection-aligned extension keys, and the ordered semantic identity from scope-resolved
   AUC inputs or the reference-backed stable `..._DocumentId` rule before any consumer records a physical row locator.
9. **Persisted receiver occurrence-identity pass** (DMS-1103): finalize each table's ordered
   `PersistedOccurrenceIdentity`, including ancestor context, API semantic-identity source roles, and stable physical row
   locators. This shared inventory is consumed by both requirement derivation and E15 merge-plan compilation; neither
   consumer reconstructs it from UNIQUE constraints or names.
10. **Transitive identity mutability pass**: determine which targets receive fixed cascade eligibility.
11. **Identity-lineage anchor demand closure pass** (DMS-1258): inventory intrinsic target lineages, initialize each
   incoming site's demand empty, add only receiver validity/correlation demands, and propagate them only through
   downstream identity/constraint consumers. Reuse existing local lineage `..._DocumentId` columns only with complete
   equivalence/presence proof; otherwise add dedicated local storage. Group equal demanded subsets into
   propagation-key/`RefKey` variants keyed by stable `AnchorSetId`.
12. **Physical reference-FK candidate pass** (DMS-1258): storage-map, positionally align, and de-duplicate candidates.
    Candidate identity excludes `OnUpdate`, provider mode, and generated constraint name.
13. **Dialect action and executable-requirement evaluation** (DMS-1258): PostgreSQL directly assigns fixed
    eligible-`CASCADE`/immutable-`NO ACTION` actions, then constructively derives requirements from those routes without
    topology classification or failure. SQL Server alone derives value-flow facts and searches globally; while evaluating
    each complete error-1785-legal assignment, derive every requirement and reject that assignment if any requirement is
    unrepresentable, then choose the deterministic best remaining assignment. For both providers, requirements cover
    every direct API mutation origin and existing request binding whose target changes along a retained same-boundary
    route, regardless of the binding FK action, cycle membership, or SQL Server certificate.
14. **Reference constraint finalization pass**: emit full-composite FKs and all-or-none checks from finalized candidates.
15. **Remaining constraint passes**: emit semantic/array/stable-collection constraints from the already-finalized
    occurrence inventory and derive descriptor and other non-reference constraints.
16. **Naming, validation, index/trigger/auxiliary inventory passes**: consume the finalized global model. Trigger
    inventory consumes the shared abstract member mappings and contains no identity-value propagation trigger.
17. **Dialect identifier shortening + canonical ordering passes** (DMS-931/DMS-934): validate collisions and normalize
    deterministic output.

`DMS-934` (manifest emission) serializes the final `DerivedRelationalModelArtifact` and does not re-derive anything
independently. Both dialects carry provider-neutral typed anchor-omission proofs. PostgreSQL diagnostics contain no
classifier decisions. SQL Server success diagnostics additionally contain the global physical-FK decisions and typed
coverage certificates.

## Acceptance Criteria

- Given a fixture with multiple `ApiSchema.json` inputs (core + at least one extension), the derived
  `DerivedRelationalModelArtifact` is stable across:
  - input file ordering,
  - JSON whitespace/property ordering within files (assuming canonicalization rules from E00),
  - and dictionary iteration order in C#.
- Projects appear exactly once in `ProjectSchemasInEndpointOrder`, sorted by `ProjectEndpointName` ordinal.
- Fail fast when project-schema normalization would create ambiguous physical schemas:
  - two projects normalize to the same physical schema name (per `data-model.md`), or
  - two projects collide on `ProjectEndpointName` after normalization/validation rules defined in E00.
- All concrete resources in the effective schema set that are **not** `isResourceExtension: true` contribute a `ConcreteResourceModel` (including descriptor resources), with `ConcreteResourcesInNameOrder` sorted ordinal by `(project_name, resource_name)` (per `compiled-mapping-set.md`). Resource-extension schemas (`isResourceExtension: true`) are excluded because they are mapped as `_ext` extension tables attached to their owning base resource per `extensions.md`.
- `_ext` project keys discovered by traversal must resolve to a configured project:
  - first match on `ProjectEndpointName` (case-insensitive),
  - fallback match on `ProjectName` (case-insensitive),
  - otherwise fail fast with an actionable error that includes the owning JSON scope and the unknown key.
- `documentPathsMapping` targets (descriptors and document references) are validated against the effective schema set:
  - missing `(project_name, resource_name)` targets fail fast during model derivation.
- PostgreSQL derivation never prunes, classifies, or fails because of cascade topology; it emits fixed full-composite
  actions and empty classifier diagnostics.
- SQL Server safely breakable cycle/diamond fixtures succeed with deterministic decisions and certificates. Proven
  infeasibility and deterministic work-limit exhaustion throw `RelationalModelDerivationException` with distinct ordered
  error codes; no partial artifact is returned.
- For every direct API mutation origin and existing request binding whose target changes along any retained same-boundary
  route, each complete mutation case that can produce a pre-statement future-identity miss emits an exact
  `SameStatementReferenceResolutionRequirement`. This applies whether that binding FK is `CASCADE` or `NO ACTION`,
  cyclic or acyclic, and certificate-backed or retained. SQL Server accepts only assignments whose complete requirement
  inventory is representable; PostgreSQL constructs the equivalent inventory from fixed actions without topology
  pruning, classification, or failure.
- A retained acyclic `R -> T -> RChild` fixture emits requirements on both providers when a direct R identity update
  cascades to T and multiple existing child bindings in the same R PUT submit T's future identity. T's future vector mixes
  a changed R-derived item with a locked unchanged target primitive/anchor; the child FK remains `CASCADE`, correlation
  and post-verification are batched by exact plan, and an incorrect unchanged submitted component fails before DML.
- Authoritative DS 5.2 and TPDM fixtures support every identity component and simultaneous component changes, including a
  reference-backed identity retarget where public values and lineage anchors change together.
- An anchor-bearing variant of the accepted `CycleA`/`CycleB` fixture has two independently replaceable reference-backed
  identity lineages plus one primitive component. It certifies the case that retargets both references together and the
  case that also changes the primitive component; the SQL Server pruned-edge decision carries the explicit combined
  `MutationCaseId` and complete `SubsetCompositionProof`, while PostgreSQL emits its fixed full-cascade cycle.
- DS 5.2 `CourseOffering -> Session` demands the School anchor due to its receiver-side School FK, while an unrelated
  Session referrer with no validity/correlation need selects the empty-demand variant.
- Anchor variants are least-demand, each reference emits one full FK to one matching variant, and hard provider key
  width/count limits are validated deterministically. Every omitted anchor has a universal no-demand proof.
- Complete mutation cases are certified directly or through typed `SubsetCompositionProof`; missing composition fails as
  `UnprovedSubsetComposition`.
- The completed contract remains `RelationalMappingVersion = v1`; no migration or physical-model hash is introduced.

## Tasks

1. Define a set-level input contract (library-first) that consumes:
   - the normalized multi-project schema set produced by E00,
   - the deterministic schema component list and resource key seed summary (`EffectiveSchemaInfo`),
   - the target SQL dialect and shared dialect rules (identifier limits/shortening/type defaults).
2. Implement a `DerivedRelationalModelSetBuilder` that:
   - constructs `ProjectSchemasInEndpointOrder` (including physical schema name normalization + collision validation),
   - defines a stable pass hook point (e.g., `IRelationalModelSetPass` + a shared builder context) so other stories can register set-level passes,
   - configures and executes ordered full-schema passes (each pass scans all projects/resources),
   - returns a single `DerivedRelationalModelArtifact` per `compiled-mapping-set.md`,
   - throws typed `RelationalModelDerivationException` for SQL Server classification failures while preserving ordered
     machine-readable errors.
3. Provide pass implementations direct access to the full effective schema set (core + extensions) so they can:
   - resolve `_ext` project keys,
   - validate descriptor/document-reference targets,
   - and discover abstract resource hierarchies.
4. Add unit tests using a small multi-project fixture (core + extension) that assert:
   1. determinism across input ordering,
   2. unknown `_ext` key failure,
   3. physical schema name collision failure,
   4. missing descriptor/reference target failure,
   5. exact same-statement requirement selection for the retained acyclic `R -> T -> RChild` fixture on both dialects,
      including multiple child occurrences and a mixed origin-write/locked-stored-target future vector.
