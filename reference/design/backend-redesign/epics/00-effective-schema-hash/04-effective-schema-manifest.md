---
jira: DMS-927
jira_url: https://edfi.atlassian.net/browse/DMS-927
---

# Story: Emit `effective-schema.manifest.json`

## Description

Emit a deterministic `effective-schema.manifest.json` artifact for the verification harness (`reference/design/backend-redesign/design-docs/ddl-generator-testing.md`), containing:

- `api_schema_format_version`
- `relational_mapping_version`
- `effective_schema_hash`
- `resource_key_count`
- `resource_key_seed_hash`
- `schema_components[]` (project endpoint/name/version/isExtension)

Optionally include the full `resource_keys[]` list for diagnostics (fixture-controlled).

## Acceptance Criteria

- Manifest file is byte-for-byte stable for the same inputs:
  - stable property ordering,
  - stable array ordering,
  - `\n` line endings only.
- `schema_components[]` are sorted by `ProjectEndpointName` ordinal.
- The manifest values match the outputs of the hash + seed derivation components.
- Small fixture snapshot/golden tests compare `effective-schema.manifest.json` exactly.

## Tasks

1. Define the manifest object model and deterministic JSON writer settings (stable ordering + formatting).
2. Emit required fields per `reference/design/backend-redesign/design-docs/ddl-generator-testing.md`.
3. Add snapshot/golden tests using small fixtures that assert exact manifest output.
4. Add a negative test asserting manifests fail/stop when effective schema inputs are invalid (mismatched versions, etc.).
