---
jira: DMS-1188
jira_url: https://edfi.atlassian.net/browse/DMS-1188
---

# Story: Apply Relationship-based `ReadChanges` Authorization to Change Query Endpoints

## Description

Apply `ReadChanges` authorization to `/deletes` and `/keyChanges` for relationship-based authorization strategies.

DMS supports the same ODS relationship-based authorization strategies for Change Query endpoints. Strategies with names shared by live `Read` authorization reuse existing authorization views. Strategies with `*IncludingDeletes` use the corresponding `ReadChanges` authorization views.

The authorization composition rules from `auth.md` apply unchanged. Relationship-based strategies are OR-composed with each other. Non-relationship strategies such as `NamespaceBased` are AND-composed with the relationship strategy group when implemented by `27-no-further-and-namespace-readchanges-authorization.md`.

Unsupported `ReadChanges` strategies are treated as unavailable authorization strategy implementations during request-time authorization resolution. DMS returns the existing security-configuration ProblemDetails from `auth.md`, using the unknown-strategy error text: `Could not find authorization strategy implementations for the following strategy names: '{strategyName1}', '{strategyName2}'.`

## Acceptance Criteria

- `/deletes` and `/keyChanges` require the `ReadChanges` action.
- Missing `ReadChanges` claims return `403 Forbidden` with the authorization ProblemDetails defined in `auth.md`.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` work against tracked-change old-value columns where appropriate.
- `RelationshipsWithEdOrgsAndPeopleIncludingDeletes` uses the `EducationOrganizationIdToStudentDocumentIdIncludingDeletes`, `EducationOrganizationIdToContactDocumentIdIncludingDeletes`, or `EducationOrganizationIdToStaffDocumentIdIncludingDeletes` view as appropriate.
- `RelationshipsWithStudentsOnlyIncludingDeletes` uses the student including-deletes view.
- `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes` uses `EducationOrganizationIdToStudentDocumentIdDeletedResponsibility`.
- People authorization predicates use denormalized old/new person `DocumentId` values from tracked-change tables rather than joining intermediate resources.
- Multiple relationship-based authorization strategies compose with OR semantics.
- Multiple authorization strategies compose with the semantics defined in `auth.md`, including AND composition with `NamespaceBased` and no-op composition with `NoFurtherAuthorizationRequired` when `27-no-further-and-namespace-readchanges-authorization.md` is implemented.
- Authorization predicates apply before paging and `totalCount`.
- Unsupported `ReadChanges` strategies fail during request-time authorization resolution with HTTP 500 Security Configuration Error ProblemDetails as defined in `auth.md`.
- The unsupported-strategy ProblemDetails use type `urn:ed-fi:api:system:configuration:security` and the existing unknown-strategy error text: `Could not find authorization strategy implementations for the following strategy names: '{strategyName1}', '{strategyName2}'.`
- Unsupported-strategy coverage includes `OwnershipBased`, `RelationshipsWithPeopleOnly`, `RelationshipsWithEdOrgsAndPeopleInverted`, and custom view-based `ReadChanges` strategies deferred from v1.
- Tests cover `/deletes` and `/keyChanges` for EdOrg-only, EdOrg-only inverted, EdOrg-and-people, students-only, responsibility-through-deleted, unsupported strategies, paging, and `totalCount`.

## Out of Scope

- `NoFurtherAuthorizationRequired` and `NamespaceBased` `ReadChanges` authorization, handled by `27-no-further-and-namespace-readchanges-authorization.md`.
- Custom view-based authorization strategies.
- Snapshot authorization behavior.
- Feature-disabled Change Query behavior.
