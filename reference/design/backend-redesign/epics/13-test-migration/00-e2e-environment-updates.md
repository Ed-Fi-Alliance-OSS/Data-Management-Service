# Story: Update E2E Workflow for Per-Schema Provisioning (No Hot Reload)

## Description

Update the E2E workflow to match the redesign’s schema management model:

- DMS does not hot-reload or swap schemas in-place.
- E2E tests provision separate databases/containers per schema/version under test.

Align with the “Related Changes Implied by This Redesign” section in `reference/design/backend-redesign/overview.md`.

## Acceptance Criteria

- E2E setup scripts provision the database using generated DDL for the target effective schema.
- Switching schemas in tests uses separate DB instances (not in-place schema mutation).
- Teardown scripts remove containers/volumes to avoid inconsistent behavior across runs.

## Tasks

1. Update E2E setup/teardown scripts to provision an empty DB using the new DDL generator outputs.
2. Update test configuration to select the correct connection string per schema under test.
3. Document the updated E2E workflow and troubleshooting steps.

