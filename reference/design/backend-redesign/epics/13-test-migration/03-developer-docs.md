# Story: Update Developer Docs and Runbooks

## Description

Update developer documentation for the new relational backend workflow:

- provisioning and schema fingerprint validation
- mapping pack build/load (optional)
- debugging write/read paths and derived metadata
- E2E setup/teardown expectations (no hot reload)

## Acceptance Criteria

- Docs clearly explain:
  - how to provision a DB for a given effective schema,
  - how DMS validates schema on first use,
  - how to run relevant tests locally.
- New/updated docs include clickable file paths within the repo.

## Tasks

1. Identify doc locations to update (`docs/`, CLI READMEs, or design folder pointers).
2. Add step-by-step instructions for provisioning and running integration tests.
3. Document mapping pack usage and configuration if packs are enabled.
4. Review docs for accuracy against the implemented workflows.

