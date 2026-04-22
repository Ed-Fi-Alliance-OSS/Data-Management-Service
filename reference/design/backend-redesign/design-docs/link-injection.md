# Link Injection Design

## Status

Draft.

This document describes **link injection** for document-reference properties in DMS GET responses
against the relational backend. The feature emits a `{ rel, href }` object on every fully-defined
document reference (FKs into `dms.Document`) in a response body, with the same shape and path-tail
form ODS uses.
This design applies only to document references backed by `..._DocumentId`.
Descriptor references remain on their existing canonical-URI string surface.

- [overview.md](overview.md) — backend redesign overview
- [data-model.md](data-model.md) — `dms.Document`, `dms.ResourceKey`, abstract identity tables
- [flattening-reconstitution.md](flattening-reconstitution.md) — reconstitution engine and
  `DocumentReferenceBinding`
- [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6 — the existing descriptor URI
  auxiliary-result-set pattern this feature reuses
- [auth.md](auth.md) — authorization strategy (link emission is source-resource-authorized only)
- Jira: [DMS-622](https://edfi.atlassian.net/browse/DMS-622)
- Epic: [DMS-988 — Relational Read Path](../epics/08-relational-read-path/EPIC.md)

---

## Table of Contents

- [Link Injection Design](#link-injection-design)
  - [Status](#status)
  - [Table of Contents](#table-of-contents)
  - [Goals and Non-Goals](#goals-and-non-goals)
    - [Goals](#goals)
    - [Non-Goals](#non-goals)
  - [Problem Statement](#problem-statement)
  - [Design](#design)
    - [Link Shape](#link-shape)
    - [Rel and Href](#rel-and-href)
    - [Auxiliary Lookup](#auxiliary-lookup)
    - [Compiled Read-Plan Extensions](#compiled-read-plan-extensions)
    - [JSON Reconstitution Integration](#json-reconstitution-integration)
  - [Feature Flag](#feature-flag)
  - [Cache and Etag](#cache-and-etag)
  - [Authorization](#authorization)
  - [Collection Responses](#collection-responses)
  - [Out of Scope](#out-of-scope)
    - [Deferred Follow-On Work](#deferred-follow-on-work)
  - [Testing Strategy](#testing-strategy)
  - [Level of Effort](#level-of-effort)
  - [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

1. **ODS document-reference parity.** Emit `{ rel, href }` on every fully-defined document reference
   in GET responses with the same shape and path-tail form ODS emits (see
   [Resources.generated.cs at v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs)).
2. **Schema-driven.** Derive reference locations, target resource types, and endpoint slugs from
   `ApiSchema.json` and the compiled read plan. No per-resource code generation.
3. **Single-pass reads.** Link emission adds one logical auxiliary
  `dms.Document` lookup per page and no per-row round-trips.
4. **Abstract references resolve uniformly with concrete.** Both resolve their concrete target type
   via `dms.Document.ResourceKeyId`; no discriminator parsing at read time.
5. **Feature-gated, default-on.** A single deployment flag controls emission as a response filter.

### Non-Goals

- Document-store backend support — relational backend only.
- OpenAPI / Discovery updates (see [Out of Scope](#out-of-scope)).
- `Location` header GUID format (tracked separately).
- Byte-for-byte reproduction of ODS codegen internals. Parity is defined at the response shape and
  path-tail level and verified by contract tests.

---

## Problem Statement

DMS GET responses today expose reference identity fields (natural keys) but no navigable pointer to
the referenced resource. Clients must reconstruct endpoint paths themselves, and for abstract
references (e.g., `educationOrganizationReference`) a client cannot determine the concrete subclass
without querying every possible concrete endpoint.

ODS exposes a `link: { rel, href }` object on each fully-defined reference. This design adds the
same object to DMS relational-backend responses, derivable entirely from `ApiSchema.json`, the
compiled read plan, and one logical auxiliary `dms.Document` lookup
per page.

---

## Design

### Link Shape

Two properties, exactly: `rel` (string) and `href` (string). No other members. Camel-cased JSON
keys consistent with existing DMS responses:

```json
"schoolReference": {
  "schoolId": 255901,
  "link": {
    "rel": "School",
    "href": "/ed-fi/schools/550e8400e29b41d4a716446655440000"
  }
}
```

**Emission gate.** `link` is emitted for a reference site if and only if:

1. The reference FK column `{ReferenceBaseName}_DocumentId` on the referencing row is non-null, **and**
2. The per-page `dms.Document` auxiliary lookup (see [Auxiliary Lookup](#auxiliary-lookup)) returns
   a row for that FK value.

Otherwise `link` is omitted. No other presence checks are performed.

Rationale: DMS's schema enforces identity completeness structurally — NOT NULL on required
reference FKs and all-or-none nullability on optional composite references make ODS's typed-default
partial-identity state unreachable. A non-null FK is equivalent to "fully defined" in DMS. If the
auxiliary lookup misses (dangling reference, concurrent delete, partial DDL state), `link` is
suppressed rather than emitting a malformed href.

### Rel and Href

Both come from `dms.Document.ResourceKeyId` resolved through `dms.ResourceKey` (see
[data-model.md](data-model.md) §`dms.Document`, §`dms.ResourceKey`). The auxiliary lookup returns
`ResourceKeyId` for each referenced document. Link injection resolves the triple
`(projectEndpointName, endpointName, ResourceName)` at reference-write time from data the runtime
already holds:

- `resourceKeyId` → `MappingSet.ResourceKeyById[resourceKeyId].Resource` yields the
  `QualifiedResourceName` (`ProjectName` + `ResourceName`). See
  [compiled-mapping-set.md](compiled-mapping-set.md) §2.
- `Resource.ProjectName` → `projectSchema.projectEndpointName` via the already-loaded
  `ApiSchema.json` project entries.
- `Resource.ResourceName` → `ProjectSchema.GetEndpointNameFromResourceName(ResourceName)` on the
  same project schema.

No new startup dictionary, no new fields on `ResourceKeyEntry`, no link-injection-owned cache.

- **`rel`**: the concrete `ResourceName` (e.g., `"School"`). Case-preserving, matches the schema.
- **`href`**: prefix-free, `/{projectEndpointName}/{endpointName}/{documentUuid:N}`
  (e.g., `/ed-fi/schools/550e8400e29b41d4a716446655440000`). GUID formatting is `"N"` — 32
  lowercase hex characters, no hyphens — matching ODS.

Abstract references (e.g., `educationOrganizationReference`) resolve to the concrete subclass
uniformly: the reference FK points at the concrete document's `dms.Document.DocumentId`
(guaranteed by `{AbstractResource}Identity(DocumentId)` FK to `dms.Document(DocumentId)`; see
[data-model.md](data-model.md) §"Abstract identity tables for polymorphic references"), so the
auxiliary lookup returns the concrete `ResourceKeyId` with no discriminator join required.

Href format matches ODS's generated pattern
(`/{schemaUriSegment}/{pluralCamelEndpointName}/{ResourceId:N}`) at the path-tail level. DMS does
not prepend `PathBase`, tenant, or `/data` segments to `link.href`; hrefs remain relative and
prefix-free, consistent with ODS's emitted form.

### Auxiliary Lookup

After the main hydration materializes a page of rows with their `..._DocumentId` FK columns, the
read plan issues one logical auxiliary lookup per page against `dms.Document`:

```sql
SELECT DocumentId, DocumentUuid, ResourceKeyId
FROM dms.Document
WHERE DocumentId IN (<distinct FK values from the page>)
```

This reuses the multi-result-command pattern already used for descriptor URI projection
([compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6). The auxiliary runs under the
same ADO.NET command and ambient transaction as the main hydration; no second round-trip or
separate transaction is opened.

**Boundary conditions.**

- **Empty set.** If no row on the page carries a reference (all FKs null), the auxiliary is
  skipped; the `DocumentId → (DocumentUuid, ResourceKeyId)` map is empty; every reference-writer
  lookup misses; `link` is suppressed via the gate in [Link Shape](#link-shape).
- **Large set.** The parameter count is the deduplicated count of distinct FK values on the page.
  When it exceeds the dialect-specific limit (SQL Server's 2,100-parameter cap is the practical
  bound), the plan partitions the FK set into sub-batches and issues one auxiliary result set per
  sub-batch within the same multi-result command. Reconstitution union-merges sub-batches into a
  single map before reference writing.

**Isolation.** Snapshot consistency matches the descriptor-URI auxiliary: a referenced document
deleted between main hydration and the auxiliary lookup within the same command produces a lookup
miss, which suppresses the link. This is strictly safer than emitting an href to a deleted
document.

### Compiled Read-Plan Extensions

The base `DocumentReferenceBinding`
([flattening-reconstitution.md](flattening-reconstitution.md) §7.3) already carries `FkColumn`
(the `..._DocumentId`) and `TargetResource`. Link injection adds nothing to the binding record
itself and does not introduce a new shared `ResourceReadPlan` or mapping-pack contract in this
story. Instead, the read-path compiler / hydration-command builder appends a feature-local
`dms.Document` lookup phase to the same multi-result hydration command already used for descriptor
URI projection. The lookup is keyed on the union of `..._DocumentId` FK columns from the
resource's `DocumentReferenceBinding`s and returns `(DocumentId, DocumentUuid, ResourceKeyId)`.
This remains a fixed-shape feature-local lookup rather than a new shared plan primitive. When the
distinct FK count exceeds the dialect threshold, the feature emits multiple contiguous result sets
for the same logical lookup and reconstitution union-merges them before reference writing. When the
feature flag is off at deployment, the lookup phase is still emitted — see
[Feature Flag](#feature-flag).

Within the composed multi-result command, the lookup follows the existing descriptor-URI lookup
pattern: main hydration result sets first, then any descriptor URI lookup already emitted for the
resource, then the link-injection `dms.Document` lookup.

Endpoint slugs are resolved at reference-write time from the runtime's already-loaded
`MappingSet` and `ApiSchema.json` project schemas per [Rel and Href](#rel-and-href); link
injection does not introduce a second startup structure. No discriminated-union endpoint
template, no per-reference-kind variant, no discriminator column binding. Concrete and abstract
references share one resolution path.

### JSON Reconstitution Integration

Link emission happens inline in the reconstituter's reference-writing loop
([flattening-reconstitution.md](flattening-reconstitution.md) §6.4). For each reference site:

1. Write the reference's identity fields from the local propagated binding columns (unchanged
   from the pre-link-injection path).
2. Read the FK column value; if null, continue to the next reference (no link).
3. Look up the FK in the page-level `DocumentId → (DocumentUuid, ResourceKeyId)` map from the
   auxiliary result set. If no row is returned, continue to the next reference (no link).
4. Resolve `(projectEndpointName, endpointName, resourceName)` from `ResourceKeyId` per
   [Rel and Href](#rel-and-href): `MappingSet.ResourceKeyById[resourceKeyId].Resource` yields the
   `QualifiedResourceName`; project endpoint and resource endpoint slugs come from the
   already-loaded project schema. `dms.ResourceKey` is immutable seed data validated against the
   mapping set at first database use
   ([flattening-reconstitution.md](flattening-reconstitution.md) §Operational mitigation); a
   post-startup seed-row addition is a deployment invariant violation, not a runtime data
   condition, so reconstitution does not spec a recovery path for a `ResourceKeyById` miss — a
   miss throws and the request fails.
5. Write a `"link": { "rel": <resourceName>, "href": "/<projectEndpointName>/<endpointName>/<documentUuid:N>" }`
   object after the reference's identity fields.

The href is written in final form during reconstitution — no post-processing or prefix-assembly
step at the serving boundary.

---

## Feature Flag

A single configuration key controls link emission:

- **Key**: `DataManagement:ResourceLinks:Enabled`
- **Default**: `true`
- **Behavior when `false`**: `link` subtrees are stripped from response bodies after cache read and
  after readable-profile projection, immediately before serialization. The auxiliary lookup and
  plan compilation are unaffected.

**Rationale for response-shaping rather than plan-shaping.** Treating the flag as a response
filter eliminates dual plan shapes, startup plan-fingerprint reconciliation, and the mixed-plan
rolling-deploy hazard. The cost is that flag-off does not reduce database work: the auxiliary
lookup still runs and the link-bearing intermediate form is still cached. Given the flag is
expected to be used as a rare opt-out rather than a performance lever, that cost is acceptable.
If link-emission cost ever becomes a deployment concern, it is a separate design change, not a
V1 feature-flag concern.

Configuration contract:

```csharp
public sealed class ResourceLinksOptions
{
  public bool Enabled { get; init; } = true;
}

services.Configure<ResourceLinksOptions>(
    configuration.GetSection("DataManagement:ResourceLinks"));
```

The flag is consumed on the response-serialization boundary, not in the plan compiler or
reconstitution engine. No per-resource, per-request, or per-reference override is provided.

---

## Cache and Etag

`dms.DocumentCache` stores the fully reconstituted caller-agnostic intermediate document, with
`link` subtrees already present (since the plan always emits them). Readable-profile projection
runs after cache retrieval; the `ResourceLinks:Enabled` flag is applied as a strip pass on the
projected document. CDC and indexing consumers of `dms.DocumentCache` therefore observe `link`
subtrees; DMS does not maintain a second link-free projection.

`dms.DocumentCache` stores the materialized `_etag` for the intermediate shape alongside the
cached `DocumentJson`. That cached `_etag` is returned directly when the served body equals the
cached intermediate (flag on, no readable profile reshaping the body). When readable-profile
projection or the `ResourceLinks:Enabled` strip pass changes the served shape, the response
serializer recomputes `_etag` from the served body using the same canonicalization rule. See
[update-tracking.md](update-tracking.md) §Serving API metadata for the normative derivation.

A flag flip does not require cache truncation, fingerprint reconciliation, or an advisory lock:
flag-off responses recompute `_etag` from the stripped body, and flag-on responses fall back to
the cached `_etag` for the intermediate shape.

The freshness check on cache reads remains unchanged:

```
cached ContentVersion == dms.Document.ContentVersion
AND cached LastModifiedAt == dms.Document.ContentLastModifiedAt
```

Cached hrefs are bound to `EffectiveSchemaHash`: any change to a `projectEndpointName` or
`resourceNameMapping` entry shifts the hash, and the DDL-generator preflight refuses a mismatched
database (see [ddl-generation.md](ddl-generation.md)), so cache rows never outlive their slug
context.

---

## Authorization

**Source-resource authorization governs link emission.** If the caller's read succeeds against the
source resource, every fully-defined reference on that source emits a link, regardless of whether
the caller can read the target resource. This matches ODS.

Consequences:

- `href` is a URL the caller may or may not be able to dereference.
- For abstract references, `rel` reveals the concrete subclass (e.g., `"School"`) to callers who
  can read the source. This matches ODS.
- Operators who need to hide a target resource type from a caller MUST hide the reference property
  itself on the source resource via readable-profile rules; target-type hiding alone is not
  sufficient.

Per-reference target-resource authorization at link-emission time is deliberately not performed:
it would multiply auth cost per response by reference sites × page size.

**Profile interaction.** Readable-profile projection MUST preserve `link` subtrees on reference
objects as server-generated fields. This story treats `link` preservation as a feature-local rule
parallel to `_etag` and `_lastModifiedDate` preservation; profiles cannot suppress `link` via
`MemberSelection.IncludeOnly`.

---

## Collection Responses

GET-many behavior is identical to GET-by-id on a per-item basis. Because reconstitution is
page-batched and the auxiliary lookup is page-scoped, link emission adds no asymptotic overhead
to collection reads. There is no N+1 risk: the auxiliary is one logical lookup per page (one
result set in the common case, multiple only when sub-batching is required), not per reference or
per item.

---

## Out of Scope

- Document-store backend link injection.
- Absolute-URL emission.
- `Location`-header GUID format migration (`D` → `N`).
- Discovery-API `link` elements on the API root document.

### Deferred Follow-On Work

Each item below should be split into a dedicated follow-on Jira once this design is approved and
the `DocumentUuid`-stamping decision in particular has been made.

| Deferred item | Reason |
|---------------|--------|
| OpenAPI / Discovery updates | Separate schema and documentation effort; V1 link injection ships runtime behavior only. Clients MUST treat `link` as additive. |
| `Location` header GUID `D` → `N` alignment | API-visible identifier-format change kept out of this rollout to limit blast radius. |
| Resource-scoped write-time `DocumentUuid` stamping | If auxiliary-lookup cost becomes a bottleneck, stamping `{ReferenceBaseName}_DocumentUuid` on referencing rows eliminates the lookup. Opt-in must be resource-scoped — never global, never per-request — via `ApiSchema.json` or operator configuration keyed by `QualifiedResourceName`. |

---

## Testing Strategy

**Unit tests** (reconstitution engine):

- Concrete reference with a fully-defined FK and an auxiliary-lookup hit → emits correct `rel` and
  `href`.
- Concrete reference with a null FK → no `link`.
- Concrete reference with a fully-defined FK but an auxiliary-lookup miss → no `link`.
- Abstract reference with a fully-defined FK → emits concrete `rel` and `href` derived from the
  target's `ResourceKeyId`. Covers polymorphic resolution without discriminator parsing.
- Abstract reference with an auxiliary-lookup miss → no `link`.
- GUID formatting → `href` contains 32 lowercase hex characters, no hyphens.
- Page with multiple references to the same target document → single auxiliary-map entry, both
  references resolve.
- SQL Server sub-batching boundary → a page that pushes the distinct FK count above 2,100
  partitions correctly and reconstitution union-merges sub-batch maps.

**Feature-flag tests:**

- Flag on + fully-defined references → response body carries `link`.
- Flag off → response body has no `link` on any reference; `_etag` reflects the link-free form.
- Flag flip across a process restart → existing cached rows remain valid for freshness-check
  purposes; `_etag` values computed pre-flip do not match post-flip responses (expected).

**Fixture tests:**

- Resource with a concrete reference (e.g., `AcademicWeek` → `School`).
- Resource with an abstract reference (any `educationOrganizationReference` site).
- Resource with a nested-collection reference (link appears inside collection elements).

**Contract / parity tests** against an ODS baseline fixture on the same semantic input, scoped to
document references only. Goal: byte-for-byte `link.rel` and `link.href` parity at the path-tail
level.

**Profile preservation test.** A readable profile whose `MemberSelection.IncludeOnly` does not
list `link` is applied to a GET with fully-defined references; assert `link` is still present on
every surviving reference.

**Source-readable / target-denied test.** Caller can read the source resource but fails a direct
GET against the target under the active authorization strategy; fully-defined references still
emit `link`.

**Caller-agnostic cache test.** Two callers who can both read the same source document — one
authorized for the target, one not — receive the same cached intermediate JSON and the same
`_etag` (before profile projection and flag-off stripping are applied per caller).

---

## Level of Effort

Small-to-medium. The implementation surfaces:

1. Read-path compiler / hydration command builder — append the `dms.Document` lookup phase to the
  existing multi-result hydration flow. No new startup structure and no changes to
  `ResourceKeyEntry`; endpoint slugs are derived on demand at reference-write time from
  `MappingSet.ResourceKeyById` and the already-loaded project schema.
2. Reconstitution reference-writer — zip FK → `(DocumentUuid, ResourceKeyId)` → endpoint, write
   `{ rel, href }`.
3. Readable profile projector — treat `link` as a server-generated subtree on nested reference
   objects.
4. Response serializer — apply the `ResourceLinks:Enabled` strip pass before serialization.
5. Feature-flag plumbing — `ResourceLinksOptions` binding.

No new per-reference DDL. No new singleton metadata tables. No abstract-identity LEFT JOINs in
hydration SQL. No startup trigger-existence validation. No advisory-lock protocol. No
cache truncate. No etag carve-out.

---

## Cross-References

- [DMS-622](https://edfi.atlassian.net/browse/DMS-622) — Jira ticket.
- [DMS-988 — Relational Read Path Epic](../epics/08-relational-read-path/EPIC.md) — parent epic.
- [06-link-injection.md](../epics/08-relational-read-path/06-link-injection.md) — story realizing
  this design.
- [02-reference-identity-projection.md](../epics/08-relational-read-path/02-reference-identity-projection.md)
  — prerequisite identity-projection story this design extends.
- [data-model.md](data-model.md) — `dms.Document`, `dms.ResourceKey`, abstract identity tables.
- [flattening-reconstitution.md](flattening-reconstitution.md) — `DocumentReferenceBinding` base
  contract.
- [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6 — descriptor URI auxiliary
  pattern this design reuses.
- [update-tracking.md](update-tracking.md) — `_etag` derivation from the served body.
- [auth.md](auth.md) — authorization strategy families.
- [profiles.md](profiles.md) — readable-profile projection boundary.
