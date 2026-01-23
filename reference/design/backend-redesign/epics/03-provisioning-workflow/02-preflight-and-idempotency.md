---
jira: DMS-952
jira_url: https://edfi.atlassian.net/browse/DMS-952
---

# Story: Provision Preflight + Idempotency + Diagnostics

## Description

Implement the provisioning guardrails that make the workflow operationally safe:

- Preflight check: fail fast when provisioning into a DB already provisioned for a different `EffectiveSchemaHash`.
- Seed validation: fail fast when `dms.ResourceKey`/`dms.SchemaComponent` contents do not match expected.
- Rerun safety: running the same provisioning DDL twice must succeed and produce no semantic drift.
- Actionable diagnostics on mismatch (expected vs actual).

## Acceptance Criteria

- Preflight mismatch produces a clear error message containing both:
  - expected `EffectiveSchemaHash`
  - actual DB `EffectiveSchemaHash`
- Re-running provisioning for the same schema succeeds (idempotent behavior) on both dialects.
- If `dms.ResourceKey` is tampered after provisioning, a subsequent validation/provision attempt fails fast with a useful diff report.

## Tasks

1. Implement dialect-specific preflight queries to detect existing `dms.EffectiveSchema` and compare hash.
2. Implement (or emit) deterministic “validate exact contents” SQL blocks for `dms.ResourceKey` and `dms.SchemaComponent`.
3. Add integration coverage that:
   1. provisions a DB for hash A,
   2. attempts provisioning for hash B and asserts failure,
   3. tampers `dms.ResourceKey` and asserts validation failure.
4. Ensure logs and exit codes are usable for CI/CD pipelines.
