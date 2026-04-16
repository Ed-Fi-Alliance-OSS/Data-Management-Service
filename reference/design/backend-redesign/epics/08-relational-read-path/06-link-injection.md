---
jira: DMS-622
jira_url: https://edfi.atlassian.net/browse/DMS-622
---

# Story: Inject Reference Links into GET Responses

## Description

Implement ODS-equivalent link injection for reference properties in relational-backend GET responses. For
every fully-defined reference in a GET-by-id or GET-many response, the reconstitution engine emits a
`link: { rel, href }` object alongside the reference identity fields, matching the Ed-Fi ODS contract.

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
  `link: { rel, href }`, adding a new stamped-at-write `..._DocumentUuid` binding column (not
  propagated, because `DocumentUuid` is immutable) and a left-joined `Discriminator` value for
  abstract references.

## Acceptance Criteria

- GET-by-id and GET-many responses emit `link: { "rel": ..., "href": ... }` on every reference property
  whose identity fields are fully populated.
- `rel` equals the concrete target resource name (e.g., `"School"`); for abstract references, `rel` is
  the concrete subclass name derived from the `Discriminator` on `{schema}.{AbstractResource}Identity`.
- `href` has the form `/{projectEndpointName}/{endpointName}/{documentUuid}` where the GUID is formatted
  with the `"N"` specifier (32 lowercase hex characters, no hyphens).
- No `link` property is emitted when any identity field of the reference is missing or null.
- Feature flag `DataManagement:ResourceLinks:Enabled` (default `true`) disables link emission globally
  when set to `false`; no other response shape changes.
- Link emission adds no additional DB round-trips beyond the reconstitution pipeline — all inputs
  (binding columns, discriminator values, document UUIDs) are hydrated in the existing page-batched
  read.
- Pre-existing rows have their new `{ReferenceBaseName}_DocumentUuid` binding columns backfilled from
  `dms.Document.DocumentUuid` (via the corresponding `..._DocumentId`) before the feature flag is
  enabled against data written prior to the migration.
- Unit tests cover: concrete reference (link emitted), partial reference (no link), abstract reference
  (concrete `rel` and endpoint slugs), feature flag off (no link), GUID formatting (N-format).
- Integration tests validate GET-by-id and GET-many response shape against fixtures that include at
  least one concrete reference, one abstract reference, and one reference inside a nested collection.
- Contract tests compare link shape and values against a matched ODS baseline fixture for at least one
  representative resource.
- The Core-owned readable profile projector preserves `link` subtrees on references within the readable
  view.
- Link emission is gated only on source-resource readability: fully-defined references to targets whose
  resource type is hidden by the caller's readable profile still emit `rel` and `href`, matching the
  accepted disclosure envelope in `design-docs/link-injection.md` (Profile-Hidden Target Resources).

## Tasks

1. Extend `DocumentReferenceBinding` in the compiled read plan with `IsAbstractTarget`,
   `DiscriminatorBinding`, `DocumentUuidBinding`, and a precomputed `LinkEndpointTemplate` (direct slugs
   for concrete targets; `Discriminator` → slugs map for abstract targets).
2. Extend DDL emission and the write path to persist a `{ReferenceBaseName}_DocumentUuid` binding
   column per reference, populated once at insert time by extending the existing bulk
   `ReferentialId → DocumentId` resolution (`flattening-reconstitution.md` §5.2, resolving into
   the `ResolvedReferenceSet` defined in §7.6) to also return the referenced `DocumentUuid`. The
   column is a one-time stamp — `DocumentUuid` is immutable, so no FK cascade is defined on it and
   the `DbTriggerKind.IdentityPropagationFallback` machinery from `data-model.md` is not extended
   to cover it.
3. Provide a one-time backfill procedure for rows created before the column is introduced: scan each
   root table and set every `{ReferenceBaseName}_DocumentUuid` value from `dms.Document.DocumentUuid`
   via the corresponding `..._DocumentId`. Backfill must complete before the feature flag is enabled
   against pre-existing data.
4. Extend the compiled hydration SQL to left-join `{schema}.{AbstractResource}Identity` on the
   reference `..._DocumentId` and project its `Discriminator` column for every abstract reference site.
5. Extend the JSON reconstitution engine to emit `link: { rel, href }` immediately after the last
   identity field of each fully-defined reference, per the design doc's integration point.
6. Thread the `DataManagement:ResourceLinks:Enabled` feature flag through the read handler into the
   reconstitution engine; gate all link emission on this flag.
7. Verify the Core-owned readable profile projector preserves `link` subtrees on references within the
   readable view; add a test case if the projector requires configuration to do so. Separately, assert
   that link emission is not suppressed by the target resource's profile readability — fully-defined
   references to profile-hidden targets still emit `rel` and `href`, per the accepted disclosure
   envelope.
8. Add unit, fixture, integration, and contract tests per the acceptance criteria; include a
   feature-flag-off regression test.
