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

## Cross-Story Dependency Notes

- Story 00 owns schema selection, staged-schema materialization, expected `EffectiveSchemaHash`
  computation, and staged claims preparation. Later slices consume that staged input contract rather than
  rebuilding it independently. **Prerequisite before Story 00 implementation begins:** confirm the exact
  core schema NuGet package name (e.g. `EdFi.DataStandard52.ApiSchema`) against the configured NuGet feed.
- Story 01 depends on Story 00's staged schema workspace and expected hash because schema provisioning and
  validation run over that already-selected staged file set.
- Story 02 depends on Story 00's staged schema and security inputs, and it is the gate for advertising any
  built-in extension seed support because it owns the `SeedLoader` contract for that path.
  **Story 02 has one DMS-internal prerequisite that must be its first deliverable:** add the top-level
  `SeedLoader` claim set definition and required core permissions to
  `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json` (Story 02 Task 3).
  The exact permission table is in `bootstrap-design.md` Section 7.2.2. This internal task has no external
  dependency and unblocks all DMS-side seed delivery testing once done.
  **Story 02 end-to-end delivery is also externally blocked** by ODS-6738 (BulkLoadClient JSONL support)
  and DMS-1119 (published seed artifact packages); it is design-complete but not deliverable end to end
  until those external dependencies land. The `SeedLoader` claim set addition can and should be done
  independently of those blockers.
- Story 03 reuses the parameter surfaces and mechanisms delivered by the other slices where applicable, but
  it is not a blanket prerequisite chain for every Story 03 task. Treat the main design and each companion
  story as the authority for the specific dependency of a given implementation task.

## Scope Guardrails

- Use these docs together with [`../bootstrap-design.md`](../bootstrap-design.md).
- If a change is not needed to satisfy the **Design-Complete Criteria** section of the main bootstrap
  design, it is out of scope.
- The composable phase-oriented commands are the normative bootstrap contract. `start-local-dms.ps1` is
  the infrastructure-lifecycle phase command; `bootstrap-local-dms.ps1` is optional thin convenience
  packaging for the common developer path when present, and is not a second control plane.
- Story wording about "skip/resume" is satisfied by safe skip behavior plus optional same-invocation
  continuation on the infra-lifecycle phase command; these docs do not define a persisted
  cross-invocation resume mechanism.
- `configure-local-dms-instance.ps1` is the phase that creates or selects target DMS instance IDs for
  the run. Within a single `bootstrap-local-dms.ps1` wrapper invocation, the wrapper captures those
  IDs in-memory and forwards them to `provision-dms-schema.ps1` and `load-dms-seed-data.ps1` via
  explicit `-InstanceId` arguments. In a manual phase flow, each downstream phase resolves its own
  target instances via explicit `-InstanceId <guid[]>` or `-SchoolYear <int[]>` selectors with a
  CMS-backed lookup - auto-selecting when exactly one instance exists, failing fast when zero or
  multiple match without an explicit selector. No downstream phase performs its own instance creation,
  broad target-selection policy, or non-selector-driven discovery; they only consume instances that
  `configure-local-dms-instance.ps1` has already established and may resolve only the explicit target
  selectors passed into that phase through CMS-backed lookup.
- "Selected ApiSchema drives DDL" means the selected schema set drives the DDL target/version/hash
  validation path and the exact physical table set for the run. Different extension selections are different
  schema-provisioning targets, not silently reusable supersets of one another.
- Only extensions backed by current schema and security artifacts belong in the DMS-916 v1
  `-Extensions` surface; deferred extensions stay out of operator-facing validation and examples.
- Story 00 stages schema and claims inputs only. Story 02 owns when built-in extension seed support may be
  advertised.
- Story 03 owns the repo-local `.bootstrap/` workspace hygiene and the user-facing migration note for the
  narrowed `-NoDmsInstance` contract. **Note:** The `.gitignore` update for `.bootstrap/` must be delivered
  first or concurrently with Story 00 to prevent accidental commits of staged artifacts. The consolidated
  breaking-changes reference for all four behavior changes is in
  [`bootstrap-design.md` Section 15](../bootstrap-design.md#15-breaking-changes-and-migration-notes).
- These docs do not add post-bootstrap runners, a persisted state-file control plane, new tenant models, or
  a second bootstrap architecture.
