# DMS-843 Change Queries Spike Package

## Status

Review-ready spike package for implementing Ed-Fi Change Queries in the current DMS architecture while staying aligned to backend-redesign artifact responsibilities and the selected Ed-Fi ODS/API behaviors for windows and tracked-change authorization, while explicitly excluding client-selectable snapshot parity in v1.

## Purpose

This folder is the canonical review package for `DMS-843`.

It consolidates the spike artifacts needed to review:

- public API behavior and synchronization rules
- execution model and routing
- required tracking artifacts and DDL responsibilities
- authorization, delete, and key-change semantics
- rollout, backfill, validation, and operational constraints

Use this package for design review first, then for implementation planning, Jira story creation, and later traceability.

## Scope And Design Posture

This package is the DMS-843 design for the current backend shape. It is not the full backend-redesign package.

The alignment target is semantic and behavioral parity with the redesign where responsibilities are the same, while still using current-backend bridge artifacts where names or storage differ.

This package also makes one explicit non-parity choice: DMS-843 v1 does not implement ODS `Use-Snapshot` behavior or any other client-selectable consistent-read mode.

The most important current-backend to redesign mappings used throughout this package are:

- current-backend `dms.Document.ChangeVersion` is the semantic equivalent of redesign `dms.Document.ContentVersion`
- `dms.Document.IdentityVersion` remains required for identity-tracking alignment, but it is not the public `/keyChanges` token
- `dms.DocumentChangeEvent` is the required live-change journal used for changed-resource `journal + verify`
- tracked-change artifacts must preserve redesign-relevant authorization inputs such as `CreatedByOwnershipTokenId` and row-local authorization basis data for DocumentId-based authorization strategies
- `dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` remain separate required artifacts
- `availableChangeVersions` is computed from committed tracking surfaces, not from the raw sequence value

## Canonical Review Set

The numbered documents in this folder are the canonical DMS-843 spike artifacts and should be reviewed in this order:

1. [01-Feature-Summary-and-Decisions.md](01-Feature-Summary-and-Decisions.md)
2. [02-API-Contract-and-Synchronization.md](02-API-Contract-and-Synchronization.md)
3. [03-Architecture-and-Execution.md](03-Architecture-and-Execution.md)
4. [04-Data-Model-and-DDL.md](04-Data-Model-and-DDL.md)
5. [05-Authorization-and-Delete-Semantics.md](05-Authorization-and-Delete-Semantics.md)
6. [06-Validation-Rollout-and-Operations.md](06-Validation-Rollout-and-Operations.md)

These six documents define the design at the contract, behavior, artifact-responsibility, authorization, and rollout level.

If review notes, local planning files, draft comments, or historical spike fragments are referenced during discussion, treat them as context only. They are not approval sources.

## Artifact Guide

| Artifact | Primary review question | Main review outcome |
| --- | --- | --- |
| [01-Feature-Summary-and-Decisions.md](01-Feature-Summary-and-Decisions.md) | What is in scope, what is out of scope, and what whole-feature decisions are fixed? | Feature scope, bridge rules, key decisions, non-goals |
| [02-API-Contract-and-Synchronization.md](02-API-Contract-and-Synchronization.md) | What does the public API do and how should clients synchronize? | Endpoint contract, window semantics, error contract, synchronization algorithm |
| [03-Architecture-and-Execution.md](03-Architecture-and-Execution.md) | How does the feature execute across routing, service, repository, and storage layers? | Routing model, `journal + verify`, write ordering, delete and key-change execution |
| [04-Data-Model-and-DDL.md](04-Data-Model-and-DDL.md) | What physical artifacts and data responsibilities are required? | `ChangeVersion`, `IdentityVersion`, `DocumentChangeEvent`, tombstones, key-change tracking, indexes, backfill |
| [05-Authorization-and-Delete-Semantics.md](05-Authorization-and-Delete-Semantics.md) | How do authorization and profile rules apply to changed resources, deletes, and key changes? | Live-query authorization parity, tombstone visibility, key-change visibility, profile behavior |
| [06-Validation-Rollout-and-Operations.md](06-Validation-Rollout-and-Operations.md) | How is the feature rolled out, validated, and operated safely? | Rollout order, validation matrix, E2E expectations, risks, review checklist |
| [07-Jira-Story-Input.md](07-Jira-Story-Input.md) | How should the approved design be broken into delivery work? | Story decomposition, dependencies, delivery order |
| [08-Requirements-Traceability.md](08-Requirements-Traceability.md) | Where is each spike requirement covered? | Requirement-to-artifact and requirement-to-story mapping |
| [Appendix-A-Feature-DDL-Sketch.sql](Appendix-A-Feature-DDL-Sketch.sql) | What does one concrete SQL sketch look like? | Informative implementation sketch only |

## Recommended Review Flow

Use the package in three passes:

1. Contract pass: review `01`, `02`, and `03` to confirm scope, public behavior, routing, and execution rules.
2. Storage and authorization pass: review `04` and `05` to confirm artifact boundaries, tracking semantics, and authorization parity.
3. Delivery pass: review `06`, then use `07`, `08`, and the appendix as supporting material for rollout, testability, and planning.

For a quick design check, the key approval questions are:

- Does the package preserve existing non-Change-Query API behavior while adding the required Change Query surface?
- Is changed-resource execution clearly defined as required `journal + verify` rather than an open-ended live-row scan?
- Are `DocumentChangeEvent`, tombstones, and key-change tracking separated cleanly by responsibility?
- Is the current-backend bridge clearly aligned to backend-redesign update-tracking and authorization concepts without pretending the physical schemas are identical?
- Are authorization, rollout, and validation obligations explicit enough to implement without relying on external notes?

## Relationship To Backend Redesign Docs

The redesign docs under `reference/design/backend-redesign/design-docs/` are related context, especially for alignment of update-tracking responsibilities and future backend direction. For DMS-843 review, use them as companion context, not as replacements for the numbered package.

Recommended cross-reference points:

| DMS-843 focus | Related redesign docs | Relationship |
| --- | --- | --- |
| whole-feature alignment and bridge posture | [`overview.md`](../design/backend-redesign/design-docs/overview.md), [`update-tracking.md`](../design/backend-redesign/design-docs/update-tracking.md) | explains why the package uses redesign-aligned artifact responsibilities |
| changed-resource execution and concurrency | [`transactions-and-concurrency.md`](../design/backend-redesign/design-docs/transactions-and-concurrency.md), [`update-tracking.md`](../design/backend-redesign/design-docs/update-tracking.md) | provides the broader `journal + verify` and committed-state execution context |
| live-row stamps, journal, and core tracking artifacts | [`data-model.md`](../design/backend-redesign/design-docs/data-model.md), [`update-tracking.md`](../design/backend-redesign/design-docs/update-tracking.md), [`ddl-generation.md`](../design/backend-redesign/design-docs/ddl-generation.md) | aligns `ResourceKey`, live stamps, journal responsibilities, indexing, and deterministic provisioning |
| authorization semantics | [`auth.md`](../design/backend-redesign/design-docs/auth.md) | companion context for ownership and DocumentId-based authorization behavior, including tracked-change implications |
| optional downstream projections and ETL ideas | [`etl-view-sketch.md`](../design/backend-redesign/design-docs/etl-view-sketch.md) | informative only; not required for DMS-843 approval |

Important boundary:

- optional redesign projections such as `dms.DocumentCache` and ETL-oriented views are not required DMS-843 spike artifacts
- the DMS-843 package is approval-complete without those optional projections

## Normative Vs Informative Material

Normative in this folder:

- the public API contract and synchronization rules
- the required tracking artifacts and their responsibilities
- execution ordering and routing behavior
- authorization, delete, and key-change semantics
- rollout, validation, and operational constraints

Informative in this folder:

- project names, component names, and current implementation touchpoints
- the Jira story breakdown
- the requirements traceability matrix
- the SQL appendix, which is a concrete sketch rather than the normative source of behavior

Informative outside this folder:

- redesign companion docs
- Ed-Fi platform and client guides
- local or reviewer notes not included in the numbered package

## Supporting Artifacts

Use these after the numbered design is understood:

- [07-Jira-Story-Input.md](07-Jira-Story-Input.md): ticket-ready story slicing, dependencies, and likely implementation areas
- [08-Requirements-Traceability.md](08-Requirements-Traceability.md): requirement coverage matrix across the package and stories
- [Appendix-A-Feature-DDL-Sketch.sql](Appendix-A-Feature-DDL-Sketch.sql): PostgreSQL-shaped implementation sketch only

## Relationship To External Ed-Fi References

This package is designed to align to the Ed-Fi ODS/API Change Query behavior where DMS-843 follows platform guidance:

- Ed-Fi ODS/API platform guide, Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/>
- Ed-Fi ODS/API client guide, Using the Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/>

The alignment target is behavioral and contract parity where appropriate, with explicit package-level decisions wherever DMS makes a product-specific choice. The main explicit non-parity choice in this package is the absence of ODS `Use-Snapshot` support in DMS-843 v1.
