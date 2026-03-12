---
jira: DMS-1064
jira_url: https://edfi.atlassian.net/browse/DMS-1064
---

# Story: Enumerate All DS 5.2 Resources with Multi-hop Person Authorization Join Paths

## Description

Per `reference/design/backend-redesign/design-docs/auth.md`, using the ResolveSecurableElementColumnPath helper function, iterate all resources in Data Standard 5.2 and compute the join path for each Person securable element (Student, Contact, Staff). Output the results for resources where the join path length is greater than one (i.e., the person is referenced transitively through one or more intermediate resources, not directly).

## Acceptance Criteria

- All DS 5.2 resources are processed — The implementation iterates every resource in the projectSchema.resourceSchemas of the DS 5.2 ApiSchema.json and invokes ResolveSecurableElementColumnPath for each Person securable element (Student, Contact, Staff) present in the resource's securableElements.
- Only Person securable elements are evaluated — EducationOrganization and Namespace securable elements are excluded from the output since they are always available directly on the root resource table (join path length is always 1).
- Only multi-hop paths are reported — The output includes only resources where the resolved join path has more than one entry (i.e., the person is reached through at least one intermediate resource).
- Output includes the full join path — For each reported resource, the output contains:
  - The resource name (e.g., CourseTranscript)
  - The person securable element type (e.g., Student)
  - The securable element JSON path (e.g., $.studentAcademicRecordReference.studentUniqueId)
  - The complete ordered join path as returned by ResolveSecurableElementColumnPath
- Known examples are verified — The output is validated against at least the following manually verified cases:
  - CourseTranscript -> Student (path: CourseTranscript.StudentAcademicRecord_DocumentId -> StudentAcademicRecord.Student_DocumentId -> Student.DocumentId)
- Output is sorted so that the resources with the longest paths are shown first
- Output uploaded as .json to this ticket
