# Link Injection Design

## Status

Draft.

This document describes **link injection** for reference properties in DMS GET responses against the relational
backend. The feature emits a `{ rel, href }` object on every fully-defined reference in a response body,
matching the Ed-Fi ODS contract (see [ODS Parity Reference](#ods-parity-reference)) without per-resource code
generation.

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
    - [Tenant and Route Qualifiers](#tenant-and-route-qualifiers)
    - [Schema-Driven Metadata](#schema-driven-metadata)
    - [Abstract Reference Resolution](#abstract-reference-resolution)
    - [Referenced DocumentUuid Availability](#referenced-documentuuid-availability)
    - [Compiled Read-Plan Extensions](#compiled-read-plan-extensions)
    - [Integration Point: JSON Reconstitution](#integration-point-json-reconstitution)
  - [Feature Flag](#feature-flag)
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

1. **ODS behavioral parity.** Emit `{ rel, href }` on every fully-defined reference property in GET responses
   with shape and values equivalent to the Ed-Fi ODS resource-link contract.
2. **Schema-driven.** Derive reference locations, target resource types, and abstract/concrete relationships
   from `ApiSchema.json` and the compiled read plan — no per-resource hand-coded link logic.
3. **Single-pass reads.** Link emission uses only data already hydrated by the relational read pipeline;
   no additional DB round-trips per read request.
4. **Abstract reference resolution.** Abstract references (e.g., `educationOrganizationReference`) emit `rel`
   and `href` for the concrete subclass (e.g., `School`), matching ODS behavior.
5. **GET-many symmetric.** Paged collection responses emit links identically to single-item GET responses.
6. **Feature-gated.** Controlled by a single configuration switch, default-on, equivalent to the ODS
   `ApiFeature.ResourceLinks` flag.

### Non-Goals

- Document-store backend support — relational backend only.
- OpenAPI / Discovery API updates (covered elsewhere).
- Emission of absolute URLs; hrefs are relative, matching ODS and today's DMS `Location` header behavior.
- Changes to the `Location` header GUID format (tracked separately; see
  [Risks and Open Questions](#risks-and-open-questions)).
- Propagation of discriminator or identity metadata beyond what is already defined in
  [data-model.md](data-model.md).

---

## Problem Statement

Consumers of the Ed-Fi DMS relational-backend GET responses today receive reference identity fields
(natural keys) but no navigable pointer to the referenced resource. This diverges from ODS, where every
reference carries a `link: { rel, href }` object, and forces consumers to reconstruct endpoint paths
client-side. For abstract references the problem is worse: a client cannot determine the concrete subclass
of an `educationOrganizationReference` without querying every possible concrete endpoint.

This design adds ODS-equivalent link objects to reference properties in relational-backend GET responses,
fully derivable from existing `ApiSchema.json` metadata and the compiled read plan, and emitted during the
reconstitution pass with no additional round-trips.

---

## ODS Parity Reference

The ODS behavior this design mirrors:

- **Link shape**: `{ "rel": "...", "href": "..." }` — exactly two `DataMember` fields, no `title`/`type`/
  `hreflang`. Defined in `~/Projects/EdFi/Ed-Fi-ODS/Application/EdFi.Ods.Api/Models/Link.cs`.
- **Href template**: relative path `/{schemaUriSegment}/{pluralCamelEndpointName}/{ResourceId:n}` —
  e.g., `"/ed-fi/schools/550e8400e29b41d4a716446655440000"`. Sourced from the generated `CreateLink()`
  methods, e.g., `Resources.generated.cs` around `SchoolReference` at approximately lines 213226–213271.
  GUID format is `"n"` — 32 hex characters, no hyphens.
- **Rel**: the concrete target resource name (e.g., `"School"`, `"LocalEducationAgency"`). For abstract
  references, the ODS parses the discriminator (format `"ProjectName.ResourceName"` in-memory, or
  `"ProjectName:ResourceName"` when read from the ODS database view) and assigns the concrete name.
- **Presence gate**: a link is emitted only when the reference is "fully defined" (all identity components
  present). Partially populated references do not emit `link`.
- **Feature flag**: `ApiFeature.ResourceLinks`
  (`~/Projects/EdFi/Ed-Fi-ODS/Application/EdFi.Ods.Common/Constants/ApiFeature.cs`, approximately line 26);
  when disabled, the `Link` property returns `null` and is omitted from the serialized output.
- **Implementation style**: per-reference-type `CreateLink()` methods generated into the resource-model
  partials. Not middleware, not serialization filters.

The DMS relational implementation replicates every observable aspect of this contract while realizing it
against a schema-driven, compiled-plan architecture instead of code generation.

---

## Design

### Link Shape

Identical to ODS:

```json
"schoolReference": {
  "schoolId": 255901,
  "link": {
    "rel": "School",
    "href": "/ed-fi/schools/550e8400e29b41d4a716446655440000"
  }
}
```

- Two properties: `rel` (string) and `href` (string).
- No additional fields.
- Emitted **only when the reference is fully defined** — every identity field of the reference is present.
  If any identity field of the reference is missing or null, the `link` property is omitted (matches ODS).
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

The href is a **relative path** of the form:

```
/{projectEndpointName}/{endpointName}/{documentUuid:N}
```

- `projectEndpointName`: the kebab-cased project segment (`ed-fi`, `tpdm`, `sample`, etc.), looked up from
  the target resource's `projectSchema.projectEndpointName`.
- `endpointName`: the camel-cased plural endpoint slug for the target resource
  (`schools`, `academicWeeks`, `localEducationAgencies`, etc.), looked up from the target resource's
  `resourceSchema.endpointName`.
- `documentUuid`: the `DocumentUuid` of the **referenced** document (see
  [Referenced DocumentUuid Availability](#referenced-documentuuid-availability)), formatted without hyphens
  (see [GUID Format](#guid-format)).

For abstract references, the `projectEndpointName` and `endpointName` are those of the **concrete** subclass
identified by the discriminator, not of the abstract type. There is no `/ed-fi/educationOrganizations/{id}`
endpoint in DMS; the href always points at a concrete endpoint.

The relative href matches today's `Location`-header assembly performed by
`PathComponents.ToResourcePath()` (`src/dms/core/EdFi.DataManagementService.Core/Model/PathComponents.cs`,
approximately lines 35–38). The frontend layer
(`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`, approximately
lines 180–209) remains responsible for converting relative paths to absolute URLs via
`HttpRequest.RootUrl()`; link hrefs are not materialized into absolute URLs server-side.

### GUID Format

All link `href` GUIDs are formatted with the `"N"` specifier — 32 lowercase hex characters, no hyphens —
matching ODS exactly.

This is a deliberate divergence from DMS's current `Location` header, which relies on the default
`Guid.ToString()` ("D" format, with hyphens). Aligning `Location` headers to `"N"` is noted as a
follow-up in [Risks and Open Questions](#risks-and-open-questions); it is **out of scope** for this story
to avoid coupling an API-visible identifier-format change to the link-injection rollout.

### Tenant and Route Qualifiers

Link hrefs **do not include** tenant or route-qualifier path segments (e.g., the leading `/{tenant}/` or
`/{schoolYear}/` fragments extracted by the frontend during request routing). This matches the current
`Location`-header convention: tenant/qualifier handling is the responsibility of the frontend/proxy layer,
not the handler layer, and the core response body emits tenant-agnostic paths.

A reverse-proxy or API gateway that injects a tenant prefix into inbound URLs is expected to perform the
equivalent inverse rewrite on response bodies if tenant-absolute URLs are required. This design does not
introduce server-side tenant-aware rewriting.

### Schema-Driven Metadata

All link-generation inputs come from `ApiSchema.json` via existing typed accessors — no new schema fields
are required:

- **Reference property locations**: `DocumentPath.IsReference` / `ProjectName` / `ResourceName` /
  `ReferenceJsonPathsElements` on `ResourceSchema.DocumentPaths`
  (`src/dms/core/EdFi.DataManagementService.Core/ApiSchema/DocumentPath.cs`,
  `ResourceSchema.cs`). These already identify every reference property in a resource and its target
  `QualifiedResourceName` plus identity JsonPaths.
- **Abstract/concrete relationships**: existing `superclassResourceName`, `superclassProjectName`, and
  `abstractResources` schema fields. A reverse map (abstract → set of concrete subclasses) is computed
  once at schema-load time and cached alongside the rest of the compiled model.
- **Endpoint slugs**: existing `projectSchema.projectEndpointName` and `resourceSchema.endpointName` (per
  resource) supply the kebab-cased project segment and camel-cased endpoint name respectively. These
  already drive Location-header assembly today.

### Abstract Reference Resolution

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

### Referenced DocumentUuid Availability

The `href` requires the referenced document's `DocumentUuid`. This is a distinct requirement from the
reference *identity* columns already addressed by story
[02-reference-identity-projection.md](../epics/08-relational-read-path/02-reference-identity-projection.md),
with different write-time mechanics: that story's natural-key identity bindings must **track source-side
changes** via propagation (PostgreSQL `ON UPDATE CASCADE`, SQL Server
`DbTriggerKind.IdentityPropagationFallback`), whereas `DocumentUuid` is **immutable for the lifetime of
the referenced document** (see [overview.md](overview.md) and [data-model.md](data-model.md)). The two
columns live side-by-side in the binding set but are maintained by different mechanisms.

**Recommended: stamp `DocumentUuid` into a local binding column once at write time.**

Extend the existing bulk reference resolution step in
[flattening-reconstitution.md](flattening-reconstitution.md) §5.2 — which already resolves each
`ReferentialId → DocumentId` via `dms.ReferentialIdentity` and populates the
`ResolvedReferenceSet.DocumentIdByReferentialId` map defined in §7.6 — to also return the referenced
`DocumentUuid` from the same lookup. At row write time,
persist that value into a `{ReferenceBaseName}_DocumentUuid` column on the referring row alongside
the existing `..._DocumentId` FK and natural-key binding columns. Reads project it directly into the
plan; reconstitution writes it into the `href` without any join.

Because `DocumentUuid` never changes after insert, this is a **one-time stamp at write**, not ongoing
propagation:

- No FK cascade is defined on `DocumentUuid`; the composite FK stays on `(DocumentId, <identity
  fields…>)` as today.
- No `DbTriggerKind.IdentityPropagationFallback` trigger fires for this column; the identity-propagation
  machinery described in [data-model.md](data-model.md) is not extended to cover UUIDs.
- If a referenced document's identity later changes and propagation updates the natural-key bindings on
  the referring row, the `{ReferenceBaseName}_DocumentUuid` value is unaffected and remains correct by
  construction (stable identifier).

Rationale:

- Write mechanics are simpler than identity propagation — one extra column in the result of the bulk
  referential-id resolution, set once at insert, never rewritten.
- Read mechanics match the other per-reference binding columns — single-pass reads are preserved, no
  join against `dms.Document`.
- `Guid` storage width is modest; cost is one 16-byte column per reference per row.
- Correctness is decoupled from the identity-cascade surface area: there is nothing to re-sync when a
  target's natural key changes.

DDL generation emits the column alongside the other per-reference binding columns; this extends the
propagated-binding-column emission surface in [ddl-generation.md](ddl-generation.md) to recognize a
`DocumentUuid` binding in addition to the identity bindings it already emits. Rows that exist prior to
the migration will have `NULL` in this column until backfilled; see
[Risks and Open Questions](#risks-and-open-questions) for the handling expectation.

**Alternative: page-batched join to `dms.Document`.** Collect all `..._DocumentId` values across the
page, issue one `SELECT DocumentId, DocumentUuid FROM dms.Document WHERE DocumentId IN (...)` as an
additional result set in the hydration multi-result, and zip by `DocumentId` during reconstitution.
This fits the existing multi-result hydration pattern
([00-hydrate-multiresult.md](../epics/08-relational-read-path/00-hydrate-multiresult.md)) and avoids
the additional stored column, at the cost of one extra result set per read and a reconstitution-time
zip step. Strictly simpler to roll out for pre-existing data (no backfill needed), and viable as a
fallback if the storage-width cost of stamping is judged unacceptable for a particular resource.

**Rejected: per-reference join in the primary hydration SQL.** Worst shape — multiplies hydration cost
by the number of distinct references and scales poorly.

### Compiled Read-Plan Extensions

The compiled `DocumentReferenceBinding`
([flattening-reconstitution.md](flattening-reconstitution.md) §7.3) gains three additions, all
populated at plan compile time:

- `IsAbstractTarget: bool` — true when `TargetResource` refers to an abstract resource.
- `DiscriminatorBinding: ColumnBinding?` — for abstract targets only, points at the hydration-projected
  `Discriminator` column (from the left-joined `{AbstractResource}Identity` row).
- `DocumentUuidBinding: ColumnBinding` — points at the `{ReferenceBaseName}_DocumentUuid` binding
  column stamped at write time (or, under the fallback strategy, at the `dms.Document.DocumentUuid`
  projected from the auxiliary result set).
- `LinkEndpointTemplate: LinkEndpointTemplate` — precomputed:
  - For concrete targets: a fixed `(projectEndpointName, endpointName)` pair.
  - For abstract targets: a **map** from the `(ProjectName, ResourceName)` pair decoded from
    `Discriminator` to the concrete target's `(projectEndpointName, endpointName)`. The map is bounded
    by the number of concrete subclasses of the abstract resource and is frozen at plan compile time.

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
      targets.
   b. Read `documentUuid` from `DocumentUuidBinding`.
   c. Format the `href` string as `/{projectEndpointName}/{endpointName}/{documentUuid:N}`.
   d. Write a `"link": { "rel": ..., "href": ... }` object property immediately after the last identity
      field of the reference.
3. Otherwise, write no `link` property (matches ODS behavior for partially-populated references and for
   feature-flag-off state).

This keeps all reference-handling logic co-located, preserves deterministic output order, and reuses the
existing streaming writer — no intermediate `JsonNode` materialization.

---

## Feature Flag

A single configuration key controls link emission:

- **Key**: `DataManagement:ResourceLinks:Enabled`
- **Default**: `true` — matching the user-facing expectation set by ODS's feature being enabled by default
  in standard deployments.
- **Behavior when `false`**: no `link` property is emitted on any reference. No other response shape
  changes. Plan compilation is unaffected; the flag is consulted inside the reconstitution engine's
  reference-writing step.

No per-resource, per-request, or per-reference override is provided. Clients that want minimal responses
disable the flag at the deployment level.

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

### Accepted Disclosure Envelope

Relative to a pre-link-injection GET response (which already exposes the natural-key identity fields of
each reference), link injection expands the observable surface in two ways:

1. **Concrete target type on abstract references.** For abstract references (e.g.,
   `educationOrganizationReference`), the `rel` value exposes the concrete subclass name
   (`"School"`, `"LocalEducationAgency"`, …), which the identity values alone do not reveal. ODS emits
   the same disclosure; DMS accepts it for parity.
2. **Referenced `DocumentUuid`.** The `href` embeds the referenced document's stable `DocumentUuid`,
   a server-assigned identifier that was not previously visible through the source resource's GET
   response.

Both disclosures are deliberate and part of the ODS-parity contract. They do **not** expose document
content beyond what the caller already sees in the reference's identity fields; they expose an
identifier and a concrete type. Callers with no target-read access cannot dereference the `href`.

### Profile-Hidden Target Resources

A caller's readable profile may hide the target resource type entirely (today's DMS returns `405` for
direct GETs against a profile-unreadable resource). Link injection does **not** suppress `rel` or `href`
in this case: if the source resource is readable under the caller's profile, any fully-defined reference
on it emits a link even when the target resource type is unreadable under that profile.

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

See [auth.md](auth.md) for the overall authorization model and its performance envelope, and
[profiles.md](profiles.md) for the readable-profile projector that governs source-side field filtering.

---

## Collection Responses

GET-many behavior is identical to GET-by-id behavior on a per-item basis. Because reconstitution is
page-batched ([04-query-execution.md](../epics/08-relational-read-path/04-query-execution.md)), link
emission adds no asymptotic overhead to collection reads — it is bounded by the number of reference
sites per item times the page size, and all inputs (binding columns, discriminators, document UUIDs) are
already hydrated as part of the page. There is no N+1 risk.

---

## Out of Scope

- Document-store backend link injection (relational backend only).
- OpenAPI specification updates and Discovery API responses — tracked in the documentation-and-discovery
  work stream.
- Absolute-URL emission server-side.
- `Location`-header GUID format migration (D → N). Tracked as a follow-up; see
  [Risks and Open Questions](#risks-and-open-questions).
- Tenant-aware or qualifier-aware href rewriting.
- Propagation of reference-target Discriminator as a local binding column on the referencing row (the
  rejected abstract-resolution alternative).
- Target-resource authorization at link-emission time.
- Discovery-API `link` elements (e.g., on the API root document) — follows a different contract.

---

## Risks and Open Questions

1. **GUID format divergence.** Introducing `"N"` format on link hrefs while `Location` headers continue
   to use `"D"` creates an internal inconsistency: a client reading a POST `Location` header and later
   finding the same document as a reference in a GET response will see two different GUID spellings for
   the same document. Mitigation: align `Location` headers to `"N"` as a follow-up. Risk accepted for
   this story to limit blast radius.
2. **Stamped `DocumentUuid` column storage and backfill cost.** One additional 16-byte column per
   reference per row. For reference-dense resources (e.g., `StudentSectionAssociation`-style join
   resources) the aggregate cost is non-trivial. Acceptable in V1 given the single-pass read benefit;
   revisit if profiling surfaces it as a bottleneck. Rows created before the column is introduced will
   hold `NULL`; a one-time backfill (scan the root table and set each column from
   `dms.Document.DocumentUuid` via the corresponding `..._DocumentId`) is required before the feature
   flag is enabled against pre-existing data. The fallback page-batched-join strategy avoids this
   backfill entirely and is the better option where the backfill window is unacceptable.
3. **Readable profile interaction (bidirectional).** The Core-owned readable profile projector (see
   [profiles.md](profiles.md)) runs after full reconstitution and filters the document tree. Two
   directions must be verified:
   - **Source side — projector preserves links.** Ensure the projector preserves `link` subtrees on
     reference objects that are themselves in the readable view; otherwise link emission silently
     disappears under profile-scoped reads.
   - **Target side — hidden-target disclosure.** Link emission does not consult the target resource's
     readability under the caller's profile. This is deliberate; see
     [Profile-Hidden Target Resources](#profile-hidden-target-resources) for the accepted disclosure
     envelope and operator guidance when a profile must hide target-type existence from a caller.
4. **Reverse-proxy tenant rewriting.** Deployments that front DMS with a tenant-prefixing proxy must
   rewrite link hrefs on the response if they want tenant-absolute URLs. The design leaves this to the
   proxy layer; document the expectation clearly in operator guidance.
5. **Feature flag default.** Default-on matches ODS expectations but changes the response shape for any
   existing DMS client that parses relational-backend GET responses. Ensure downstream clients tolerate
   an additional `link` property on reference objects (additive, JSON-safe).
6. **Abstract resolution map drift.** The plan-compile-time map from `Discriminator` → concrete
   endpoint slugs must be regenerated whenever a new concrete subclass is added. Normal schema-refresh
   flows already handle this; call it out explicitly in the compiled-plan invalidation rules.

---

## Testing Strategy

- **Unit tests** (reconstitution engine):
  - Concrete reference with a fully-defined identity → emits correct `rel` and `href`.
  - Concrete reference with a partial/missing identity → emits no `link`.
  - Abstract reference → resolves `Discriminator` to the correct concrete `rel` and endpoint slugs.
  - Feature flag off → no `link` emitted even for fully-defined references.
  - GUID formatting → `href` contains 32 hex chars with no hyphens.
- **Fixture tests** covering at least:
  - Resource with a concrete reference (e.g., `AcademicWeek` referencing `School`).
  - Resource with an abstract reference (e.g., any `educationOrganizationReference` site).
  - Resource with a nested-collection reference (link appears inside collection elements).
- **Contract tests** comparing link shape and values against an ODS baseline fixture on the same
  semantic input (goal: byte-for-byte `link` parity where feasible).
- **GET-many** integration tests verifying link emission at page boundaries.
- **Feature-flag-off regression** ensuring legacy clients continue to see link-free responses when
  operators opt out.

---

## Level of Effort

Qualitative; refined during the story tasks.

- Compiled-plan extension (new `DocumentReferenceBinding` fields, endpoint-template precomputation):
  small.
- Hydration SQL change (left-join `{AbstractResource}Identity`; emit and stamp `..._DocumentUuid`
  binding column): small-to-medium, dominated by DDL-emission and write-path stamping changes for the
  new UUID binding column (extending the bulk `ReferentialId → DocumentId` lookup to also return
  `DocumentUuid`).
- Reconstitution-engine reference-writing extension: small, additive.
- Feature-flag plumbing: trivial.
- Tests (unit + fixture + contract + integration): medium — the bulk of the effort, especially ODS
  parity contract tests.

Overall sizing: a single story in the read-path epic.

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
