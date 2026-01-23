---
jira: DMS-1025
jira_url: https://edfi.atlassian.net/browse/DMS-1025
---

# Story: Descriptor Integration Coverage (Writes, Queries, Seeding)

## Description

Add end-to-end runtime coverage for descriptor behaviors that are foundational for many resources:

- descriptor CRUD (with identity immutability),
- descriptor query filtering/paging,
- descriptor reference resolution for non-descriptor writes, and
- optional provisioning-time descriptor seeding (`--seed-descriptors`) when enabled.

This story complements the DDL generator harness by validating runtime descriptor behavior across dialects.

## Acceptance Criteria

- Integration tests validate:
  - descriptor POST creates a retrievable descriptor resource,
  - descriptor PUT updates non-identity fields and updates `_etag/_lastModifiedDate`,
  - descriptor PUT rejects identity changes (`Namespace`/`CodeValue`),
  - descriptor queries filter correctly on key fields and page deterministically,
  - a descriptor-referencing resource write fails fast when a required descriptor does not exist and succeeds once seeded/created.
- Where both engines are supported in CI, tests run on PostgreSQL and SQL Server with equivalent assertions.

## Tasks

1. Add runtime integration tests for descriptor create/update/query behaviors.
2. Add a “descriptor reference required” test that exercises write-time descriptor resolution and error mapping.
3. When descriptor seeding is enabled, add an integration test that seeds a small `InterchangeDescriptors` input and verifies descriptors become resolvable.
