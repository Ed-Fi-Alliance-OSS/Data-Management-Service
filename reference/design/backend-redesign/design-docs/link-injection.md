# Link Injection Design

## Status

Draft.

This document describes **link injection** for document-reference properties in DMS GET responses against the relational
backend. The feature emits a `{ rel, href }` object on every fully-defined document reference in a response
body (FKs into `dms.Document`; descriptor references are intentionally deferred in V1 — see Non-Goals), with
reference-object shape closely aligned to the Ed-Fi ODS contract (see [ODS Parity Reference](#ods-parity-reference))
while retaining the deliberate DMS-routable served-href divergence described in [Href Construction](#href-construction),
all without per-resource code generation.

This document is also the sole feature-local source of truth for
link-injection-specific startup validation, discriminator-failure logging,
startup-bound cache-reconciliation rules, and the feature-local additions
to `DocumentReferenceBinding` and the hydration-command auxiliary-result-set
surface (see [Compiled Read-Plan Extensions](#compiled-read-plan-extensions)).
Shared redesign docs ([compiled-mapping-set.md](compiled-mapping-set.md),
[flattening-reconstitution.md](flattening-reconstitution.md),
[new-startup-flow.md](new-startup-flow.md)) describe the base binding
contracts and generic lifecycle hooks where these extensions and checks run,
but they do not restate the feature-local protocol; this document owns
the full feature-local contract and the base docs deliberately do not
carry reciprocal back-pointers.

- [overview.md](overview.md) — backend redesign overview and context
- [data-model.md](data-model.md) — `dms.Document`, abstract identity tables, propagated reference-identity binding columns
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
    - [Deferred Follow-On Work](#deferred-follow-on-work)
  - [Risks, Open Questions, and Decided Constraints](#risks-open-questions-and-decided-constraints)
    - [Risks](#risks)
    - [Decided Constraints](#decided-constraints)
  - [Testing Strategy](#testing-strategy)
  - [Level of Effort](#level-of-effort)
  - [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

1. **ODS document-reference parity (V1 scope).** Emit `{ rel, href }` on every fully-defined **document**
  reference property in GET responses (FKs into `dms.Document`) with reference-object shape and cached
  path-tail values closely aligned to the Ed-Fi ODS resource-link contract for document references,
  subject to the deliberate DMS divergences called out in this document. V1 deliberately does **not** target full ODS link parity:
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
  [Risks, Open Questions, and Decided Constraints](#risks-open-questions-and-decided-constraints)).
- Propagation of discriminator or identity metadata beyond what is already defined in
  [data-model.md](data-model.md).
- **Descriptor references.** Descriptor references on resources are not targeted for `link`
  emission in V1. DMS keeps their current wire surface: fully-qualified descriptor URI strings.
  The ODS generated resources file contains both a `DescriptorReference` helper with `CreateLink()`
  and ordinary descriptor members on resource payloads modeled as string properties (for example,
  `sexDescriptor`). This design therefore does **not** rely on a dead-code claim to justify scope:
  V1 simply defers any descriptor-link contract expansion. Clients MUST treat `link` as
  **optional on a per-reference basis**: document references emit `link`; descriptor references do
  not. OpenAPI and Discovery follow-on work MUST document that heterogeneous behavior. See
  [Out of Scope](#out-of-scope) for the decision gate that controls any future change.

---

## Problem Statement

Consumers of the Ed-Fi DMS relational-backend GET responses today receive reference identity fields
(natural keys) but no navigable pointer to the referenced resource. This diverges from ODS document-reference
behavior, where reference objects carry a `link: { rel, href }` object, and forces consumers to reconstruct endpoint paths
client-side. For abstract references the problem is worse: a client cannot determine the concrete subclass
of an `educationOrganizationReference` without querying every possible concrete endpoint.

This design adds ODS-shaped link objects to reference properties in relational-backend GET responses,
fully derivable from existing `ApiSchema.json` metadata and the compiled read plan, and emitted during the
reconstitution pass with no additional round-trips while keeping the final served href DMS-routable per
[Href Construction](#href-construction).

---

## ODS Parity Reference

The ODS behavior this design mirrors:

Citations below reference [Ed-Fi-Alliance-OSS/Ed-Fi-ODS at tag `v7.3`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/v7.3),
which implements Data Standard 5.2.0. Stable tag URLs and class/method names are used instead of local
checkout line numbers because `Resources.generated.cs` is generated and shifts between standard versions.

- **Link shape**: `{ "rel": "...", "href": "..." }` — exactly two `DataMember` fields, no `title`/`type`/
  `hreflang`. Defined by [`Link`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Api/Models/Link.cs)
  at tag `v7.3`.
- **Href template**: relative path `/{schemaUriSegment}/{pluralCamelEndpointName}/{ResourceId:N}` —
  e.g., `"/ed-fi/schools/550e8400e29b41d4a716446655440000"`. Sourced from generated `CreateLink()`
  methods in
  [Resources.generated.cs @ v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs):
  - Concrete reference example: `SchoolReference.CreateLink()` assigns `Rel = "School"` and constructs
    `Href` from the schema URI segment and the `Schools` plural endpoint name plus `ResourceId.ToString("n")`
    in the ODS source (equivalent to the `"N"` shape used throughout this document; format specifiers
    are case-insensitive).
  - Abstract reference example: `AcademicWeek.SchoolReference.CreateLink()` (an abstract
    `educationOrganizationReference` resolution) calls `Discriminator.Split('.')` to extract
    `(ProjectName, ResourceName)`, then assigns `Rel` from the resource-name segment and constructs
    `Href` using `SchemaUriSegment()` and `PluralName.ToCamelCase()`. GUID format is `"N"` — 32 hex
    characters, no hyphens. If `Discriminator` is null, malformed, or resolves to no matching resource,
    the generated ODS code path falls back to its default abstract link rather than suppressing
    `link`. DMS treats that as the observed v7.3 code-generation pattern and validates the intended
    parity boundary with contract tests rather than assuming broader runtime equivalence from code
    inspection alone.
- **Descriptor references in ODS**: the generated resources file contains both a `DescriptorReference`
  helper type with a `link` property and `CreateLink()` method **and** ordinary descriptor members on
  resource payloads modeled as string properties (for example, `sexDescriptor`). This design relies on
  the generated resource-payload shape, not on the helper type alone, when describing current descriptor
  behavior. DMS V1 keeps descriptor references on their existing canonical-URI surface and does not
  introduce descriptor `link` objects. Any stronger descriptor-link parity claim should be backed by
  runtime fixture evidence and is deferred from this design.
- **Rel**: the concrete target resource name (e.g., `"School"`, `"LocalEducationAgency"`). Abstract
  reference resolution uses a `(ProjectName, ResourceName)` pair extracted from a discriminator string;
  the ODS and DMS formats differ:
  - **ODS format.** `Discriminator` is dot-separated with a lowercased project token (e.g.,
    `"edfi.School"`); `AcademicWeek.SchoolReference.CreateLink()` in `Resources.generated.cs` at v7.3
    splits on `.` to produce the pair.
  - **DMS format.** `Discriminator` is colon-separated with the business-cased project name (e.g.,
    `"Ed-Fi:School"`; see [data-model.md](data-model.md) "Abstract identity tables for polymorphic
    references"). "Business-cased" means the project name is emitted exactly as it appears in the
    project's business identity — for the core project that is the literal hyphenated brand form
    `Ed-Fi`, not the identifier-safe form `EdFi`. The separator and casing differences are intentional
    DMS divergences for readability.
  - **Safety guarantee.** The DMS parser accepts **only** the native `:` form and **only** the
    business-cased project name that the DMS write path emits; no ODS `.` normalization and no
    lowercase-project-name alias is attempted. DMS abstract identity tables are populated exclusively
    by the DMS write-path triggers, so a legacy ODS-format value cannot appear in a healthy deployment
    — if one is observed at read time, it is treated as an *unparseable* discriminator and the link is
    suppressed per
    [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator).
    Any migration or backfill that writes abstract-identity rows outside those triggers MUST normalize
    incoming discriminator values to the native DMS format before serving traffic, or link suppression
    is the intended read-time outcome.
  - **Out of scope.** Defining a stable normalization between ODS project tokens and DMS `ProjectName`
    values is out of scope for V1; if ever needed, it becomes a dedicated follow-up with its own
    design.
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
prepends the current request's routed prefix (see [Href Construction](#href-construction)).

Served form (post–routed-prefix assembly, as clients see it):

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
      `default(DateTime)` / `default(DateTimeOffset)` for temporal types (that is, the declared type's
      C# default-value sentinel), and the analogous default for any other identity-field type. If any
      identity field fails this gate, the `link` property is omitted (matches ODS). The rule is stated
      defensively — to guard against future data-integrity drift and to preserve exact ODS `default(T)`
      semantics — even though DMS NOT NULL constraints on identity columns make the zero-value path
      unreachable in practice today.
  2. **The resolved target `DocumentUuid` is non-null** — if the `DocumentUuid` for the referenced
     document is null or missing for any reason (no matching row returned by the per-page batched
     `dms.Document` lookup, or an unresolvable reference after a future schema change), the `link`
     property is suppressed entirely. This gate prevents malformed
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
  binding's compiled `EndpointTemplate` slot. Endpoint-slug resolution and caching happen **at plan
  compile time** for every reference kind (concrete and abstract); no endpoint-slug lookup occurs on
  the read-request hot path. Precomputing the slug removes per-request schema traversal from the
  reconstitution path while keeping the template stable for the lifetime of the compiled plan.
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
`/ed-fi/educationOrganizations/{id:N}` (see
[Resources.generated.cs @ v7.3](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3/Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs)),
and — critically — keeps `dms.DocumentCache` caller-agnostic: the cached materialization is identical for
every caller who can read the document, regardless of ingress path. See
[Cache and Etag Interaction](#cache-and-etag-interaction).

**Served stage: prepend the routed prefix.** After cache retrieval (and after readable-profile projection,
if any), DMS prepends the current request's deployment-visible routed prefix to the cached suffix before
serializing the response body. The routed prefix is a slash-separated concatenation of the following
components, where `{PathBase}` is reused exactly as ASP.NET exposes it and every later segment is
either absent or emitted with a single leading `/`:

- `{PathBase}` — the ASP.NET path base, if any (typically already carrying its leading slash).
- `/{tenant}` — the tenant segment, if multitenancy is active.
- `/{qualifier}` — one or more route qualifier segments, in order, if any.
- `/data` — always present; this is the fixed data-root segment.

Absent components are omitted entirely (including their leading slash). The fixed `/data` segment
always precedes the cached suffix with exactly one `/` between them, because the cached suffix itself
begins with `/{projectEndpointName}`. The final served href therefore has the shape:

```
{PathBase}[/{tenant}][/{qualifier}...]/data/{projectEndpointName}/{endpointName}/{documentUuid:N}
```

Examples (showing the concrete slash layout for three deployment shapes):

- no `PathBase`, no tenant, no qualifiers:
  `/data/ed-fi/schools/550e8400e29b41d4a716446655440000`
- multitenant + qualifier:
  `/district-a/2026/data/ed-fi/schools/550e8400e29b41d4a716446655440000`
- `PathBase` + tenant:
  `/dms/tenant-a/data/ed-fi/schools/550e8400e29b41d4a716446655440000`

This keeps generic clients correct on DMS deployments without forcing request-scoped data into the
cache. The resulting V1 contract: ODS parity is retained for the cached path tail and the
`{ rel, href }` shape; the served body href follows DMS's actual routed `/data/...` surface so
generic DMS clients can dereference it directly. Byte-for-byte mimicry of ODS's prefix-free
body-path contract is explicitly not a V1 goal — see [Non-Goals](#non-goals).

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
follow-up in [Risks, Open Questions, and Decided Constraints](#risks-open-questions-and-decided-constraints); it is **out of scope** for this story
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
  resolves the endpoint slug once at plan compile time and freezes it into the binding's
  `EndpointTemplate` slot rather than expecting it on `ResourceSchema`. The resolved template is
  immutable for the lifetime of the compiled plan.
- **Serving prefix inputs**: the final DMS-routable prefix comes from the request's existing frontend
  routing context (`PathBase`, tenant / qualifier segments, and the fixed `/data` segment). Link
  injection does not require new schema fields to model that prefix; it reuses the same deployment-
  visible routing inputs that already drive `Location` header assembly.

### Abstract Reference Resolution

**Prerequisites.** Missing abstract-identity tables, missing `Discriminator` columns, or missing
discriminator-maintenance triggers are **deployment-drift conditions**. They MUST fail per-instance
startup validation for the affected database and are never treated as runtime link-suppression
paths. That validation is a backend-startup responsibility executed during per-instance mapping
initialization (see [new-startup-flow.md](new-startup-flow.md)), before the instance enters service.
This resolution strategy depends on two invariants established elsewhere in the design:

1. **Abstract identity tables and discriminator-maintenance triggers are emitted.**
  [ddl-generation.md](ddl-generation.md) §3 requires that every abstract resource referenced by a
  concrete resource in the hydrated set has a corresponding `{schema}.{AbstractResource}Identity`
  table deployed, with a trigger-maintained non-null `Discriminator` column and the per-concrete
  subclass trigger that maintains it.
2. **Per-instance schema validation enforces the invariant at startup.**
  The existence check belongs to backend mapping initialization for the
  instance, alongside the existing DB fingerprint checks. This document
  defines the feature-local validation checklist and failure behavior.

**Startup validation checklist.** For every abstract reference site reachable from the compiled
mapping set, per-instance schema validation MUST verify the following in each attached database:

1. The target `{schema}.{AbstractResource}Identity` table exists.
2. The `Discriminator` column exists on that table with the declared type and a `NOT NULL`
   constraint (no runtime recovery is defined for a nullable or missing `Discriminator` column —
   those are deployment-drift states, not data anomalies, and must fail fast).
3. The trigger for each concrete subclass that maintains `Discriminator` on the concrete member root
  table is present (using the `TR_{TableName}_AbstractIdentity` name defined by
  [data-model.md](data-model.md) §5 "Constraint, index, and trigger names"; validation still relies
  on [ddl-generation.md](ddl-generation.md) §3 for which tables emit that trigger). The check
  asserts trigger existence by name, not trigger body equivalence.

**Consequences of a failed check.** A failure of any of the three sub-checks fails
**per-instance startup validation** for that instance, with a clear error naming the abstract
resource, the referencing concrete resource, the specific missing object (table / column / trigger),
and the affected database, and prevents that instance from entering service. The shared mapping-set
compile still succeeds, and other healthy instances may continue serving. Plan compilation itself
remains schema-driven and database-agnostic, so a single compiled mapping set can be reused across
instances with compatible fingerprints. Runtime warn-and-suppress (see
[Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)) is reserved
only for the three row-level data anomalies defined there (`null`, `unparseable`, `unmapped`) —
**not** for deployment drift where the table, column, or trigger is missing outright.

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
plan-bound main-result column binding. During reconstitution the engine parses the `Discriminator` into
`(ProjectName, ResourceName)` by splitting on the first `:` and requiring both sides to be non-empty;
otherwise the discriminator is `unparseable`. No trimming, case normalization, or alternate-separator
handling is applied. It then resolves:

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
   is attempted in V1 (see [ODS Parity Reference](#ods-parity-reference) for the rationale);
   parsing produces no valid `(ProjectName, ResourceName)` pair.
3. **`Discriminator` parses cleanly but the resulting `(ProjectName, ResourceName)` pair is absent
   from the precomputed `EndpointTemplate` map (i.e., the abstract variant of `LinkEndpointTemplate`).**
   The concrete subclass may have been removed in a schema refresh that has not yet propagated to the
   running instance, or metadata was regenerated without that subclass. The pair is valid but has no
   matching entry in the frozen compile-time map.

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
  logger applies per-process, per-category rate limiting using fixed wall-clock one-minute windows
  aligned to UTC: at most 10 warn-level entries per window for each failure category (`null`,
  `unparseable`, `unmapped`). When one or more occurrences are suppressed during a window, the logger
  emits one summary warn entry for that category when the window closes (or at shutdown as a best-effort
  flush). That summary entry is diagnostic bookkeeping and does **not** consume the next window's
  per-category budget.
- **Sanitization.** Every external-origin field in the payload (source `QualifiedResourceName`,
  reference JsonPath) MUST pass through
  [`LoggingSanitizer.SanitizeForLogging(...)`](../../../../src/dms/core/EdFi.DataManagementService.Core/Utilities/LoggingSanitizer.cs)
  before emission. That utility delegates to the canonical structured-log whitelist sanitizer and allows
  only letters, digits, spaces, and safe punctuation (`_-.:/`); control characters are excluded.
  `DocumentId` values are integers and are safe as-is.

These warn-level entries apply only to the three runtime row-level anomaly cases above. Deployment
drift where the table, column, or trigger is missing remains a startup failure and never degrades into
runtime warn-and-suppress behavior.

### Referenced DocumentUuid Availability

The `href` requires the referenced document's `DocumentUuid`. This is a distinct requirement from the
reference *identity* columns already addressed by story
[02-reference-identity-projection.md](../epics/08-relational-read-path/02-reference-identity-projection.md),
with different read-time mechanics: those natural-key identity bindings are stored locally on the
referencing row via write-time propagation, whereas `DocumentUuid` is resolved at read time via a
batched auxiliary lookup against `dms.Document`.

**V1 Design: page-batched `dms.Document` lookup.**

After the main relational read materializes a hydrated page of result rows with their reference
`..._DocumentId` FK columns, the read plan issues one logical auxiliary lookup per hydrated page
against `dms.Document`. In the common case this is a single auxiliary query/result set:

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

**Isolation invariant.** The main hydration result sets and the auxiliary `dms.Document` lookup share
a single ADO.NET command and therefore run under the same ambient transaction and isolation level as
the rest of the read path (see
[transactions-and-concurrency.md](transactions-and-concurrency.md) for the read-path isolation
contract). No second round-trip or separate transaction is opened. If a referenced document is
deleted between the main hydration phase and the auxiliary lookup phase within the same command, the
lookup misses; `link` is suppressed via the null gate in [Link Shape](#link-shape). This is the
intended behavior — suppressing a link for a vanished target is strictly safer than emitting an href
to a deleted document — and it is the same snapshot-consistency posture as the descriptor URI
auxiliary lookup.

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
- **Large set.** The auxiliary-lookup parameter count equals the deduplicated set of **distinct**
  `{ReferenceBaseName}_DocumentId` FK values present anywhere on the page. Its worst case is
  `page-size × distinct-reference-sites-per-row`, reached only when every reference occurrence points
  at a different target document. The partitioning threshold MUST be dialect-aware: PostgreSQL
  tolerates high parameter counts, but SQL Server has a hard limit of 2,100 parameters per statement.
  A page of 500 rows with 5 distinct reference sites each can therefore exceed the SQL Server bound
  when every FK is unique (`500 × 5 = 2,500`).
  When the distinct FK count for a page exceeds the dialect-specific threshold, the auxiliary lookup
  partitions the FK set into sub-batches and issues one auxiliary result set per sub-batch within the same
  multi-result hydration command. The sub-batches are zipped together at reconstitution time and surface to
  the reference writer as a single unified `DocumentId → DocumentUuid` map. When descriptor URI expansion is
  also needed for the page, the common command shape is: main hydration rows, descriptor URI lookup result
  set, and document-uuid lookup result set. The exact threshold and partitioning strategy remain hydration-
  command-builder implementation details, but the SQL Server cap is a hard upper bound.

### Compiled Read-Plan Extensions

The compiled `DocumentReferenceBinding`
([flattening-reconstitution.md](flattening-reconstitution.md) §7.3) gains four additions, populated at
plan compile time. `IsAbstractTarget` and `EndpointTemplate` are emitted unconditionally;
`DiscriminatorBinding` and `DocumentUuidBinding` are emitted **only when `ResourceLinks:Enabled=true`
at startup**, because they correspond to the abstract-identity LEFT JOIN and the per-page auxiliary
`dms.Document` lookup — both omitted from the compiled read plan when the flag is `false` (see
[Feature Flag](#feature-flag)).

**Plan-shape semantics (normative).** Flag-off is a **compile-time plan-shape omission**, not a
runtime gate on a unified plan. The `DiscriminatorBinding` and `DocumentUuidBinding` fields are
modeled as nullable on the binding record (`HydrationProjectionBinding?` and
`AuxiliaryResultSetProjection?`) purely for type representation; when the flag is `false`, the
plan compiler produces a binding record with those fields set to `null` **and** the plan's hydration
SQL omits the corresponding LEFT JOINs and auxiliary lookup entirely. Reconstitution checks for
null on the binding, not a runtime flag value — no flag lookup occurs on the hot path after plan
compilation. Flag-on and flag-off therefore produce materially different plan shapes, not the same
plan with a runtime switch; cache invalidation between shapes is handled by the startup plan-shape
fingerprint (see [Cache and Etag Interaction](#cache-and-etag-interaction)).

The four additions:

- `IsAbstractTarget: bool` — true when `TargetResource` refers to an abstract resource.
- `DiscriminatorBinding: HydrationProjectionBinding?` — for abstract targets only, points at the
  hydration-projected `Discriminator` column (from the left-joined `{AbstractResource}Identity` row).
  This keeps row-local `DbColumnName` addressing distinct from the main hydration result-set column
  binding.
  Null for concrete targets. Also null when `ResourceLinks:Enabled=false`, and the corresponding
  abstract-identity LEFT JOIN is omitted from the compiled hydration SQL in that case.
- `DocumentUuidBinding: AuxiliaryResultSetProjection?` — describes how to look up `DocumentUuid`
  from the per-page auxiliary `dms.Document` result set: keyed by the local
  `{ReferenceBaseName}_DocumentId` FK column on the referencing row, resolved against the page-level
  `DocumentId → DocumentUuid` map built from the auxiliary result set. This mirrors the
  `DescriptorEdgeSource → (DescriptorId, Uri)` auxiliary-result-set projection in
  [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 6. Null when
  `ResourceLinks:Enabled=false`, and the corresponding auxiliary `dms.Document` lookup is omitted
  from the compiled hydration command in that case.
- `EndpointTemplate: LinkEndpointTemplate` — precomputed slot holding the endpoint-resolution template
  for this reference:
  - For concrete targets: a fixed `(projectEndpointName, endpointName)` pair.
  - For abstract targets: a **map** keyed by the `(ProjectName, ResourceName)` **tuple produced after
    splitting and parsing the `Discriminator` string** (not keyed by the raw discriminator string), with
    values `(projectEndpointName, endpointName)` for each concrete subclass. The map is bounded by the
    number of concrete subclasses of the abstract resource and is frozen at plan compile time. Adding a
    new concrete subclass therefore requires plan recompilation / restart; an unseen pair at read time
    is the `unmapped` failure mode described in
    [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator).

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
  DbColumnName LocalKeyColumn,
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
- `AuxiliaryResultSetName`: a **logical** identifier for the auxiliary lookup within the multi-result
  hydration command (the `dms.Document` lookup for link injection; the descriptor URI lookup for the
  descriptor projection). Each logical auxiliary materializes as one or more contiguous physical
  result sets in a known position range, consistent with the positional `NextResult()` consumption
  pattern already used by the read path (see
  [compiled-mapping-set.md](compiled-mapping-set.md) §4.3 step 3 and
  [flattening-reconstitution.md](flattening-reconstitution.md) §Reconstitution inner loop). The
  common case is a single physical result set per logical auxiliary; sub-batching (see
  [Referenced DocumentUuid Availability](#referenced-documentuuid-availability) "Large set")
  produces multiple contiguous physical result sets for the same logical auxiliary, which the
  reconstitution engine zips into a single unified map before reference writing.

  Positional ordering is part of the compiled plan contract. The plan compiler MUST emit logical
  auxiliaries in a deterministic total order: main hydration result sets first, then each logical
  auxiliary as a contiguous block, with blocks ordered by **declaration order in the compiled plan**
  (i.e., the order in which `AuxiliaryResultSetProjection` records are appended to the plan during
  compilation — descriptor URI projection before link-injection `dms.Document` projection when both
  are present on a resource). The reconstitution reader consumes result sets by the same positional
  contract, so block ordering is a single source of truth defined by the plan compiler and mirrored
  by the reader; it is not re-derived at read time. Within a single logical auxiliary, sub-batches
  are consumed in emission order and their contents are union-merged into the same map, so
  intra-block ordering is not caller-visible.
- `AuxiliaryKeyColumn` / `AuxiliaryValueColumn`: the columns of each physical auxiliary result set
  that form the `key → value` map the reconstitution engine zips during reference writing —
  `DocumentId` and `DocumentUuid` respectively for link injection. Sub-batches use the same
  key/value column pair.
- Null-on-miss semantics: a local key with no matching auxiliary row (in any of the physical result
  sets that make up the logical auxiliary) yields a null lookup result, which `link` emission
  treats as a suppression gate (see [Link Shape](#link-shape)).

`AuxiliaryResultSetProjection` is distinct from both `DbColumnName` and `HydrationProjectionBinding`:
`DbColumnName` names a single column on the referencing row; `HydrationProjectionBinding` addresses a
single projected slot in the main hydration result; `AuxiliaryResultSetProjection` addresses a lookup
against a side-channel result set keyed by one of the main row's columns. The three coexist in
`DocumentReferenceBinding` without overlap.

No new top-level plan objects are introduced; all additions live inside existing binding records.

### Integration Point: JSON Reconstitution

Link emission is a concern of the reconstitution engine, not a post-processing pass. Per
[flattening-reconstitution.md](flattening-reconstitution.md) §6.4, the reconstituter writes references
from binding columns inside a `Utf8JsonWriter` loop. The engine's reference-writing step is extended:

1. Write identity fields of the reference from local propagated binding columns (as today, per story 02).
2. If the reference is **fully defined** — every identity-field value is present and non-default per
   the typed-default rule in [Link Shape](#link-shape) — and the compiled `DocumentReferenceBinding`
   carries a `DocumentUuidBinding` (i.e., the plan was compiled under `ResourceLinks:Enabled=true`;
   see [Compiled Read-Plan Extensions](#compiled-read-plan-extensions)):

   - Resolve the concrete `(projectEndpointName, endpointName)` from the binding's
     `EndpointTemplate` — direct for concrete targets, `Discriminator`-keyed map lookup for abstract
     targets. **If the lookup fails for any of the three reasons enumerated in
     [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator)** (null
     discriminator, unparseable discriminator, or `(ProjectName, ResourceName)` pair absent from the
     map), **skip link emission entirely** — write no `link` property and continue to the next reference.
     This is symmetric to the `documentUuid`-null skip below; both are safety gates on the same
     abstract-reference link-emission path.
   - Read `documentUuid` from `DocumentUuidBinding`: look up the referencing row's
     `{ReferenceBaseName}_DocumentId` FK value in the page-level `DocumentId → DocumentUuid` map built
     from the per-page auxiliary `dms.Document` result set.
   - **If `documentUuid` is null, skip link emission entirely** — write no `link` property and
     continue to the next reference. This covers any null path: a `dms.Document` auxiliary lookup that
     returned no matching row (e.g., a dangling reference), a null FK column, or an unresolvable
     reference after a future schema change.
   - Format the caller-agnostic cached href **suffix** as
     `/{projectEndpointName}/{endpointName}/{documentUuid:N}`. No request-scoped inputs are involved in
     this step. See [Href Construction](#href-construction).
   - Write a `"link": { "rel": ..., "href": ... }` object property after all identity fields of the
     reference have been written, storing the suffix form in the cached / intermediate document.
3. Otherwise, write no `link` property. This covers partially-populated references (any identity field
   null, missing, or equal to its declared type's default, matching ODS typed-default semantics) **and**
   the feature-flag-off state (where `DocumentUuidBinding` is absent from the compiled plan so step 2 is
   never entered).

This keeps all reference-handling logic co-located, preserves deterministic output order, and reuses the
existing streaming writer — no intermediate `JsonNode` materialization. A later frontend serving step
prepends the current request's routed prefix to each emitted `link.href` before the response is
serialized, using the same request-visible routing inputs that already drive `Location` header assembly.

Response-behavior matrix:

| Flag | Reference kind | Identity fully defined | Endpoint resolution | `DocumentUuid` resolution | Output |
|------|----------------|------------------------|---------------------|---------------------------|--------|
| Off | Any document reference | Any | n/a | n/a | Identity fields only; no `link` |
| On | Concrete | No | Fixed | Any | Identity fields only; no `link` |
| On | Abstract | No | n/a | Any | Identity fields only; no `link` |
| On | Concrete | Yes | Fixed | Hit | Emit `link` with fixed `rel` / `href` |
| On | Concrete | Yes | Fixed | Miss or local FK null | Identity fields only; no `link` |
| On | Abstract | Yes | `Discriminator` resolves to mapped concrete endpoint | Hit | Emit `link` with concrete `rel` / `href` |
| On | Abstract | Yes | `Discriminator` null, unparseable, or unmapped | Any | Identity fields only; no `link` |

Identity completeness short-circuits link emission before any reference-kind-specific endpoint-resolution
work, so the `Identity fully defined = No` cases are symmetric for concrete and abstract references.

### Profile Compatibility

Per [profiles.md](profiles.md) §Read Path Under Profiles, readable projection MUST preserve
server-generated fields that are outside profile member selection. For this feature, that set includes
top-level `_etag`, top-level `_lastModifiedDate`, and nested reference `link` subtrees. For link
injection, that durable contract means **`link` on any nested reference object MUST be copied unchanged
into the projected output whenever the reference object itself survives projection**. Profiles cannot
suppress `link` via `MemberSelection.IncludeOnly`.

If a readable profile hides the reference property itself on the source resource, neither the reference
identity fields nor `link` appear in the served response. The preservation rule here applies only once
the reference object has survived source-side projection.

Implementation note (subject to refactor): the runtime readable projector's nested-object path must
honor the same server-generated-field exemption that the profile OpenAPI surface already advertises.
The exact class or helper that carries that exemption is not part of the design contract.

**Relationship to [Profile-Hidden Target Resources](#profile-hidden-target-resources).** The rule
above is a *runtime projector* obligation: once `link` has been emitted by reconstitution, no profile
projector on the source side may strip it. The Profile-Hidden Target Resources section answers a
*different* question — whether `link` should be *emitted at all* when the target resource type is
unreadable under the caller's profile — and decides that it is (source-resource authorization governs
link emission, not target readability). The two sections are therefore complementary, not
contradictory: target-type hiding does not suppress emission, and profile member-selection does not
suppress preservation. Operators who need to hide a target type from a caller must hide the
reference property itself on the source resource, as described in
[Profile-Hidden Target Resources](#profile-hidden-target-resources); no runtime projector filtering
on `link` is offered or permitted.

---

## Feature Flag

A single configuration key controls link emission:

- **Key**: `DataManagement:ResourceLinks:Enabled`
- **Default**: `true` — matching the user-facing expectation set by ODS's feature being enabled by default
  in standard deployments.
- **Behavior when `false`**: link emission is suppressed and the supporting read-path work is elided at
  plan-compile time, not at request time. Specifically:
  - No `link` property is emitted on any reference; no other response shape changes.
  - Plan compilation excludes the per-page `dms.Document` auxiliary lookup (see
    [Referenced DocumentUuid Availability](#referenced-documentuuid-availability)) and the
    abstract-identity LEFT JOINs (see [Abstract Reference Resolution](#abstract-reference-resolution))
    from compiled read plans.
  - The flag is evaluated at startup and baked into plan shape; it is not toggled per request, so
    toggling requires the plan-shape refresh flow described under
    [Cache and Etag Interaction](#cache-and-etag-interaction).

**Consumption model.** The runtime binds the `DataManagement:ResourceLinks` section to a dedicated options
type at process startup and treats it as a process-lifetime deployment setting. V1 does not introduce
in-process hot reload for this flag. In the sections below, a "flag flip" means traffic has moved from
processes started with the old value to processes started with the new value.

Backend startup code owns this binding and threads the resulting `ResourceLinksOptions` into read-plan
compilation during mapping initialization. The flag is evaluated once per process startup and baked into
compiled plan shape; no per-request flag lookup occurs on the hot path.

Note that `DataManagement:ResourceLinks` is a **new configuration section** distinct from the existing
Core `AppSettings` section. Current Core `AppSettings` remains a flat set of properties; link injection
MUST NOT assume a nested `AppSettings.DataManagement` object already exists, and MUST NOT try to read
`ResourceLinks:Enabled` out of `AppSettings`. The two sections coexist side-by-side at the configuration
root and are bound independently.

Normative configuration contract:

```csharp
public sealed class ResourceLinksOptions
{
  public bool Enabled { get; init; } = true;
}
```

Bind `ResourceLinksOptions` from the `DataManagement:ResourceLinks` section and consume it via
`IOptions<ResourceLinksOptions>` (or an equivalent startup-bound options snapshot).

Example DI registration:

```csharp
services.Configure<ResourceLinksOptions>(configuration.GetSection("DataManagement:ResourceLinks"));
```

No per-resource, per-request, or per-reference override is provided. Clients that want minimal responses
disable the flag at the deployment level.

**Operational rule.** Because mixed-plan overlap is not supported, flipping the flag requires a
drained or blue-green restart rather than overlapping old-plan and new-plan processes. See
[Cache and Etag Interaction](#cache-and-etag-interaction).

**Database cost when the flag is off.** Because the flag is evaluated at plan compilation time (startup),
compiled read plans omit the auxiliary `dms.Document` lookup result set and the abstract-identity LEFT
JOINs entirely when `ResourceLinks:Enabled` is `false`. Disabling the flag therefore genuinely reduces
read-path database work — it is a hydration-cost reduction, not merely a response-shape switch.
Re-enabling the flag requires restarting the process so the read plans are recompiled with the auxiliary
lookup and LEFT JOINs included.

### Cache and Etag Interaction

**Cache layer contract (normative).** `dms.DocumentCache` stores the **fully reconstituted caller-agnostic
intermediate document** — the same JSON the reconstitution engine produces before any readable-profile
filtering is applied and before request-scoped href-prefix assembly. The read pipeline is:

1. Reconstitute the document from relational storage (with `link` subtrees already emitted when
   `ResourceLinks:Enabled` is `true`, with `link.href` carrying the caller-agnostic suffix form —
   link emission is a concern of the reconstitution engine, see
   [Integration Point: JSON Reconstitution](#integration-point-json-reconstitution)).
2. Write the reconstituted JSON to `dms.DocumentCache` (if projection is enabled), keyed by
   `DocumentId`. The stored JSON is identical for every caller who can read the document.
3. On subsequent reads, a cache-validity check reads the authoritative `ContentVersion` and
   `ContentLastModifiedAt` columns from `dms.Document` for the requested `DocumentId`, compares them
   against the stamps stored with the cached document, and reuses the cached JSON only when both
   match; otherwise it re-reconstitutes.
4. **After** cache read, Core runs readable-profile projection (per
   [profiles.md](profiles.md) "Read Path Under Profiles") and recomputes `_etag` from the projected
   document with `link.href` still in canonical suffix form. See
   [update-tracking.md](update-tracking.md) §Serving API metadata for the normative derivation rule.
   `link` subtrees are preserved by the projector as server-generated fields; see
   [Profile Compatibility](#profile-compatibility). Both `link.rel` and `link.href` (in canonical
   suffix form) are load-bearing inputs to the canonical hash — a reference whose `link` subtree
   changes (including acquiring or losing the subtree on a `ResourceLinks:Enabled` flip) produces a
   different `_etag`. Any projector or serializer that drops `link` before hashing silently breaks
   the etag contract.
5. The frontend serving boundary prepends the current request's routed prefix to every emitted
   `link.href`, turning the cached suffix into the final DMS-routable relative path.
   **Normative: routed-prefix assembly is a request-scoped transport concern and MUST NOT trigger
   `_etag` recomputation; `_etag` is finalized in step 4 against the pre-prefix-assembly projected
   document, and implementations MUST NOT hash the post-prefix-assembly response body.** This
   preserves ingress-stability: identical documents are etag-equivalent regardless of `PathBase`,
   tenant qualifier, or reverse-proxy prefix. The routed-prefix etag-stability contract test in the
   story pins this invariant.

Because projection and prefix assembly run after cache retrieval, the cache is keyed only by `DocumentId`
— **not** by readable profile, caller claims, authorization context, `PathBase`, tenant, or route
qualifiers. Profile projection and routed-prefix assembly reshape cache output downstream and need no
cache-key participation. The `ResourceLinks:Enabled` flag is evaluated at plan compilation time (startup)
and therefore affects which JSON the reconstitution engine produces; when the flag changes, cached rows
that were materialized under the previous plan shape are stale.

`ResourceLinks:Enabled` changes the **served document shape** — reference objects gain or lose the `link`
subtree — without changing `dms.Document.ContentVersion` or `dms.Document.ContentLastModifiedAt`. This
creates a correctness gap for the cache freshness check, which compares
`ContentVersion`/`ContentLastModifiedAt` from `dms.Document` against the cached stamp. A flag flip
produces cached entries whose stored `DocumentJson` has the wrong link-presence shape, but whose stamps
still match — so without further action the serving path will return stale cached JSON.

**Normative rule (V1): startup plan-shape fingerprint auto-invalidation.** The V1 design invalidates
`dms.DocumentCache` automatically at serving-process startup whenever the compiled plan shape differs
from the shape under which the cache was previously materialized. Because `dms.DocumentCache` is an
optional projection (see [data-model.md](data-model.md) §`dms.DocumentCache`), this reconciliation
is conditional on provisioning: **when `dms.DocumentCache` is not provisioned for the instance, the
reconciliation step is a no-op** — the serving process skips the fingerprint read, skips acquiring
the advisory lock, and does not attempt to access `dms.DocumentCachePlanFingerprint`, whose DDL is
gated on the same cache-provisioning decision — see
[ddl-generation.md](ddl-generation.md) §"Link injection and DDL (V1 note)" for the provisioning
rule. No plan-shape correctness gap exists in that deployment: with no cache, there are no stale
cached rows to reconcile, and every response is materialized fresh from `dms.Document` plus the
compiled read plan.

When `dms.DocumentCache` **is** provisioned, a singleton metadata table
`dms.DocumentCachePlanFingerprint` (DDL in [data-model.md](data-model.md) §`dms.DocumentCachePlanFingerprint`)
stores the fingerprint of the plan shape that produced the current cache contents. On startup, each
serving process whose instance has the cache provisioned:

1. Computes its own plan-shape fingerprint from the startup-bound options. For V1 the only contributing
  input is `DataManagement:ResourceLinks:Enabled`. The fingerprint is the lowercase hex SHA-256 of the
  UTF-8 bytes of a canonical JSON object whose keys are the full configuration names in deterministic
  lexicographic order and whose values are normalized scalars. For V1 the canonical payload is
  `{"DataManagement:ResourceLinks:Enabled":true|false}`. The scheme is deliberately extensible so
  future plan-shape-affecting inputs can be appended to its canonical input set without schema change.
2. Acquires a transaction-scoped advisory lock for the reconciliation transaction
  (`pg_advisory_xact_lock` in PostgreSQL; `sp_getapplock` with `@LockMode = 'Exclusive'` and
  `@LockOwner = 'Transaction'` in SQL Server) so that concurrently starting processes serialize the
  fingerprint check. The exact key value is an implementation detail, but it MUST be a shared constant
  for this fingerprint-reconciliation path. The lock is held across the fingerprint read and any cache
  `TRUNCATE` / fingerprint-row upsert work, and releases automatically when the reconciliation
  transaction commits or rolls back.
3. Reads the stored fingerprint. If absent (first startup, or an upgrade from a schema version that
  did not yet store a fingerprint row), treats the absence as a mismatch and proceeds to step 4.
4. If the stored fingerprint is absent or differs from its own: `TRUNCATE dms.DocumentCache`, upsert the new
   fingerprint into `dms.DocumentCachePlanFingerprint` — the upsert MUST refresh `UpdatedAt` on both
   insert and update (e.g., `ON CONFLICT (Id) DO UPDATE SET Fingerprint = EXCLUDED.Fingerprint,
   UpdatedAt = now()` for Postgres) so the diagnostic timestamp reflects the most recent
  reconciliation rather than the row's original insertion — commit the reconciliation transaction
  (which releases the lock), and begin serving.
5. If the stored fingerprint matches: commit the reconciliation transaction (which releases the lock)
  and begin serving without touching the cache.

This reconciliation runs as part of backend mapping initialization before
an instance begins serving; it is a startup-time backend responsibility,
not a per-request cache-read check. See [new-startup-flow.md](new-startup-flow.md) for the generic
backend-mapping-initialization lifecycle hook in which this feature-local reconciliation runs. This
document defines the feature-local fingerprint inputs, reconciliation storage, and mismatch behavior.

The freshness check on cache reads remains:

```
cached ContentVersion == dms.Document.ContentVersion
AND cached LastModifiedAt == dms.Document.ContentLastModifiedAt
```

Correctness is delivered by the startup fingerprint check plus the two-input freshness check, **under
the required drained or blue-green restart path** (see "Mixed-plan overlap is not supported" below):
stale rows are removed before any new-plan process begins serving, and no old-plan process remains
that could repopulate the cache thereafter, so the reader never sees cache rows materialized under a
different plan. No `ResourceLinksFlag` column is added to `dms.DocumentCache` — the fingerprint
metadata row carries the single plan-shape input for the whole table rather than per-row.

Per the [cache layer contract above](#cache-and-etag-interaction), `dms.DocumentCache` stores the
pre-profile-projection document keyed only by `DocumentId`, and is caller-agnostic. The materialized
`link` shape depends only on source-document content and the compiled plan shape; it does **not** depend
on target-side authorization, readable profile, or caller claims. Two callers who can read the same
source document therefore share the same cached materialized JSON even when one caller would fail a
direct GET against the target resource or has a different readable profile. Those callers may still
observe different final served responses after readable-profile projection if one profile hides the
reference property on the source resource; the shared cache contract applies to the pre-projection
intermediate document only.

**Mixed-plan overlap is not supported.** Flipping `ResourceLinks:Enabled` as part of a rolling
deploy is not a supported operation. Cache reads validate freshness only against `ContentVersion`
and `ContentLastModifiedAt` — not plan shape — so an old-plan process that repopulates
`dms.DocumentCache` after a new-plan process has truncated it will produce rows whose stamps match
the source document but whose materialized shape is wrong. A new-plan reader accepts those rows
and returns stale-shape JSON; the fingerprint row itself follows last-writer-wins and cannot detect
the disagreement after the fact. Operators MUST drain old-plan processes fully (drained or
blue-green restart) before any new-plan process begins serving under the flipped flag. Flag flips
without rolling deploys (single-process restart or coordinated all-at-once restart) are fully
covered by the auto-invalidation with no operator intervention beyond the restart itself.

**Flag-flip operational guidance.** Because plan-shape invalidation is automatic at startup, a flag flip
requires only a process restart; the cache truncation happens as part of serving-process startup.
Recommended procedure:

- Flip the flag value in configuration.
- Restart the serving process(es). The cache is truncated automatically if the fingerprint changed; no
  manual `TRUNCATE` is required.
- Optionally pre-warm the cache via a targeted read pass across hot documents before caller traffic
  lands to avoid an initial cache-miss wave.

Flag flips are expected to be rare operational events; the one-time truncate cost is bounded and runs
inside the same restart window as plan recompilation.

**Etag consequence — canonical-form carve-out from update-tracking.md.** A `ResourceLinks:Enabled`
flip changes the caller-agnostic canonical form of every document carrying at least one fully-defined
reference: the `link` subtree is gained or lost. Under the normal source-content-driven rule in
[update-tracking.md](update-tracking.md) §Stamping rules "What counts as a canonical-form change?",
any canonical-form change bumps `ContentVersion` and `ContentLastModifiedAt` on the affected row.
**The flag flip is an explicit carve-out from that rule:** because the canonical-form change is
driven by a plan-shape input rather than by any persisted source-document content change, V1 does
not bump per-row representation stamps on flip. The startup plan-shape fingerprint auto-invalidation
above is the designated cache-invalidation mechanism for plan-shape-driven canonical-form changes;
the per-row stamping rule in update-tracking.md continues to govern all **source-content-driven**
canonical-form changes unchanged.

Etag values computed before a flag flip are therefore invalid against post-flip responses — the
canonical form, and hence the hash, has changed even though per-row stamps have not. Clients see an
etag mismatch on their next conditional read and re-fetch the full document. This is an acceptable
one-time cost of the flip; etag derivation is consistent again once the cache is re-warmed.

**Cross-references (maintained in their owning docs):**

- [update-tracking.md](update-tracking.md) §Serving API metadata — `_etag` derivation from the
  caller-agnostic cached document; routed-prefix assembly is applied after etag finalization and has
  no etag consequence.

---

## Authorization

**Source-resource authorization governs link emission.** If the caller's read succeeds against the
source resource, every reference on that source that (a) survives source-side readable-profile
projection and (b) is fully defined per [Link Shape](#link-shape) emits a link. Link injection never
consults target-resource authorization.

This rule produces a finite, deliberate set of caller-visible disclosures beyond what a
pre-link-injection GET response already exposes; the enumeration and acceptance rationale live in the
[Accepted Disclosure Envelope](#accepted-disclosure-envelope) subsection below. Subsequent
sections — including the [Profile-Hidden Target Resources](#profile-hidden-target-resources) stance
and the `[Decided]` readable-profile entry in
[Risks, Open Questions, and Decided Constraints](#risks-open-questions-and-decided-constraints) —
refer to that envelope by name rather than re-listing its contents.

Consequences:

- Link emission does **not** imply the caller can read the target resource — `href` is a URL the
  caller may or may not be able to dereference. This matches ODS.
- The rule is independent of the source-read strategy family (for example, namespace-based,
  ownership-based, relationship-based, custom-view, or `NoFurtherAuthorizationRequired`; see [auth.md](auth.md)). What varies by
  strategy is only **which callers succeed at the source read**; once that read succeeds, link
  emission follows the same rule.

Per-reference target-resource authorization at link-emission time is deliberately not performed: it
would multiply auth cost per response by reference sites × page size, undoing the single-pass
property, and target-URL visibility without target-read access is already accepted in ODS.

### Accepted Disclosure Envelope

Relative to a pre-link-injection GET response — which already exposes the natural-key identity fields
of every reference — link injection adds three disclosures. All three are deliberate and part of the
V1 document-reference link contract; none of them expose document content beyond what the reference's
identity fields already reveal.

1. **Concrete target type on abstract references.** For abstract references (e.g.,
   `educationOrganizationReference`), `rel` exposes the concrete subclass name (`"School"`,
   `"LocalEducationAgency"`, …). ODS emits the same disclosure; DMS accepts it for parity.
2. **Referenced `DocumentUuid`.** The `href` embeds the referenced document's stable `DocumentUuid`,
   a server-assigned identifier that was not previously visible through the source GET response.
3. **Link presence/absence as a lifecycle signal.** The null gate in [Link Shape](#link-shape)
   suppresses `link` when the referenced `DocumentUuid` is unresolvable. In a healthy system this
   state is unreachable through the supported DMS write path — composite FKs protect abstract
   references per [data-model.md](data-model.md), and deletes of still-referenced documents fail
   per [transactions-and-concurrency.md](transactions-and-concurrency.md) — so the gate fires only
   under anomalies such as partial DDL/backfill states, manual database modification, or
   corruption. A caller with repeated source-read access could, in principle, infer an anomaly from
   `link` disappearance. This observability is accepted within the envelope because the triggering
   state is not reachable through supported write paths.

Callers with no target-read access cannot dereference the `href`; the disclosures are limited to an
identifier, a concrete type, and a lifecycle signal.

### Profile-Hidden Target Resources

Source-resource-only means: if the source resource is readable under the caller's profile, any
reference surviving source-side projection emits `link` regardless of whether the **target** resource
type is readable under that same profile, and regardless of whether a direct target-side read would
succeed under the active authorization strategy family (for example, Namespace-based,
Ownership-based, Relationship-based, or Custom view-based authorization).

**Operator-visible consequence.** A profile designed to hide the *existence* of a target resource
type from a caller will still leak the concrete type (for abstract references) and the target
`DocumentUuid` whenever a readable source resource carries a fully-defined reference to that target.
Operators with a hard requirement to hide target-type existence MUST hide the **reference property
itself on the source resource** via readable-profile rules; target-type hiding alone is not sufficient.
No strategy-level tightening of link emission is offered in V1.

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
- Absolute-URL emission server-side.
- `Location`-header GUID format migration (D → N).
- Discovery-API `link` elements (e.g., on the API root document) — follows a different contract.

### Deferred Follow-On Work

Deferred follow-on work. This design intentionally does not invent Jira ids for work that has not yet been
split from DMS-622. Once this design is approved, each item below should be split into a dedicated follow-on
Jira and linked from this section.

| Deferred item | Why deferred in V1 | Tracking requirement |
|---------------|--------------------|----------------------|
| Descriptor-reference links | V1 stays scoped to document-reference hydration; descriptor references remain canonical URI strings in DMS. The ODS codebase shows both flat string descriptor members and a `DescriptorReference` helper with link generation, so this design deliberately defers any broader descriptor-link contract claim. | **Decision gate (open both before creating the Jira):** (1) Pin the intended ODS baseline with runtime fixture evidence for descriptor references. (2) Pick a DMS contract stance — (a) status quo: flat URI string only; (b) additive: emit `link` alongside URI; (c) replacement: replace URI with `link`; (d) conditional: opt-in `link` per request. Ticket seed covers the chosen path. |
| `Location`-header GUID alignment (`D` → `N`) | API-visible identifier-format change kept out of the initial rollout | After design approval, create and link a dedicated follow-up Jira from this section. Ticket seed: align frontend `Location` header generation with link-href GUID formatting and update contract coverage for POST/PUT response headers |
| OpenAPI / Discovery updates | Requires schema, documentation, and discovery-surface changes beyond runtime link emission | After design approval, create and link a dedicated follow-up Jira from this section. Ticket seed: document and advertise reference `link` behavior accurately across OpenAPI and Discovery, including the V1 document-versus-descriptor split or its eventual removal |
| Resource-scoped write-time `DocumentUuid` stamping optimization | V1 uses the per-page auxiliary `dms.Document` lookup and intentionally avoids new per-reference `..._DocumentUuid` columns, write-time stamping, and backfill work | After design approval, create and link a dedicated follow-up Jira from this section if profiling justifies it. Ticket seed: add optional `{ReferenceBaseName}_DocumentUuid` storage, extend write-time referential resolution to stamp `DocumentUuid`, and provide backfill/runbook guidance. **Opt-in MUST be resource-scoped — never global, never per-request** — expressed either as an `ApiSchema.json` field on the resource schema or as an operator configuration list keyed by `QualifiedResourceName`. |

---

## Risks, Open Questions, and Decided Constraints

No open questions are currently tracked for V1; remaining settled choices are recorded as decided
constraints rather than implied follow-on work.

### Risks

1. **[Risk] GUID format divergence.** Introducing `"N"` format on link hrefs while `Location` headers
  continue to use `"D"` creates an internal inconsistency: a client reading a POST `Location` header
  and later finding the same document as a reference in a GET response will see two different GUID
  spellings for the same document. Mitigation: align `Location` headers to `"N"` as a follow-up. Risk
  accepted for this story to limit blast radius.
2. **[Risk] Row-level discriminator anomaly handling.** Runtime discriminator failures are intentionally
  limited to the three row-level categories defined in
  [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator): `null`,
  `unparseable`, and `unmapped`. V1 suppresses only the affected `link` and emits a structured
  warn-level log entry with rate limiting, rather than failing the entire read response. Risk accepted
  because safe suppression is preferable to emitting malformed hrefs or breaking unrelated data.
3. **[Risk] Reverse-proxy path fidelity.** The served response now carries a DMS-routable href, so
  the serving boundary must assemble the routed prefix from the same deployment-visible path contract
  used to route the request. This is straightforward because `Location` header assembly already
  depends on the same inputs (`PathBase`, tenant / qualifiers, `/data`). The risk is therefore not
  conceptual mismatch but implementation drift between `Location` assembly and body-link assembly;
  tests MUST assert they stay aligned (see [Testing Strategy](#testing-strategy)).
4. **[Risk] Feature flag default.** Default-on matches ODS expectations but changes the response shape
  for any existing DMS client that parses relational-backend GET responses. Downstream clients must
  tolerate an additional `link` property on reference objects (additive, JSON-safe).
5. **[Risk] Flag-flip operational discipline.** The concrete failure mode is mixed-plan overlap:
  if an operator flips `ResourceLinks:Enabled` without fully draining old-plan processes before
  new-plan processes begin serving, an old-plan writer after the new-plan truncation can repopulate
  `dms.DocumentCache` with rows whose shape does not match the current plan, and a new-plan reader
  will serve them without re-validating plan shape. Mitigation: the startup plan-shape fingerprint
  auto-invalidation truncates cache on restart; the normative runbook requires drained or blue-green
  restarts; the two-input freshness check and one-time `_etag` invalidation on flip handle the
  happy path. See [Cache and Etag Interaction](#cache-and-etag-interaction) for the full protocol.
  Risk accepted with operator-discipline mitigation.

### Decided Constraints

1. **[Decided] Readable profile interaction (bidirectional).** Source-side readable projection MUST
  preserve `link` subtrees on reference objects regardless of profile member-selection rules, and
  target-side unreadability does not suppress link emission. See
  [Profile Compatibility](#profile-compatibility) and
  [Profile-Hidden Target Resources](#profile-hidden-target-resources).
2. **[Decided] Abstract resolution map regeneration.** The plan-compile-time map from `Discriminator`
  → concrete endpoint slugs is regenerated whenever a new concrete subclass appears in the schema.
  Under the current redesign there is no runtime schema refresh; the map
  is rebuilt at process startup when the compiled plan is rebuilt. Adding
  a subclass therefore
  requires a coordinated restart to pick up the new map entry.
3. **[Decided] Caller-agnostic cache keying and materialization.** The cached document remains
  caller-agnostic and is not keyed or materialized by tenant / qualifier routed prefix. Request-
  visible prefix assembly happens only at the serving boundary.
4. **[Decided] No local Discriminator propagation on the referencing row.** V1 keeps abstract
  resolution on the abstract-identity join path rather than propagating the target Discriminator into
  a local binding column. The propagation alternative remains rejected unless profiling justifies a
  new design pass.
5. **[Decided] No target-resource authorization at link-emission time.** Link emission remains
  source-resource-authorized only; per-reference target authorization is not performed during read
  assembly. See [Authorization](#authorization) and
  [Profile-Hidden Target Resources](#profile-hidden-target-resources).

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
    - `Discriminator` column is null (`null`) → `link` suppressed; identity fields still emitted.
    - `Discriminator` is non-null but does not match the native DMS `"ProjectName:ResourceName"`
      format (`unparseable`, including legacy ODS-style `edfi.School`, lowercase-project-name
      variants, and other non-native values — no normalization is attempted) → `link` suppressed; no
      exception propagates.
    - `Discriminator` parses cleanly but `(ProjectName, ResourceName)` is absent from the
      `EndpointTemplate` abstract-variant map (`unmapped`) → `link` suppressed; no exception propagates.
  - Feature flag off → no `link` emitted even for fully-defined references.
  - GUID formatting → `href` contains 32 hex chars with no hyphens.
  - Auxiliary result set with multiple referenced resources on the same page → each reference resolves
    to its own `DocumentUuid` correctly from the shared page-level map.
  - Hydration-command-builder boundary test → distinct FK values are deduplicated before auxiliary lookup;
    a SQL Server page that lands exactly on 2,100 parameters stays in one batch; a page above 2,100
    parameters partitions into sub-batches; and reconstitution union-merges the sub-batch maps back into
    one correct `DocumentId → DocumentUuid` view before link emission.
- **Fixture tests** covering at least:
  - Resource with a concrete reference (e.g., `AcademicWeek` referencing `School`).
  - Resource with an abstract reference (e.g., any `educationOrganizationReference` site).
  - Resource with a nested-collection reference (link appears inside collection elements).
- **Contract tests** comparing link shape and values against an ODS baseline fixture on the same
  semantic input, scoped to **document references only** (goal: byte-for-byte `link` parity where
  feasible for document references; descriptor-reference link parity is out of scope in V1).
- **Routed-prefix etag-stability contract test.** Fetch the same document through two different routed
  prefixes (different `PathBase` values, or the same `PathBase` with distinct tenant / qualifier
  prefixes) and assert **different `link.href` values** but an **identical `_etag`**. The fixture for
  this test MUST include at least one fully-defined reference so the `link.href` variation is observable
  and the `_etag` assertion is non-trivial.
- **GET-many** integration tests verifying link emission at page boundaries.
- **Routable href regression.** Emitted hrefs MUST follow the current request's actual DMS route shape.
  Test matrix:
  - deployment without `PathBase`, tenant, or qualifiers → `/data/{project}/{endpoint}/{uuid:N}`
  - multitenant / qualifier deployment → `/{tenant?}/{qualifiers...}/data/{project}/{endpoint}/{uuid:N}`
  - `PathBase` deployment → `{PathBase}/.../data/{project}/{endpoint}/{uuid:N}`
- **Feature-flag-off regression.** Ensure legacy clients continue to see link-free responses when
  operators opt out; confirm that compiled read plans omit the auxiliary lookup and LEFT JOINs when
  the flag is `false` at startup (plan-structure assertion, not runtime suppression).
- **Startup plan-shape fingerprint auto-invalidation.** Exercise each branch of the five-step
  startup protocol in [Cache and Etag Interaction](#cache-and-etag-interaction):
  - First startup (no fingerprint row present) → protocol treats the missing row as a mismatch,
    invalidates any preexisting `dms.DocumentCache` rows before serving, and inserts the current
    fingerprint. If the cache is already known empty, eliding the physical `TRUNCATE` is an
    acceptable implementation optimization.
  - Matching fingerprint → protocol releases the advisory lock without modifying
    `dms.DocumentCache` or `dms.DocumentCachePlanFingerprint`.
  - Mismatched fingerprint (simulates a flag flip) → protocol `TRUNCATE`s `dms.DocumentCache` and
    upserts the new fingerprint; the upsert refreshes `UpdatedAt` on both insert and update paths.
  - Concurrent startup contention → two processes racing the advisory-lock acquisition serialize
    deterministically; only one performs the truncate/upsert, the other observes the refreshed
    fingerprint and skips truncation.
  See story AC in [06-link-injection.md](../epics/08-relational-read-path/06-link-injection.md) for
  the task list this test pins.
- **Location-vs-body-link routed-prefix alignment.** Assert that the routed prefix assembled for
  `Location` header generation and for body-link `href` finalization consumes the identical
  request-scoped inputs (`PathBase`, tenant / qualifier segments, `/data`) and produces byte-equal
  prefixes for the same request. Validates [Risks](#risks-open-questions-and-decided-constraints)
  item 3.
- **Profile-scoped read preserves link.** A readable profile with `MemberSelection.IncludeOnly` that
  does **not** list `link` in its property set is applied to a GET request against a resource whose
  references emit `link` under the unrestricted read path. Assert that `link` is still present on
  every reference in the projected response — confirming the readable projector treats `link` as a
  server-generated field rather than a suppressible member. See
  [Profile Compatibility](#profile-compatibility) for the normative contract this test validates.
- **Source-readable / target-denied authorization scenario.** A caller can read the source resource but
  would fail a direct GET against the target resource under the active authorization strategy; fully-defined
  references still emit `rel` and `href`.
- **Caller-agnostic cache across authorization contexts.** Two callers who can both read the same source
  document — one whose active strategy also authorizes the target, one whose does not — receive the
  **same cached intermediate JSON** (identical `link.rel` and `link.href` suffix) from
  `dms.DocumentCache`. The cache is keyed only by `DocumentId` and is not re-materialized per caller or
  per strategy; this test pins that the authorization asymmetry does not create cache divergence. Also
  asserts that the pre-prefix-assembly `_etag` is identical across the two callers, confirming the
  caller-agnostic hash contract at [Cache and Etag Interaction](#cache-and-etag-interaction).
- **Discriminator-failure logging hygiene.** For each of the three unresolvable-discriminator sub-cases
  (null, unparseable, unmapped), assert that the structured log entry:
  - emits at `Warn` level once per occurrence up to the per-process per-category rate cap of 10
    entries per fixed UTC-minute window, then suppresses further per-occurrence entries and emits
    exactly one summary entry when that window closes with the suppressed count for that category; the
    summary entry does **not** consume the next window's per-category budget;
  - carries the failure-mode category (`null` / `unparseable` / `unmapped`), the source resource's
    `QualifiedResourceName`, the reference JsonPath at which the failure occurred, and the source
    row's `DocumentId`;
  - routes `QualifiedResourceName` and reference JsonPath through
    [`LoggingSanitizer.SanitizeForLogging(...)`](../../../../src/dms/core/EdFi.DataManagementService.Core/Utilities/LoggingSanitizer.cs)
    before emission, preserving only letters, digits, spaces, and safe punctuation (`_-.:/`);
  - **does not** include the raw `Discriminator` column value, the target document's identity-field
    values, any content-JSON fragment from the source resource, or the caller's authorization
    principal.
  See [Failure Modes: Unresolvable Discriminator](#failure-modes-unresolvable-discriminator) for the
  normative logging rules this test pins.
- **Startup validation failure coverage.** For each abstract reference site reachable from the compiled
  mapping set, simulate a missing prerequisite and verify startup-time failure for that instance:
  `{schema}.{AbstractResource}Identity` table missing, `Discriminator` column missing or nullable, and
  discriminator-maintenance trigger missing for a participating concrete subclass. These tests pin that
  deployment drift fails fast during backend mapping initialization rather than degrading to runtime link
  suppression.
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
  reuses the existing `..._DocumentId` FK. A single new singleton metadata table
  `dms.DocumentCachePlanFingerprint` is introduced (DDL in
  [data-model.md](data-model.md) §`dms.DocumentCachePlanFingerprint`) to drive the startup plan-shape
  auto-invalidation described in [Cache and Etag Interaction](#cache-and-etag-interaction), and
  `dms.DocumentCache` itself gains a cached `ContentVersion` column to support the two-input
  freshness check. This feature therefore depends on the provisioning contract including both that
  singleton table and the `dms.DocumentCache.ContentVersion` column even though the rest of V1 link
  injection adds no per-reference storage columns.
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

Overall sizing (V1): **medium** — a single story in the read-path epic, but one that touches eight
distinct implementation surfaces:

1. Plan compiler — new `DocumentReferenceBinding` fields and `EndpointTemplate` precomputation.
2. Hydration SQL builder — abstract-identity LEFT JOIN, auxiliary `dms.Document` result set with
   empty-set skip and large-set partitioning.
3. Auxiliary reader — collect page FKs, issue the auxiliary query, build the page-level
   `DocumentId → DocumentUuid` map.
4. Reconstitution writer — consume the map and the discriminator projection, then emit `{rel, href}`
   via the null gate.
5. Readable profile projector (`ReadableProfileProjector.cs`) — treat `link` as server-generated on
   nested reference objects.
6. Backend startup path — per-instance abstract-reference prerequisite validation plus startup
  plan-shape reconciliation, including the advisory-lock / fingerprint flow backed by the new
  `dms.DocumentCachePlanFingerprint` singleton table.
7. Frontend serving boundary — finalize cached suffix hrefs into request-routable body links using the
  same request-visible prefix inputs as `Location` header assembly.
8. Feature-flag plumbing — `DataManagement:ResourceLinks:Enabled` wired into the plan compiler as a
  startup-scoped deployment setting.

Plus document-reference parity contract tests that assert link-shape and presence-gate equivalence
against the legacy ODS response bodies for representative concrete and abstract reference sites
(descriptor-reference parity is deferred; see [Non-Goals](#non-goals)).

Story-task-to-surface map: task 1 → surface 1; tasks 2-3 → surfaces 2-3; task 4 → surface 4; task 5
→ surfaces 1 and 8; task 6 → surface 6; task 7 → surface 7; task 8 → surface 5 plus the
authorization/profile contract assertions above; task 9 → surfaces 6 and 8; task 10 → the unit /
fixture / integration / contract coverage in this section.

Each surface is small-to-medium individually; the integration cost across the eight surfaces — and
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
- [data-model.md](data-model.md) — abstract identity tables, `Discriminator` column, and
  `dms.DocumentCachePlanFingerprint` DDL.
- [flattening-reconstitution.md](flattening-reconstitution.md) — reference reconstitution engine and
  `DocumentReferenceBinding`.
- [compiled-mapping-set.md](compiled-mapping-set.md) — auxiliary-result-set hydration pattern
  (descriptor URI projection) this design extends for `DocumentUuid` lookup.
- [ddl-generation.md](ddl-generation.md) — abstract identity table and `Discriminator` trigger
  generation that link injection depends on.
- [update-tracking.md](update-tracking.md) — `_etag` derivation and canonical-form stamping rule;
  the flag-flip etag carve-out above is scoped relative to it.
- [auth.md](auth.md) — authorization strategy families.
- [profiles.md](profiles.md) — readable profile projection (link preservation requirement).
