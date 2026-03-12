---
jira: DMS-1053
jira_url: https://edfi.atlassian.net/browse/DMS-1053
---

# Story: Create the ResolveSecurableElementColumnPath Function

## Description

Implement the ResolveSecurableElementColumnPath helper function that, given a subject resource and a securable element, resolves the chain of table joins needed to reach the authorization column in the relational model.

For more information, refer to the design document: `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- When the securableElement type is EducationOrganization or Namespace, the function returns a single-entry collection where targetTable and targetColumnName are null — the column is resolved directly from the root resource table using the Derived Relational Model, with no traversal of intermediate references.
- When the securableElement type is a person (Student, Contact, or Staff) and the person is referenced directly from the subject resource, the function returns a single-entry collection pointing to the person's DocumentId column on the subject table.
- When the securableElement type is a person and the person is referenced transitively (e.g., CourseTranscript -> StudentAcademicRecord -> Student), the function returns an ordered chain of join steps, each entry representing one hop toward the person resource.
- When multiple paths exist for a given securable element (due to key unification), the function selects the shortest path.
- The function uses the canonical column (not an alias column), since canonical columns are indexed.
- The returned sourceColumnName and targetColumnName values match the physical column names produced by the Derived Relational Model for the corresponding schema.

NOTE: The `ResolveSecurableElementColumnPath(subjectResourceFullName, basisResourceFullName)` overload (for the View-based auth strategy) will be implemented in [DMS-1061](https://edfi.atlassian.net/browse/DMS-1061).
