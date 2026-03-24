# Authorization and Delete Semantics

## Objective

Ensure Change Queries preserve the correct authorization behavior for each surface:

- live changed-resource queries continue to use the current live-read authorization behavior
- `/deletes` and `/keyChanges` target the same tracked-change authorization criteria ODS applies for `ReadChanges`
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
| `dms.EducationOrganizationHierarchy` | Stores education-organization parent-child relationships. | Unchanged. Existing cleanup remains part of delete execution. |
| `dms.EducationOrganizationHierarchyTermsLookup` | Stores hierarchy expansion values used in EdOrg filters. | Reused by live and tracked-change authorization logic. |
| `dms.StudentSchoolAssociationAuthorization` | Derives student authorization values from student-school relationships. | Continues feeding live-row projection data, but tracked changes cannot rely on the row still existing later. |
| `dms.StudentSecurableDocument` | Maps student identifiers to securable documents. | Live-only helper; tracked changes cannot assume it survives delete timing. |
| `dms.StudentContactRelation` | Bridges student-contact relationships for contact authorization. | Live-only helper; tracked changes need preserved basis data when rows are gone. |
| `dms.ContactStudentSchoolAuthorization` | Derives contact authorization values. | Continues feeding live-row projection data. |
| `dms.ContactSecurableDocument` | Maps contact identifiers to securable documents. | Live-only helper. |
| `dms.StaffEducationOrganizationAuthorization` | Derives staff authorization values. | Continues feeding live-row projection data. |
| `dms.StaffSecurableDocument` | Maps staff identifiers to securable documents. | Live-only helper. |
| `dms.StudentEducationOrganizationResponsibilityAuthorization` | Derives student responsibility authorization values. | Continues feeding live-row projection data, but tracked changes need preserved basis data after delete timing. |

The Change Queries feature does not require these companion tables to become the tracked-change source of truth. The tracked-change source of truth is the authorization data preserved on the tombstone or key-change row.

## Live Changed-Resource Authorization

Implementation rule:

- apply the existing live collection authorization filters after `dms.DocumentChangeEvent` candidate selection and verification against `dms.Document.ChangeVersion`
- continue to use the current live-row authorization model for namespace, education-organization, relationship, custom-view, and ownership checks

Expected effect:

- a caller who can read a live resource in the current collection GET path can also read the same resource in changed-resource mode if it falls within the requested window
- a caller who cannot read the live resource remains blocked in changed-resource mode

## Why Tracked Changes Need More Than the Current Live Projection

ODS tracked changes do not simply reuse the current live-read query shape.

Relevant ODS behavior:

- tracked changes use `ReadChanges` authorization criteria
- delete visibility can remain valid even when the authorizing relationship row has already been deleted
- relationship authorization uses delete-aware semantics
- ownership and custom-view authorization apply to tracked changes too

The redesign authorization direction has the same implication in DMS:

- ownership-based authorization depends on `CreatedByOwnershipTokenId`
- relationship and custom-view authorization depend on basis-resource `DocumentId` values rather than natural keys

Therefore, copied live-row projection columns alone are not enough for tracked changes. A tombstone or key-change row must preserve the tracked-change authorization state needed to reproduce the correct outcome after the live row, companion rows, or related relationship rows are gone.

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

## `AuthorizationBasis` Semantics

`AuthorizationBasis` is a resource-scoped payload stored on tombstone and key-change rows.

Required meaning:

- it preserves the basis-resource `DocumentId` values needed for redesign relationship and custom-view authorization
- it preserves any additional delete-aware relationship inputs needed to reproduce ODS-style tracked-change visibility when the authorizing relationship row has already been deleted
- it is captured from the same pre-delete or pre-update authorization-resolution pass used to determine the row's live authorization state
- it is interpreted only in the context of the tracked row's routed resource

This design is intentionally outcome-oriented. A conforming implementation may choose any deterministic internal shape for `AuthorizationBasis` as long as it can reproduce the documented tracked-change authorization results.

## Tracked-Change Authorization Model

The tracked-change endpoints should follow the same authorization criteria that ODS applies for `ReadChanges`, even though DMS may satisfy that requirement with different physical SQL than ODS.

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

Implementation guidance:

- namespace and direct education-organization checks may reuse copied row-local projection data directly
- ownership checks use the stored `CreatedByOwnershipTokenId`
- relationship and custom-view checks use the stored `AuthorizationBasis` rather than assuming the required live relationship rows still exist
- key-change-query authorization filtering must happen before collapse
- the promised earliest `oldKeyValues`, latest `newKeyValues`, and latest `ChangeVersion` semantics apply only to the rows that remain after authorization filtering
- rows removed by authorization filtering must not participate in collapse
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
- delete queries target ODS-style tracked-change authorization criteria rather than only current live-read semantics
- key-change queries target ODS-style tracked-change authorization criteria while preserving pre-update transition visibility
- tombstones preserve enough tracked-change authorization data to survive deletion of the live row and related relationship rows
- key-change tracking preserves enough tracked-change authorization data to survive later key mutations or deletion of the live row
- redesign ownership and DocumentId-based authorization concepts are represented in the tracked-change artifacts
- authorization-maintenance updates do not create false change records
- changed-resource eligibility remains resource-level even when readable profiles filter the returned representation
