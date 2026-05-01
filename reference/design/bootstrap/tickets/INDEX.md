---
design: DMS-916
---

# Bootstrap Ticket Definitions

These companion ticket-definition docs translate
[`../bootstrap-design.md`](../bootstrap-design.md) into implementation-sized stories without expanding scope
beyond DMS-916. They follow the same small `Description / Acceptance Criteria / Tasks` structure used by the
backend-redesign story docs.

## Stories

- `00-schema-and-security-selection.md`
- `01-schema-deployment-safety.md`
- `02-api-seed-delivery.md`
- `03-entry-point-and-ide-workflow.md`
- `04-apischema-runtime-content-loading.md` - replace DMS ApiSchema DLL resource loading with file-based
  workspace loading
- `05-metaed-apischema-asset-packaging.md` - publish asset-only ApiSchema NuGet packages from MetaEd
- `06-package-backed-standard-schema-selection.md` - implement omitted `-Extensions` core-only mode and
  named `-Extensions` package-backed standard mode

## Cross-Story Dependency Notes

- Story 00 owns direct filesystem schema selection through `-ApiSchemaPath`, normalized ApiSchema
  asset-container materialization, expected `EffectiveSchemaHash` computation, staged claims preparation,
  and the root bootstrap manifest sections for schema, claims, and seed handoff. Later slices consume that
  staged input contract rather than rebuilding it independently. Package-backed no-argument core-only mode and
  named `-Extensions` standard modes are not part of Story 00; they belong to Story 06 after Story 05 publishes
  asset-only ApiSchema packages.
- Story 01 depends on Story 00's staged schema workspace because schema provisioning and validation run over
  that already-selected staged file set. The expected hash from Story 00 is diagnostic metadata for logging
  or comparison, not a required SchemaTools provisioning input.
- Story 02 depends on Story 00's root bootstrap manifest over the staged schema and security inputs,
  and it is the gate for advertising any built-in extension seed support because it owns the `SeedLoader`
  contract for that path.
  **Story 02 has one DMS-internal prerequisite that must be its first deliverable:** add the top-level
  `SeedLoader` claim set definition and required core permissions to
  `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json` (Story 02 Task 3).
  The exact permission table is in `bootstrap-design.md` Section 7.2.2. This internal task has no external
  dependency and unblocks all DMS-side seed delivery testing once done.
  **Story 02 end-to-end delivery is also externally blocked** by ODS-6738 (BulkLoadClient JSONL support).
  The built-in `Minimal` and `Populated` seed templates are DMS-owned repo-local developer assets, so
  DMS-1119 package distribution work does not block this story. The `SeedLoader` claim set addition can and
  should be done independently of the BulkLoadClient blocker.
- Story 03 reuses the parameter surfaces and mechanisms delivered by the other slices where applicable, but
  it is not a blanket prerequisite chain for every Story 03 task. Treat the main design and each companion
  story as the authority for the specific dependency of a given implementation task.
- Story 04 depends on Story 00's normalized ApiSchema workspace and ApiSchema asset manifest contract. It removes the DMS
  runtime bootstrap-path dependency on `*.ApiSchema.dll` assemblies for metadata/specification JSON and XSD
  content. It does not depend on published asset-only packages.
- Story 05 is a cross-repo MetaEd package-production switch-over. It enables Story 06 package-backed
  standard mode against published packages, but it is not a prerequisite for direct filesystem
  ApiSchema loading. Story 00, Story 04, and Story 05 can proceed in parallel because all three meet at the
  normalized filesystem workspace contract.
- Story 06 depends on Story 05 for asset-only ApiSchema packages and on Story 00 for the shared staged
  workspace, ApiSchema asset manifest, claims-staging, and root bootstrap manifest contracts. Story 06 owns omitted
  `-Extensions` core-only mode and named `-Extensions` standard mode. Story 02 remains the owner for actual
  seed delivery and built-in extension seed lookup from the bootstrap manifest.

## Scope Guardrails

- Use these docs together with [`../bootstrap-design.md`](../bootstrap-design.md) for rationale and
  [`../command-boundaries.md`](../command-boundaries.md) for the authoritative phase contract.
- If a story, example, or summary here conflicts with `command-boundaries.md`, `command-boundaries.md`
  wins.
- Each story should keep its own acceptance criteria focused on the behavior it delivers and reference
  `command-boundaries.md` instead of restating phase order, parameter ownership, selector rules, or wrapper
  prohibitions.
- These docs do not add post-bootstrap runners, a persisted workflow control plane or resume state, new tenant
  models, or a second bootstrap architecture.
