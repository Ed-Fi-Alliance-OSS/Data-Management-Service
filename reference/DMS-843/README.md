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
- snapshots are avoided in favor of bounded `minChangeVersion` and `maxChangeVersion` windows
- the canonical live-row change token lives on `dms.Document`
- deletes are tracked in `dms.DocumentDeleteTracking` in the `dms` schema
- `keyChanges` are part of the feature and use dedicated tracking for old and new natural-key values
- `dms.DocumentChangeEvent` is treated as an optional internal journal artifact for scalability and redesign alignment, not as a competing delete mechanism
- deletes and key changes use separate dedicated tracking tables rather than one mixed event table

## Deliverables

Read the package in this order:

1. `01-Feature-Summary-and-Decisions.md`
2. `02-API-Contract-and-Synchronization.md`
3. `03-Architecture-and-Execution.md`
4. `04-Data-Model-and-DDL.md`
5. `05-Authorization-and-Delete-Semantics.md`
6. `06-Validation-Rollout-and-Operations.md`
7. `07-Jira-Story-Input.md`
8. `08-Requirements-Traceability.md`
9. `Appendix-A-Feature-DDL-Sketch.sql`
10. `story-map.json`
11. `workitems/tasks.json`
12. `workitems/progress.json`

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

- `DMS-843/changequeries/*`
- `reference/DMS-843/*`
- `DMS-843/Design/*`
- `reference/design/backend-redesign/design-docs/update-tracking.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/auth.md`

## How to Use This Package for Jira Story Creation

- Use `07-Jira-Story-Input.md` for the narrative decomposition.
- Use `story-map.json` for machine-readable story grouping, dependencies, and acceptance themes.
- Use `08-Requirements-Traceability.md` to confirm each story group maps back to the spike constraints and expected outputs.
