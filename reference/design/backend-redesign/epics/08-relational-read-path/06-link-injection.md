---
jira: DMS-622
jira_url: https://edfi.atlassian.net/browse/DMS-622
---

# Story: Inject Reference Links into GET Responses

## Description

Implement ODS-aligned link injection for document-reference properties in relational-backend GET
responses. For every fully-defined document reference in a GET-by-id or GET-many response, the
reconstitution engine emits a `link: { rel, href }` object alongside the reference identity fields,
following the contract in `design-docs/link-injection.md`, including the caller-agnostic cached href
suffix plus routed-prefix assembly at response time, the suppress-on-unresolvable-discriminator safety
rule, and the source-resource-only authorization gate.
Descriptor-reference links are intentionally out of scope for this story; V1 keeps descriptor
references on their current canonical-URI surface and deliberately defers any descriptor-link contract
expansion. The generated ODS resources file contains both flat string descriptor members and a
`DescriptorReference` helper with link generation, so any follow-on work must first pin the intended
runtime baseline with fixture evidence. See the Deferred Follow-On Work table for the decision gate.

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
- `../01-relational-model/04-abstract-union-views.md` (DMS-933) — derives abstract identity tables
  with the trigger-maintained `Discriminator` column that abstract-reference link resolution depends
  on. Without the abstract identity tables in place, abstract references cannot resolve a concrete
  `rel` or endpoint slug at read time.

## Acceptance Criteria

- GET-by-id and GET-many responses emit `link: { "rel": ..., "href": ... }` on every document-reference
  property whose identity fields are fully populated and non-default.
- Descriptor references do **not** emit `link` in V1; they continue to emit canonical descriptor URI values
  only. This story deliberately defers any descriptor-link contract expansion; follow-on work must first
  pin the intended baseline with ODS runtime fixture evidence and then choose the DMS contract stance.
  See the Deferred Follow-On Work table for the decision seed.
- `rel` equals the concrete target resource name (e.g., `"School"`); for abstract references, `rel` is
  the concrete subclass name derived from the `Discriminator` on `{schema}.{AbstractResource}Identity`
  when discriminator resolution succeeds.
- `href` is served as a DMS-routable relative path of the form
  `{PathBase}{tenant/qualifier prefix}/data/{projectEndpointName}/{endpointName}/{documentUuid:N}`.
  The cached intermediate document stores only the caller-agnostic suffix
  `/{projectEndpointName}/{endpointName}/{documentUuid:N}`, and the serving boundary prepends the
  current request's routed prefix before returning the response. This keeps `dms.DocumentCache`
  caller-agnostic while making the final href dereferenceable on DMS.
- `endpointName` is resolved from the target project's `resourceNameMapping`
  (`ProjectSchema.GetEndpointNameFromResourceName(...)`) and cached into the binding's
  `EndpointTemplate`; the story must not assume a `ResourceSchema.endpointName` property exists.
- No `link` property is emitted when any identity field of the reference is missing, null, or equal to
  its declared type's default value; when the referenced `DocumentUuid` cannot be resolved; or when an
  abstract-reference `Discriminator` is null, malformed, or unmapped. Suppressing `link` for an
  unresolvable discriminator is an intentional DMS safety divergence from ODS's default abstract
  fallback link.
- Before an instance begins serving, per-instance startup validation verifies the abstract-reference
  prerequisites for every reachable abstract reference site: the target
  `{schema}.{AbstractResource}Identity` table exists, the `Discriminator` column is present and
  non-null, and the discriminator-maintenance trigger exists for each concrete subclass. Missing
  objects are deployment-drift failures that fail startup for that instance rather than runtime
  link-suppression paths.
- When discriminator resolution fails because of a row-level data anomaly (`null`, unparseable, or
  unmapped discriminator) rather than deployment drift, the read succeeds with `link` suppressed on
  the affected reference only and emits a structured warn-level log entry under the sanitization and
  rate-limiting rules in `design-docs/link-injection.md`.
- Feature flag `DataManagement:ResourceLinks:Enabled` (default `true`) disables link emission globally
  when set to `false`; no other response shape changes. The flag is evaluated at plan compilation time
  (startup): when `false`, compiled read plans omit the per-page `dms.Document` auxiliary lookup and the
  abstract-identity LEFT JOINs entirely, genuinely reducing read-path database work.
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
- When `dms.DocumentCache` is provisioned, materialized-document cache freshness uses a two-input
  check (`cached ContentVersion == dms.Document.ContentVersion AND cached LastModifiedAt ==
  dms.Document.ContentLastModifiedAt`) as described in `design-docs/link-injection.md` §Cache and
  Etag Interaction. `dms.DocumentCache` therefore stores cached `ContentVersion` alongside the
  materialized `_etag/_lastModifiedDate`. Flag flips are covered by a startup plan-shape
  fingerprint auto-invalidation: each serving process computes its own fingerprint, compares it
  against the singleton `dms.DocumentCachePlanFingerprint` row under a transaction-scoped advisory
  lock, and truncates `dms.DocumentCache` automatically on mismatch. No per-row flag column is
  stored in `dms.DocumentCache`, and no manual `TRUNCATE` is required at flag-flip time. When
  `dms.DocumentCache` is **not** provisioned for the instance, the freshness check, the
  fingerprint reconciliation, and `dms.DocumentCachePlanFingerprint` itself are all absent; the
  serving process skips the fingerprint/lock/truncate path entirely and materializes every
  response fresh, so no plan-shape correctness gap exists in that deployment. See
  `design-docs/link-injection.md` §Cache and Etag Interaction for the normative cache-disabled
  branch.
- The materialized document cache remains caller-agnostic: callers who can read the same source document
  may share the same cached intermediate JSON even if one caller would fail a direct GET against the
  target resource, because target-side authorization is not consulted for link emission and routed-prefix
  assembly happens after cache retrieval.
- Unit tests cover: concrete reference (link emitted), partial reference (no link), typed-default
  identity value (no link), unresolved `DocumentUuid` (no link), abstract reference (concrete `rel` and
  endpoint slugs), unresolvable discriminator (no link), feature flag off (no link), SQL Server threshold
  partitioning, and GUID formatting (`N`-format).
- Integration tests validate GET-by-id and GET-many response shape against fixtures that include at
  least one concrete reference, one abstract reference, and one reference inside a nested collection.
- Integration tests cover DMS-routable href emission under `PathBase` / tenant / qualifier deployments,
  and the mixed V1 contract where document references emit `link` but descriptor references do not.
- A contract test asserts that fetching the same document through two different routed prefixes
  (different `PathBase` values, or the same `PathBase` with distinct tenant/qualifier prefixes)
  produces **different `link.href` values** (prefix-varying) and **identical `_etag` values**. The
  fixture for this test MUST include at least one fully-defined reference so the `link.href`
  variation is observable and the etag assertion is non-trivial; a reference-less document would
  pass the etag half of the assertion without exercising the invariant. This pins the contract that
  `_etag` is derived from the pre-routed-prefix canonical-suffix-href form and that routed-prefix
  assembly happens after etag finalization. See `design-docs/update-tracking.md` §Serving API
  metadata.
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
  `DiscriminatorBinding`, `DocumentUuidBinding`, and a precomputed `EndpointTemplate` (direct slugs
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
  malformed / unmapped abstract discriminators, and emitting the structured warn-level log entries
  required for the runtime discriminator-anomaly cases under the design doc's sanitization and
  rate-limit rules.
5. Introduce the config plumbing described in the design doc: bind `DataManagement:ResourceLinks` to a
  dedicated startup-scoped options type and thread it through into the plan compiler so that flag-off
  omits the auxiliary lookup and abstract-identity LEFT JOINs from compiled read plans at startup.
6. Add the per-instance startup validation described in the design doc for abstract-reference
  prerequisites: verify the target `{schema}.{AbstractResource}Identity` table, non-null
  `Discriminator` column, and discriminator-maintenance trigger set before the instance begins
  serving, and fail startup for that instance on missing objects rather than treating them as
  runtime suppression cases.
7. Add the serving-boundary href finalization step described in the design doc so cached suffixes are
  converted into DMS-routable relative hrefs using the current request's visible routed prefix.
8. Update the Core-owned readable profile projector to preserve `link` subtrees on references within the
  readable view. Separately, assert that link emission is not suppressed by the target resource's
  profile readability or by target-side authorization — fully-defined references to profile-hidden or
  target-denied resources still emit `rel` and `href`, per the accepted disclosure envelope.
9. When `dms.DocumentCache` is provisioned, implement the cache freshness check against the
  two-input condition (`cached ContentVersion == dms.Document.ContentVersion AND cached
  LastModifiedAt == dms.Document.ContentLastModifiedAt`), and add the startup plan-shape
  fingerprint auto-invalidation:

   - Extend `dms.DocumentCache` to store cached `ContentVersion` alongside the materialized
     `_etag/_lastModifiedDate` so the two-input freshness check is representable in the schema.
   - Add the singleton `dms.DocumentCachePlanFingerprint` table defined in
     `design-docs/data-model.md` §`dms.DocumentCachePlanFingerprint` (PostgreSQL + SQL Server DDL).
   - At serving-process startup, compute the plan-shape fingerprint from the startup-bound options
     (V1 input set: `DataManagement:ResourceLinks:Enabled`), acquire the dialect-appropriate advisory
     lock (`pg_advisory_xact_lock` / `sp_getapplock` with `@LockOwner = 'Transaction'`), compare
     against the stored fingerprint, and — on mismatch or missing row — `TRUNCATE dms.DocumentCache`
     and upsert the new fingerprint before releasing the lock.
   - No `ResourceLinksFlag` column is added to `dms.DocumentCache` itself.
   - Document the operational procedure in the runbook: operators flip the flag and restart; the
     cache truncation happens automatically. Mixed-plan overlap is not supported — operators MUST
     drain old-plan processes fully (drained or blue-green restart) before any new-plan process
     begins serving, because cache reads do not validate plan shape and an old-plan writer after
     truncation produces rows a new-plan reader would serve in the wrong shape.
   - When `dms.DocumentCache` is **not** provisioned for the instance, the entire startup
     reconciliation step is a no-op: the process does not compute or compare the fingerprint,
     does not acquire the advisory lock, does not attempt to read or upsert
     `dms.DocumentCachePlanFingerprint` (whose DDL is gated on the same cache-provisioning
     decision), and does not execute `TRUNCATE dms.DocumentCache`. Per-request freshness
     checking against cached stamps is likewise absent because there is no cache; every
     response is materialized fresh. Operators flip `DataManagement:ResourceLinks:Enabled` and
     restart in the same way, but the cache-disabled path incurs no fingerprint cost and needs
     no runbook guidance beyond the flag flip itself. See
     `design-docs/link-injection.md` §Cache and Etag Interaction.

10. Add unit, fixture, integration, and contract tests per the acceptance criteria; include a
   feature-flag-off regression test (confirming compiled plans omit the auxiliary lookup and LEFT JOINs
   when the flag is `false`), routed-prefix assembly coverage, an etag-stability contract test
   (same document through two different routed prefixes yields identical `_etag`), SQL Server
  threshold coverage, source-readable/target-denied authorization coverage, startup-validation
  failure coverage for missing abstract-reference prerequisites, discriminator-failure logging
  hygiene, the temporary document-reference-versus-descriptor-reference heterogeneity check,
  startup plan-shape fingerprint auto-invalidation coverage on a cache-provisioned instance
  (fingerprint match → no truncate; fingerprint mismatch or missing row → `TRUNCATE` plus
  fingerprint upsert, with the advisory lock held across both steps), and a cache-disabled
  instance regression asserting that startup performs no fingerprint read, no advisory-lock
  acquisition, no access to `dms.DocumentCachePlanFingerprint`, and no `TRUNCATE`, while
  GET responses still serve the correct plan-shape JSON materialized fresh.

## Deferred Follow-On Work

These items are intentionally not required for DMS-622 acceptance. They are recorded here so the
approved story and design contain enough context to create follow-on Jira tickets without reopening the
core link-injection design.

| Deferred item | Why deferred from DMS-622 | Follow-on ticket seed |
|---------------|---------------------------|-----------------------|
| Descriptor-reference links | V1 stays scoped to document-reference hydration and keeps descriptor references on canonical descriptor URIs. The ODS codebase contains both flat string descriptor members and a `DescriptorReference` helper with link generation, so the follow-on must start by pinning the intended runtime baseline rather than assuming a settled parity story. | **Decision 1:** Verify the target ODS runtime wire contract for descriptor references with fixture evidence. **Decision 2:** Pick the DMS contract stance — (a) status quo; (b) additive `link` + URI; (c) replace URI with `link`; (d) opt-in conditional emission. Ticket seed covers the chosen path. |
| `Location`-header GUID alignment (`D` → `N`) | Link hrefs intentionally ship with `Guid.ToString("N")`, but `Location` continues to use the current `"D"` formatting to avoid expanding the initial blast radius | Align frontend `Location` header generation with link href GUID formatting, assess POST/PUT client impact, and update contract tests that assert `Location` values |
| OpenAPI / Discovery updates | Runtime link emission can ship before schema/discovery documentation changes, and V1 still has mixed behavior between document and descriptor references | Update OpenAPI and Discovery surfaces to advertise reference `link` behavior accurately, including the V1 document-versus-descriptor split or its eventual removal once descriptor parity lands |
| Resource-scoped write-time `DocumentUuid` stamping optimization | V1 uses the per-page auxiliary `dms.Document` lookup and intentionally avoids new per-reference `..._DocumentUuid` columns, write-path stamping, and backfill work | For profiled hot resources, add optional `{ReferenceBaseName}_DocumentUuid` storage, extend write-time referential resolution to stamp `DocumentUuid`, and provide backfill/runbook guidance. **Opt-in MUST be resource-scoped — never global, never per-request** — expressed either as an `ApiSchema.json` field on the resource schema or as an operator configuration list keyed by `QualifiedResourceName`. |
