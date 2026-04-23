# Authorization and Delete Semantics

## Objective

Ensure Change Queries preserve the correct authorization behavior for each surface:

- live changed-resource queries continue to use the current live-read authorization behavior
- `/deletes` and `/keyChanges` target the documented tracked-change authorization contract, preserving ODS-style delete-aware relationship visibility while applying the accepted DMS-specific ownership exception
- tracked-change artifacts preserve the redesign authorization inputs needed for ownership-based and DocumentId-based authorization
- authorization-maintenance updates do not create false change records

This document therefore distinguishes live current-state authorization from tracked-change authorization. They are related, but they are not identical read paths.

## Current Live Authorization Baseline

In the current DMS storage and runtime model, collection GET authorization is enforced primarily from data already stored on `dms.Document`, with support from education-organization hierarchy lookup tables.

Primary live-row authorization inputs:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`
- `CreatedByOwnershipTokenId` when redesign-aligned ownership authorization is enabled on the live row

Supporting lookup table:

- `dms.EducationOrganizationHierarchyTermsLookup`

This means live changed-resource queries can preserve live-read parity by reusing the same authorization filters that current collection GET queries already apply to `dms.Document`.

## Authorization Companion Table Inventory

The current authorization model also depends on companion tables and triggers that derive or support the live authorization projection.

| Table | Role in the current storage/runtime model | Change Queries impact |
| --- | --- | --- |
| `dms.EducationOrganizationHierarchy` | Stores education-organization parent-child relationships. | Reused both as live authorization support and as a capture-time resolver source for `educationOrganizationDocumentIds`; existing cleanup remains part of delete execution after tracked-change capture is complete. |
| `dms.EducationOrganizationHierarchyTermsLookup` | Stores hierarchy expansion values used in EdOrg filters. | Reused by live and tracked-change authorization logic. |
| `dms.StudentSchoolAssociationAuthorization` | Derives student authorization values from student-school relationships. | Continues feeding live-row projection data, but tracked changes cannot rely on the row still existing later. |
| `dms.StudentSecurableDocument` | Maps student identifiers to securable documents. | Capture-time resolver source for `studentDocumentIds`, but not a safe tracked-change read-time source after delete. |
| `dms.StudentContactRelation` | Bridges student-contact relationships for contact authorization. | Live-only helper; tracked changes need preserved basis data when rows are gone. |
| `dms.ContactStudentSchoolAuthorization` | Derives contact authorization values. | Continues feeding live-row projection data. |
| `dms.ContactSecurableDocument` | Maps contact identifiers to securable documents. | Capture-time resolver source for `contactDocumentIds`, but not a safe tracked-change read-time source after delete. |
| `dms.StaffEducationOrganizationAuthorization` | Derives staff authorization values. | Continues feeding live-row projection data. |
| `dms.StaffSecurableDocument` | Maps staff identifiers to securable documents. | Capture-time resolver source for `staffDocumentIds`, but not a safe tracked-change read-time source after delete. |
| `dms.StudentEducationOrganizationResponsibilityAuthorization` | Derives student responsibility authorization values. | Continues feeding live-row projection data, but tracked changes need preserved basis data after delete timing. |

The Change Queries feature does not require these companion tables to become the tracked-change source of truth. The tracked-change source of truth is the authorization data preserved on the tombstone or key-change row.

## Live Changed-Resource Authorization

Implementation rule:

- apply the existing live collection authorization filters after `dms.DocumentChangeEvent` candidate selection and verification against `dms.Document.ChangeVersion`
- continue to use the current live-row authorization model for namespace, education-organization, relationship, custom-view, and ownership checks

Expected effect:

- a caller who can read a live resource in the current collection GET path can also read the same resource in changed-resource mode if it falls within the requested window
- a caller who cannot read the live resource remains blocked in changed-resource mode

## `ReadChanges` Claim-Set Action Gate

In ODS, `/deletes`, `/keyChanges`, and changed-resource queries with change-version parameters require the `ReadChanges` action from the active claim set, evaluated separately from `Read`. DMS already defines `ReadChanges` as action ID 5 with URI `uri://ed-fi.org/api/actions/readChanges` in the CMS `ClaimSetRepository` and exposes it in the `token_info` response. DMS-843 must enforce this gate for all three Change Query data surfaces.

The existing `ResourceActionAuthorizationMiddleware` maps HTTP methods to action names using `_methodToActionNameMapping` (`GET → "Read"`, etc.). For Change Query request contexts, the resolved action name must be `"ReadChanges"` instead of `"Read"`.

Required rule:

- a collection GET that carries `minChangeVersion`, `maxChangeVersion`, or both resolves to the `"ReadChanges"` action for claim-set authorization
- a request to `/{resource}/deletes` resolves to the `"ReadChanges"` action for claim-set authorization
- a request to `/{resource}/keyChanges` resolves to the `"ReadChanges"` action for claim-set authorization
- `availableChangeVersions` requires authentication but is not filtered by per-resource claim-set authorization; the `ReadChanges` gate does not apply to it
- a caller authorized for `Read` on a resource but not `ReadChanges` must receive `403 Forbidden` on those three Change Query surfaces for that resource
- `ResourceActionAuthorizationMiddleware` must detect the Change Query request context and resolve `"ReadChanges"` as the action name; one conforming approach is to pass the resolved action name through `RequestInfo` (set by middleware that detects change-query routes and parameters earlier in the pipeline) so `ResourceActionAuthorizationMiddleware` uses it in place of the HTTP-method-derived name

## Why Tracked Changes Need More Than the Current Live Projection

ODS tracked changes do not simply reuse the current live-read query shape.

Relevant ODS behavior:

- tracked changes use `ReadChanges` authorization criteria
- delete visibility can remain valid even when the authorizing relationship row has already been deleted
- relationship authorization uses delete-aware semantics
- this package additionally applies redesign-aligned ownership filtering and eligible custom-view authorization to tracked changes using captured tracked-change state

The redesign authorization direction has the same implication in DMS:

- ownership-based authorization depends on `CreatedByOwnershipTokenId`
- relationship and custom-view authorization depend on basis-resource `DocumentId` values rather than natural keys

Therefore, copied live-row projection columns alone are not enough for tracked changes. A tombstone or key-change row must preserve the tracked-change authorization state needed to reproduce the correct outcome after the live row, companion rows, or related relationship rows are gone.

## Accepted DMS Authorization Exception

The tracked-change ownership rule in DMS-843 is an accepted DMS-specific authorization exception, not an unresolved review comment.

Architectural basis:

- backend-redesign [`auth.md`](../design/backend-redesign/design-docs/auth.md) says DMS follows the ODS authorization design unless specified otherwise and then specifies that `CreatedByOwnershipTokenId` is stored on shared `dms.Document` and always populated in DMS
- backend-redesign [`data-model.md`](../design/backend-redesign/design-docs/data-model.md) treats `CreatedByOwnershipTokenId` as part of the canonical DMS row model and as an ownership-based authorization input
- backend-redesign [`transactions-and-concurrency.md`](../design/backend-redesign/design-docs/transactions-and-concurrency.md) includes ownership-based authorization in the normal DMS write pipeline
- redesign [`auth-redesign-subject-edorg-model.md`](../design/auth/auth-redesign-subject-edorg-model.md) preserves ownership-style constraints in the target authorization direction

Required interpretation for DMS-843:

- `/deletes` and `/keyChanges` preserve ODS-style delete-aware relationship visibility and the selected DMS tracked-change authorization contract
- that contract intentionally includes ownership filtering using captured `CreatedByOwnershipTokenId`
- reviewers should evaluate this as an explicit DMS exception justified by redesign auth, not as a hidden claim of strict legacy ODS parity
- implementers must preserve the exception consistently in write-side capture, read-side filtering, validation, and tests

## Required Tracked-Change Authorization Data

Each tombstone row must preserve:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`
- `CreatedByOwnershipTokenId`
- `AuthorizationBasis`

Each key-change row must preserve the pre-update tracked-change authorization state:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`
- `CreatedByOwnershipTokenId`
- `AuthorizationBasis`

The design does not store a second post-update authorization snapshot on the key-change row.

**Auth-redesign transition note:** The auth redesign direction for the live-row authorization model is currently defined by two competing documents in this repository - `reference/design/backend-redesign/design-docs/auth.md` (which follows the ODS-equivalent `auth.EducationOrganizationIdTo{securableElementName}` view/table approach and explicitly rejects a `dms.DocumentSubject` table) and `reference/design/auth/auth-redesign-subject-edorg-model.md` (which proposes `dms.DocumentSubject` / `dms.SubjectEdOrg` as the new relational authorization model). Neither document defines tracked-change authorization for `/deletes` or `/keyChanges`, and no single document has been declared the authoritative auth redesign reference for DMS. **DMS-843 does not depend on either competing auth redesign document winning.** The current-backend tracked-change authorization contract defined in this package is self-contained because it captures two different categories of data during the live pre-change authorization pass:

- copied row-local authorization projection fields from `dms.Document`, which remain tracked-change read-time inputs and context
- separately resolved `AuthorizationBasis.basisDocumentIds`, which are capture-time artifacts resolved from the live current-backend resolver graph rather than from those copied JSONB projection columns

When a future auth redesign story is approved and ships, a separate DMS-843 follow-on story must define the exact mapping from the then-current live authorization structures to these same tracked-change artifacts. Until that follow-on story is approved, implementations must use the current-backend capture-time resolver mapping defined below. Implementations must not anticipate either competing auth redesign direction or hardcode references to `dms.DocumentSubject`, `dms.SubjectEdOrg`, or any other redesign-era tables that have not yet been committed as the canonical auth model.

**Required `AuthorizationBasis.basisDocumentIds` mapping by authorization strategy (normative):**

The following table defines what `basisDocumentIds` must capture per strategy for the current backend. The copied JSONB authorization projection columns on `dms.Document` remain tracked-change row data, but they are not the source of these `DocumentId` values. Instead, `basisDocumentIds` are resolved during the same pre-delete or pre-update authorization pass from the live current-backend resolver graph. When a future auth redesign ships and those live resolver structures change, a dedicated follow-on story must update this mapping table to name the then-current capture-time resolver source; until that story is approved no implementation may substitute a speculative future source.

| Authorization strategy | `basisDocumentIds` key | What is captured |
| --- | --- | --- |
| `RelationshipsWithStudentsOnly` / `RelationshipsWithStudentsOnlyThroughResponsibility` / `RelationshipsWithStudentsAndEdOrgs` | `studentDocumentIds` | Sorted unique `DocumentId` values for the `Student` basis documents resolved during the pre-change authorization pass from the resource's resolved student securable identifiers through `dms.StudentSecurableDocument` |
| `RelationshipsWithEdOrgsOnly` (direct EdOrg) | `educationOrganizationDocumentIds` | Sorted unique `DocumentId` values for the education-organization basis documents resolved during the pre-change authorization pass from the routed resource's resolved EdOrg securable identifiers through `dms.EducationOrganizationHierarchy` |
| `RelationshipsWithContactsAndEdOrgs` | `contactDocumentIds` | Sorted unique `DocumentId` values for the `Contact` basis documents resolved during the pre-change authorization pass from the resource's resolved contact securable identifiers through `dms.ContactSecurableDocument` |
| `RelationshipsWithStaffAndEdOrgs` | `staffDocumentIds` | Sorted unique `DocumentId` values for the `Staff` basis documents resolved during the pre-change authorization pass from the resource's resolved staff securable identifiers through `dms.StaffSecurableDocument` |
| Custom-view authorization | `customViewDocumentIds` (or resource-specific key) | Basis-resource `DocumentId` values emitted by the same live authorization/view logic that authorizes the resource; if that logic cannot deterministically emit basis-resource `DocumentId` values at write time, the resource is not eligible for tracked-change custom-view authorization |

**Current-backend capture-time resolver implementation rule (normative):**

For the current PostgreSQL backend, `AuthorizationBasis.basisDocumentIds` must be populated by dedicated capture-time resolver queries that return basis-resource `dms.Document.Id` values. The existing helper methods that populate live authorization projections on `dms.Document` remain valid only for live EdOrg-array projections and copied tracked-change context; they are not valid sources for `basisDocumentIds` because they aggregate education-organization ids rather than basis-resource `DocumentId` values.

| `basisDocumentIds` key | Required current-backend capture-time resolver source | Returned basis value |
| --- | --- | --- |
| `studentDocumentIds` | `dms.StudentSecurableDocument` by the resolved student securable identifier from the pre-change authorization pass | `StudentSecurableDocumentId` (`dms.Document.Id`) |
| `educationOrganizationDocumentIds` | `dms.EducationOrganizationHierarchy` by the resolved EdOrg securable identifier from the pre-change authorization pass | `DocumentId` (`dms.Document.Id`) |
| `contactDocumentIds` | `dms.ContactSecurableDocument` by the resolved contact securable identifier from the pre-change authorization pass | `ContactSecurableDocumentId` (`dms.Document.Id`) |
| `staffDocumentIds` | `dms.StaffSecurableDocument` by the resolved staff securable identifier from the pre-change authorization pass | `StaffSecurableDocumentId` (`dms.Document.Id`) |

For avoidance of doubt, the following current-backend artifacts are not valid sources for `AuthorizationBasis.basisDocumentIds`:

- `dms.Document.StudentSchoolAuthorizationEdOrgIds`
- `dms.Document.StudentEdOrgResponsibilityAuthorizationIds`
- `dms.Document.ContactStudentSchoolAuthorizationEdOrgIds`
- `dms.Document.StaffEducationOrganizationAuthorizationEdOrgIds`
- `GetStudentSchoolAuthorizationEducationOrganizationIds(...)`
- `GetStudentEdOrgResponsibilityAuthorizationIds(...)`
- `GetContactStudentSchoolAuthorizationEducationOrganizationIds(...)`
- `GetStaffEducationOrganizationAuthorizationEdOrgIds(...)`

Those columns and helper methods remain EdOrg-array projections only. A conforming DMS-843 implementation on the current backend must add separate resolver queries for tracked-change capture rather than expanding those arrays and reinterpreting them as basis-resource `DocumentId` values.

For relationship strategies that require ODS-style delete-aware visibility, `relationshipInputs` must also capture the named relationship values that cannot be derived from `DocumentId` lookups alone after the live row has been deleted. The required `relationshipInputs` members must be declared per-resource in the tracked-change authorization contract and validated at claim-set load time.

Required interpretation:

- `dms.StudentSecurableDocument`, `dms.ContactSecurableDocument`, and `dms.StaffSecurableDocument` are valid capture-time resolver sources even though they are not safe tracked-change read-time sources after delete
- `dms.EducationOrganizationHierarchy.DocumentId` is the valid current-backend direct-EdOrg capture-time resolver source for `educationOrganizationDocumentIds`
- JSONB columns such as `StudentSchoolAuthorizationEdOrgIds`, `ContactStudentSchoolAuthorizationEdOrgIds`, and `StaffEducationOrganizationAuthorizationEdOrgIds` remain copied tracked-change row data, but they are not the source of `basisDocumentIds`
- existing old-PostgreSQL helper methods that aggregate EdOrg ids from `StudentSchoolAssociationAuthorization`, `StudentEducationOrganizationResponsibilityAuthorization`, `ContactStudentSchoolAuthorization`, or `StaffEducationOrganizationAuthorization` are live-projection helpers only and must not be reused as tracked-change `basisDocumentIds` resolvers

**Future auth-redesign tombstone authorization evaluation (deferred):**

When a future auth redesign ships and the current capture-time resolver structures no longer exist in their present form, tracked-change authorization for `/deletes` and `/keyChanges` must be re-evaluated against the then-current live authorization structures. The exact evaluation mapping depends on which auth redesign design is approved and is not defined in this package. The normative evaluation contract for that future state must be defined in a dedicated follow-on story after the auth redesign direction is settled. Until that story is approved, implementations must use the current-backend tracked-change evaluation contract:

- namespace-based: use stored `SecurityElements.Namespace` on the tombstone or key-change row
- EdOrg-based: join the stored `educationOrganizationDocumentIds` against the live-row authorization structures for the caller's authorized EdOrg set
- student/contact/staff relationship: join the stored `studentDocumentIds` / `contactDocumentIds` / `staffDocumentIds` against the current-backend authorization structures that recognize those basis-resource `DocumentId` values
- ownership: use stored `CreatedByOwnershipTokenId` unchanged
- custom-view: evaluate the resource's declared tracked-change authorization contract using stored `basisDocumentIds` and `relationshipInputs`

Implementations must not hardcode references to `dms.DocumentSubject`, `dms.SubjectEdOrg`, or any other redesign-era authorization tables until a follow-on story explicitly commits them as the canonical auth model and updates this evaluation contract accordingly.

## `AuthorizationBasis` Semantics

`AuthorizationBasis` is a resource-scoped payload stored on tombstone and key-change rows.

Required meaning:

- it preserves the basis-resource `DocumentId` values needed for redesign relationship and custom-view authorization
- it preserves any additional delete-aware relationship inputs needed to reproduce ODS-style tracked-change visibility when the authorizing relationship row has already been deleted
- it is captured from the same pre-delete or pre-update authorization-resolution pass used to determine the row's live authorization state
- its `basisDocumentIds` members are capture-time artifacts resolved from the live current-backend resolver graph before the delete or update mutates or removes the relevant resolver rows; they are not direct reuses of the copied JSONB EdOrg-array projections on `dms.Document`
- it is interpreted only in the context of the tracked row's routed resource

Required structural contract:

- the payload root is a JSON object
- when `AuthorizationBasis` is present, the payload root must contain `contractVersion`
- `contractVersion` is a positive integer identifying the resource-scoped tracked-change authorization contract version used when the row was captured
- when relationship or custom-view tracked-change authorization applies, the payload must contain `basisDocumentIds`
- `basisDocumentIds` maps stable basis-resource identifiers to sorted unique arrays of positive `DocumentId` values resolved during the same authorization pass
- when a resource needs additional delete-aware relationship facts beyond those ids, the payload must also contain `relationshipInputs`, a deterministic resource-scoped object of named captured values
- each change-query-enabled resource that relies on this payload must define the expected `basisDocumentIds` keys and any `relationshipInputs` members as part of its tracked-change authorization contract
- incompatible changes to the meaning or required members of `basisDocumentIds` or `relationshipInputs` must bump `contractVersion`
- retained tracked rows are interpreted through their stored `contractVersion`; DMS must not infer old rows against only the current live contract shape

DMS-843 does not support open-ended tracked-change custom-view authorization that depends on arbitrary mutable non-identifying live-row values at read time. A resource is eligible for tracked-change relationship or custom-view authorization only when its required inputs can be reduced at write time to captured basis-resource `DocumentId` values plus any named `relationshipInputs` in this contract. If that reduction is not possible, the resource's tracked-change design is incomplete and the affected security metadata must be rejected when claim-set metadata is loaded or refreshed rather than silently degrading.

**`contractVersion` registry, derivation, and multi-instance enforcement:**

The `contractVersion` value for a given resource is owned and stored in the DMS security metadata for that resource (not in `ApiSchema.json`). The following rules govern its lifecycle:

- **Storage location:** `contractVersion` is declared as part of the `ReadChanges` action claim in the CMS claim-set configuration, as a `trackedChangeAuthorizationContract` property on the resource's `ReadChanges` `ResourceClaim` entry. DMS loads it at startup and on claim-set refresh via `IClaimSetProvider`, as an additional field on the `ResourceClaim` for the `ReadChanges` action. The `ResourceClaim` model must be extended to carry this optional property. A concrete example for `studentSchoolAssociations` with `RelationshipsWithStudentsAndEdOrgs`:

  ```json
  {
    "resourceClaimUri": "uri://ed-fi.org/api/claims/ed-fi/studentSchoolAssociations",
    "action": "ReadChanges",
    "authorizationStrategies": ["RelationshipsWithStudentsAndEdOrgs"],
    "trackedChangeAuthorizationContract": {
      "contractVersion": 1,
      "basisDocumentIds": {
        "studentDocumentIds": "student_basis",
        "educationOrganizationDocumentIds": "edorg_basis"
      },
      "relationshipInputs": {}
    }
  }
  ```

  Resources that do not require relationship or custom-view tracked-change authorization (e.g., namespace-only or ownership-only) may omit `trackedChangeAuthorizationContract` from their `ReadChanges` claim entry; DMS treats the absence as meaning only row-local authorization fields (`SecurityElements`, `CreatedByOwnershipTokenId`) are required for tracked-change evaluation.
- **Derivation:** implementers bump `contractVersion` by incrementing the integer in the resource's security configuration before deploying the incompatible change. There is no automated derivation from schema structure; a developer must increment it as a deployment step whenever the meaning, names, or required presence of `basisDocumentIds` or `relationshipInputs` members changes.
- **What constitutes an incompatible change:** renaming a `basisDocumentIds` key, adding a newly required `basisDocumentIds` key, removing a previously emitted key, changing the semantics of a `relationshipInputs` field, or removing a field from `relationshipInputs` when retained rows still carry it.
- **Per-resource scope:** `contractVersion` is scoped per resource; compatible changes to one resource's contract do not require bumping another resource's `contractVersion`.
- **Multi-instance / rolling-restart coordination:** in a multi-pod deployment, all active pods must agree on the set of supported `contractVersion` values before tracked-change routes are served. Required operational protocol:
  1. during a rolling deployment that bumps `contractVersion`, the new version must be backward-compatible or the deployment must drain tracked-change write traffic before rolling pods;
  2. each pod validates, at startup and on claim-set refresh, that all retained tombstone and key-change rows use only `contractVersion` values present in the current claim-set metadata; if any retained row carries an unsupported `contractVersion`, the pod must refuse to serve tracked-change routes for the affected resource and surface the failure as a health-check violation until the inconsistency is resolved;
  3. deployments must not silently route requests to pods with split `contractVersion` support; use rolling-pod health checks to prevent out-of-date pods from serving tracked-change routes after a bumped `contractVersion` is deployed.
- **Validation gates (reiterated):** claim-set load at startup rejects unsupported `contractVersion` values; claim-set refresh rejects them; write-path capture fails the request if the `contractVersion` cannot be determined before tombstone or key-change-row insert.

Enforcement ownership and gates:

- enforcement of the resource-scoped `AuthorizationBasis` contract is owned by the DMS core authorization and claim-set metadata pipeline in this feature scope; it is not delegated to optional external components
- claim-set and authorization metadata validation must run both at initial bootstrap and whenever the claim-set cache is refreshed
- if a refreshed claim set introduces an invalid tracked-change relationship/custom-view contract, or drops support for a retained `AuthorizationBasis.contractVersion` that still exists in tracked rows, DMS must mark the affected authorization metadata invalid and fail requests with a security configuration error rather than requiring a process restart or silently weakening authorization
- write-path capture must fail the request if required tracked-change authorization inputs for that routed resource cannot be resolved to the declared contract shape before tombstone or key-change-row insert
- when that write-path failure reaches the API surface, DMS returns `500 Internal Server Error` ProblemDetails with type `urn:ed-fi:api:system-configuration:security`; no tombstone or key-change row may be committed in that failure path
- deployments must treat these failures as contract-safety failures; silent fallback to weaker tracked-change authorization is not allowed
- before exposing an incompatible tracked-change authorization contract, deployments must either migrate retained tracked rows, purge/reinitialize the retained tracked-change artifacts, or keep backward-compatible evaluation support for the older `contractVersion`

## Tracked-Change Authorization Model

The tracked-change endpoints should preserve the selected ODS-aligned `ReadChanges` outcomes adopted by this package, plus the accepted DMS-specific ownership exception justified above.

The query sources are:

| Query type | Source | Authorization source |
| --- | --- | --- |
| Live changed-resource query | `dms.Document` | live authorization columns and live authorization joins |
| Delete query | `dms.DocumentDeleteTracking` | tracked-change authorization data captured on the tombstone |
| Key change query | `dms.DocumentKeyChangeTracking` | tracked-change authorization data captured on the pre-update tracking row |

Required tracked-change categories:

- namespace-based authorization
- direct education-organization authorization
- student relationship authorization
- contact relationship authorization
- staff relationship authorization
- ownership-based authorization
- custom-view authorization using basis-resource `DocumentId` values

Ownership note:

- ownership-based tracked-change authorization is included because backend-redesign [`auth.md`](../design/backend-redesign/design-docs/auth.md) stores `CreatedByOwnershipTokenId` on `dms.Document` as a first-class authorization input and redesign [`auth-redesign-subject-edorg-model.md`](../design/auth/auth-redesign-subject-edorg-model.md) preserves ownership-style constraints
- this is an accepted DMS-specific authorization exception, not a claim that legacy ODS `ReadChanges` currently applies ownership filtering on `/deletes` or `/keyChanges`

Implementation guidance:

- namespace and direct education-organization checks may reuse copied row-local projection data directly
- ownership checks use the stored `CreatedByOwnershipTokenId`
- a tracked row with `CreatedByOwnershipTokenId = null` does not bypass ownership filtering; under ownership-based authorization it is treated as ownership-uninitialized and is not returned through that strategy
- relationship and custom-view checks use the stored `AuthorizationBasis` rather than assuming the required live relationship rows still exist
- key-change-query authorization filtering must happen before ordering, paging, and `totalCount`
- each surviving key-change tracking row is evaluated and returned as its own public key-change event with that row's stored `oldKeyValues`, `newKeyValues`, and `ChangeVersion`
- rows removed by authorization filtering must not consume page slots or contribute to `totalCount`
- hierarchy-based filters continue to use `dms.EducationOrganizationHierarchyTermsLookup` where they are still part of the active authorization evaluation

## How Delete-Aware Relationship Visibility Is Preserved

The core delete problem is timing:

- the resource row disappears
- related authorization rows may disappear
- the relationship that originally granted access may itself have been deleted earlier in the same synchronization history

ODS addresses this with delete-aware tracked-change authorization behavior.

DMS-843 must preserve the same outcome. One conforming strategy is:

- resolve the tracked-change authorization state before the live row is deleted or before the key change mutates the row
- persist that state on the tombstone or key-change row
- evaluate tracked-change authorization from the persisted row-local state later, without depending on the deleted live relationships still existing

This is why `AuthorizationBasis` is a required tracked-change artifact rather than an optional optimization.

## Delete Request Authorization

The existing DELETE route behavior remains a live-row authorization decision first.

Required order:

- load and lock the live row
- resolve the tracked-change authorization data that would be persisted on the tombstone
- authorize the delete against the live row
- insert the tombstone only after authorization succeeds
- then delete the live row

This prevents unauthorized callers from generating tombstone side effects.

## Key-Change Capture Authorization

For identity-changing updates, tracked-change authorization capture is also pre-change.

Required order:

- load and lock the live row
- resolve the pre-update tracked-change authorization data
- authorize the update against the live row
- apply the identity-changing update
- insert the key-change row with the pre-update tracked-change authorization data

Rationale:

- the caller needs visibility to retire the old key they were authorized to see
- using only the post-update live row would lose some legitimate transitions
- the stored pre-update state is also the only durable source once later updates or deletes occur

## Profile Behavior for Change Query GET Endpoints

Changed-resource mode on the existing collection GET route continues to use the normal profile-resolution and profile-response-filtering behavior because it still returns standard resource representations.

Changed-resource eligibility remains resource-level:

- the API decides whether a resource qualifies for changed-resource mode from the underlying resource `ChangeVersion` before profile filtering
- a readable profile may therefore return a filtered representation whose visible fields appear unchanged even though the resource qualified because some non-profile-visible field changed
- the design intentionally accepts that behavior because profile-visible-level change tracking would require separate profile-specific stamps or journals and is not part of DMS-843

The new non-resource Change Query GET endpoints use different rules:

- `/deletes`, `/keyChanges`, and `availableChangeVersions` do not participate in profile resolution or profile response filtering
- readable profile media types on those endpoints are ignored rather than validated or enforced
- profile-based readability must not block the request, alter authorization, alter row eligibility, reshape the payload, or change the response content type
- those endpoints return ordinary `application/json` rather than profile-specific media types

Implementation guidance:

- route `/deletes`, `/keyChanges`, and `availableChangeVersions` through pipelines that omit `ProfileResolutionMiddleware` and `ProfileFilteringMiddleware`

## `availableChangeVersions` Authorization

The endpoint is instance-scoped metadata rather than resource payload data.

Recommended behavior:

- require normal JWT authentication and instance resolution
- do not apply per-resource claim-set filtering

Reason:

- the endpoint returns only synchronization bounds
- it does not reveal resource payloads, identifiers, or delete details

## Auth-Only Updates Must Not Emit Change Records

This is a hard invariant for the feature.

Current authorization-maintenance triggers can update derived authorization columns on `dms.Document` without changing `EdfiDoc`.

Required behavior:

- those updates must not change `dms.Document.ChangeVersion`
- those updates must not create `dms.DocumentChangeEvent` rows
- those updates must not create `dms.DocumentKeyChangeTracking` rows

Reason:

- authorization-table churn is not a representation change
- emitting changes for such updates would create false synchronization noise for clients

## Education Organization Cleanup Ordering

The current delete flow may run hierarchy cleanup such as `DeleteEducationOrganizationHierarchy(...)`.

That cleanup can continue, but the tombstone must already have been inserted before the live `dms.Document` row is deleted.

Reason:

- after the delete, the live authorization state and dependent relationship rows may no longer be reconstructible

## Descriptor Resources

The design supports descriptors because they are stored in the same canonical `dms.Document` table and share the same overall change-tracking model.

DMS-843 v1 includes descriptor endpoints in Change Queries with no special exclusion.

If product policy later decides to exclude some descriptor endpoints from Change Queries, that should be an explicit endpoint-level product decision rather than a change to the underlying authorization or change-tracking design.

## Review Criteria

The authorization design is acceptable if reviewers agree that:

- live changed-resource queries reuse the current live authorization semantics
- delete queries target the documented tracked-change authorization contract, including ODS-style delete-aware relationship visibility and the accepted DMS-specific ownership exception
- key-change queries target the documented tracked-change authorization contract while preserving pre-update transition visibility and the accepted DMS-specific ownership exception
- tombstones preserve enough tracked-change authorization data to survive deletion of the live row and related relationship rows
- key-change tracking preserves enough tracked-change authorization data to survive later key mutations or deletion of the live row
- redesign ownership and DocumentId-based authorization concepts are represented in the tracked-change artifacts
- authorization-maintenance updates do not create false change records
- changed-resource eligibility remains resource-level even when readable profiles filter the returned representation
