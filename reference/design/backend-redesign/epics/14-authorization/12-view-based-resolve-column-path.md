---
jira: DMS-1061
jira_url: https://edfi.atlassian.net/browse/DMS-1061
---

# Story: Add Support for View-based Strategy in the ResolveSecurableElementColumnPath Function

## Description

Implement the ResolveSecurableElementColumnPath overload that takes a subject resource name and a basis resource name (the basis resource). This overload is used by the View-based authorization strategy per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- Given a sourceResourceFullName (the resource being authorized, e.g., CourseTranscript) and a basisResourceFullName (the basis resource, e.g., Student), the function returns an ordered collection of join steps, where each step contains sourceTable (schema + name), sourceColumnName, targetTable (schema + name), and targetColumnName.
- The function recursively traverses all references from the subject resource and identifies all paths that reach the basis resource.
- When multiple paths exist, the winning path is selected based on the following priority (in order):
  - Prioritize references that are part of identity.
  - Prioritize required references over optional.
  - Prioritize non-role-named references.
  - Shortest path (fewest joins).
- Non-part-of-identity references are only allowed in the subject resource (the first hop); all intermediate references in the path must be part of identity.
- Join columns use DocumentId (the DMS surrogate key), not natural keys (UniqueId/USI).
- When key unification produces multiple paths, each path is followed and the shortest one is selected.
- Canonical columns are used (not aliases) since canonical columns are indexed.
- When the basis resource is directly referenced by the subject resource, the result contains a single join step (with null basis table/column).
- When the basis resource is transitively referenced (e.g., CourseTranscript -> StudentAcademicRecord -> Student), the result contains multiple join steps in traversal order.
- When no path from the subject resource to the basis resource exists, the function returns an empty result indicating the view-based strategy cannot be applied.
- The function uses ApiSchema.json and the Derived Relational Model to resolve resource references and map them to DB tables and columns.
