---
jira: DMS-1123
jira_url: https://edfi.atlassian.net/browse/DMS-1123
---

# Story: Integrate `ProfileAppliedWriteRequest.WritableRequestBody` into the Flattener

## Description

Implement the profile-specific boundary work that adapts the Core/backend profile write contract from `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md` to the backend-local flattener contract introduced by `reference/design/backend-redesign/epics/07-relational-write-path/02-flattening-executor.md`.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/overview.md`

Dependency note: this follow-on exists only if `DMS-983` is re-scoped as the no-profile initial relational write seam above. It is hard-blocked on:

- `DMS-983` (`02-flattening-executor.md`) — provides the reusable backend-local flattener contract, initial relational repository seam, and traversal/buffering mechanics, and
- `DMS-1106` (`01b-profile-write-context.md`) — supplies `ProfileAppliedWriteRequest` and the profile/runtime hand-off.

This story owns the request-body source selection that was removed from the re-scoped `DMS-983`:

- when no writable profile applies, backend invokes the flattener with the normal validated request body,
- when a writable profile applies, backend invokes the flattener with `ProfileAppliedWriteRequest.WritableRequestBody`,
- backend does not re-evaluate profile member filters, collection value predicates, visibility, or creatability while flattening, and
- profile-specific branching stays at the orchestration boundary instead of inside the flattener hot loop.

`DMS-984` should consume this integration rather than reopening request-body selection inside the persist/no-op executor.

## Acceptance Criteria

- When no writable profile applies, the write path invokes the flattener with the normal validated request body and behavior matches the re-scoped `DMS-983` initial relational write seam.
- When a writable profile applies, the write path invokes the flattener with `ProfileAppliedWriteRequest.WritableRequestBody` exactly as provided by Core.
- Backend does not re-filter the original request JSON, recover hidden members from the original request, or infer hidden-vs-absent semantics during flattening.
- Reference binding, nested collection handling, `_ext` handling, request sibling-order capture, and semantic-identity extraction continue to work when the selected input body is `WritableRequestBody`.
- The flattener contract remains backend-local; direct dependence on Core profile contract types is confined to the orchestration boundary that selects the body source.
- Unit or integration tests cover:
  - one no-profile path using the normal validated body,
  - one writable-profile path using `WritableRequestBody`, and
  - at least one case proving members absent from `WritableRequestBody` are not read back from the original request body by backend.

## Tasks

1. Adapt the `DMS-1106` request/context contract to the backend-local flattening input contract produced by the re-scoped `DMS-983`.
2. Implement body-source selection at the orchestration boundary: normal validated request body when no writable profile applies, `WritableRequestBody` when one does.
3. Keep profile-specific branching out of the flattener hot loop so the traversal/buffering logic remains reusable for both profiled and non-profiled writes.
4. Add tests validating body-source selection and pass-through semantics without backend-side re-filtering or hidden-member recovery.
