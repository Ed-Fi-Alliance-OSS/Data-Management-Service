# Backend Redesign Alternative: Minimal Natural-Key Propagation (Identity Only) + Touch Cascades (Non-Identity)

## Status

Draft (alternative design exploration).

## Context

The baseline backend redesign keeps stable surrogate keys (`DocumentId`) for all references and avoids ODS-style rewrite cascades by:

- storing references as `..._DocumentId` foreign keys, and
- maintaining derived artifacts (notably `dms.ReferentialIdentity`, `_etag/_lastModifiedDate`, and Change Query support) via application-managed closure recompute + `dms.ReferenceEdge` + read-time derivation (`reference/design/backend-redesign/update-tracking.md`).

This alternative combines two DB-centric patterns:

1. **Natural-key propagation for identity components only** (from `alternatives/ods-style-natural-key-cascades.md`), used to keep *identity-bearing* relationships row-local and allow the DB to propagate identity values needed to recompute `ReferentialId` without application-managed identity-closure traversal.
2. **“Touch cascades” for representation metadata** (from `alternatives/touch-cascade.md`), used to update stored `_etag/_lastModifiedDate/ChangeVersion` for documents whose representations change due to **non-identity** reference dependencies.

The key constraint driving this hybrid is:

- **Minimize natural key propagation**: only propagate identity values where required to keep `ReferentialId` correct (identity components).
- **Still support indirect representation metadata** changes for non-identity references.
- **Avoid double-touch**: identity-component referrers should not be touched by the “touch cascade”, because they are already updated locally by natural-key propagation.

## Goals

1. Keep `DocumentId` as the only persisted reference key for relational integrity and joins.
2. Maintain `dms.ReferentialIdentity` correctness for reference-bearing identities without `dms.ReferenceEdge` closure recompute.
3. Make stored representation metadata (`_etag/_lastModifiedDate/ChangeVersion`) change when any embedded reference identity changes, including non-identity dependencies.
4. Avoid “double touch” on identity-component referrers.
5. Remain implementable on PostgreSQL and SQL Server (noting engine caveats).

## Core Concepts

### Identity components vs representation dependencies

- **Identity component**: a document reference whose projected identity values participate in the parent document’s identity (`identityJsonPaths`).
  - If an identity component’s projected values change, the parent’s `ReferentialId` must change.
- **Representation dependency (non-identity)**: a document reference whose projected identity values appear in the API representation but do *not* participate in the parent’s identity.
  - If it changes, the parent’s representation metadata must change, but its `ReferentialId` does not.

### Two cascades, different purposes

1. **Natural-key propagation (identity-only)**:
   - propagates referenced identity values into *dependent identity component columns* via `ON UPDATE CASCADE`,
   - enabling row-local recompute of the dependent’s `ReferentialId`.
2. **Touch cascade (non-identity)**:
   - updates stored representation metadata for documents affected by non-identity reference identity changes,
   - without rewriting dependent resource rows.

## Data Model Changes

### 1) Identity-component natural key columns (minimal propagation)

For each **identity-component** document reference site (derived from ApiSchema `identityJsonPaths` + `documentPathsMapping.referenceJsonPaths`):

- Keep the stable FK column: `<RefName>_DocumentId bigint NOT NULL`.
- Add only the referenced identity value columns that are needed for the parent identity, e.g.:
  - `SchoolId int NOT NULL` for `SchoolReference`,
  - `StudentUniqueId nvarchar(32) NOT NULL` for `StudentReference`.

Then add:

- a unique constraint on the referenced resource root table to support a composite FK, e.g.:
  - `UNIQUE (DocumentId, SchoolId)`
- a composite FK from the referencing table:
  - `FOREIGN KEY (<RefName>_DocumentId, <RefIdentityCols...>) REFERENCES <RefTable>(DocumentId, <RefIdentityCols...>) ON UPDATE CASCADE`

Result:
- referenced identity updates propagate only into the minimal set of dependent identity-component columns,
- allowing downstream `ReferentialId` recompute triggers to be row-local.

### 2) Non-identity references remain `DocumentId`-only

For **non-identity** document references:

- store only the stable `<RefName>_DocumentId` FK.
- do not duplicate referenced identity values.

This is the “minimize natural-key propagation” requirement.

### 3) Stored representation metadata on `dms.Document`

This alternative assumes stored representation metadata, for example:

- `RepresentationChangeVersion bigint NOT NULL`
- `RepresentationLastModifiedAt timestamp/datetime2 NOT NULL`
- optional `RepresentationEtag` (or derive ETag from `RepresentationChangeVersion`)

Local writes (content/identity changes) and touch cascades both update these columns.

## Reverse-Reference Projection (No `dms.ReferenceEdge`)

To “touch” referrers without an edge table, the DB needs reverse lookups by `ChildDocumentId`.

This design generates a projection over the physical FK columns:

### `dms.AllDocumentReferences` (generated)

A DDL-generated `UNION ALL` view (or per-target views) with shape:

```text
(ParentDocumentId, ChildDocumentId, IsIdentityComponent)
```

Where:
- `ParentDocumentId` is the root document id (all child tables carry it as the first parent key part),
- `ChildDocumentId` is the referenced `..._DocumentId` FK value,
- `IsIdentityComponent` is a constant per FK site derived from ApiSchema classification.

Index requirement:
- every `..._DocumentId` FK column must be indexed, ideally `(<fk>) INCLUDE (DocumentId)`.

## Write-Side Behavior

### A) Direct writes (POST/PUT)

Within a transaction updating document `P`:

1. Persist resource root/child rows (still `DocumentId`-centric).
2. Update the document’s own local identity columns (including identity-component natural key columns) as needed.
3. Recompute and upsert `dms.ReferentialIdentity` for `P` (DB trigger or application code).
4. Stamp `dms.Document.IdentityVersion` when identity projection changes, and stamp stored representation metadata for local changes.

### B) Identity updates propagate via `ON UPDATE CASCADE` (identity-only)

When a referenced document `D` changes an identity value:

- the DB cascades that update into identity-component natural key columns on dependent rows,
- dependent-row triggers recompute each dependent’s `dms.ReferentialIdentity` row(s) and stamp `dms.Document.IdentityVersion`,
- this continues transitively through identity-bearing relationships (like ODS key cascades), but only along identity-component sites (minimal propagation).

This is the replacement for application-managed identity-closure recompute.

## Touch Cascades for Non-Identity Dependencies (No Double Touch)

When a document’s **identity projection** changes (represented by `dms.Document.IdentityVersion` changing), the DB must update stored representation metadata for referrers whose representations embed the changed identity values.

### No-double-touch rule

If a parent references a child as an **identity component**, then natural-key propagation causes a *local row update* on the parent, and local stamping already updates the parent’s representation metadata.

Therefore the touch cascade targets only:

```text
TouchTargets(child) =
  NonIdentityReferrers(child)
  \ IdentityReferrers(child)
```

Where referrers are computed via `dms.AllDocumentReferences` using the `IsIdentityComponent` flag.

This also handles “same child referenced in multiple places”: if any identity-component site exists, the parent is excluded from touch.

### Trigger location

A practical trigger point is:

- `AFTER UPDATE OF IdentityVersion ON dms.Document`

Because:
- natural-key propagation + per-resource triggers should already update `dms.Document.IdentityVersion` when a document’s identity projection changes (whether direct or cascaded),
- touching only needs to react to “identity changed” events, not to raw table updates.

The touch cascade trigger then:
- finds `changed_children` = documents whose `IdentityVersion` changed,
- finds `touch_targets` via `dms.AllDocumentReferences` with the no-double-touch rule,
- updates `dms.Document.RepresentationChangeVersion/RepresentationLastModifiedAt` for those targets.

See `alternatives/touch-cascade.md` for example trigger SQL patterns.

## Read-Side Behavior

- Resource representations are still reconstituted from relational tables and current referenced rows (stable `DocumentId` FKs).
- Identity-component natural key columns may optionally be used to avoid some joins when projecting reference identity objects, but non-identity references still require reading the referenced identity values from the referenced resource (or an abstract identity table).
- `_etag/_lastModifiedDate/ChangeVersion` are served from stored representation metadata columns on `dms.Document`.

## Cross-Engine Considerations (Important)

- PostgreSQL generally supports deep cascade graphs, but large identity updates can still generate heavy write amplification and lock contention.
- SQL Server has restrictions around cycles and multiple cascade paths; `ON UPDATE CASCADE` across many tables may be blocked by DDL validation or may be operationally risky at scale.
  - If `ON UPDATE CASCADE` is not viable on SQL Server, the “identity-only propagation” may require trigger-based propagation instead, which reintroduces complexity similar to ODS.

## Benefits

- Avoids `dms.ReferenceEdge` while still achieving identity-correct `ReferentialId` maintenance (DB-driven propagation).
- Minimizes natural key propagation: only identity-component references carry duplicated identity values.
- Supports indirect representation metadata changes for non-identity dependencies via DB touch cascades.
- Avoids double-touch by construction (`NonIdentityReferrers EXCEPT IdentityReferrers`).

## Costs / Risks

- Identity updates can still fan out widely (even when limited to identity components), causing write amplification, locks, and log/WAL growth.
- DDL generation becomes substantially more complex (extra columns, unique constraints, composite FKs, abstract identity tables, triggers).
- SQL Server feasibility depends heavily on cascade-path constraints and may require a different implementation strategy.

## Open Questions

1. Should representation metadata stamping use one stamp per touched row or one per triggering transaction?
2. Do we journal touched updates for Change Queries (e.g., insert into change-event tables), and if so, how is that enforced (trigger on `dms.Document`)?
3. What is the minimum viable abstract-identity materialization needed to support polymorphic references with identity-only cascades?
4. How do we detect and guard against “hub” identity updates that would touch millions of rows (operational guardrails)?
