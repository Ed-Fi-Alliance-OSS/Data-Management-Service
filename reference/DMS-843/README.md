# DMS-843 Change Queries Feature Design V2

## Purpose

This folder consolidates the Change Queries spike package, the detailed design package, and the backend-redesign alignment notes into one review-ready feature design for `DMS-843`.

The goal of this package is to define the whole Change Queries feature for the current DMS architecture without splitting the design into separate phase documents.

This package is intended to be used for:

- feature review
- implementation planning
- Jira user story creation
- later implementation traceability

## Canonical Status

The numbered documents in this folder are the canonical design for `DMS-843`.

If older spike notes or draft variants are referenced during review, treat them as historical context only. The numbered package is the authoritative source of truth.

## Normative vs Informative Content

The normative design in this package is defined by:

- the public API contract
- the required behavior and synchronization rules
- the data model and tracking artifacts
- the authorization and consistency rules
- the rollout and validation constraints

Project, component, and file references are informative implementation touchpoints only. They exist to help parity review and later planning, but they are not design dependencies and do not constrain the planned backend replacement to the current repo structure.

## Scope and Decisions

The consolidated design preserves the approved spike decisions:

- existing APIs remain non-breaking
- server-side snapshot history tables are avoided; DMS-843 v1 does not expose a client-selectable snapshot or consistent-read mode, so synchronization under concurrent writes is intentionally documented as best-effort
- the canonical live-row change token lives on `dms.Document`
- deletes are tracked in `dms.DocumentDeleteTracking` in the `dms` schema
- `keyChanges` are part of the feature and use dedicated tracking for old and new natural-key values
- `dms.DocumentChangeEvent` is treated as an optional internal journal artifact for scalability and redesign alignment, not as a competing delete mechanism
- deletes and key changes use separate dedicated tracking tables rather than one mixed event table

## Required Review Set

To achieve the spike goal with the least review noise, the required core review set is:

1. `01-Feature-Summary-and-Decisions.md`
2. `02-API-Contract-and-Synchronization.md`
3. `03-Architecture-and-Execution.md`
4. `04-Data-Model-and-DDL.md`
5. `05-Authorization-and-Delete-Semantics.md`
6. `06-Validation-Rollout-and-Operations.md`

These six documents contain the normative design needed to review the spike outputs:

- feature decisions
- public API contract
- synchronization rules
- architecture and execution model
- data model and DDL requirements
- authorization semantics
- rollout, validation, and operational constraints

## Supporting Review Artifacts

The following documents remain useful, but they are supporting artifacts rather than the minimum review set for the spike goal:

1. `07-Jira-Story-Input.md`
2. `08-Requirements-Traceability.md`
3. `Appendix-A-Feature-DDL-Sketch.sql`
4. `story-map.json`
5. `workitems/tasks.json`
6. `workitems/progress.json`

Supporting-artifact notes:

- `07-Jira-Story-Input.md` is for Jira decomposition after design review
- `08-Requirements-Traceability.md` is for auditability and coverage confirmation
- `Appendix-A-Feature-DDL-Sketch.sql` is informative only; it is an implementation-oriented sketch, not a normative design source
- `Strict-Review-Comments*.md` files are historical review trails; use their status notes and post-remediation sections for current package status
- `story-map.json`, `workitems/tasks.json`, and `workitems/progress.json` are planning aids

## Output Coverage

This package covers the spike template outputs directly:

- architecture design
- DDL proposal
- API specification
- synchronization algorithm
- delete tracking strategy
- migration and backfill strategy
- performance considerations
- validation scenarios

## Relationship to Existing References

This package is aligned to:

- Ed-Fi ODS/API platform guide, Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/>
- Ed-Fi ODS/API client guide, Using the Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/>

## How to Use This Package for Jira Story Creation

- Use `07-Jira-Story-Input.md` for the narrative decomposition.
- Use `story-map.json` for machine-readable story grouping, dependencies, and acceptance themes.
- Use `08-Requirements-Traceability.md` to confirm each story group maps back to the spike constraints and expected outputs.
