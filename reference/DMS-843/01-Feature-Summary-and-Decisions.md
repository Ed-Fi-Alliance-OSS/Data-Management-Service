# Feature Summary and Decisions

## Objective

Implement Ed-Fi Change Queries for the current Data Management Service storage and runtime model in a way that:

- preserves Ed-Fi changed-record-query semantics for route shape, window behavior, and tracked-change authorization criteria where DMS-843 adopts ODS parity
- preserves existing DMS API behavior when Change Queries are not used
- integrates `Use-Snapshot` into synchronization reads so absent or `false` uses the live flow and `true` uses the snapshot-backed flow
- uses the canonical `dms.Document` row as the source of truth
- keeps the design aligned to the backend-redesign update-tracking and authorization directions where DMS-843 introduces bridge artifacts

## Approval Target and Design Posture

**This package targets ODS-compatible Change Query behavior in DMS context through a redesign-aligned implementation.**

The approval bar is correctness and alignment, not mechanical reproduction of ODS internals:

- ODS route shapes, windowing, and synchronization semantics are adopted where they apply directly to DMS.
- `Use-Snapshot` is a first-class feature in this package; both live and snapshot-backed synchronization flows are in scope.
- `availableChangeVersions` uses ODS-compatible sequence-ceiling watermark semantics.
- Each deliberate departure from legacy ODS behavior is documented as an explicit owned decision in the relevant section, not as an undeclared omission.

Known explicit DMS-specific choices in this package:

- tracked-change authorization includes the accepted DMS-specific ownership exception for `/deletes` and `/keyChanges`; legacy ODS `ReadChanges` does not currently apply ownership filtering on those surfaces, but DMS-843 does because redesign auth treats ownership as a first-class DMS authorization input.
- when the selected synchronization surface has not yet allocated any change-version values, `availableChangeVersions` returns bootstrap `0/0`; this is verified ODS-output-compatible behavior: legacy ODS returns `0/0` on an empty instance because `MAX(ChangeVersion)` across empty tables evaluates to `NULL`, which ODS serializes as `0`; DMS-843 reaches the same output via sequence-ceiling arithmetic (`next value - 1` = `0` before the first allocation); the output contracts are identical, though the internal computation paths differ.
- `_etag` and `_lastModifiedDate` remain inside `EdfiDoc` on the current backend; redesign metadata-stamp storage ownership is deferred to a later backend replacement phase.
- Snapshot history tables remain out of scope, but snapshot lifecycle behavior for `Use-Snapshot` requests is in scope and must preserve ODS-compatible synchronization guarantees without changing existing route shapes.

**Custom-view tracked-change eligibility:**

Tracked-change authorization supports only resources whose authorization inputs are reducible at write time to captured basis-resource `DocumentId` values plus any named `relationshipInputs`. Open-ended custom-view authorization that depends on arbitrary mutable non-identifying live-row values at query time is explicitly not supported for tracked changes in this package. Resources whose tracked-change authorization cannot be reduced to this contract must be rejected by security-metadata validation when claim-set metadata is loaded or refreshed rather than silently degrading. See `05-Authorization-and-Delete-Semantics.md`, `AuthorizationBasis Semantics`, for the normative structural contract, eligibility restriction, enforcement ownership, and failure gates.

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
- The active relational-backend work in `src/dms/backend/EdFi.DataManagementService.Backend.*` includes both PostgreSQL and MSSQL targets, so DMS-843 implementation planning must preserve shared relational seams while accounting for dialect-specific behavior where required.

These facts require the feature design to distinguish representation changes from authorization-maintenance updates, and they require dedicated `/deletes` and `/keyChanges` routes.

## Whole-Feature Decisions

## 1. Public API compatibility is additive

The feature adds Change Query behavior to existing collection GET routes and introduces new Change Query endpoints where needed, but it does not break existing resource APIs.

## 2. The canonical update-tracking stamps live on `dms.Document`

The design stores the public Change Query token on the canonical live row as `dms.Document.ChangeVersion`.

For backend-redesign alignment, this column is the semantic equivalent of redesign `dms.Document.ContentVersion`.

The current backend must also persist `dms.Document.IdentityVersion` as the bridge equivalent of redesign `dms.Document.IdentityVersion`.

Within the DMS-843 package:

- `ChangeVersion` is the current-backend physical name for the served representation-change stamp
- redesign references to `ContentVersion` map to that same stamp responsibility
- `IdentityVersion` is a distinct live-row stamp used for identity-change tracking alignment and must not be collapsed into `ChangeVersion`

Bridge migration rule:

- redesign-aligned migrations must map the existing current-backend `dms.Document.ChangeVersion` column to redesign `ContentVersion` semantics instead of adding a second physical stamp column
- a conforming migration path may either keep `ChangeVersion` as the physical column name and map redesign logic to it, or rename `ChangeVersion` to `ContentVersion` in one migration step
- a conforming migration path must fail fast if both `ChangeVersion` and `ContentVersion` appear simultaneously as active live-row representation stamps

## 3. Deletes require tombstones

Deletes cannot be inferred from the live row because the live row disappears. The feature therefore requires `dms.DocumentDeleteTracking` in the `dms` schema.

Public `/deletes` reads must still apply ODS-style same-window re-add suppression, so tombstone capture is required even though a captured tombstone is not always emitted back to clients.

## 4. Updates do not require historical payload storage

Ed-Fi changed-resource queries return the latest current representation of resources that changed within the requested window. They do not return every intermediate mutation and they do not require snapshot payload history.

## 5. `DocumentChangeEvent` is required and remains distinct from tombstones

The design requires `dms.DocumentChangeEvent` as the append-only live-change journal for changed-resource queries.

It does not replace tombstones because delete queries need data that survives deletion of the live row.

## 6. `keyChanges` is a peer endpoint to `/deletes`

The design includes the Ed-Fi `keyChanges` route as part of the core Change Queries feature, alongside `/deletes`. Because current-state rows do not preserve prior natural-key values, the design adds explicit old-and-new natural-key tracking rather than trying to infer key changes later from the current row, deletes, or snapshots.

For ODS parity, `/keyChanges` uses its own tracked-row public token rather than reusing the live document `ChangeVersion` from the identity-changing write.

## 7. Delete and key-change tracking stay in separate tables

The design intentionally keeps `dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` as separate artifacts rather than merging them into one generic mixed event table.

Reasons:

- delete queries require tombstones that survive removal of the live row and preserve `keyValues`
- key-change queries require transition rows that preserve both `oldKeyValues` and `newKeyValues`
- the two routes have different read shapes, ordering concerns, and event/result-shaping rules
- live changed-resource selection remains a current-state problem solved by the live-row content stamp and required journal, not by tombstones

## 8. Backend-redesign alignment uses required bridge artifacts for update tracking and tracked-change authorization

The backend-redesign update-tracking docs are already normative for live representation stamping, identity stamping, `dms.ResourceKey`, and `dms.DocumentChangeEvent`.

The backend-redesign authorization docs are companion context for ownership-based and DocumentId-based authorization behavior. DMS-843 therefore has to preserve not only update-tracking artifact responsibilities, but also the tracked-change authorization inputs needed to keep delete and key-change visibility aligned to the chosen ODS criteria.

Accepted tracked-change ownership exception:

- backend-redesign [`auth.md`](../design/backend-redesign/design-docs/auth.md) says DMS follows the ODS authorization design unless specified otherwise and then specifies that `CreatedByOwnershipTokenId` is stored on shared `dms.Document` and always populated in DMS
- backend-redesign [`data-model.md`](../design/backend-redesign/design-docs/data-model.md) and [`transactions-and-concurrency.md`](../design/backend-redesign/design-docs/transactions-and-concurrency.md) treat that field as a normal DMS authorization input in the canonical row model and write pipeline
- redesign [`auth-redesign-subject-edorg-model.md`](../design/auth/auth-redesign-subject-edorg-model.md) preserves ownership-style constraints in the target authorization direction
- DMS-843 therefore treats ownership filtering on `/deletes` and `/keyChanges` as an accepted DMS-specific authorization exception, not as an unresolved review gap and not as a claim that legacy ODS `ReadChanges` already behaves that way

This design remains aligned by preserving the same artifact responsibilities:

- current-backend `dms.ResourceKey` and `dms.Document.ResourceKeyId` provide the redesign resource-key lookup and narrow journal filter key
- current-backend `dms.Document.ChangeVersion` is the semantic equivalent of redesign `dms.Document.ContentVersion`
- current-backend `dms.Document.IdentityVersion` is the semantic equivalent of redesign `dms.Document.IdentityVersion`
- current-backend `dms.DocumentChangeEvent` is the same kind of live-change journal used by redesign and is required for changed-resource execution
- current-backend tracked-change rows must preserve redesign-relevant authorization inputs such as `CreatedByOwnershipTokenId` and row-local authorization basis data needed for DocumentId-based relationship and custom-view authorization after deletes or later key changes
- current-backend `dms.DocumentDeleteTracking` is a bridge artifact for delete semantics that redesign will also need in semantically equivalent form
- current-backend `dms.DocumentKeyChangeTracking` is a bridge artifact for old/new natural-key transitions that redesign will also need in semantically equivalent form

The alignment target is therefore artifact-responsibility and behavior parity where DMS-843 explicitly chooses it, not reuse of identical table names across storage models.

Required interpretation:

- current-backend implementations of this design persist one live-row representation-change stamp, `dms.Document.ChangeVersion`
- redesign references to `ContentVersion` map to that same stamp responsibility
- current-backend implementations also persist one distinct identity-change stamp, `dms.Document.IdentityVersion`
- tracked-change artifacts must preserve enough authorization data to support redesign ownership and DocumentId-based authorization concepts during `/deletes` and `/keyChanges`
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
- shared relational-backend metadata and DDL modules under `src/dms/backend/EdFi.DataManagementService.Backend.*`
- dialect-specific backend modules including `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql` and `src/dms/backend/EdFi.DataManagementService.Backend.Mssql`

This keeps the design anchored to current behavior where parity matters without wiring approval of the design to the lifetime of the transitional project.

## 10. Key payload field names use the shortest unique identity-path suffix

`keyValues`, `oldKeyValues`, and `newKeyValues` stay resource-scoped JSON objects even though delete and key-change tracking share common tables.

Required rule:

- derive field aliases from `ResourceSchema.IdentityJsonPaths`, not from physical column names
- start with the leaf property name and prepend parent property segments only when needed to make the alias unique within that resource
- emit the final alias in lower camel case without separators, so `$.schoolReference.schoolId` becomes `schoolId` when unique or `schoolReferenceSchoolId` when another identity path also ends in `schoolId`
- materialize fields in declared `IdentityJsonPaths` order

This rule is required because authoritative schemas already contain composite identities with repeated leaf names such as multiple `schoolId` or `educationOrganizationId` members.

## 11. DMS-843 uses ODS-style independently optional ChangeVersion bounds

The feature uses the following Change Query window semantics when both bounds are supplied:

```text
minChangeVersion <= ChangeVersion <= maxChangeVersion
```

When only `minChangeVersion` is supplied, the effective rule is:

```text
minChangeVersion <= ChangeVersion
```

When only `maxChangeVersion` is supplied, the effective rule is:

```text
ChangeVersion <= maxChangeVersion
```

For the overloaded collection GET route:

- if both bounds are absent, the request remains an ordinary non-Change-Query collection GET
- if either bound is supplied, the request switches into changed-resource mode

For the dedicated `/deletes` and `/keyChanges` routes:

- each bound remains independently optional
- if both bounds are absent, the route returns the full retained tracked-change surface for that resource

This aligns DMS-843 to ODS query-window behavior while keeping the existing collection GET route backward-compatible when no change-query parameters are supplied.

## 12. DMS-843 integrates `Use-Snapshot` into the core synchronization design with in-scope lifecycle handling

The feature continues to avoid server-side snapshot history tables and does not require historical payload reconstruction.

The Ed-Fi ODS/API platform exposes `Use-Snapshot` behavior when a client needs one correctness-safe view across a synchronization pass.

DMS-843 uses a client-selectable `Use-Snapshot` header to choose the synchronization flow. When the header is absent or `false`, requests operate against current committed state exactly as described elsewhere in this package.

When `Use-Snapshot = true`, the request executes against a configured read-only snapshot source for the resolved DMS instance. The same changed-resource, delete, key-change, and `availableChangeVersions` artifacts remain authoritative; only the selected flow and read source change.

Required interpretation:

- the design always defines both flows; if the deployment cannot provide a usable snapshot source, `Use-Snapshot = true` requests must fail explicitly rather than silently reverting to the live flow
- snapshot mode does not add snapshot payload tables, alternate change tokens, or different route shapes
- snapshot-backed reads return the current representation visible inside the snapshot, not historical payload versions
- snapshot configuration is instance-scoped: each resolved DMS instance either has no snapshot binding or has one configured read-only derivative binding that DMS resolves for `Use-Snapshot = true`
- the configured derivative must expose the same DMS schema and synchronization artifacts as the live instance, including `dms.ChangeVersionSequence`, journals, tombstones, key-change rows, and tracked-change authorization companion tables
- the configured derivative binding must remain frozen long enough for one synchronization pass or equivalent bounded workflow; clients obtain one window from that derivative and then continue to read against that same bound derivative for the pass
- DMS owns snapshot lifecycle enforcement for `Use-Snapshot` requests, including derivative selection, binding validation, required-artifact validation, and explicit failure when the configured derivative is missing, expired, refreshed away, or otherwise no longer usable
- if lifecycle validity cannot be preserved for the requested snapshot-backed pass, DMS must fail the request or pass explicitly with the documented snapshot-unavailable `404 Not Found` contract rather than degrading silently

## Existing DMS-specific choices that `Use-Snapshot` does not change

Adding `Use-Snapshot` does not change several existing DMS-specific product choices. The package does not claim blanket parity with every legacy ODS internal semantic or every backend-redesign storage decision.

Those unchanged DMS-specific choices include:

- tracked-change authorization continues to include the accepted DMS-specific ownership exception on `/deletes` and `/keyChanges`; `Use-Snapshot` does not alter that DMS product choice
- bootstrap `0/0` remains the DMS starting watermark normalization when the selected synchronization surface has not yet allocated any change-version values
- current-backend `_etag` and `_lastModifiedDate` remain stored inside `EdfiDoc`; DMS-843 therefore aligns to redesign change-tracking responsibilities without yet adopting redesign-style dedicated metadata-stamp columns for those fields

Key-change token boundary:

- the `/keyChanges` public token model is part of the DMS-843 public API contract and is therefore in scope for this package
- DMS-843 adopts legacy ODS parity for this contract by allocating a distinct public key-change token from the same `dms.ChangeVersionSequence` for the tracked key-change row rather than reusing the live document `ChangeVersion`

## Why the remaining DMS-specific tracked-change choices are intentional

These choices are not arbitrary. They follow the current DMS storage model and the backend-redesign direction that DMS-843 is trying to preserve while keeping `/keyChanges` aligned to legacy ODS event semantics.

- tracked-change authorization stores basis-resource `DocumentId` values as `basisDocumentIds` because backend-redesign custom-view and relationship authorization in DMS is DocumentId-based rather than natural-key-based, and `/deletes` plus `/keyChanges` must still authorize correctly after the live row or relationship rows are gone
- `/keyChanges` adopts the legacy ODS public token model because strict key-change sequencing parity matters more here than collapsing every public tracked-change surface onto one shared live-row token
- tracked-change authorization includes ownership filtering because backend-redesign [`auth.md`](../design/backend-redesign/design-docs/auth.md) treats `CreatedByOwnershipTokenId` on `dms.Document` as a first-class authorization input and redesign [`auth-redesign-subject-edorg-model.md`](../design/auth/auth-redesign-subject-edorg-model.md) preserves ownership-style constraints, even though that extends legacy ODS `ReadChanges` behavior
- if a future product requirement later demands one shared public token across changed resources and key changes, that should be treated as a deliberate design change rather than as an implementation-side substitution

## 13. Ordering must be deterministic and backend-shape-aware

The feature requires deterministic ordering, but it does not require one immutable physical tie-breaker shape across all backend generations.

Required rule:

- changed-resource, `/deletes`, and `/keyChanges` are ordered by `ChangeVersion` plus a stable backend-local document tie-breaker
- for the current backend, the tie-breaker is `DocumentPartitionKey`, `DocumentId`
- for redesign-aligned backends where `DocumentPartitionKey` is absent, the tie-breaker is the redesign-equivalent stable document key (for example, `DocumentId`)
- query contracts, paging behavior, and deterministic replay guarantees must remain equivalent across those backend shapes

## 14. Change Queries is a configurable feature that follows the Ed-Fi default-on posture

DMS-843 should introduce an application configuration flag for Change Queries rather than making the capability permanently on.

When the setting is absent, DMS should follow the same default-on posture documented by Ed-Fi ODS/API for Change Queries.

Required rule:

- add `AppSettings.EnableChangeQueries`
- if the flag is absent, treat it as `true`
- when the flag is `false`, dedicated Change Query routes are not exposed and collection GET requests that supply change-query parameters fail explicitly rather than silently falling back
- use the flag to control exposure of the Change Queries API surface
- do not treat the flag as a substitute for the required database artifacts, migrations, or cleanup operations

## Feature Scope

The full Change Queries feature defined by this package includes:

- `GET /{routePrefix}changeQueries/v1/availableChangeVersions`
- changed-resource filtering on existing collection GET routes via independently optional `minChangeVersion` and `maxChangeVersion`
- `GET /{routePrefix}data/{projectNamespace}/{endpointName}/deletes`
- `GET /{routePrefix}data/{projectNamespace}/{endpointName}/keyChanges`
- live-row `ChangeVersion` stamping on inserts and representation-changing updates
- live-row `IdentityVersion` stamping on inserts and identity-changing updates
- resource-key lookup and `ResourceKeyId` assignment for live rows
- required `journal + verify` changed-resource selection through `dms.DocumentChangeEvent`
- delete tombstones with natural-key and tracked-change authorization data
- key-change tracking rows with old-key and new-key values plus tracked-change authorization data
- deterministic ordering for changed-resource and delete queries
- deterministic ordering and one-row-per-key-change-event semantics for key-change queries
- same-window delete re-add suppression on the public `/deletes` surface
- tracked-change authorization inputs sufficient for the selected tracked-change authorization contract, including ODS-style delete-aware relationship visibility plus redesign ownership and DocumentId-based authorization concepts
- bootstrap `oldestChangeVersion = newestChangeVersion = 0` semantics for empty surfaces in phase 1, with replay-floor-aware bounds left available for a later retention phase
- `Use-Snapshot` handling for synchronization reads so clients select either the live best-effort flow or the snapshot-backed flow without changing route shapes

## Non-Goals

This feature design does not include:

- server-side snapshot tables
- DMS-managed snapshot creation, refresh, or retention orchestration
- CDC-based or streaming-based change-query behavior
- event sourcing
- a historical payload store for updates
- redesign of the existing item payload shape
- breaking changes to GET, POST, PUT, or DELETE routes already used by clients

## Short Glossary

- resolved DMS instance: the API instance selected by the route prefix or equivalent instance-resolution mechanism
- derivative: one read surface for that resolved instance, either the live-primary derivative or the configured snapshot derivative
- snapshot binding: the instance-scoped configuration that tells DMS which read-only derivative `Use-Snapshot = true` should use
- instance-scoped sequence ceiling: the highest allocated value of that resolved instance derivative's `dms.ChangeVersionSequence`

## Feature Artifact Set

The feature is represented by two kinds of internal artifacts.

## Required core artifacts

These artifacts are required for the feature to exist:

- `dms.ChangeVersionSequence`
- `dms.ResourceKey`
- `dms.Document.ResourceKeyId`
- `dms.Document.ChangeVersion`
- `dms.Document.IdentityVersion`
- `dms.DocumentChangeEvent`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- request validation and routing for `availableChangeVersions`, `/deletes`, and `/keyChanges`
- changed-resource filtering on existing collection GET routes

## Deferred retention artifact

This artifact is not required for the initial DMS-843 delivery. It is a later-phase option to consider only if retention purge is introduced:

- `dms.ChangeQueryRetentionFloor`

If a later phase adds purge, this artifact makes replay-floor-safe `availableChangeVersions` computation explicit. It does not change changed-resource payload semantics, delete semantics, or any public route shape.

## Decision Summary

| Decision area | Decision |
| --- | --- |
| API compatibility | Additive only |
| Canonical live source | `dms.Document` |
| Change token model | changed-resource rows use live-row `ChangeVersion`; `/keyChanges` uses a distinct tracked-row public token |
| Delete model | `dms.DocumentDeleteTracking` tombstones |
| Mixed change table | Rejected |
| Delete vs key-change storage | Separate dedicated tables |
| Key payload naming | Shortest unique identity-path suffix aliases in `IdentityJsonPaths` order |
| Delete table schema | `dms` |
| Window semantics | Inclusive `minChangeVersion`; inclusive `maxChangeVersion` when supplied |
| Snapshot policy | Avoid snapshot history tables; `Use-Snapshot` absent or `false` selects the live flow and `true` selects the snapshot-backed flow against an instance-scoped configured read-only snapshot derivative binding |
| Ordering model | Instance-scoped monotonic `dms.ChangeVersionSequence` within each resolved DMS instance derivative |
| Replay floor model | Phase 1 uses retained tracking data with bootstrap `0/0`; a later retention phase may add per-surface replay-floor metadata if purge is introduced |
| Update history | Not required for public changed-resource queries |
| Feature availability | `AppSettings.EnableChangeQueries`; absent = `true` to align with Ed-Fi default-on behavior, and feature-off requests fail explicitly rather than silently downgrading |
| `keyChanges` | Peer Change Queries endpoint to `/deletes`, backed by explicit tracking |
| Resource keying | `dms.ResourceKey` plus `dms.Document.ResourceKeyId` provide the redesign-aligned filter key for live journals |
| Identity tracking | `dms.Document.IdentityVersion` is required as the current-backend equivalent of redesign `IdentityVersion` |
| Journal alignment | `dms.DocumentChangeEvent` is required for redesign-aligned `journal + verify` execution |
| Backend-redesign relationship | Semantic alignment plus required bridge artifacts where current-backend physical names differ |
| Profile interaction | Changed-resource eligibility is resource-level; readable profiles filter only the returned representation |

## Open Operational Inputs

The design is complete enough for implementation planning, but the following later-phase operational choice should be confirmed before rollout planning is finalized:

- whether a later phase should introduce tombstone retention and purge behavior
- descriptor endpoints participate in Change Queries with no exclusions in DMS-843 v1; any future exclusion must be an explicit endpoint-level product decision rather than an implicit gap in this package
