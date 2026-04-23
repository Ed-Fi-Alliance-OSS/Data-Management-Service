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
  rebuilding it independently.
- Story 01 depends on Story 00's staged schema workspace and expected hash because schema provisioning and
  validation run over that already-selected staged file set.
- Story 02 depends on Story 00's staged schema and security inputs, and it is the gate for advertising any
  built-in extension seed support because it delivers the top-level `SeedLoader` contract.
  **Story 02 is externally blocked** by ODS-6738 (BulkLoadClient JSONL support) and DMS-1119 (published
  seed artifact packages); it is design-complete but not implementation-ready end to end.
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
- Step 7 is the only phase that resolves target DMS instance IDs; all subsequent phases consume the
  selected set without re-querying CMS.
- "Selected ApiSchema drives DDL" means the selected schema set drives the DDL target/version/hash
  validation path and the exact physical table set for the run. Different extension selections are different
  schema-provisioning targets, not silently reusable supersets of one another.
- Only extensions backed by current schema and security artifacts belong in the DMS-916 v1
  `-Extensions` surface; deferred extensions stay out of operator-facing validation and examples.
- Story 00 may not advertise built-in extension seed support until Story 02 adds the top-level `SeedLoader`
  claim set to the embedded claims metadata.
- Story 03 owns the repo-local `.bootstrap/` workspace hygiene and the user-facing migration note for the
  narrowed `-NoDmsInstance` contract. **Note:** The `.gitignore` update for `.bootstrap/` must be delivered
  first or concurrently with Story 00 to prevent accidental commits of staged artifacts. The consolidated
  breaking-changes reference for all four behavior changes is in
  [`bootstrap-design.md` Section 15](../bootstrap-design.md#15-breaking-changes-and-migration-notes).
- These docs do not add post-bootstrap runners, a persisted state-file control plane, new tenant models, or
  a second bootstrap architecture.
