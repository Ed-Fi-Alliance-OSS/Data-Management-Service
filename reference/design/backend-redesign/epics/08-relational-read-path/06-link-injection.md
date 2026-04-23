---
jira: DMS-622
jira_url: https://edfi.atlassian.net/browse/DMS-622
---

# Story: Inject Reference Links into GET Responses

## Description

Implement ODS-aligned link injection for document-reference properties in relational-backend GET
responses. For every fully-defined document reference in a GET-by-id or GET-many response, the
reconstitution engine emits a `link: { rel, href }` object alongside the reference identity fields,
following the contract in `design-docs/link-injection.md`. V1 emits prefix-free hrefs matching ODS
path-tail form, resolves concrete and abstract references uniformly through
`dms.Document.ResourceKeyId` (no discriminator parsing), and gates emission on source-resource
authorization only.
This story applies only to document references backed by `..._DocumentId`.
Descriptor references remain on their existing canonical-URI string surface.

Align with:

- `reference/design/backend-redesign/design-docs/link-injection.md` (link contract, plan extensions,
  auxiliary lookup, feature flag, cache interaction)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reconstitution engine
  and `DocumentReferenceBinding`)
- `reference/design/backend-redesign/design-docs/data-model.md` (`dms.Document`, `dms.ResourceKey`,
  abstract identity tables)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (`_etag` derivation from the
  served body)
- `reference/design/backend-redesign/design-docs/auth.md` (source-resource authorization gate)
- `reference/design/backend-redesign/design-docs/profiles.md` (readable-profile projection
  boundary)
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` §4.3 step 6 (descriptor
  URI auxiliary-result-set pattern this design reuses)

Builds on:

- `01-json-reconstitution.md` (DMS-990) — owns the reconstitution engine this story hooks into for
  the reference-writing step.
- `02-reference-identity-projection.md` (DMS-991) — emits natural-key fields from propagated
  binding columns; this story extends that same per-reference binding-column approach to also emit
  `link: { rel, href }`, adding one per-page logical auxiliary `dms.Document` lookup returning
  `(DocumentId, DocumentUuid, ResourceKeyId)`.
- `../07-relational-write-path/01d-profile-namespace-and-server-generated-fields.md` (Jira TBD) —
  establishes the Core profile namespace rule that keeps `link` outside profile addressability,
  so readable-profile projection preserves `link` by construction. Without this story, the
  projector would treat `link` as an ordinary member and strip it under `MemberSelection.IncludeOnly`
  profiles that do not list it.

Soft dependency:

- `../01-relational-model/04-abstract-union-views.md` (DMS-933) — derives the
  `{schema}.{AbstractResource}Identity` tables. V1 link injection only requires the table's
  `DocumentId` FK to `dms.Document(DocumentId)` so abstract references point at concrete document
  rows; the `Discriminator` column and its maintenance trigger are not on the link-injection read
  path.

## Acceptance Criteria

- GET-by-id and GET-many responses emit `link: { "rel": ..., "href": ... }` on every
  document-reference property whose `..._DocumentId` FK is non-null **and** the per-page auxiliary
  `dms.Document` lookup returns a row for that FK value. When either condition fails, `link` is
  omitted. No other presence checks are performed at the reference site.
- `rel` equals the concrete target `ResourceName` (e.g., `"School"`) resolved by looking up
  `dms.Document.ResourceKeyId` against the existing `MappingSet.ResourceKeyById` lookup
  (see `design-docs/compiled-mapping-set.md` §2). Abstract references resolve to the concrete
  subclass through the same `ResourceKeyId` path; no discriminator parsing, no discriminator
  binding, no per-reference-kind variant.
- `href` is prefix-free and has the form
  `/{projectEndpointName}/{endpointName}/{documentUuid}` (e.g.,
  `/ed-fi/schools/550e8400-e29b-41d4-a716-446655440000`). DMS does not prepend `PathBase`, tenant,
  qualifier, or `/data` segments to `link.href`; the path structure mirrors ODS's emitted
  path-tail form.
- GUID formatting is `"D"` — 36 characters, lowercase hex with hyphens — matching the current DMS
  `Location` header. This intentionally differs from ODS's `"N"` rendering; DMS does not support
  cross-platform `id` interchange, so keeping `link.href` aligned with DMS's existing `Location`
  output is preferred.
- `projectEndpointName` and `endpointName` are derived at reference-write time from
  `ApiSchema.json` using `projectSchema.projectEndpointName` and
  `ProjectSchema.GetEndpointNameFromResourceName(...)` keyed by the `QualifiedResourceName`
  returned from `MappingSet.ResourceKeyById[resourceKeyId].Resource`. Link injection does not add
  fields to `ResourceKeyEntry` or introduce a separate startup dictionary. The story must not
  assume a `ResourceSchema.endpointName` property exists.
- The href is written in final form during reconstitution. There is no post-processing or
  prefix-assembly step at the serving boundary.
- `dms.ResourceKey` is immutable seed data protected by the runtime database principal (see
  `design-docs/flattening-reconstitution.md` §Operational mitigation). A post-startup
  `ResourceKeyById` miss is a deployment invariant violation, not a runtime data condition, and
  no read-path recovery branch is specced.
- Link emission adds no per-reference or per-item round-trips. Reference identity comes from
  row-level hydration; one per-page logical auxiliary lookup against `dms.Document` issues inside
  the existing hydration command and ambient transaction. The lookup is sourced SQL-side by
  joining each `DocumentReferenceBinding`'s source table to the page keyset and UNION-ing FK
  values across reference sites, then joining `dms.Document` — the filter predicate is the keyset
  join, not a parameterized IN-list of FK values. Reconstitution builds a single
  `DocumentId → (DocumentUuid, ResourceKeyId)` map from that result set before reference writing.
- V1 adds no per-reference DDL, no new metadata tables, no abstract-identity LEFT JOINs in
  hydration SQL, no discriminator column binding, no startup trigger-existence validation, and no
  backfill for reference-target `DocumentUuid`.
- Feature flag `DataManagement:ResourceLinks:Enabled` (default `true`) controls emission as a
  **response filter**. When `false`, `link` subtrees are stripped from response bodies after cache
  read and after readable-profile projection, immediately before serialization. The auxiliary
  lookup and plan compilation are unaffected by the flag; flag-off does not reduce database work.
  No per-resource, per-request, or per-reference override is provided.
- Runtime configuration binds `DataManagement:ResourceLinks` to a dedicated `ResourceLinksOptions`
  type; the story must not assume a nested `AppSettings.DataManagement` object already exists in
  Core.
- When `dms.DocumentCache` is provisioned, materialized-document cache freshness uses a two-input
  check (`cached ContentVersion == dms.Document.ContentVersion AND cached LastModifiedAt ==
  dms.Document.ContentLastModifiedAt`). `dms.DocumentCache` stores cached `ContentVersion`
  alongside the materialized `_etag/_lastModifiedDate`.
- `dms.DocumentCache` stores the fully reconstituted caller-agnostic intermediate document with
  `link` subtrees already present. Readable-profile projection runs after cache retrieval; the
  `ResourceLinks:Enabled` strip pass runs on the projected document immediately before
  serialization.
- `_etag` is the cached materialized value when the served body equals the cached intermediate
  shape (flag on, no readable-profile reshaping). When readable-profile projection or the
  `ResourceLinks:Enabled` strip pass changes the served shape, the response serializer recomputes
  `_etag` from the served body. See `design-docs/update-tracking.md` §Serving API metadata for the
  normative derivation.
- A flag flip does not require cache truncation or plan-shape fingerprinting: flag-off responses
  recompute `_etag` from the stripped body, flag-on responses use the cached `_etag` for the
  intermediate shape. No advisory lock, no `dms.DocumentCachePlanFingerprint` table, no etag
  carve-out.
- When `dms.DocumentCache` is not provisioned, responses are materialized fresh per request and no
  freshness check runs.
- The cache remains caller-agnostic: callers who can both read the same source document share the
  same cached intermediate JSON even if one caller would fail a direct GET against the target
  resource, because target-side authorization is not consulted for link emission.
- Link emission is gated only on source-resource readability: fully-defined references to targets
  whose resource type is hidden by the caller's readable profile still emit `rel` and `href`,
  matching the accepted disclosure envelope in `design-docs/link-injection.md` §Authorization. For
  abstract references, `rel` reveals the concrete subclass to callers who can read the source —
  this matches ODS.
- Link emission is likewise not suppressed when the source read succeeds but a direct GET against
  the target would fail under the caller's authorization grants; target-side authorization is
  never consulted during link emission.
- Unit tests cover: concrete reference with fully-defined FK and auxiliary-lookup hit (link
  emitted); concrete reference with null FK (no link); concrete reference with non-null FK but
  auxiliary-lookup miss (no link); abstract reference with fully-defined FK (concrete `rel` and
  `href` via `ResourceKeyId`, no discriminator parsing); abstract reference with auxiliary-lookup
  miss (no link); GUID formatting (`D`-format, 36 characters, lowercase hex with hyphens); page
  with multiple references to the same target document (single auxiliary-map entry, both
  references resolve); child-table-hosted binding (source table is a collection or
  nested-collection table keyed by `CollectionItemId` with `<Root>_DocumentId` as the root
  locator) — asserts the auxiliary SQL joins through the binding table's root-document locator
  column rather than assuming `DocumentId`, and that `link` is emitted on references inside
  collection/nested-collection elements.
- Feature-flag tests cover: flag on with fully-defined references (body carries `link`); flag off
  (body has no `link` on any reference and `_etag` reflects the link-free form); flag flip across
  a process restart (existing cached rows remain valid for freshness-check purposes, and `_etag`
  values computed pre-flip do not match post-flip responses — expected).
- Fixture tests cover: a concrete reference (e.g., `AcademicWeek` → `School`); an abstract
  reference (any `educationOrganizationReference` site); a nested-collection reference (link
  appears inside collection elements).
- Contract/parity tests against an ODS baseline fixture on the same semantic input, scoped to
  document references only, assert byte-for-byte `link.rel` parity and `link.href` path-structure
  parity (projectEndpointName, endpointName, GUID identity) at the path-tail level; the GUID
  rendering is normalized before comparison because DMS emits `"D"` format and ODS emits `"N"`.
- Source-readable / target-denied test: caller can read the source resource but fails a direct GET
  against the target under the active authorization strategy; fully-defined references still emit
  `link`.
- Caller-agnostic cache test: two callers who can both read the same source document — one
  authorized for the target, one not — receive the same cached intermediate JSON and the same
  `_etag` before profile projection and flag-off stripping are applied per caller.

## Tasks

1. Read-path compiler / hydration command builder: append a feature-local `dms.Document` lookup
  phase to the existing multi-result hydration command, keyed on the union of
  `..._DocumentId` FK columns from the resource's `DocumentReferenceBinding`s and returning
  `(DocumentId, DocumentUuid, ResourceKeyId)`. Reuse the existing descriptor-URI multi-result
  pattern rather than introducing a new shared `ResourceReadPlan` or mapping-pack shape. The
  lookup phase is always emitted regardless of the feature flag's value.
2. Endpoint slug resolution: at reference-write time, derive
   `(projectEndpointName, endpointName, resourceName)` from `ResourceKeyId` by reading
   `MappingSet.ResourceKeyById[resourceKeyId].Resource` and calling
   `projectSchema.projectEndpointName` and
   `ProjectSchema.GetEndpointNameFromResourceName(...)` on the already-loaded project schema.
   Do not add fields to `ResourceKeyEntry` or build a new startup dictionary.
3. Lookup-phase execution: emit the `dms.Document` auxiliary as a single SQL statement over the
   page keyset — join each `DocumentReferenceBinding`'s source table to the keyset, UNION FK
   values across reference sites, and join `dms.Document` to that projection. The filter
   predicate is the keyset join, not a parameterized IN-list, so there is no client-side FK
   collection and no parameter-cap sub-batching. Reconstitution consumes the single result set
   into a `DocumentId → (DocumentUuid, ResourceKeyId)` map before reference writing. Mirrors the
   descriptor-URI auxiliary pattern (`design-docs/compiled-mapping-set.md` §4.3 step 6;
   `DescriptorProjectionPlanCompiler.EmitSelectByKeysetSql`) without requiring a shared
   plan-shape change for this story.
4. Reconstitution reference-writer: for each reference site, write the reference's identity fields
   from the local propagated binding columns (unchanged from the pre-link-injection path); read
   the `..._DocumentId` FK value; if null, skip link. Otherwise look up the FK in the page-level
   `DocumentId → (DocumentUuid, ResourceKeyId)` map; on miss, skip link. On hit, resolve the
   endpoint-slug triple per task 2 and write a final-form
   `"link": { "rel": <resourceName>, "href": "/<projectEndpointName>/<endpointName>/<documentUuid:N>" }`
   object after the reference's identity fields. `dms.ResourceKey` is immutable seed data, so a
   `ResourceKeyById` map miss is a deployment invariant violation and is not handled as a
   runtime suppression path.
5. Config plumbing: introduce `ResourceLinksOptions { bool Enabled = true }` bound from the
   `DataManagement:ResourceLinks` configuration section as `IOptions<ResourceLinksOptions>`
   (flag flips take effect at next process restart; hot-reload via `IOptionsMonitor<T>` is not a
   V1 requirement). The options type is consumed only at the response-serialization boundary, not
   in the plan compiler or reconstitution engine.
6. Response serializer: apply the `ResourceLinks:Enabled` strip pass to the projected document
   immediately before serialization, after readable-profile projection. The strip removes exactly
   the `link` subtree on reference objects; other server-generated fields (`_etag`,
   `_lastModifiedDate`) are untouched. Use the cached materialized `_etag` when the served body
   equals the cached intermediate (flag on, no readable profile reshaping); otherwise recompute
   `_etag` from the served body per `design-docs/update-tracking.md` §Serving API metadata.
7. When `dms.DocumentCache` is provisioned: extend it to store cached `ContentVersion` alongside
   the materialized `_etag/_lastModifiedDate` so the two-input freshness check
   (`cached ContentVersion == dms.Document.ContentVersion AND cached LastModifiedAt ==
   dms.Document.ContentLastModifiedAt`) is representable in the schema. Cache entries store the
   caller-agnostic intermediate document with `link` subtrees already present. No plan-shape
   fingerprint, no advisory lock, no `dms.DocumentCachePlanFingerprint` table, no cache truncate,
   and no `ResourceLinksFlag` column are introduced. Flag flips are handled implicitly through
   `_etag` derivation from the served body.
8. Tests: add unit, fixture, integration, and contract tests per the acceptance criteria. Include
   feature-flag-on and flag-off coverage; a flag-flip-across-restart regression; source-readable
   / target-denied authorization coverage; the caller-agnostic cache test; and an ODS baseline
   parity check scoped to document references.

## Deferred Follow-On Work

These items are intentionally not required for DMS-622 acceptance. They are recorded here so the
approved story and design contain enough context to create follow-on Jira tickets without reopening
the core link-injection design. Each item should be split into a dedicated follow-on once this
design is approved.

| Deferred item | Why deferred from DMS-622 | Follow-on ticket seed |
|---------------|---------------------------|-----------------------|
| OpenAPI / Discovery updates | V1 link injection ships runtime behavior only; schema and documentation surfaces are a separate effort. Clients MUST treat `link` as additive. | Update OpenAPI and Discovery surfaces to advertise reference `link` behavior accurately. |
| Resource-scoped write-time `DocumentUuid` stamping optimization | V1 uses the per-page auxiliary `dms.Document` lookup and intentionally avoids new per-reference `..._DocumentUuid` columns, write-path stamping, and backfill work. | For profiled hot resources, add optional `{ReferenceBaseName}_DocumentUuid` storage, extend write-time referential resolution to stamp `DocumentUuid`, and provide backfill/runbook guidance. **Opt-in MUST be resource-scoped — never global, never per-request** — expressed either as an `ApiSchema.json` field on the resource schema or as an operator configuration list keyed by `QualifiedResourceName`. |
