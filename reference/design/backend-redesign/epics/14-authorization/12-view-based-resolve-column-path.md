---
jira: DMS-1061
jira_url: https://edfi.atlassian.net/browse/DMS-1061
---

# Story: Add Support for View-based Strategy in the ResolveSecurableElementColumnPath Function

## Description

Implement the ResolveSecurableElementColumnPath overload that takes a source resource name and a target resource name (the basis resource). This overload is used by the View-based authorization strategy per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Given a sourceResourceFullName (the resource being authorized, e.g., CourseTranscript) and a targetResourceFullName (the basis resource, e.g., Student), the function returns an ordered collection of join steps, where each step contains sourceTable (schema + name), sourceColumnName, targetTable (schema + name), and targetColumnName.
- The function recursively traverses all references from the source resource and identifies all paths that reach the target resource.
- When multiple paths exist, the winning path is selected based on the following priority (in order):
  - Prioritize references that are part of identity.
  - Prioritize required references over optional.
  - Prioritize non-role-named references.
  - Shortest path (fewest joins).
- Non-part-of-identity references are only allowed in the source resource (the first hop); all intermediate references in the path must be part of identity.
- Join columns use DocumentId (the DMS surrogate key), not natural keys (UniqueId/USI).
- When key unification produces multiple paths, each path is followed and the shortest one is selected.
- Canonical columns are used (not aliases) since canonical columns are indexed.
- When the target resource is directly referenced by the source resource, the result contains a single join step (with null target table/column).
- When the target resource is transitively referenced (e.g., CourseTranscript -> StudentAcademicRecord -> Student), the result contains multiple join steps in traversal order.
- When no path from the source resource to the target resource exists, the function returns an empty result indicating the view-based strategy cannot be applied.
- The function uses ApiSchema.json and the Derived Relational Model to resolve resource references and map them to DB tables and columns.
