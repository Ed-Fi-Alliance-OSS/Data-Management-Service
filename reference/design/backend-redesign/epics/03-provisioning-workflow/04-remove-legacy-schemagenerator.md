# Story: Remove Legacy `EdFi.DataManagementService.SchemaGenerator`

## Description

The relational-primary-store redesign makes the legacy `EdFi.DataManagementService.SchemaGenerator` toolchain obsolete. After the new DDL generator/provisioning workflow is in place, remove the legacy generator and migrate any remaining references to the new tooling described in:

- `reference/design/backend-redesign/overview.md` (“Remove legacy SchemaGenerator”)
- `reference/design/backend-redesign/ddl-generation.md` (DDL generation utility + provisioning semantics)
- `reference/design/backend-redesign/ddl-generator-testing.md` (verification harness)

Authorization objects remain out of scope.

## Acceptance Criteria

- The legacy schema generator projects/scripts are removed from the repository (or clearly deprecated with no remaining callers).
- Build/test/CI workflows do not invoke the legacy generator.
- Documentation references are updated to point to the new DDL generator commands (`dms-schema ddl emit` / `dms-schema ddl provision`) and the new verification harness.
- Any remaining “schema generation” tests are migrated or removed in favor of the new harness fixtures and goldens.

## Tasks

1. Inventory references to the legacy SchemaGenerator in code, scripts, and documentation.
2. Remove the legacy generator projects and related build artifacts.
3. Update scripts and docs to use the new DDL generator CLI and fixture-based harness.
4. Update CI/test pipelines to use the new verification approach (no legacy generator usage).
