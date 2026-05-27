---
jira: DMS-1197
jira_url: https://edfi.atlassian.net/browse/DMS-1197
---

# Story: Apply `NoFurtherAuthorizationRequired` and `NamespaceBased` `ReadChanges` Authorization

## Description

Implement the non-relationship `ReadChanges` authorization strategies for Change Query `/deletes` and `/keyChanges` endpoints:

- `NoFurtherAuthorizationRequired`
- `NamespaceBased`

This story is the companion split to `25-readchanges-authorization.md`, which owns the relationship-based `ReadChanges` strategies.

`NoFurtherAuthorizationRequired` grants access after authentication and a matching `ReadChanges` claim without adding an authorization predicate. When combined with other supported strategies, it remains a no-op and does not restrict results or contribute failure hints.

`NamespaceBased` predicates use tracked-change old namespace values so tombstones and key-change rows are authorized against the namespace that was present when the tracked-change row was written.

Descriptor Change Query endpoints preserve the ODS descriptor exceptions. Most descriptors use `NoFurtherAuthorizationRequired`. The two ODS exceptions use `NamespaceBased`:

- `CrisisTypeDescriptor`
- `NonMedicalImmunizationExemptionDescriptor`

The authorization composition rules from `auth.md` apply unchanged. `NamespaceBased` is AND-composed with the relationship strategy OR group implemented by `25-readchanges-authorization.md`, while `NoFurtherAuthorizationRequired` remains a no-op.

## Acceptance Criteria

- `NoFurtherAuthorizationRequired` works for `ReadChanges`.
- `NoFurtherAuthorizationRequired` does not add a SQL predicate for `/deletes` or `/keyChanges`.
- `NoFurtherAuthorizationRequired` remains a no-op when combined with relationship-based `ReadChanges` strategies.
- `NamespaceBased` works for `/deletes` and `/keyChanges` when a resource or descriptor's `ReadChanges` action is configured with the strategy.
- Namespace predicates use tracked-change old-value namespace columns, not live resource namespace values.
- Descriptor `NamespaceBased` authorization uses `tracked_changes_edfi.Descriptor.Old_Namespace` and filters by the descriptor `Discriminator`.
- The descriptor exceptions `CrisisTypeDescriptor` and `NonMedicalImmunizationExemptionDescriptor` use `NamespaceBased`; descriptors without those exceptions continue to work through `NoFurtherAuthorizationRequired`.
- Resource `NamespaceBased` authorization resolves the namespace securable element from `ApiSchema.json` to the corresponding tracked-change old-value storage column.
- Rows with null or empty tracked namespace values are unauthorized.
- API clients with no configured namespace prefixes receive the Namespace authorization ProblemDetails defined in `auth.md`.
- Namespace prefix matching uses the same semantics as live `NamespaceBased` authorization.
- Namespace predicates apply before paging and `totalCount`.
- `NamespaceBased` composes with `NoFurtherAuthorizationRequired` as defined in `auth.md`; `NoFurtherAuthorizationRequired` remains a no-op.
- When relationship-based `ReadChanges` strategies are also configured, `NamespaceBased` is AND-composed with the relationship strategy OR group.
- Tests cover `/deletes` and `/keyChanges` for `NoFurtherAuthorizationRequired`, descriptor namespace exceptions, a namespace-authorized resource where available in the fixture model, missing namespace prefixes, namespace mismatch, null or empty tracked namespace values, paging, and `totalCount`.

## Out of Scope

- Relationship-based `ReadChanges` authorization, handled by `25-readchanges-authorization.md`.
- Custom view-based authorization strategies.
- Snapshot authorization behavior.
- Feature-disabled Change Query behavior.
