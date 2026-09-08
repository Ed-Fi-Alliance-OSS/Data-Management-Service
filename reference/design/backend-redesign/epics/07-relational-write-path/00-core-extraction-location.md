---
jira: DMS-981
jira_url: https://edfi.atlassian.net/browse/DMS-981
---

# Story: Core Emits Concrete JSON Locations for Document References

## Description

Implement the only required DMS Core change called out in `reference/design/backend-redesign/design-docs/overview.md` and `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Document reference extraction must include a concrete JSON location including numeric indices (e.g., `$.addresses[2].periods[0].calendarReference`).

This enables efficient write-time FK population for references inside nested collections without per-row JSONPath evaluation.

## Acceptance Criteria

- Each extracted document reference instance includes a concrete JSON path with indices (no `[*]` wildcards).
- Paths are stable/canonical (same input JSON → same path strings).
- Existing descriptor reference extraction behavior remains correct (descriptor references already include paths).
- Core unit tests cover at least:
  - a reference at root,
  - a reference inside a collection,
  - a reference inside nested collections.

## Tasks

1. Extend the core extracted reference model to carry a `Path` (or equivalent) for document references.
2. Populate the path during reference extraction, including correct index emission for arrays.
3. Update any downstream handlers/serializers/tests impacted by the new field.
4. Add unit tests for nested-collection reference path correctness.

