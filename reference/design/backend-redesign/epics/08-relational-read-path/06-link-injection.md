---
jira: DMS-622
jira_url: https://edfi.atlassian.net/browse/DMS-622
---

# Story: Inject Reference Links into GET Responses

## Description

Implement ODS-aligned link injection for document-reference properties in relational-backend GET
responses. For every fully-defined document reference in a GET-by-id or GET-many response, the
reconstitution engine emits a `link: { rel, href }` object alongside the reference identity fields,
following the contract in `design-docs/link-injection.md`, including the prefix-free href shape, the
suppress-on-unresolvable-discriminator safety rule, and the source-resource-only authorization gate.
Descriptor-reference links are intentionally out of scope for this story; V1 keeps descriptor
references on their current canonical-URI surface and defers ODS descriptor-link parity to follow-on work.

Align with:

- `reference/design/backend-redesign/design-docs/link-injection.md` (link contract, plan extensions, and
  integration point)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reconstitution engine and
  `DocumentReferenceBinding`)
- `reference/design/backend-redesign/design-docs/data-model.md` (abstract identity tables,
  `Discriminator`, propagated binding columns)
- `reference/design/backend-redesign/design-docs/auth.md` (source-resource authorization gate)

Builds on:

- `01-json-reconstitution.md` (DMS-990) — owns the reconstitution engine this story hooks into for the
  reference-writing step.
- `02-reference-identity-projection.md` (DMS-991) — emits natural-key fields from propagated binding
  columns; this story extends that same per-reference binding-column approach to also emit
  `link: { rel, href }`, adding a page-batched auxiliary `dms.Document` lookup for `DocumentUuid`
  values in V1 plus a left-joined `Discriminator` value for abstract references.

## Acceptance Criteria

- GET-by-id and GET-many responses emit `link: { "rel": ..., "href": ... }` on every document-reference
  property whose identity fields are fully populated and non-default.
- Descriptor references do **not** emit `link` in V1; they continue to emit canonical descriptor URI values
  only. This is a deliberate deferred-feature gap from ODS parity, not an implementation accident.
- `rel` equals the concrete target resource name (e.g., `"School"`); for abstract references, `rel` is
  the concrete subclass name derived from the `Discriminator` on `{schema}.{AbstractResource}Identity`
  when discriminator resolution succeeds.
- `href` is a prefix-free relative path of the form
  `/{projectEndpointName}/{endpointName}/{documentUuid:N}`. It does **not** embed `PathBase`, tenant
  segments, or route-qualifier segments; callers prepend their deployment-visible routed prefix when
  dereferencing. This matches ODS-generated links and keeps `dms.DocumentCache` caller-agnostic.
- `endpointName` is resolved from the target project's `resourceNameMapping`
  (`ProjectSchema.GetEndpointNameFromResourceName(...)`) and cached into `LinkEndpointTemplate`; the
  story must not assume a `ResourceSchema.endpointName` property exists.
- No `link` property is emitted when any identity field of the reference is missing, null, or equal to
  its declared type's default value; when the referenced `DocumentUuid` cannot be resolved; or when an
  abstract-reference `Discriminator` is null, malformed, or unmapped. Suppressing `link` for an
  unresolvable discriminator is an intentional DMS safety divergence from ODS's default abstract
  fallback link.
- Feature flag `DataManagement:ResourceLinks:Enabled` (default `true`) disables link emission globally
  when set to `false`; no other response shape changes.
- Runtime configuration binds `DataManagement:ResourceLinks` to a dedicated startup-scoped options type;
  the story must not assume a nested `AppSettings.DataManagement` object already exists in Core.
- Link emission adds no per-reference or per-item round-trips; reference identity and discriminator
  values come from row-level hydration, and referenced `DocumentUuid` values come from one additional
  per-page auxiliary result set issued inside the existing hydration command. Distinct FK values are
  deduplicated per page before lookup, and sub-batching is dialect-aware so SQL Server never crosses its
  2,100-parameter limit. See
  `design-docs/link-injection.md` §Referenced DocumentUuid Availability.
- V1 requires no new DDL or backfill for reference-target `DocumentUuid`; pre-existing rows resolve
  `DocumentUuid` through the per-page auxiliary lookup keyed by the existing `..._DocumentId` FK.
- Materialized-document cache freshness incorporates `ResourceLinksFlag` as described in
  `design-docs/link-injection.md` §Cache and Etag Interaction. Option 1 is normative for V1: a
  `ResourceLinksFlag` mismatch is treated as a cache miss and forces rematerialization under the serving
  process's startup snapshot. Explicit invalidation remains fallback-only if the column cannot land with
  the story.
- The materialized document cache remains caller-agnostic: callers who can read the same source document
  may share the same cached JSON even if one caller would fail a direct GET against the target resource,
  because target-side authorization is not consulted for link emission.
- Unit tests cover: concrete reference (link emitted), partial reference (no link), typed-default
  identity value (no link), unresolved `DocumentUuid` (no link), abstract reference (concrete `rel` and
  endpoint slugs), unresolvable discriminator (no link), feature flag off (no link), SQL Server threshold
  partitioning, and GUID formatting (`N`-format).
- Integration tests validate GET-by-id and GET-many response shape against fixtures that include at
  least one concrete reference, one abstract reference, and one reference inside a nested collection.
- Integration tests cover prefix-free href emission under `PathBase` / tenant / qualifier deployments
  (hrefs MUST NOT embed those segments), cache-hit with mismatched `ResourceLinksFlag`
  rematerialization, and the mixed V1 contract where document references emit `link` but descriptor
  references do not.
- Contract tests compare link shape and values against a matched ODS baseline fixture for at least one
  representative resource, treating the suppress-on-unresolvable-discriminator behavior as an
  intentional DMS divergence.
- The Core-owned readable profile projector preserves `link` subtrees on references within the readable
  view.
- Link emission is gated only on source-resource readability: fully-defined references to targets whose
  resource type is hidden by the caller's readable profile still emit `rel` and `href`, matching the
  accepted disclosure envelope in `design-docs/link-injection.md` (Profile-Hidden Target Resources).
- Link emission is likewise not suppressed when the source read succeeds but a direct GET against the
  target would fail under the caller's authorization grants; target-side authorization is never consulted
  during link emission.

## Tasks

1. Extend `DocumentReferenceBinding` in the compiled read plan with `IsAbstractTarget`,
   `DiscriminatorBinding`, `DocumentUuidBinding`, and a precomputed `LinkEndpointTemplate` (direct slugs
   for concrete targets; `(ProjectName, ResourceName)` → slugs map for abstract targets). Use the design
   doc's split between row-local `ColumnBinding`, main-result `HydrationProjectionBinding`, and
   side-channel `AuxiliaryResultSetProjection` rather than overloading a single binding contract.
2. Extend the hydration command builder and auxiliary readers to collect distinct per-page
  `..._DocumentId` FK values, deduplicate them before lookup, skip the lookup when the set is empty,
  partition large sets with a dialect-aware threshold, and issue one logical auxiliary
  `SELECT DocumentId, DocumentUuid FROM dms.Document WHERE DocumentId IN (...)` lookup phase per page.
  SQL Server's 2,100-parameter ceiling is a hard upper bound.
3. Extend the compiled hydration SQL to left-join `{schema}.{AbstractResource}Identity` on the
   reference `..._DocumentId` and project its `Discriminator` column for every abstract reference site.
4. Extend the JSON reconstitution engine to emit `link: { rel, href }` immediately after the last
  identity field of each fully-defined reference, per the design doc's integration point, while
  suppressing `link` for typed-default identities, unresolved `DocumentUuid` values, and null /
  malformed / unmapped abstract discriminators.
5. Introduce the config plumbing described in the design doc: bind `DataManagement:ResourceLinks` to a
  dedicated startup-scoped options type and thread it through into the reconstitution engine. No
  request-scoped prefix plumbing is required — hrefs are prefix-free.
6. Update the Core-owned readable profile projector to preserve `link` subtrees on references within the
  readable view. Separately, assert that link emission is not suppressed by the target resource's
  profile readability or by target-side authorization — fully-defined references to profile-hidden or
  target-denied resources still emit `rel` and `href`, per the accepted disclosure envelope.
7. Update `dms.DocumentCache` materialization/freshness handling so cache validity incorporates
  `ResourceLinksFlag`. Option 1 is the required V1 implementation: a mismatch is a cache miss that
  forces rematerialization under the serving process's startup snapshot. The documented explicit-
  invalidation path is fallback-only if the column cannot land with the story.
8. Add unit, fixture, integration, and contract tests per the acceptance criteria; include a
   feature-flag-off regression test, frontend prefix-capture coverage, SQL Server threshold coverage,
   cache-flag mismatch coverage, source-readable/target-denied authorization coverage, and the temporary
   document-reference-versus-descriptor-reference heterogeneity check.
