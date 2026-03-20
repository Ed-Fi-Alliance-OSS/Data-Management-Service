# Authorization and Delete Semantics

## Objective

Ensure Change Queries preserve the same logical read-authorization behavior as the current DMS collection GET implementation.

This requires:

- understanding the current authorization data sources
- preserving authorization-relevant data for deletes
- preserving authorization-relevant data for key changes
- ensuring authorization-maintenance updates do not create false change records

## Current Read Authorization Model

In the current DMS storage and runtime model, collection GET authorization is enforced primarily from the authorization projection already stored on `dms.Document`, with support from education-organization hierarchy lookup tables.

Primary live-row authorization inputs:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`

Supporting lookup table:

- `dms.EducationOrganizationHierarchyTermsLookup`

This means live changed-resource queries can preserve authorization parity by reusing the same authorization filters that current collection GET queries already apply to `dms.Document`.

## Authorization Companion Table Inventory

The current authorization model also depends on companion tables and triggers that derive the live authorization projection.

| Table | Role in the current storage/runtime model | Change Queries impact |
| --- | --- | --- |
| `dms.EducationOrganizationHierarchy` | Stores education-organization parent-child relationships. | Unchanged. Existing cleanup remains part of delete execution. |
| `dms.EducationOrganizationHierarchyTermsLookup` | Stores hierarchy expansion values used in EdOrg filters. | Reused by live and delete query authorization logic. |
| `dms.StudentSchoolAssociationAuthorization` | Derives student authorization values from student-school relationships. | Unchanged. Continues feeding `dms.Document` projection columns. |
| `dms.StudentSecurableDocument` | Maps student identifiers to securable documents. | Unchanged. Rows disappear after delete cascade. |
| `dms.StudentContactRelation` | Bridges student-contact relationships for contact authorization. | Unchanged. |
| `dms.ContactStudentSchoolAuthorization` | Derives contact authorization values. | Unchanged. |
| `dms.ContactSecurableDocument` | Maps contact identifiers to securable documents. | Unchanged. |
| `dms.StaffEducationOrganizationAuthorization` | Derives staff authorization values. | Unchanged. |
| `dms.StaffSecurableDocument` | Maps staff identifiers to securable documents. | Unchanged. |
| `dms.StudentEducationOrganizationResponsibilityAuthorization` | Derives student responsibility authorization values. | Unchanged, but the projection must be preserved in tombstones. |

The Change Queries feature does not alter these companion tables or their triggers. The transitional `EdFi.DataManagementService.Old.Postgresql` project may still be consulted to verify current authorization behavior, but the design does not depend on that project remaining the long-term implementation.

## Live Changed-Resource Authorization

Implementation rule:

- add `ChangeVersion` predicates to the existing live collection query logic
- do not redesign the authorization predicates

Expected effect:

- a caller who can read a live resource in the current collection GET path can also read the same resource in changed-resource mode if it falls within the requested window
- a caller who cannot read the live resource remains blocked in changed-resource mode

## Why Deletes Need Authorization Projection Stored on Tombstones

When a document is deleted:

- the `dms.Document` row disappears
- authorization companion rows disappear by cascade
- securable-document rows disappear

Without copied authorization projection data, `/deletes` would either:

- return deletes without adequate authorization filtering
- or lose the ability to return deletes that an otherwise authorized caller should still see

The correct design is to copy the authorization projection from the live row into `dms.DocumentDeleteTracking` before the live row is deleted.

## Why Key Changes Need Authorization Projection Stored on Tracking Rows

When a natural key changes:

- the current live row moves to a new key state
- later updates may move it again
- a later delete may remove the live row entirely

Without copied authorization projection data, `/keyChanges` would either:

- depend on the current live row and lose the ability to return historical key transitions consistently
- or return key changes without adequate authorization filtering

The correct design is to copy the authorization projection from the live row into `dms.DocumentKeyChangeTracking` when the key change is recorded.

## Required Tombstone Authorization Columns

Each tombstone row must preserve:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`

This preserves parity with the logical authorization categories used by current collection GET queries.

## Required Key-Change Authorization Columns

Each key-change tracking row must preserve:

- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`

## Delete Query Authorization Model

Delete queries must apply the same logical read-authorization categories as live collection GET queries:

- namespace-based authorization
- direct education-organization authorization
- student relationship authorization
- contact relationship authorization
- staff relationship authorization

The difference is only the physical source table.

| Query type | Source | Authorization source |
| --- | --- | --- |
| Live changed-resource query | `dms.Document` | existing live authorization columns |
| Delete query | `dms.DocumentDeleteTracking` | copied tombstone authorization columns |
| Key change query | `dms.DocumentKeyChangeTracking` | copied key-change tracking authorization columns |

Implementation guidance:

- the delete-query authorization builder should mirror the current collection GET authorization behavior against tombstone columns
- the key-change-query authorization builder should mirror the current collection GET authorization behavior against key-change tracking columns
- hierarchy-based filters continue to use `dms.EducationOrganizationHierarchyTermsLookup`

## `availableChangeVersions` Authorization

The endpoint is instance-scoped metadata rather than resource payload data.

Recommended behavior:

- require normal JWT authentication and instance resolution
- do not apply per-resource claim-set filtering

Reason:

- the endpoint returns only synchronization bounds
- it does not reveal resource payloads, identifiers, or delete details

## Delete Request Authorization

The existing DELETE route behavior remains unchanged.

Required order:

- load the live row
- authorize against the live row
- insert the tombstone only after authorization succeeds
- then delete the live row

This prevents unauthorized callers from generating tombstone side effects.

## Auth-Only Updates Must Not Emit Change Records

This is a hard invariant for the feature.

Current authorization-maintenance triggers can update derived authorization columns on `dms.Document` without changing `EdfiDoc`.

Required behavior:

- those updates must not change `dms.Document.ChangeVersion`
- those updates must not create `dms.DocumentChangeEvent` rows if the optional journal is enabled
- those updates must not create `dms.DocumentKeyChangeTracking` rows

Reason:

- authorization-table churn is not a representation change
- emitting changes for such updates would create false synchronization noise for clients

## Education Organization Cleanup Ordering

The current delete flow may run hierarchy cleanup such as `DeleteEducationOrganizationHierarchy(...)`.

That cleanup can continue, but the tombstone must already have been inserted before the live `dms.Document` row is deleted.

Reason:

- after the delete, the live authorization projection and dependent authorization rows may no longer be reconstructible

## Descriptor Resources

The design supports descriptors because they are stored in the same canonical `dms.Document` table and share the same overall change-tracking model.

If product policy later decides to exclude some descriptor endpoints from Change Queries, that should be an endpoint-level product decision rather than a change to the underlying authorization or change-tracking design.

## Review Criteria

The authorization design is acceptable if reviewers agree that:

- live changed-resource queries reuse the current DMS authorization semantics
- delete queries are no more permissive and no more restrictive than logically equivalent live reads
- key-change queries are no more permissive and no more restrictive than logically equivalent live reads
- tombstones preserve enough authorization projection to survive deletion of the live row
- key-change tracking preserves enough authorization projection to survive later key mutations or deletion of the live row
- authorization-maintenance updates do not create false change records
