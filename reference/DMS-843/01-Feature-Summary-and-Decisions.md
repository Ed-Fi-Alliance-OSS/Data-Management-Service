# Feature Summary and Decisions

## Objective

Implement Ed-Fi Change Queries for the current Data Management Service storage and runtime model in a way that:

- preserves Ed-Fi changed-record-query semantics
- preserves existing DMS API behavior when Change Queries are not used
- avoids snapshots if possible
- uses the canonical `dms.Document` row as the source of truth
- keeps the design aligned to the backend-redesign update-tracking direction

## Current DMS Facts That Shape the Design

The current DMS persisted document model stores all resources and descriptors in a single canonical table:

- `dms.Document`

Relevant current characteristics:

- The served resource body is stored in `dms.Document.EdfiDoc`.
- The table is partitioned by `DocumentPartitionKey`.
- Collection GET authorization is enforced from columns already stored on `dms.Document`, with support from authorization companion tables and `dms.EducationOrganizationHierarchyTermsLookup`.
- Existing generic GET routing supports collection routes and item-by-id routes, but not `/{resource}/deletes`.
- The current backend already performs authorization-maintenance updates on `dms.Document` that do not necessarily change the public representation.
- The repo still contains `EdFi.DataManagementService.Old.Postgresql` as a transitional compatibility implementation. It remains useful as a current-behavior reference, but this design is specified so the planned replacement backend can implement it without inheriting that project structure.

These facts require the feature design to distinguish representation changes from authorization-maintenance updates, and they require dedicated `/deletes` and `/keyChanges` routes.

## Whole-Feature Decisions

## 1. Public API compatibility is additive

The feature adds Change Query behavior to existing collection GET routes and introduces new Change Query endpoints where needed, but it does not break existing resource APIs.

## 2. The canonical change token lives on `dms.Document`

The design stores the public Change Query token on the canonical live row as `dms.Document.ChangeVersion`.

For backend-redesign alignment, this column is the semantic equivalent of redesign `dms.Document.ContentVersion`.

This design does not introduce a second live-row column named `ContentVersion` in the current backend. Within the DMS-843 package, `ContentVersion` is redesign terminology used only as a cross-reference for the same representation-change stamp stored physically as `dms.Document.ChangeVersion`.

## 3. Deletes require tombstones

Deletes cannot be inferred from the live row because the live row disappears. The feature therefore requires `dms.DocumentDeleteTracking` in the `dms` schema.

## 4. Updates do not require historical payload storage

Ed-Fi changed-resource queries return the latest current representation of resources that changed within the requested window. They do not return every intermediate mutation and they do not require snapshot payload history.

## 5. `DocumentChangeEvent` is complementary, not contradictory

The design includes `dms.DocumentChangeEvent` as an internal live-change journal artifact that can be enabled for scalability and redesign alignment. It does not replace tombstones because delete queries need data that survives deletion of the live row.

## 6. `keyChanges` is a peer endpoint to `/deletes`

The design includes the Ed-Fi `keyChanges` route as part of the core Change Queries feature, alongside `/deletes`. Because current-state rows do not preserve prior natural-key values, the design adds explicit old-and-new natural-key tracking rather than trying to infer key changes later from the current row, deletes, or snapshots.

## 7. Delete and key-change tracking stay in separate tables

The design intentionally keeps `dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` as separate artifacts rather than merging them into one generic mixed event table.

Reasons:

- delete queries require tombstones that survive removal of the live row and preserve `keyValues`
- key-change queries require transition rows that preserve both `oldKeyValues` and `newKeyValues`
- the two routes have different read shapes, ordering concerns, and collapse rules
- live changed-resource selection remains a current-state problem solved by the live row stamp and optional journal, not by tombstones

## 8. Backend-redesign alignment is semantic, not physical-name matching

The backend-redesign update-tracking docs are already normative for live representation stamping and `dms.DocumentChangeEvent`.

This design remains aligned by preserving the same artifact responsibilities:

- current-backend `dms.Document.ChangeVersion` is the semantic equivalent of redesign `dms.Document.ContentVersion`
- optional current-backend `dms.DocumentChangeEvent` is the same kind of live-change journal used by redesign
- current-backend `dms.DocumentDeleteTracking` is a bridge artifact for delete semantics that redesign will also need in semantically equivalent form
- current-backend `dms.DocumentKeyChangeTracking` is a bridge artifact for old/new natural-key transitions that redesign will also need in semantically equivalent form

The alignment target is therefore contract and behavior parity, not reuse of identical table names across storage models.

Required interpretation:

- current-backend implementations of this design persist one live-row representation-change stamp, `dms.Document.ChangeVersion`
- redesign references to `ContentVersion` map to that same stamp responsibility
- the current-backend schema must not persist both `dms.Document.ChangeVersion` and `dms.Document.ContentVersion` as separate live-row fields for the same purpose

## 9. The design is not coupled to the transitional compatibility backend

`EdFi.DataManagementService.Old.Postgresql` may remain in the repo temporarily for backward compatibility and current-behavior comparison, but DMS-843 is defined at the contract, data-model, authorization, and core-service seam level so the planned replacement backend can implement it directly.

Normative design scope:

- public API behavior
- synchronization rules
- tracking artifacts and storage semantics
- authorization parity requirements
- rollout and validation constraints

Informative references only:

- project names
- component names
- file and folder paths used as current implementation touchpoints

Implementation planning instead targets:

- public API and middleware seams in `src/dms/core` and `src/dms/frontend`
- relational backend contracts such as `IDocumentStoreRepository` and `IQueryHandler`
- relational-backend metadata, DDL, and dialect modules under `src/dms/backend/EdFi.DataManagementService.Backend.*`

This keeps the design anchored to current behavior where parity matters without wiring approval of the design to the lifetime of the transitional project.

## 10. Key payload field names use the shortest unique identity-path suffix

`keyValues`, `oldKeyValues`, and `newKeyValues` stay resource-scoped JSON objects even though delete and key-change tracking share common tables.

Required rule:

- derive field aliases from `ResourceSchema.IdentityJsonPaths`, not from physical column names
- start with the leaf property name and prepend parent property segments only when needed to make the alias unique within that resource
- emit the final alias in lower camel case without separators, so `$.schoolReference.schoolId` becomes `schoolId` when unique or `schoolReferenceSchoolId` when another identity path also ends in `schoolId`
- materialize fields in declared `IdentityJsonPaths` order

This rule is required because authoritative schemas already contain composite identities with repeated leaf names such as multiple `schoolId` or `educationOrganizationId` members.

## 11. DMS-843 uses Ed-Fi-style inclusive lower-bound windows

The feature uses the following Change Query window semantics:

```text
minChangeVersion <= ChangeVersion <= maxChangeVersion
```

When `maxChangeVersion` is omitted, the effective rule is:

```text
minChangeVersion <= ChangeVersion
```

This aligns the package to current Ed-Fi client guidance, which treats `minChangeVersion` as the next starting watermark to include.

## 12. Avoiding snapshot tables does not create a snapshot-free correctness guarantee

The feature continues to avoid server-side snapshot history tables and uses bounded synchronization windows:

```text
minChangeVersion <= ChangeVersion <= maxChangeVersion
```

The Ed-Fi ODS/API client guidance uses snapshot isolation when a client needs one correctness-safe view across a full synchronization pass.

DMS-843 v1 does not expose a client-selectable snapshot or equivalent consistent-read mode. All Change Query requests therefore operate against current committed state, and the package treats synchronization under concurrent writes as best-effort rather than a guaranteed gap-free export.

## Feature Scope

The full Change Queries feature defined by this package includes:

- `GET /{routePrefix}changeQueries/v1/availableChangeVersions`
- changed-resource filtering on existing collection GET routes via `minChangeVersion` and `maxChangeVersion`
- `GET /{routePrefix}data/{projectNamespace}/{endpointName}/deletes`
- `GET /{routePrefix}data/{projectNamespace}/{endpointName}/keyChanges`
- live-row `ChangeVersion` stamping on inserts and representation-changing updates
- delete tombstones with natural-key and authorization projection data
- key-change tracking rows with old-key and new-key values plus authorization projection data
- deterministic ordering for changed-resource and delete queries
- deterministic ordering and window collapse for key-change queries
- replay-floor-aware `oldestChangeVersion` and `newestChangeVersion` semantics
- an optional `dms.DocumentChangeEvent` journal path that uses the same API contract

## Non-Goals

This feature design does not include:

- server-side snapshot tables
- CDC-based or streaming-based change-query behavior
- event sourcing
- a historical payload store for updates
- redesign of the existing item payload shape
- breaking changes to GET, POST, PUT, or DELETE routes already used by clients

## Feature Artifact Set

The feature is represented by two kinds of internal artifacts.

## Required core artifacts

These artifacts are required for the feature to exist:

- `dms.ChangeVersionSequence`
- `dms.Document.ChangeVersion`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- request validation and routing for `availableChangeVersions`, `/deletes`, and `/keyChanges`
- changed-resource filtering on existing collection GET routes

## Optional internal alignment artifact

This artifact is optional and may be implemented when scale or redesign alignment justifies it:

- `dms.DocumentChangeEvent`

When present, it changes only the internal selection strategy for live changed-resource queries. It does not change the public API contract or the delete strategy.

## Conditional retention artifact

This artifact is required before any purge capability is enabled:

- `dms.ChangeQueryRetentionFloor`

When present, it makes replay-floor-safe `availableChangeVersions` computation explicit under purge. It does not change changed-resource payload semantics, delete semantics, or any public route shape.

## Decision Summary

| Decision area | Decision |
| --- | --- |
| API compatibility | Additive only |
| Canonical live source | `dms.Document` |
| Change token model | Live-row `ChangeVersion` column |
| Delete model | `dms.DocumentDeleteTracking` tombstones |
| Mixed change table | Rejected |
| Delete vs key-change storage | Separate dedicated tables |
| Key payload naming | Shortest unique identity-path suffix aliases in `IdentityJsonPaths` order |
| Delete table schema | `dms` |
| Window semantics | Inclusive `minChangeVersion`; inclusive `maxChangeVersion` when supplied |
| Snapshot policy | Avoid snapshot history tables; no client-visible snapshot or consistent-read mode in DMS-843 v1 |
| Ordering model | Global monotonic `dms.ChangeVersionSequence` |
| Replay floor model | Instance-wide `oldestChangeVersion` replay floor; per-surface floor metadata when purge is enabled |
| Update history | Not required for public changed-resource queries |
| `keyChanges` | Peer Change Queries endpoint to `/deletes`, backed by explicit tracking |
| Journal alignment | `dms.DocumentChangeEvent` is optional and complementary |
| Backend-redesign relationship | Semantic artifact alignment rather than physical-name matching |
| Profile interaction | Changed-resource eligibility is resource-level; readable profiles filter only the returned representation |

## Open Operational Inputs

The design is complete enough for implementation planning, but the following operational choices should be confirmed before rollout planning is finalized:

- tombstone retention period
- whether descriptor endpoints participate in Change Queries with no exclusions
- whether the optional `dms.DocumentChangeEvent` journal should be implemented in the initial build or left as a later optimization story
