---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Story: Split Relationship-based Authorization for GET-by-id, POST, PUT, and DELETE

## Planning Split

This planning branch recuts `DMS-1056` into six implementation slices so the team can land the relationship CRUD authorization work without combining the operation refactor, EdOrg strategy behavior, People strategy behavior, and final error-response hardening in one acceptance boundary.

All slice documents keep the `DMS-1056` ticket number until the Jira split is finalized.

This document becomes the umbrella index for the slice docs under:

- `reference/design/backend-redesign/epics/14-authorization/07-relationship-auth-crud/`

## Shared Constraints

All slices inherit these constraints:

- Relationship authorization follows `reference/design/backend-redesign/design-docs/auth.md`.
- DMS reuses the ODS-parity concrete root-table EducationOrganization subject scope established by `DMS-1055`; child-table EdOrg predicates are not introduced unless a later design change explicitly changes DMS semantics.
- Relationship strategies compose as OR strategies; multiple securable subjects within one relationship strategy compose with AND semantics.
- `RelationshipsWithEdOrgsOnlyInverted` and `RelationshipsWithEdOrgsAndPeopleInverted` swap `SourceEducationOrganizationId` and `TargetEducationOrganizationId` filtering for EdOrg hierarchy checks.
- SQL generation and parameter binding must support PostgreSQL and SQL Server, including the DMS-1055 `ClaimEducationOrganizationIds` parameter contract and SQL Server `dms.BigIntTable` TVP threshold behavior.
- Generated operation-specific authorization SQL should not be cached. Resolved securable path metadata and operation-neutral authorization specs may be cached by effective schema, mapping set, resource, strategy, and securable element.
- Authorization failures must preserve strategy identity and configured strategy index ordering so `AUTH1` failures and final ProblemDetails can point back to the correct strategy.

## Slice Order

1. [01-relationship-crud-auth-core.md](07-relationship-auth-crud/01-relationship-crud-auth-core.md)
   - Goal: generalize DMS-1055 relationship strategy classification, subject resolution, parameterization, and SQL-fragment inputs beyond GET-many.
   - After merge: operation-neutral EdOrg relationship authorization specs are available for stored-value and proposed-value single-record checks.
   - Still out of scope: endpoint execution, People-involved subjects, and final ProblemDetails formatting.
2. [02-edorg-only-get-by-id-and-delete.md](07-relationship-auth-crud/02-edorg-only-get-by-id-and-delete.md)
   - Goal: prove the core vertically with EdOrg-only stored-value checks for GET-by-id and DELETE.
   - After merge: unauthorized GET-by-id does not reconstitute, and unauthorized DELETE does not delete.
   - Still out of scope: POST, PUT, POST-as-update, People subjects, and final ProblemDetails hardening.
3. [03-edorg-only-post-create.md](07-relationship-auth-crud/03-edorg-only-post-create.md)
   - Goal: authorize proposed request-body EdOrg values before creating a new document.
   - After merge: unauthorized POST-create does not insert `dms.Document` or resource rows.
   - Still out of scope: POST-as-update, PUT, DELETE changes beyond Slice 2, People subjects, and final ProblemDetails hardening.
4. [04-edorg-only-put-and-post-as-update.md](07-relationship-auth-crud/04-edorg-only-put-and-post-as-update.md)
   - Goal: authorize EdOrg-only updates by checking stored values first and proposed values second when identifying authorization values can change.
   - After merge: PUT and POST-as-update close the EdOrg-only CRUD operation surface.
   - Still out of scope: People CRUD endpoint execution and final ProblemDetails hardening.
5. [05-people-relationship-auth-core.md](07-relationship-auth-crud/05-people-relationship-auth-core.md)
   - Goal: implement the shared People-involved relationship authorization core consumed by GET-many and later People CRUD work.
   - After merge: People strategy classification, person DocumentId path resolution, auth-view selection, inverted EdOrg metadata, and failure-hint metadata are available operation-neutrally.
   - Still out of scope: GET-many filtering execution, People CRUD endpoint execution, database execution, and endpoint ProblemDetails mapping.
6. [06-relationship-auth-problemdetails-hardening.md](07-relationship-auth-crud/06-relationship-auth-problemdetails-hardening.md)
   - Goal: harden relationship authorization error handling to the exact `auth.md` RFC 9457 ProblemDetails contract.
   - After merge: relationship CRUD authorization failures format readable securable names, EdOrg claims, singular/plural messages, and OR-strategy hints consistently.
   - Still out of scope: new authorization strategies or new database objects.

## Operation Ownership Map

- Operation-neutral EdOrg CRUD auth specs, shared strategy metadata, and SQL-fragment inputs — Slice 1
- `GET /{resource}/{id}` EdOrg-only stored-value checks — Slice 2
- `DELETE /{resource}/{id}` EdOrg-only stored-value checks — Slice 2
- `POST /{resource}` create-new EdOrg-only proposed-value checks — Slice 3
- `POST /{resource}` upsert-as-update EdOrg-only stored/proposed checks — Slice 4
- `PUT /{resource}/{id}` EdOrg-only stored/proposed checks — Slice 4
- People-involved strategy core metadata and path resolution — Slice 5
- Exact relationship authorization ProblemDetails behavior — Slice 6

## Notes For Review

- The first implementation slice is deliberately operation-neutral. It should remove GET-many-specific naming and contracts from reusable relationship auth infrastructure instead of copying `PageDocumentId...` or `RelationalGetMany...` concepts into single-record operations.
- Slice 2 is the first vertical proof because it needs only stored values and can validate the read/delete batching model before write executor integration.
- Slice 4 is the highest-risk EdOrg-only operation slice because it touches existing target resolution, `If-Match`, current-state loading, guarded no-op, profile-aware writes, and authorization-before-mutation behavior.
- Slice 5 is intentionally a core story, not an endpoint story. It gives `DMS-1095` and later People CRUD work the same reusable People subject model.
- Slice 6 should not introduce new authorization semantics. It closes response-shape and hint aggregation gaps after the operation paths can produce structured relationship authorization failures.
