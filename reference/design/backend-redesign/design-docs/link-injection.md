# Link Injection Design

## Status

Draft.

This document describes **link injection** for document-reference properties in DMS GET responses against the relational
backend. The feature emits a `{ rel, href }` object on every fully-defined document reference in a response
body (FKs into `dms.Document`; descriptor references are intentionally deferred in V1 — see Non-Goals), closely aligned
to the Ed-Fi ODS contract (see [ODS Parity Reference](#ods-parity-reference)) without per-resource code generation.

- [overview.md](overview.md) — backend redesign overview and context
- [data-model.md](data-model.md) — `dms.Document`, abstract identity tables, propagated identity columns
- [flattening-reconstitution.md](flattening-reconstitution.md) — reconstitution engine and `DocumentReferenceBinding`
- [auth.md](auth.md) — authorization strategy (link emission is governed by source-resource authorization only)
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
  - [ODS Parity Reference](#ods-parity-reference)
  - [Design](#design)
    - [Link Shape](#link-shape)
    - [Rel Resolution](#rel-resolution)
    - [Href Construction](#href-construction)
    - [GUID Format](#guid-format)
    - [Schema-Driven Metadata](#schema-driven-metadata)
    - [Abstract Reference Resolution](#abstract-reference-resolution)
    - [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)
    - [Referenced DocumentUuid Availability](#referenced-documentuuid-availability)
    - [Future Optimization: Write-Time Stamping](#future-optimization-write-time-stamping)
    - [Compiled Read-Plan Extensions](#compiled-read-plan-extensions)
    - [Integration Point: JSON Reconstitution](#integration-point-json-reconstitution)
    - [Profile Compatibility](#profile-compatibility)
  - [Feature Flag](#feature-flag)
    - [Cache and Etag Interaction](#cache-and-etag-interaction)
  - [Authorization](#authorization)
    - [Accepted Disclosure Envelope](#accepted-disclosure-envelope)
    - [Profile-Hidden Target Resources](#profile-hidden-target-resources)
  - [Collection Responses](#collection-responses)
  - [Out of Scope](#out-of-scope)
  - [Risks and Open Questions](#risks-and-open-questions)
  - [Testing Strategy](#testing-strategy)
  - [Level of Effort](#level-of-effort)
  - [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

1. **ODS document-reference parity (V1 scope).** Emit `{ rel, href }` on every fully-defined **document**
  reference property in GET responses (FKs into `dms.Document`) with shape and values closely aligned
  to the Ed-Fi ODS resource-link contract for document references, subject to the deliberate DMS
  divergences called out in this document. V1 deliberately does **not** target full ODS link parity:
  descriptor-reference links are out of scope and deferred to follow-on work. "Parity" in this design
  always means document-reference parity unless stated otherwise; see Non-Goals.
2. **Schema-driven.** Derive reference locations, target resource types, and abstract/concrete relationships
   from `ApiSchema.json` and the compiled read plan — no per-resource hand-coded link logic.
3. **Single-pass reads.** Link emission adds no per-reference or per-item round-trips; it consumes
  row-level hydration data plus a per-page auxiliary lookup phase issued within the existing hydration
  command — one additional result set in the common case, partitioned into multiple result sets only
  when large FK sets require sub-batching. See
  [Referenced DocumentUuid Availability](#referenced-documentuuid-availability) for the precise shape.
4. **Abstract reference resolution.** Abstract references (e.g., `educationOrganizationReference`) emit `rel`
  and `href` for the concrete subclass (e.g., `School`) when discriminator resolution succeeds; if
  concrete resolution fails, DMS suppresses `link` rather than emitting ODS's default abstract fallback.
5. **GET-many symmetric.** Paged collection responses emit links identically to single-item GET responses.
6. **Feature-gated.** Controlled by a single configuration switch, default-on, equivalent to the ODS
   `ApiFeature.ResourceLinks` flag.

### Non-Goals

- Document-store backend support — relational backend only.
- OpenAPI / Discovery API updates (deferred to dedicated follow-on work; see [Out of Scope](#out-of-scope)).
- Emission of absolute URLs; hrefs are relative. DMS body hrefs are **DMS-routable** on the wire:
  the served response prepends the current request's deployment-visible routed prefix (including
  `PathBase`, tenant / qualifier segments, and `/data`) to a caller-agnostic cached route suffix.
  The cached materialization therefore stays caller-agnostic even though the final emitted href is
  request-routable; see [Href Construction](#href-construction).
- Changes to the `Location` header GUID format (tracked separately; see
  [Risks and Open Questions](#risks-and-open-questions)).
- Propagation of discriminator or identity metadata beyond what is already defined in
  [data-model.md](data-model.md).
- **Descriptor references.** This design addresses *document* references (FKs into `dms.Document`).
  Descriptor references on resources — whose values are already emitted today as fully-qualified
  descriptor URIs via the auxiliary descriptor URI projection (see
  [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6) — are not targeted for `link`
  emission in V1. ODS v7.3 does emit descriptor links today — generated `DescriptorReference.CreateLink()`
  in [Resources.generated.cs @ v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs)
  returns `{ rel, href }` for descriptor references — so the omission here is a deliberate deferred feature,
  not an unverified parity assumption. V1 keeps descriptor references on their current DMS surface: canonical
  descriptor URI values only. Clients MUST therefore treat `link` as optional on a per-reference basis until
  descriptor-link parity lands. OpenAPI and Discovery follow-on work MUST document that heterogeneous behavior
  explicitly. The follow-on Jira for descriptor-link parity can be created after this design is approved;
  until then, this section records the gap so V1 cannot be read as claiming broad ODS parity while
  descriptor links remain deferred.

---

## Problem Statement

Consumers of the Ed-Fi DMS relational-backend GET responses today receive reference identity fields
(natural keys) but no navigable pointer to the referenced resource. This diverges from ODS, where every
reference carries a `link: { rel, href }` object, and forces consumers to reconstruct endpoint paths
client-side. For abstract references the problem is worse: a client cannot determine the concrete subclass
of an `educationOrganizationReference` without querying every possible concrete endpoint.

This design adds ODS-aligned link objects to reference properties in relational-backend GET responses,
fully derivable from existing `ApiSchema.json` metadata and the compiled read plan, and emitted during the
reconstitution pass with no additional round-trips.

---

## ODS Parity Reference

The ODS behavior this design mirrors:

Citations below reference [Ed-Fi-Alliance-OSS/Ed-Fi-ODS at tag `v7.3`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/v7.3),
which implements Data Standard 5.2.0. Stable tag URLs and class/method names are used instead of local
checkout line numbers because `Resources.generated.cs` is generated and shifts between standard versions.

- **Link shape**: `{ "rel": "...", "href": "..." }` — exactly two `DataMember` fields, no `title`/`type`/
  `hreflang`. Defined by [`Link`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Api/Models/Link.cs)
  at tag `v7.3`.
- **Href template**: relative path `/{schemaUriSegment}/{pluralCamelEndpointName}/{ResourceId:n}` —
  e.g., `"/ed-fi/schools/550e8400e29b41d4a716446655440000"`. Sourced from generated `CreateLink()`
  methods in
  [Resources.generated.cs @ v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs):
  - Concrete reference example: `SchoolReference.CreateLink()` assigns `Rel = "School"` and constructs
    `Href` from the schema URI segment and the `Schools` plural endpoint name plus `ResourceId.ToString("n")`.
  - Abstract reference example: `AcademicWeek.SchoolReference.CreateLink()` (an abstract
    `educationOrganizationReference` resolution) calls `Discriminator.Split('.')` to extract
    `(ProjectName, ResourceName)`, then assigns `Rel` from the resource-name segment and constructs
    `Href` using `SchemaUriSegment()` and `PluralName.ToCamelCase()`. GUID format is `"n"` — 32 hex
    characters, no hyphens. If `Discriminator` is null, malformed, or resolves to no matching resource,
    the generated ODS code returns its default abstract link rather than suppressing `link`.
- **Descriptor references in ODS**: generated `DescriptorReference.CreateLink()` in the same file also emits
  `link`, defaulting to `/ed-fi/descriptors/{ResourceId:n}` and refining the target with `Discriminator`
  when present. DMS V1 does not implement that surface; see Non-Goals.
- **Rel**: the concrete target resource name (e.g., `"School"`, `"LocalEducationAgency"`). For abstract
  references, ODS splits the in-memory `Discriminator` on `.` (verified in
  `AcademicWeek.SchoolReference.CreateLink()` in `Resources.generated.cs` at v7.3) producing a
  `(ProjectName, ResourceName)` pair from a string like `"edfi.School"`. DMS stores the discriminator
  in its **native** format only — `"ProjectName:ResourceName"` with business-cased project name
  (e.g., `"Ed-Fi:School"`; see [data-model.md](data-model.md) "Abstract identity tables for
  polymorphic references"). The separator and casing differences are intentional DMS divergences for
  readability. The DMS parser accepts **only** the native `:` form and **only** the business-cased
  project name that the DMS write path emits; no ODS `.` normalization and no lowercase-project-name
  alias is attempted. DMS abstract identity tables are populated exclusively by the DMS write-path
  triggers, so a legacy ODS-format value cannot appear in a healthy deployment — if one is observed
  at read time, it is treated as an *unparseable* discriminator and the link is suppressed per
  [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator). Defining a
  stable normalization between ODS project tokens and DMS `ProjectName` values is out of scope for
  V1; if ever needed, it becomes a dedicated follow-up with its own design.
- **Presence gate**: a link is emitted only when the reference is "fully defined" (all identity components
  present and non-default). ODS uses `default(T)` logic for this gate: an identity field that equals the
  declared type's default value — zero for numerics, empty string for strings, `default(DateTime)` /
  `default(DateTimeOffset)` for temporals — is treated as not fully defined and suppresses the link.
  Partially populated references do not emit `link`. DMS adopts the same typed-default semantics; the
  detailed gate rule lives in [Link Shape](#link-shape).
- **Feature flag**: `ApiFeature.ResourceLinks` at
  `Application/EdFi.Ods.Common/Constants/ApiFeature.cs` line 26 (v7.3); when disabled, the `Link`
  property returns `null` and is omitted from the serialized output.
- **Implementation style**: per-reference-type `CreateLink()` methods generated into the resource-model
  partials. Not middleware, not serialization filters.

The DMS relational implementation matches the main observable shape of this contract while realizing it
against a schema-driven, compiled-plan architecture instead of code generation. One deliberate
behavioral divergence is called out explicitly:

- For abstract references whose discriminator cannot be resolved, DMS suppresses `link` entirely rather
  than returning ODS's default abstract fallback link, because DMS has no abstract resource endpoint to
  target safely. See [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator).

DMS body hrefs share the same ODS-like path tail (`/{projectEndpointName}/{endpointName}/{uuid:N}`), but
the **served** href is DMS-routable rather than prefix-free: DMS prepends the current request's routed
prefix so the final body value matches the deployment's actual `.../data/...` route shape. The cached
materialization retains only the caller-agnostic suffix; see [Href Construction](#href-construction) for
the two-stage assembly rule.

---

## Design

### Link Shape

Structurally identical to ODS — two properties, `rel` and `href` — but with a DMS-routable relative href
on the wire. The cached intermediate shape holds only the stable route suffix; the served response
prepends the current request's routed prefix (see [Href Construction](#href-construction)):

```json
"schoolReference": {
  "schoolId": 255901,
  "link": {
    "rel": "School",
    "href": "/data/ed-fi/schools/550e8400e29b41d4a716446655440000"
  }
}
```

- Two properties: `rel` (string) and `href` (string).
- No additional fields.
- Emitted **only when both of the following conditions are met**:
  1. **The reference is fully defined** — every identity field of the reference is present and non-default.
     An identity field is treated as not fully defined if it is null, missing, or equals the default value
     for its declared type: `0` for numeric types, empty string (`""`) for string types,
     `default(DateTime)` / `default(DateTimeOffset)` for temporal types, and the analogous default for any
     other identity-field type. If any identity field fails this gate, the `link` property is omitted
     (matches ODS). This rule is stated for ODS-behavioral parity with ODS `default(T)` semantics; DMS NOT
     NULL constraints on identity columns make the zero-value path unreachable in practice today, but the
     rule remains stated this way to be defensive against future data integrity issues.
  2. **The resolved target `DocumentUuid` is non-null** — if the `DocumentUuid` for the referenced
     document is null or missing for any reason (no matching row returned by the per-page batched
     `dms.Document` lookup, an unresolvable reference after a future schema change, or — under the
     optional write-time-stamping optimization — an unstamped `..._DocumentUuid` column in a
     partial-backfill state), the `link` property is suppressed entirely. This gate prevents malformed
     hrefs (e.g., `/ed-fi/schools/00000000000000000000000000000000`) from appearing in responses.
     **Missing lookup results are safe without operator coordination** — when the batched lookup finds
     no row for a referenced document (e.g., a dangling reference), `link` is simply suppressed on
     that reference rather than emitting a broken href. (For **abstract references**, an additional
     safety gate applies: if discriminator resolution fails for any reason, the `link` is suppressed
     even before the `DocumentUuid` lookup. See
     [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator).)
- Camel-cased JSON keys (`rel`, `href`, `link`) consistent with existing DMS response conventions.

### Rel Resolution

`rel` is always the **concrete** target resource name:

- **Concrete references.** Read directly from
  `DocumentReferenceBinding.TargetResource.ResourceName` in the compiled read plan (see
  [flattening-reconstitution.md](flattening-reconstitution.md) §7.3, `DocumentReferenceBinding` — and
  §7.1 for the `QualifiedResourceName` primitive it holds), e.g., `"School"`.
- **Abstract references.** The compiled `DocumentReferenceBinding.TargetResource` holds the abstract
  resource name (e.g., `"EducationOrganization"`). The concrete name is resolved at read time from the
  hydrated `Discriminator` value (see
  [Abstract Reference Resolution](#abstract-reference-resolution)).

Rel values are case-preserving — they match the resource name as it appears in `ApiSchema.json` / the
compiled model. No camel-case transformation.

### Href Construction

The href is assembled in **two stages**.

The caller-agnostic cached **route suffix** is:

```
/{projectEndpointName}/{endpointName}/{documentUuid:N}
```

- `projectEndpointName`: the kebab-cased project segment (`ed-fi`, `tpdm`, `sample`, etc.), looked up from
  the target resource's `projectSchema.projectEndpointName`.
- `endpointName`: the camel-cased plural endpoint slug for the target resource
  (`schools`, `academicWeeks`, `localEducationAgencies`, etc.), resolved from the target project's
  `resourceNameMapping` via `ProjectSchema.GetEndpointNameFromResourceName(...)`, then cached into the
  compiled `LinkEndpointTemplate`.
- `documentUuid`: the `DocumentUuid` of the **referenced** document (see
  [Referenced DocumentUuid Availability](#referenced-documentuuid-availability)), formatted without hyphens
  (see [GUID Format](#guid-format)).

For abstract references, the `projectEndpointName` and `endpointName` are those of the **concrete** subclass
identified by the discriminator, not of the abstract type. There is no `/ed-fi/educationOrganizations/{id}`
endpoint in DMS; the suffix always points at a concrete endpoint.

**Cached stage: no request-scoped inputs.** The route suffix is fully derivable from the target document's
identity and the compiled read plan. It omits `PathBase`, tenant segments, route qualifiers, and `/data`,
which keeps the cached document caller-agnostic and ODS-like at the path-tail level. This still matches
ODS-generated links such as
`/ed-fi/educationOrganizations/{id:n}` (see
[Resources.generated.cs @ v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs)),
and — critically — keeps `dms.DocumentCache` caller-agnostic: the cached materialization is identical for
every caller who can read the document, regardless of ingress path. See
[Cache and Etag Interaction](#cache-and-etag-interaction).

**Served stage: prepend the routed prefix.** After cache retrieval (and after readable-profile projection,
if any), DMS prepends the current request's deployment-visible routed prefix to the cached suffix before
serializing the response body. The routed prefix consists of:

```
{PathBase}{tenant/qualifier segments}/data
```

where any optional components that are not in use are omitted. The final served href is therefore:

```
{PathBase}{tenant/qualifier prefix}/data/{projectEndpointName}/{endpointName}/{documentUuid:N}
```

Examples:

- no `PathBase`, no tenant, no qualifiers:
  `/data/ed-fi/schools/550e8400e29b41d4a716446655440000`
- multitenant + qualifiers:
  `/district-a/2026/data/ed-fi/schools/550e8400e29b41d4a716446655440000`
- `PathBase` + tenant:
  `/dms/tenant-a/data/ed-fi/schools/550e8400e29b41d4a716446655440000`

This keeps generic clients correct on DMS deployments without forcing request-scoped data into the cache.
V1 therefore chooses **DMS-correct dereferenceability** over byte-for-byte mimicry of ODS's
prefix-free body-path contract: ODS parity is retained for the cached path tail and the `{ rel, href }`
shape, but the served body href follows DMS's actual routed `/data/...` surface so generic DMS clients
can follow it directly.

**Relationship to the `Location` header.** The `Location` header continues to be assembled at the
frontend boundary by prepending the current request's visible prefix (scheme + host + `PathBase` +
tenant/qualifier segments + `/data`) to the path-only fragment produced by
`PathComponents.ToResourcePath()`. Link hrefs now follow that same routed-path assembly pattern, but
remain relative and continue to use the `"N"` GUID format while `Location` continues to use `"D"`. See
[GUID Format](#guid-format).

V1 keeps this request-visible prefix assembly entirely at the ASP.NET frontend response boundary,
where the current `HttpContext.Request` is already available. It does **not** assume a new
request-visible data-root field on `FrontendRequest` or `PathComponents`.

### GUID Format

All link `href` GUIDs are formatted with the `"N"` specifier — 32 lowercase hex characters, no hyphens —
matching ODS exactly.

This is a deliberate divergence from DMS's current `Location` header, which relies on the default
`Guid.ToString()` ("D" format, with hyphens). Aligning `Location` headers to `"N"` is noted as a
follow-up in [Risks and Open Questions](#risks-and-open-questions); it is **out of scope** for this story
to avoid coupling an API-visible identifier-format change to the link-injection rollout.

### Schema-Driven Metadata

All **schema-derived inputs for cached href-suffix generation** come from `ApiSchema.json` via existing
typed accessors — no new schema fields are required. Final **served** href assembly additionally uses
existing request-scoped routing inputs at the frontend boundary (`PathBase`, tenant / qualifier
segments, and `/data`), but those are serving inputs rather than new schema metadata:

- **Reference property locations**: `DocumentPath.IsReference` / `ProjectName` / `ResourceName` /
  `ReferenceJsonPathsElements` on `ResourceSchema.DocumentPaths`
  (`src/dms/core/EdFi.DataManagementService.Core/ApiSchema/DocumentPath.cs`,
  `ResourceSchema.cs`). These already identify every reference property in a resource and its target
  `QualifiedResourceName` plus identity JsonPaths.
- **Abstract/concrete relationships**: existing `superclassResourceName`, `superclassProjectName`, and
  `abstractResources` schema fields. A reverse map (abstract → set of concrete subclasses) is computed
  once at schema-load time and cached alongside the rest of the compiled model.
- **Endpoint slugs**: existing `projectSchema.projectEndpointName` supplies the kebab-cased project segment.
  The per-resource endpoint slug comes from the same schema metadata the runtime already uses for request
  parsing and `Location` assembly — specifically `ProjectSchema.GetEndpointNameFromResourceName(...)`, backed
  by `resourceNameMapping` / `caseInsensitiveEndpointNameMapping`, and the `PathComponents.EndpointName`
  value type. `ResourceSchema` does not currently expose an `endpointName` property, so link injection
  caches the resolved endpoint slug in `LinkEndpointTemplate` at plan compile time rather than expecting it
  on `ResourceSchema`.
- **Serving prefix inputs**: the final DMS-routable prefix comes from the request's existing frontend
  routing context (`PathBase`, tenant / qualifier segments, and the fixed `/data` segment). Link
  injection does not require new schema fields to model that prefix; it reuses the same deployment-
  visible routing inputs that already drive `Location` header assembly.

### Abstract Reference Resolution

**Prerequisites.** This resolution strategy depends on two invariants established elsewhere in the
design:

- **Abstract identity tables are emitted.** [ddl-generation.md](ddl-generation.md) §3 requires that
  every abstract resource referenced by a concrete resource in the hydrated set has a corresponding
  `{schema}.{AbstractResource}Identity` table deployed, with a trigger-maintained non-null
  `Discriminator` column. If DDL generation skips the abstract identity table for a referenced abstract
  type (for example, because the abstract resource's schema entry is malformed or its subclasses are
  not marked), link injection cannot produce a `rel` or `href` for references to that abstract type.
- **Per-instance schema validation enforces the invariant at startup.** The existence check belongs to
  the per-instance schema validation step described in
  [new-startup-flow.md](new-startup-flow.md) §Per-instance schema validation, alongside the existing
  DB fingerprint checks. For every abstract reference site reachable from the compiled mapping set,
  that validation step MUST verify, in each attached database:
  1. The target `{schema}.{AbstractResource}Identity` table exists.
  2. The `Discriminator` column exists on that table with the declared type and a `NOT NULL`
     constraint (no runtime recovery is defined for a nullable or missing `Discriminator` column —
     those are deployment-drift states, not data anomalies, and must fail fast).
  3. The trigger that maintains `Discriminator` on each concrete member root table is present
     (identified by the well-known trigger name emitted by [ddl-generation.md](ddl-generation.md) §3;
     the check asserts the trigger's existence by name, not its body).

  A failure of any of the three sub-checks fails **per-instance startup validation** for that
  instance, with a clear error naming the abstract resource, the referencing concrete resource, the
  specific missing object (table / column / trigger), and the affected database. The failure is then
  cached as a deterministic per-instance startup error and that instance serves `503` at request
  time, without failing the shared mapping-set compile and without preventing other healthy
  instances from serving. Plan compilation itself remains schema-driven and database-agnostic, so a
  single compiled mapping set can be reused across instances with compatible fingerprints. Runtime
  warn-and-suppress (see
  [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)) is reserved
  for true data anomalies — an individual row whose `Discriminator` is null or corrupted after a
  write-path incident — **not** for deployment drift where the column, trigger, or identity table is
  missing outright.

Abstract references (e.g., `educationOrganizationReference`) require a runtime lookup of the concrete
resource type to produce correct `rel` and `href` values. Two properties of the existing
[data-model.md](data-model.md) design make this straightforward:

1. The abstract identity table `{schema}.{AbstractResource}Identity` (see
   [data-model.md](data-model.md) "Abstract identity tables for polymorphic references") already carries
   a non-null `Discriminator` column (literal format `"ProjectName:ResourceName"`, e.g., `"Ed-Fi:School"`),
   trigger-maintained from each concrete member root table.
2. Every abstract reference site has a composite FK to
   `{schema}.{AbstractResource}Identity(DocumentId, <AbstractIdentityFields...>)`, so the identity row
   is guaranteed to exist and be in sync with the referenced concrete row.

**Resolution strategy (recommended): hydrate the Discriminator during the read pass.**

The compiled hydration SQL for a resource with abstract references adds a left join from the referencing
row to `{schema}.{AbstractResource}Identity` on `(_DocumentId)` (the `DocumentId` column alone is
sufficient given it is the PK of the identity table) and selects the `Discriminator` column into a
plan-bound projection slot. During reconstitution the engine parses the `Discriminator` into
`(ProjectName, ResourceName)` and resolves:

- `rel` = `ResourceName`.
- `href` endpoint slugs = looked up from the schema for the concrete `QualifiedResourceName`.

This is preferred over propagating a discriminator column to the referencing row because:

- The `Discriminator` is **already stored and maintained** in `{AbstractResource}Identity` by existing
  write-path triggers; no new propagation is introduced.
- The composite FK guarantees referential integrity between the two rows; no reconciliation risk.
- The join is on a primary-key index; cost is negligible and bounded by the number of abstract reference
  sites in the hydrated set.
- Reconstitution remains a single page-batched pass — no per-row follow-up queries.

**Alternative considered: propagate Discriminator as a local binding column on the referencing table.**
Rejected for V1. It avoids the join but duplicates storage and requires extending the existing identity
propagation triggers. If profiling shows the join is a hot spot, propagation can be added later without
changing the reconstitution contract; the plan layer already owns the column binding.

**Rejected: post-reconstitution Discriminator lookup.** Would add one or more additional DB round-trips
per read and break the single-pass reconstitution property.

### Failure Modes: Unresolvable Discriminator

Although the design above guarantees a non-null, trigger-maintained `Discriminator` for all rows written
through normal DMS write paths, three exceptional conditions can produce an unresolvable discriminator
at read time:

1. **`Discriminator` is null.** A row may have been migrated into the abstract identity table before the
   discriminator-maintaining trigger was deployed, or a schema-refresh race may have left the column
   unpopulated. In either case the discriminator column produces a SQL `NULL` in the hydration result set.
2. **`Discriminator` is non-null but unparseable.** The value does not match the expected native
   DMS `"ProjectName:ResourceName"` format — for example, a legacy ODS-style `edfi.School` value
   from a migration tool, a lowercase-project-name variant, a value written by an external tool with
   a different convention, or an outright corrupted column. No normalization from alternate formats
   is attempted in V1 (see [Rel](#ods-parity-reference) for the rationale); parsing produces no
   valid `(ProjectName, ResourceName)` pair.
3. **`Discriminator` parses cleanly but the resulting `(ProjectName, ResourceName)` pair is absent from
   the precomputed `LinkEndpointTemplate` map.** The concrete subclass may have been removed in a schema
   refresh that has not yet propagated to the running instance, or metadata was regenerated without that
   subclass. The pair is valid but has no matching entry in the frozen compile-time map.

**Normative rule.** In any of the three cases above, the entire `link` property for that abstract
reference MUST be suppressed. The reconstitution engine MUST NOT emit a malformed `href`, and MUST NOT
propagate an exception out of the reconstitution path as a result of discriminator resolution failure.
The response body is returned normally with the `link` property absent on the affected reference only.

**Rationale.** This rule is parallel to the null-`DocumentUuid` gate in [Link Shape](#link-shape) and
[Integration Point: JSON Reconstitution](#integration-point-json-reconstitution): link emission is
best-effort metadata. Any resolution failure degrades silently — the reference's identity fields are
still emitted, and only the navigable pointer is omitted — rather than breaking the read response for
the caller. This is preferable to throwing (which breaks the entire request) or emitting a malformed
href (which breaks clients that attempt to dereference it). This is an intentional DMS safety divergence
from ODS's generated fallback-to-abstract-link behavior.

**Logging rule.** Silent degradation for the caller is not silence for operators. Each of the three
failure modes above MUST produce a structured warn-level log entry when it occurs:

- **Payload (included):** the failure-mode category (null / unparseable / unmapped), the source
  resource's `QualifiedResourceName`, the reference JsonPath at which the failure occurred, and the
  source row's `DocumentId`. These are sufficient to locate the offending row and reference site
  in the relational store for remediation.
- **Payload (excluded):** the raw `Discriminator` column value, the target document's identity-field
  values, any content-JSON fragment from the source resource, and the caller's authorization
  principal. These are excluded to avoid leaking source-resource content or caller identity into
  log sinks. The `QualifiedResourceName`/JsonPath/`DocumentId` tuple is enough to diagnose; the raw
  discriminator string in particular is withheld because it may have been corrupted with arbitrary
  bytes that log-shipping pipelines mishandle.
- **Rate limit.** Systemic discriminator corruption (e.g., a bad migration that zeroes the column
  across an entire table) would otherwise produce one log entry per affected reference per read. The
  logger applies per-process, per-category rate limiting: at most 10 warn-level entries per minute for
  each failure category (`null`, `unparseable`, `unmapped`). When one or more occurrences are suppressed
  during a one-minute window, the logger emits one summary warn entry at the end of that window with the
  suppressed count for that category. Shutdown-time flush is best-effort and is not required for
  correctness.
- **Sanitization.** Every external-origin field in the payload (source `QualifiedResourceName`,
  reference JsonPath) MUST pass through the project's log-sanitization helper before emission, per
  the project's log-injection policy. `DocumentId` values are integers and are safe as-is.

### Referenced DocumentUuid Availability

The `href` requires the referenced document's `DocumentUuid`. This is a distinct requirement from the
reference *identity* columns already addressed by story
[02-reference-identity-projection.md](../epics/08-relational-read-path/02-reference-identity-projection.md),
with different read-time mechanics: those natural-key identity bindings are stored locally on the
referencing row via write-time propagation, whereas `DocumentUuid` is resolved at read time via a
batched auxiliary lookup against `dms.Document`.

**V1 Design: page-batched `dms.Document` lookup.**

After the main relational read materializes a page of result rows with their reference `..._DocumentId`
FK columns, the read plan issues one logical auxiliary lookup per page against `dms.Document`. In the
common case this is a single auxiliary query/result set:

```sql
SELECT DocumentId, DocumentUuid
FROM dms.Document
WHERE DocumentId IN (<all ..._DocumentId values from the page>)
```

This query is issued as an **additional result set** in the same multi-result hydration command,
following the same pattern that
[compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6 already uses for descriptor URI
projection: collect all descriptor FK ids from the page, issue one `(DescriptorId, Uri)` lookup as a
batched result set, and zip during reconstitution. The `dms.Document` lookup is the reference-identity
analogue of that descriptor result set.

During reconstitution, the engine builds a `DocumentId → DocumentUuid` map from the auxiliary result
set and consults it while writing each reference. A `DocumentId` absent from the map (e.g., a dangling
reference) yields a null lookup, which causes the `link` to be suppressed by the null gate in
[Link Shape](#link-shape).

Rationale for V1:

- **No schema or write-path changes required.** No new `{ReferenceBaseName}_DocumentUuid` columns,
  no DDL emission extension, no write-path stamping. The `..._DocumentId` FK already exists on every
  referencing row; the auxiliary lookup reuses it directly.
- **No backfill required.** Pre-existing rows are handled transparently: the auxiliary lookup resolves
  `DocumentUuid` for any row whose `DocumentId` FK is populated, regardless of when the row was
  written.
- **Same read-time resolution goal as ODS, different mechanism.** Both systems resolve reference
  identifiers at read time rather than denormalizing them onto referencing rows. The mechanism
  differs, however: ODS left-join-fetches `...ReferenceData` associations in the per-aggregate
  hydration HQL (see `EdFi.Ods.Common/Infrastructure/Repositories/GetEntitiesBase.cs`) and reads
  hydrated `ResourceId`/`Discriminator` from the resulting reference object in generated
  `CreateLink()` code. DMS V1 keeps the primary hydration free of per-reference joins and instead
  issues one batched `DocumentId → DocumentUuid` auxiliary lookup per page, matching the existing
  multi-result hydration pattern rather than the ODS join-fetch pattern.
- **Fits the established multi-result hydration model.** The descriptor URI projection in
  [compiled-mapping-set.md](compiled-mapping-set.md) already validates this approach end to end.
- **Bounded overhead.** The auxiliary lookup is one logical query phase per page, keyed by a primary-key
  index (`dms.Document.DocumentId`). In the common case it is a single query/result set; in large-set
  cases it may partition into multiple sub-batch result sets within the same hydration command. Cost is
  proportional to the number of distinct referenced documents on the page, not to the number of
  references per row; for reference-dense resources the set of referenced `DocumentId`s may be much
  smaller than the total reference count.
- **Reconstitution remains single-pass per page.** The auxiliary result set is fetched alongside the
  main hydration; reconstitution zips without additional round-trips.

**Rejected: per-reference join in the primary hydration SQL.** Worst shape — multiplies hydration cost
by the number of distinct references per row and scales poorly with reference-dense resources.

**IN-list boundary conditions.**

- **Empty set.** A page with zero document references — either because no rows on the page carry a
  reference, or because every reference FK is null — does not trigger the auxiliary query. The
  auxiliary lookup is skipped entirely; the `DocumentId → DocumentUuid` map is an empty map; every
  attempted lookup misses; the `link` property is suppressed via the existing null gate in
  [Link Shape](#link-shape). No empty-IN-list SQL is ever issued.
- **Large set.** The IN-list parameter count is bounded by `page-size × distinct-references-per-row`.
  More precisely, the builder first deduplicates to the set of **distinct** `{ReferenceBaseName}_DocumentId`
  FK values present on the page, and only that deduplicated set contributes parameters to the auxiliary
  lookup. The partitioning threshold MUST be dialect-aware: PostgreSQL tolerates high parameter counts, but
  SQL Server has a hard limit of 2,100 parameters per statement. A page of 500 rows with 5 distinct
  reference sites each can therefore exceed the SQL Server bound when every FK is unique (`500 × 5 = 2,500`).
  When the distinct FK count for a page exceeds the dialect-specific threshold, the auxiliary lookup
  partitions the FK set into sub-batches and issues one auxiliary result set per sub-batch within the same
  multi-result hydration command. The sub-batches are zipped together at reconstitution time and surface to
  the reference writer as a single unified `DocumentId → DocumentUuid` map. When descriptor URI expansion is
  also needed for the page, the common command shape is: main hydration rows, descriptor URI lookup result
  set, and document-uuid lookup result set. The exact threshold and partitioning strategy remain hydration-
  command-builder implementation details, but the SQL Server cap is a hard upper bound.

### Future Optimization: Write-Time Stamping

If profiling shows the per-page `dms.Document` lookup dominates read latency for reference-dense
resources (e.g., `StudentSectionAssociation` with many distinct references per page), consider opting
into write-time stamping for those resources.

**Mechanism.** Extend the existing bulk reference resolution step in
[flattening-reconstitution.md](flattening-reconstitution.md) §5.2 — which already resolves each
`ReferentialId → DocumentId` via `dms.ReferentialIdentity` and populates the
`ResolvedReferenceSet.DocumentIdByReferentialId` map defined in §7.6 — to also return the referenced
`DocumentUuid` from the same lookup. At row write time, persist that value into a
`{ReferenceBaseName}_DocumentUuid` column on the referring row alongside the existing `..._DocumentId`
FK and natural-key binding columns. Reads project it directly into the plan; reconstitution reads it
from the stamped column instead of the auxiliary result set.

Because `DocumentUuid` never changes after insert, this would be a **one-time stamp at write**, not
ongoing propagation:

- No FK cascade is defined on `DocumentUuid`; the composite FK stays on `(DocumentId, <identity
  fields…>)` as today.
- No `DbTriggerKind.IdentityPropagationFallback` trigger fires for this column.
- If a referenced document's identity later changes and propagation updates the natural-key bindings on
  the referring row, the `{ReferenceBaseName}_DocumentUuid` value is unaffected and remains correct by
  construction (stable identifier).

**DDL sketch (optimization path only).**

```sql
ALTER TABLE edfi.{ReferringResource}
  ADD COLUMN {ReferenceBaseName}_DocumentUuid uuid NULL;
```

DDL generation would emit this column alongside the other per-reference binding columns, extending the
propagated-binding-column emission surface in [ddl-generation.md](ddl-generation.md) to recognize a
`DocumentUuid` binding.

**Switching criteria.** Adopt write-time stamping for a given resource when all of the following hold:

1. Profiling confirms the per-page `dms.Document` auxiliary lookup is a measured bottleneck for that
   resource's read path (not merely theoretical).
2. The one-time backfill cost for existing rows is acceptable for the deployment window (see trade-off
   note below).
3. The resource's reference count per row is high enough that the saved auxiliary-lookup round-trip
   materially outweighs the additional write cost and storage overhead.

The V1 batched-lookup design continues to apply for all resources until those criteria are met; opting
in is per-resource, not global.

**Opt-in mechanism.** Per-resource opt-in MUST be expressed as a configuration-driven, per-resource
surface — not a global flag and not a per-request parameter. Two candidate surfaces are acceptable; the
future design that actually adopts this optimization chooses between them:

- An `ApiSchema.json` extension field on the resource schema (e.g., `resourceSchema.linkOptimization`
  with values `"batched"` (default) and `"stamped"`), consumed by the plan compiler at startup to emit
  the stamped-column binding for that resource's plan.
- An operator configuration list keyed by `QualifiedResourceName` (e.g.,
  `DataManagement:ResourceLinks:StampedResources = ["Ed-Fi:StudentSectionAssociation", …]`),
  consumed the same way but decoupled from the schema package.

Both surfaces share the same normative property: opt-in is resource-scoped, not global, and the plan
compiler reads it once at startup. This design does not decide between the two; it only fixes the
shape of the surface so that a future optimization cannot smuggle in a global toggle.

**Trade-off to weigh when opting in.** One additional 16-byte `uuid` column per reference per row. For
reference-dense resources the aggregate storage cost is non-trivial. Rows created before the column is
introduced will hold `NULL` until backfilled; a one-time backfill (scan the root table and set each
column from `dms.Document.DocumentUuid` via the corresponding `..._DocumentId`) is required before
stamped-column reads produce populated UUIDs. The V1 batched-lookup path (which does not require a
backfill) remains available as a fallback if the backfill window is unacceptable.

### Compiled Read-Plan Extensions

The compiled `DocumentReferenceBinding`
([flattening-reconstitution.md](flattening-reconstitution.md) §7.3) gains four additions, all
populated at plan compile time:

- `IsAbstractTarget: bool` — true when `TargetResource` refers to an abstract resource.
- `DiscriminatorBinding: HydrationProjectionBinding?` — for abstract targets only, points at the
  hydration-projected `Discriminator` column (from the left-joined `{AbstractResource}Identity` row).
  This keeps existing `ColumnBinding` semantics row-local while making the projection-slot contract explicit.
  Null for concrete targets.
- `DocumentUuidBinding: AuxiliaryResultSetProjection` — by default, describes how to look up
  `DocumentUuid` from the per-page auxiliary `dms.Document` result set: keyed by the local
  `{ReferenceBaseName}_DocumentId` FK column on the referencing row, resolved against the page-level
  `DocumentId → DocumentUuid` map built from the auxiliary result set. This mirrors the
  `DescriptorEdgeSource → (DescriptorId, Uri)` auxiliary-result-set projection in
  [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6. For resources where the write-time
  stamping optimization has been opted into (see
  [Future Optimization: Write-Time Stamping](#future-optimization-write-time-stamping)), the binding
  instead points at the `{ReferenceBaseName}_DocumentUuid` stamped column on the referencing row
  directly; the reconstitution engine reads from that column path rather than the auxiliary result set.
- `LinkEndpointTemplate: LinkEndpointTemplate` — precomputed:
  - For concrete targets: a fixed `(projectEndpointName, endpointName)` pair.
  - For abstract targets: a **map** keyed by the `(ProjectName, ResourceName)` **tuple produced after
    splitting and parsing the `Discriminator` string** (not keyed by the raw discriminator string), with
    values `(projectEndpointName, endpointName)` for each concrete subclass. The map is bounded by the
    number of concrete subclasses of the abstract resource and is frozen at plan compile time.

Normative contract sketch:

```csharp
internal sealed record HydrationProjectionBinding(string ResultSetName, string ColumnName);

internal abstract record LinkEndpointTemplate
{
  internal sealed record Concrete(
    ProjectEndpointName ProjectEndpointName,
    EndpointName EndpointName
  ) : LinkEndpointTemplate;

  internal sealed record Abstract(
    IReadOnlyDictionary<
      (ProjectName ProjectName, ResourceName ResourceName),
      (ProjectEndpointName ProjectEndpointName, EndpointName EndpointName)
    > EndpointsByConcreteResource
  ) : LinkEndpointTemplate;
}

internal sealed record AuxiliaryResultSetProjection(
  ColumnBinding LocalKeyColumn,
  string AuxiliaryResultSetName,
  string AuxiliaryKeyColumn,
  string AuxiliaryValueColumn
);
```

For standard Ed-Fi abstract reference sites, `LinkEndpointTemplate.Abstract` is expected to stay a low-
cardinality map (typically low single digits, roughly 2–5 entries).

`AuxiliaryResultSetProjection` is a plan-compile-time record describing how reconstitution reads a
value that is materialized in a separate hydration result set rather than on the main row. For
`DocumentUuidBinding` the record specifies:

- `LocalKeyColumn`: the per-row FK column on the referencing row that keys into the auxiliary map —
  for link injection, `{ReferenceBaseName}_DocumentId`.
- `AuxiliaryResultSetName`: the identifier of the auxiliary result set within the multi-result
  hydration command (the `dms.Document` lookup result set for link injection; descriptor URI result
  set for the descriptor projection).
- `AuxiliaryKeyColumn` / `AuxiliaryValueColumn`: the columns of the auxiliary result set that form
  the `key → value` map the reconstitution engine zips during reference writing — `DocumentId` and
  `DocumentUuid` respectively for link injection.
- Null-on-miss semantics: a local key with no matching auxiliary row yields a null lookup result,
  which `link` emission treats as a suppression gate (see [Link Shape](#link-shape)).

`AuxiliaryResultSetProjection` is distinct from both `ColumnBinding` and `HydrationProjectionBinding`:
`ColumnBinding` addresses a single column on a single hydrated row; `HydrationProjectionBinding` addresses a
single projected slot in the main hydration result; `AuxiliaryResultSetProjection` addresses a lookup
against a side-channel result set keyed by one of the main row's columns. The three coexist in
`DocumentReferenceBinding` without overlap.

No new top-level plan objects are introduced; all additions live inside existing binding records.

### Integration Point: JSON Reconstitution

Link emission is a concern of the reconstitution engine, not a post-processing pass. Per
[flattening-reconstitution.md](flattening-reconstitution.md) §6.4, the reconstituter writes references
from binding columns inside a `Utf8JsonWriter` loop. The engine's reference-writing step is extended:

1. Write identity fields of the reference from local propagated binding columns (as today, per story 02).
2. If **all** identity-field values were present (the reference is fully defined) and
   `ResourceLinks:Enabled` is true:
   a. Resolve the concrete `(projectEndpointName, endpointName)` from the binding's
      `LinkEndpointTemplate` — direct for concrete targets, `Discriminator`-keyed map lookup for abstract
      targets. **If the lookup fails for any of the three reasons enumerated in
      [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)** (null
      discriminator, unparseable discriminator, or `(ProjectName, ResourceName)` pair absent from the
      map), **skip link emission entirely** — write no `link` property and continue to the next reference.
      This is symmetric to the `documentUuid`-null skip in step (c) below; both are safety gates on the
      same abstract-reference link-emission path.
   b. Read `documentUuid` from `DocumentUuidBinding`: look up the referencing row's
      `{ReferenceBaseName}_DocumentId` FK value in the page-level `DocumentId → DocumentUuid` map built
      from the per-page auxiliary `dms.Document` result set. (For resources where the write-time stamping
      optimization has been adopted, read the stamped `{ReferenceBaseName}_DocumentUuid` column instead.)
   c. **If `documentUuid` is null, skip link emission entirely** — write no `link` property and
      continue to the next reference. This covers any null path: a `dms.Document` auxiliary lookup that
      returned no matching row (e.g., a dangling reference), a null FK column, or an unresolvable
      reference after a future schema change. Under the optional write-time-stamping optimization,
      also covers an unstamped `..._DocumentUuid` column in a partial-backfill state.
   d. Format the caller-agnostic cached href **suffix** as
      `/{projectEndpointName}/{endpointName}/{documentUuid:N}`. No request-scoped inputs are involved in
      this step. See [Href Construction](#href-construction).
   e. Write a `"link": { "rel": ..., "href": ... }` object property immediately after the last identity
      field of the reference, storing the suffix form in the cached / intermediate document.
3. Otherwise, write no `link` property (matches ODS behavior for partially-populated references and for
    feature-flag-off state).

This keeps all reference-handling logic co-located, preserves deterministic output order, and reuses the
existing streaming writer — no intermediate `JsonNode` materialization. A later serving step prepends the
current request's routed prefix to each emitted `link.href` before the response is serialized.

Response-behavior matrix:

| Flag | Reference kind | Identity fully defined | Endpoint resolution | `DocumentUuid` resolution | Output |
|------|----------------|------------------------|---------------------|---------------------------|--------|
| Off | Any document reference | Any | n/a | n/a | Identity fields only; no `link` |
| On | Concrete | No | Fixed | Any | Identity fields only; no `link` |
| On | Concrete | Yes | Fixed | Hit | Emit `link` with fixed `rel` / `href` |
| On | Concrete | Yes | Fixed | Miss or local FK null | Identity fields only; no `link` |
| On | Abstract | Yes | `Discriminator` resolves to mapped concrete endpoint | Hit | Emit `link` with concrete `rel` / `href` |
| On | Abstract | Yes | `Discriminator` null, malformed, or unmapped | Any | Identity fields only; no `link` |

### Profile Compatibility

**`link` MUST be preserved by the readable profile projector on nested reference objects, regardless of
whether `link` appears in a profile's `MemberSelection.IncludeOnly` property set.**

The OpenAPI contract already treats `link` as a server-generated field:
`ProfileOpenApiSpecificationFilter` (`OpenApi/ProfileOpenApiSpecificationFilter.cs`) includes `"link"`
in its static `_serverGeneratedFields` set (lines 23–29), alongside `"id"`, `"_etag"`, and
`"_lastModifiedDate"`. The parity gap is in the runtime `ReadableProfileProjector`
(`Profile/ReadableProfileProjector.cs`), which does not yet mirror that exemption for nested reference
objects — `ProjectNestedObject` routes all scalar properties through `IsMemberIncluded`, so a profile
with `MemberSelection.IncludeOnly` that omits `link` will silently drop it. The runtime then diverges
from the advertised OpenAPI schema: callers who expect navigable references see them in some reads but
not in profile-scoped reads.

The rule: the readable projector treats `link` on any reference object as a server-generated field and
copies it unconditionally into the projected output, analogous to how `id` is handled at the document
root. This is the normative target behavior. This document update does not itself implement that runtime
behavior; until the projector adopts the exemption, profile-scoped GET responses remain non-conformant to
this design. If projector alignment does not ship as part of the initial implementation, it becomes
explicit deferred follow-on work and must be tracked as such from [Out of Scope](#out-of-scope).

See [Profile-Hidden Target Resources](#profile-hidden-target-resources) for the authorization stance
when a profile hides the target resource type; `link` is still emitted in that case because the
source-resource authorization governs link emission, not the target's readability under the profile.

---

## Feature Flag

A single configuration key controls link emission:

- **Key**: `DataManagement:ResourceLinks:Enabled`
- **Default**: `true` — matching the user-facing expectation set by ODS's feature being enabled by default
  in standard deployments.
- **Behavior when `false`**: no `link` property is emitted on any reference. No other response shape
  changes. Plan compilation is unaffected; the flag is consulted inside the reconstitution engine's
  reference-writing step.

**Consumption model.** The runtime binds the `DataManagement:ResourceLinks` section to a dedicated options
type at process startup and treats it as a process-lifetime deployment setting. V1 does not introduce
in-process hot reload for this flag. In the sections below, a "flag flip" means traffic has moved from
processes started with the old value to processes started with the new value. Current Core `AppSettings`
remains a flat set of properties; link injection MUST NOT assume a nested `AppSettings.DataManagement`
object already exists.

Normative configuration contract:

```csharp
public sealed class ResourceLinksOptions
{
  public bool Enabled { get; init; } = true;
}
```

Bind `ResourceLinksOptions` from the `DataManagement:ResourceLinks` section and consume it via
`IOptions<ResourceLinksOptions>` (or an equivalent startup-bound options snapshot).

No per-resource, per-request, or per-reference override is provided. Clients that want minimal responses
disable the flag at the deployment level.

**Database cost when the flag is off.** The flag is consulted only at reconstitution time; the hydration
SQL runs unchanged. Specifically, the abstract-identity LEFT JOINs (see
[Abstract Reference Resolution](#abstract-reference-resolution)) and the per-page `dms.Document`
auxiliary result set (see [Referenced DocumentUuid Availability](#referenced-documentuuid-availability))
are issued unconditionally, because they are compiled into the read plan at startup and cannot be
toggled per request without re-planning. Callers disabling the flag to reduce response size will see
smaller response payloads, but the database round-trip cost per read is unchanged. This is deliberate:
the flag is a serving-shape switch, not a hydration optimization. Operators who need to eliminate the
auxiliary round-trip must remove the reference-carrying resource from the deployment, not disable the
flag.

### Cache and Etag Interaction

**Cache layer contract (normative).** `dms.DocumentCache` stores the **fully reconstituted caller-agnostic
intermediate document** — the same JSON the reconstitution engine produces before any readable-profile
filtering is applied and before request-scoped href-prefix assembly. The read pipeline is:

1. Reconstitute the document from relational storage (with `link` subtrees already emitted under the
   current `ResourceLinks:Enabled` flag, but with `link.href` carrying the caller-agnostic suffix form —
   link emission is a concern of the reconstitution engine, see
   [Integration Point: JSON Reconstitution](#integration-point-json-reconstitution)).
2. Write the reconstituted JSON to `dms.DocumentCache` (if projection is enabled), keyed by
   `DocumentId`. The stored JSON is identical for every caller who can read the document.
3. On subsequent reads, a cache-validity check determines whether to reuse the cached JSON or re-
   reconstitute (see flag-flip rule below).
4. **After** cache read, Core runs readable-profile projection (per
   [profiles.md](profiles.md) "Read Path Under Profiles") and recomputes `_etag` from the projected
   document (per [update-tracking.md](update-tracking.md) §Serving API metadata — "readable-profile
   responses recompute `_etag` from the projected document"). `link` subtrees are preserved by the
   projector as server-generated fields; see [Profile Compatibility](#profile-compatibility).
5. The frontend serving boundary prepends the current request's routed prefix to every emitted
   `link.href`, turning the cached suffix into the final DMS-routable relative path. Because this step
   changes the served response shape when prefixes differ, `_etag` for the final response is recomputed
   after prefix assembly rather than reused from the cached intermediate shape.

Because projection and prefix assembly run after cache retrieval, the cache is keyed only by `DocumentId`
— **not** by readable profile, caller claims, authorization context, `PathBase`, tenant, or route
qualifiers. `ResourceLinksFlag` is the only serving-shape input that must participate in cache validity,
because it is the only serving-shape input baked into the cached intermediate JSON itself; profile
projection and routed-prefix assembly reshape cache output downstream and therefore need no cache-key
participation.

`ResourceLinks:Enabled` changes the **served document shape** — reference objects gain or lose the `link`
subtree — without changing `dms.Document.ContentVersion` or `dms.Document.ContentLastModifiedAt`. This
creates a correctness gap for two dependent subsystems:

- **`dms.DocumentCache` freshness** (`data-model.md`, §5 around line 507): the current freshness check
  compares `ContentVersion`/`ContentLastModifiedAt` from `dms.Document` against the cached stamp. A flag
  flip produces cached entries whose stored `DocumentJson` has the wrong link-presence shape relative to
  the serving flag, but whose stamps still match — so the serving path will return stale cached JSON.
- **`_etag` derivation** (`update-tracking.md`, §Serving API metadata around line 123): `_etag` is a
  deterministic hash of the **served response document**. After a flag flip, etag values computed under
  the previous flag state become invalid against responses produced under the new state: the served shapes
  differ, so hashes differ. The same serving rule applies after request-scoped href-prefix assembly: if a
  different routed prefix produces a different served href, the response etag is recomputed from that
  final routed form rather than reused from the cached intermediate document.

**Normative rule.** The design MUST handle this in one of the following two documented ways.
**Option 1 is normative for V1.** Option 2 remains an acceptable fallback only if the
`dms.DocumentCache.ResourceLinksFlag` column cannot be delivered alongside this story.

**Option 1 (normative): include the flag value as a cache-validity input.**

Extend the `dms.DocumentCache` freshness check to:

```
ContentVersion == cached
AND ContentLastModifiedAt == cached
AND ResourceLinksFlag == cached
```

A `ResourceLinksFlag` column is stored alongside the existing stamps in each `dms.DocumentCache` row at
materialization time. On a flag flip, every cached row whose stored `ResourceLinksFlag` value differs
from the current runtime value is treated as stale; the serving path materializes fresh JSON with the
correct shape for the new flag state. No operator coordination is required — the system self-heals on
the first read after the flip. Entries are lazily refreshed as they are read; no sweep is needed.

Because the flag is startup-scoped, "current runtime value" here means the value loaded by the serving
process at startup. During a rollout where old-value and new-value processes overlap against the same
`dms.DocumentCache` table, cache rows may churn between the two shapes; correctness is preserved because
each process treats a `ResourceLinksFlag` mismatch as a cache miss, not a cache hit: it re-materializes
the JSON under its own startup snapshot before serving a response.
Operators should minimize the mixed-value rollout window to avoid unnecessary cache churn.

Per the [cache layer contract above](#cache-and-etag-interaction), `dms.DocumentCache` stores the
pre-profile-projection document keyed only by `DocumentId`, and is caller-agnostic. The materialized
`link` shape depends only on source-document content and `ResourceLinksFlag`; it does **not** depend
on target-side authorization, readable profile, or caller claims. Two callers who can read the same
source document therefore share the same cached materialized JSON even when one caller would fail a
direct GET against the target resource or has a different readable profile.

**Cache-miss storm on flip.** Self-healing is not free. A flag flip invalidates every cached row
simultaneously; the first wave of reads after the flip pays the full materialization cost — re-hydrate
from relational storage, re-reconstitute the JSON, re-project under the readable profile — for every
distinct document read during the invalidation wave. Under load this can produce a measurable latency
spike and a transient load increase against relational storage. Mitigation options available to
operators:

- Schedule flag flips during low-traffic windows so the invalidation wave coincides with reduced
  demand.
- Pre-warm the cache after the flip via a targeted read pass across hot documents before the caller
  traffic lands.
- Accept the spike as acceptable operational cost; flag flips are expected to be rare.

This is an inherent property of option 1's lazy refresh; option 2 exchanges it for an explicit cold
cache at flip time rather than eliminating the cost.

Adding the `ResourceLinksFlag` column requires a companion edit to
[data-model.md](data-model.md) §`dms.DocumentCache` (the DDL and freshness-check definition live there,
not here). That edit is in scope for this story and is delivered alongside the link-injection
implementation; link-injection.md only names the requirement.

**Option 2 (fallback only): explicit invalidation on flip.** If `ResourceLinksFlag` cannot be delivered,
flipping the flag triggers explicit invalidation of all `dms.DocumentCache` entries (e.g., a
`TRUNCATE dms.DocumentCache` or a background sweep that deletes rows). Simpler to reason about, but
requires a coordinated operational step at flip time; the cache is cold immediately after the flip until
entries are re-materialized on demand.

**Etag consequence (both options).** Etag values computed before a flag flip are invalid against
post-flip responses because the served shape changed. This is an acceptable one-time cost of the flip:
clients will see an etag mismatch on their next conditional read and re-fetch the full document. This
is not an ongoing concern — once the flip is complete, etag derivation is consistent again.

**Cross-references (maintained in their owning docs):**

- `reference/design/backend-redesign/design-docs/update-tracking.md` §Serving API metadata (~line 123)
  — `_etag` derivation from the final served document.

---

## Authorization

Link emission is governed by the **source-resource** authorization that already gates the overall GET
response:

- If the caller is authorized to read the source resource, every fully-defined reference on it emits a
  link.
- Link emission **does not** imply the caller is authorized to read the target resource — it is
  equivalent to a URL that the caller may or may not be able to dereference.

This matches ODS behavior. Link injection intentionally does not perform per-reference target-resource
authorization at link-emission time, for two reasons:

- Per-reference target-resource authorization checks would multiply auth cost per response by the
  number of reference sites per item times the page size, undoing the single-pass property.
- Target-URL visibility without target-read access is already accepted in ODS.

The same rule applies when a caller can read the **source** resource through relationship-based,
namespace-based, ownership-based, or custom-view authorization but would fail a direct GET against the
**target** resource under that same strategy family. Link emission is source-readable / not-source-
readable only; target-side authorization is never consulted.

### Accepted Disclosure Envelope

Relative to a pre-link-injection GET response (which already exposes the natural-key identity fields of
each reference), link injection expands the observable surface in three ways:

1. **Concrete target type on abstract references.** For abstract references (e.g.,
   `educationOrganizationReference`), the `rel` value exposes the concrete subclass name
   (`"School"`, `"LocalEducationAgency"`, …), which the identity values alone do not reveal. ODS emits
   the same disclosure; DMS accepts it for parity.
2. **Referenced `DocumentUuid`.** The `href` embeds the referenced document's stable `DocumentUuid`,
   a server-assigned identifier that was not previously visible through the source resource's GET
   response.
3. **Defensive-path lifecycle observability (not a normal state).** The relational design does not
   produce dangling references under normal operation: abstract references are protected by the
   composite FKs described in [data-model.md](data-model.md), and deletes of a still-referenced
   document fail per [transactions-and-concurrency.md](transactions-and-concurrency.md). A
   reference row whose `DocumentId` FK is populated therefore always has a matching `dms.Document`
   row in a healthy system. The null gate in [Link Shape](#link-shape) is a defensive safety net
   only; it fires under anomalies such as partial DDL/backfill states, manual database modification,
   or future corruption. In those defensive-path situations, a caller with repeated source-read
   access but no target-read access could in principle infer the anomaly from `link` disappearance.
   This residual observability is accepted as within the disclosure envelope because the triggering
   state is not reachable through the supported DMS write path.

All three disclosures are deliberate and part of the V1 document-reference link contract (ODS
behavioral parity for *document* references only; descriptor-reference links remain deferred — see
[Non-Goals](#non-goals)). They do **not** expose document content beyond what the caller already
sees in the reference's identity fields; they expose an identifier, a concrete type, and a lifecycle
signal. Callers with no target-read access cannot dereference the `href`.

### Profile-Hidden Target Resources

A caller's readable profile may hide the target resource type entirely (today's DMS returns `405` for
direct GETs against a profile-unreadable resource). Link injection does **not** suppress `rel` or `href`
in this case: if the source resource is readable under the caller's profile, any fully-defined reference
on it emits a link even when the target resource type is unreadable under that profile.

The same behavior applies when the target is hidden by authorization rather than by profile filtering: if
the source read succeeds but a direct target read would fail under the caller's claims, ownership, or
namespace scope, `rel` and `href` still emit.

This is consistent with the source-resource-only stance above and with ODS behavior, but it has one
operator-visible consequence: a profile designed to hide *existence* of a target resource type from a
caller will leak the concrete type (for abstract references) and the `DocumentUuid` whenever a readable
source resource carries a fully-defined reference to that target. The reference's identity fields are
already disclosed at the readable-source boundary; link injection adds the two disclosures enumerated
in the envelope above.

Operators with a hard requirement to hide existence of a target resource type from a given caller must
hide the **reference property itself** on the source resource via readable profile rules, not rely on
target-type hiding alone. This is called out explicitly here to prevent the misreading that hiding
the target type hides it everywhere.

**Strategy independence.** The source-resource authorization gate that governs link emission is
independent of the strategy family — relationship-based, namespace-based, ownership-based, or
custom-view — that grants the caller read access to the source. Whichever family is in effect for the
read, link emission depends only on whether that read succeeds; if it succeeds, fully-defined references
emit links regardless of which strategy governed the grant. See [auth.md](auth.md) for the strategy
family definitions and their performance envelope, and [profiles.md](profiles.md) for the
readable-profile projector that governs source-side field filtering.

---

## Collection Responses

GET-many behavior is identical to GET-by-id behavior on a per-item basis. Because reconstitution is
page-batched ([04-query-execution.md](../epics/08-relational-read-path/04-query-execution.md)), link
emission adds no asymptotic overhead to collection reads — it is bounded by the number of reference
sites per item times the page size, and all inputs (binding columns, discriminators, and the
`DocumentId → DocumentUuid` map from the auxiliary `dms.Document` result set) are already hydrated as
part of the page. There is no N+1 risk: the auxiliary `dms.Document` lookup is one logical additional
lookup phase per page — one result set in the common case and multiple result sets only when sub-batching
is required for parameter-count limits — not one lookup per reference or per item.

---

## Out of Scope

- Document-store backend link injection (relational backend only).
- OpenAPI specification updates and Discovery API responses.
- Absolute-URL emission server-side.
- `Location`-header GUID format migration (D → N).
- Caller-specific cache keying or materialization by tenant / qualifier routed prefix. The cached
  document remains caller-agnostic; request-visible prefix assembly happens only at the serving
  boundary.
- Propagation of reference-target Discriminator as a local binding column on the referencing row (the
  rejected abstract-resolution alternative).
- Target-resource authorization at link-emission time.
- Discovery-API `link` elements (e.g., on the API root document) — follows a different contract.

Deferred follow-on work. This design intentionally does not invent Jira ids for work that has not yet been
split from DMS-622. Once this design is approved, each item below should be split into a dedicated follow-on
Jira and linked from this section.

| Deferred item | Why deferred in V1 | Tracking requirement |
|---------------|--------------------|----------------------|
| Descriptor-reference links | ODS parity exists, but V1 stays scoped to document-reference hydration and continues to use canonical descriptor URIs only | After design approval, create and link a dedicated follow-up Jira from this section. Ticket seed: extend descriptor-reference emission to produce ODS-like `{ rel, href }`, define coexistence or migration from canonical descriptor URIs, and add parity/OpenAPI/Discovery coverage |
| `Location`-header GUID alignment (`D` → `N`) | API-visible identifier-format change kept out of the initial rollout | After design approval, create and link a dedicated follow-up Jira from this section. Ticket seed: align frontend `Location` header generation with link-href GUID formatting and update contract coverage for POST/PUT response headers |
| OpenAPI / Discovery updates | Requires schema, documentation, and discovery-surface changes beyond runtime link emission | After design approval, create and link a dedicated follow-up Jira from this section. Ticket seed: document and advertise reference `link` behavior accurately across OpenAPI and Discovery, including the V1 document-versus-descriptor split or its eventual removal |
| Resource-scoped write-time `DocumentUuid` stamping optimization | V1 uses the per-page auxiliary `dms.Document` lookup and intentionally avoids new per-reference `..._DocumentUuid` columns, write-time stamping, and backfill work | After design approval, create and link a dedicated follow-up Jira from this section if profiling justifies it. Ticket seed: add optional `{ReferenceBaseName}_DocumentUuid` storage, extend write-time referential resolution to stamp `DocumentUuid`, define resource-scoped opt-in, and provide backfill/runbook guidance |
| Readable-profile projector alignment for `link` | The current runtime `ReadableProfileProjector` still drops nested `link` fields unless it is updated to treat them as server-generated, so profile-scoped reads remain non-conformant until this lands | After design approval, create and link a dedicated follow-up Jira from this section unless it is delivered in the initial implementation. Ticket seed: update `ReadableProfileProjector` to preserve nested `link` as a server-generated field and add profile-scoped regression coverage |

---

## Risks and Open Questions

1. **GUID format divergence.** Introducing `"N"` format on link hrefs while `Location` headers continue
   to use `"D"` creates an internal inconsistency: a client reading a POST `Location` header and later
   finding the same document as a reference in a GET response will see two different GUID spellings for
   the same document. Mitigation: align `Location` headers to `"N"` as a follow-up. Risk accepted for
   this story to limit blast radius.
2. **Residual note — stamped column storage and backfill cost (only relevant if the write-time stamping
   optimization is adopted).** V1 uses the per-page `dms.Document` auxiliary lookup and incurs no
   storage or backfill cost. If write-time stamping is later opted into for a specific resource
   (see [Future Optimization: Write-Time Stamping](#future-optimization-write-time-stamping)), at that
   point a one-time backfill of the `{ReferenceBaseName}_DocumentUuid` column for existing rows is
   required; sizing is per-resource and small relative to the `dms.Document` table itself. The V1
   batched-lookup path remains available as a fallback if the backfill window is unacceptable.
3. **Readable profile interaction (bidirectional).** The Core-owned readable profile projector (see
   [profiles.md](profiles.md)) runs after full reconstitution and filters the document tree.
   - **Source side — projector preserves links.** This is a normative contract, not an open question:
     the readable projector MUST preserve `link` subtrees on reference objects regardless of profile
     member-selection rules. See [Profile Compatibility](#profile-compatibility) for the full rule
     and rationale.
   - **Target side — hidden-target disclosure.** Link emission does not consult the target resource's
     readability under the caller's profile. This is deliberate; see
     [Profile-Hidden Target Resources](#profile-hidden-target-resources) for the accepted disclosure
     envelope and operator guidance when a profile must hide target-type existence from a caller.
4. **Reverse-proxy path fidelity.** The served response now carries a DMS-routable href, so the serving
   boundary must assemble the routed prefix from the same deployment-visible path contract used to route
   the request. This is straightforward because `Location` header assembly already depends on the same
   inputs (`PathBase`, tenant / qualifiers, `/data`). The risk is therefore not conceptual mismatch but
   implementation drift between `Location` assembly and body-link assembly; tests should assert they stay
   aligned.
5. **Feature flag default.** Default-on matches ODS expectations but changes the response shape for any
   existing DMS client that parses relational-backend GET responses. Ensure downstream clients tolerate
   an additional `link` property on reference objects (additive, JSON-safe).
6. **Abstract resolution map drift.** The plan-compile-time map from `Discriminator` → concrete
   endpoint slugs must be regenerated whenever a new concrete subclass is added. Normal schema-refresh
   flows already handle this; call it out explicitly in the compiled-plan invalidation rules.
7. **Flag flip interacts with `dms.DocumentCache` and `_etag`.** Flag flip interacts with
   `dms.DocumentCache` freshness inputs (see `data-model.md` §`dms.DocumentCache` ~line 507) and
   `_etag` derivation (see `update-tracking.md` §Serving API metadata ~line 123). The cache-validity
   rule in [Cache and Etag Interaction](#cache-and-etag-interaction) ensures correctness; downstream
   cross-references in those two docs are tracked separately.

---

## Testing Strategy

- **Unit tests** (reconstitution engine):
  - Concrete reference with a fully-defined identity and a matching row in the auxiliary `dms.Document`
    result set → emits correct `rel` and `href`.
  - Concrete reference with a partial/missing identity → emits no `link`.
  - Concrete reference with an identity that has a zero-value component (e.g., `SchoolId = 0`) → emits no
    `link`. This is the parity-with-ODS typed-default case; DMS NOT NULL constraints make it unreachable
    in practice today, but the test confirms the gate is correctly implemented for ODS behavioral parity.
  - Concrete reference with a fully-defined identity but no matching row in the auxiliary `dms.Document`
    result set (lookup miss / dangling reference) → `DocumentUuid` resolves to null → emits no `link`.
    This directly exercises the Task 3 null gate via the most common V1 null path.
  - Concrete reference with a fully-defined identity and a null `DocumentId` FK → emits no `link`.
  - Abstract reference → resolves `Discriminator` to the correct concrete `rel` and endpoint slugs.
  - **Unresolvable discriminator → no `link` emitted** (three sub-cases covering each failure mode in
    [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)):
    - `Discriminator` column is null → `link` suppressed; identity fields still emitted.
    - `Discriminator` is non-null but does not match the native DMS `"ProjectName:ResourceName"`
      format (including legacy ODS-style `edfi.School`, lowercase-project-name variants, and other
      non-native values — no normalization is attempted) → `link` suppressed; no exception propagates.
    - `Discriminator` parses cleanly but `(ProjectName, ResourceName)` is absent from the
      `LinkEndpointTemplate` map → `link` suppressed; no exception propagates.
  - Feature flag off → no `link` emitted even for fully-defined references.
  - GUID formatting → `href` contains 32 hex chars with no hyphens.
  - Auxiliary result set with multiple referenced resources on the same page → each reference resolves
    to its own `DocumentUuid` correctly from the shared page-level map.
  - Hydration-command-builder boundary test → distinct FK values are deduplicated before auxiliary lookup,
    and SQL Server partitions before crossing its 2,100-parameter ceiling.
- **Fixture tests** covering at least:
  - Resource with a concrete reference (e.g., `AcademicWeek` referencing `School`).
  - Resource with an abstract reference (e.g., any `educationOrganizationReference` site).
  - Resource with a nested-collection reference (link appears inside collection elements).
- **Contract tests** comparing link shape and values against an ODS baseline fixture on the same
  semantic input, scoped to **document references only** (goal: byte-for-byte `link` parity where
  feasible for document references; descriptor-reference link parity is out of scope in V1).
- **GET-many** integration tests verifying link emission at page boundaries.
- **Routable href regression.** Emitted hrefs MUST follow the current request's actual DMS route shape.
  Test matrix:
  - deployment without `PathBase`, tenant, or qualifiers → `/data/{project}/{endpoint}/{uuid:N}`
  - multitenant / qualifier deployment → `/{tenant?}/{qualifiers...}/data/{project}/{endpoint}/{uuid:N}`
  - `PathBase` deployment → `{PathBase}/.../data/{project}/{endpoint}/{uuid:N}`
- **Feature-flag-off regression** ensuring legacy clients continue to see link-free responses when
  operators opt out.
- **Cache-flag mismatch regression.** A cached row materialized with the old `ResourceLinksFlag` value is
  treated as stale by a process started with the new flag and is re-materialized before serving; the stale
  cached JSON is never returned as a hit.
- **Profile-scoped read preserves link.** A readable profile with `MemberSelection.IncludeOnly` that
  does **not** list `link` in its property set is applied to a GET request against a resource whose
  references emit `link` under the unrestricted read path. Assert that `link` is still present on
  every reference in the projected response — confirming the readable projector treats `link` as a
  server-generated field rather than a suppressible member. See
  [Profile Compatibility](#profile-compatibility) for the normative contract this test validates.
- **Source-readable / target-denied authorization scenario.** A caller can read the source resource but
  would fail a direct GET against the target resource under the active authorization strategy; fully-defined
  references still emit `rel` and `href`.
- **Descriptor-vs-document heterogeneity regression.** Document references emit `link` in V1 while
  descriptor references continue to emit canonical descriptor URIs only, confirming the temporary mixed
  client contract described in [Non-Goals](#non-goals).

---

## Level of Effort

Qualitative; refined during the story tasks.

- Compiled-plan extension (new `DocumentReferenceBinding` fields, endpoint-template precomputation):
  small.
- Hydration SQL change (left-join `{AbstractResource}Identity` for abstract references): small,
  additive to existing multi-result hydration structure.
- **Per-reference resource-table DDL changes for V1: none.** No new
  `{ReferenceBaseName}_DocumentUuid` columns are emitted in V1; the auxiliary `dms.Document` lookup
  reuses the existing `..._DocumentId` FK. The only schema-surface change in scope is the companion
  `dms.DocumentCache.ResourceLinksFlag` column described in [Cache and Etag Interaction](#cache-and-etag-interaction)
  and owned by [data-model.md](data-model.md).
- **Write-path changes for V1: none.** No stamping at insert/update/upsert. The write path is
  unchanged relative to the pre-link-injection baseline.
- Read-path auxiliary result set (per-page `dms.Document` lookup): small. One logical `SELECT
  DocumentId, DocumentUuid FROM dms.Document WHERE DocumentId IN (...)` lookup per page — a single
  result set in the common case, partitioned only when parameter-count limits require it — keyed by a
  primary-key index. Analogous in scope and complexity to the descriptor URI projection auxiliary
  result set already implemented in [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6.
- Reconstitution-engine reference-writing extension (auxiliary result set zip + null gate): small,
  additive.
- Feature-flag plumbing: trivial.
- Tests (unit + fixture + contract + integration): medium — the bulk of the effort, especially
  document-reference parity contract tests against ODS baselines (descriptor-reference parity is
  out of scope; see [Non-Goals](#non-goals)).

**Future optimization (write-time stamping, if later adopted for specific resources):**

- DDL emission extension ([ddl-generation.md](ddl-generation.md)): small — add `uuid` column
  alongside existing per-reference binding columns.
- Write-path stamping (extend bulk `ReferentialId → DocumentId` lookup to return `DocumentUuid` and
  persist it at insert): small-to-medium.
- Per-resource backfill tooling/runbook: small.

Overall sizing (V1): **medium** — a single story in the read-path epic, but one that touches seven
distinct implementation surfaces:

1. Plan compiler — new `DocumentReferenceBinding` fields and `LinkEndpointTemplate` precomputation.
2. Hydration SQL builder — abstract-identity LEFT JOIN, auxiliary `dms.Document` result set with
   empty-set skip and large-set partitioning.
3. Auxiliary reader — collect page FKs, issue the auxiliary query, build the page-level
   `DocumentId → DocumentUuid` map.
4. Reconstitution writer — consume the map and the discriminator projection, then emit `{rel, href}`
   via the null gate.
5. Readable profile projector (`ReadableProfileProjector.cs`) — treat `link` as server-generated on
   nested reference objects.
6. Cache freshness check plus `dms.DocumentCache.ResourceLinksFlag` DDL column (via a companion edit
   to [data-model.md](data-model.md)).
7. Feature-flag plumbing — `DataManagement:ResourceLinks:Enabled` wired into the reconstitution engine as a startup-scoped deployment setting.

Plus document-reference parity contract tests that assert link-shape and presence-gate equivalence
against the legacy ODS response bodies for representative concrete and abstract reference sites
(descriptor-reference parity is deferred; see [Non-Goals](#non-goals)).

Each surface is small-to-medium individually; the integration cost across the seven surfaces — and
the contract-test authoring for document-reference parity — is the dominant factor in the overall
medium sizing.

---

## Cross-References

- [DMS-622](https://edfi.atlassian.net/browse/DMS-622) — Jira ticket for this feature.
- [DMS-988 — Relational Read Path Epic](../epics/08-relational-read-path/EPIC.md) — parent epic.
- [06-link-injection.md](../epics/08-relational-read-path/06-link-injection.md) — story realizing this
  design.
- [02-reference-identity-projection.md](../epics/08-relational-read-path/02-reference-identity-projection.md)
  — prerequisite identity-projection story this design extends.
- [data-model.md](data-model.md) — abstract identity tables and Discriminator column.
- [flattening-reconstitution.md](flattening-reconstitution.md) — reference reconstitution engine and
  `DocumentReferenceBinding`.
- [auth.md](auth.md) — authorization model.
- [profiles.md](profiles.md) — readable profile projection (link preservation requirement).
