---
jira: DMS-978
jira_url: https://edfi.atlassian.net/browse/DMS-978
---

# Story: Configuration + Fail-Fast Behaviors for Schema/Pack Selection

## Description

Provide clear configuration and consistent runtime behaviors for:

- enabling/disabling mapping packs,
- requiring packs vs allowing runtime compile fallback,
- mapping set cache sizing/eviction policy,
- and error reporting when selection/validation fails.

Align with `reference/design/backend-redesign/design-docs/aot-compilation.md` and `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`.

## Acceptance Criteria

- Configuration supports (at minimum):
  - mapping pack enable/required toggles,
  - pack root path,
  - allow runtime compile fallback,
  - cache mode/capacity.
- Failures include actionable diagnostics:
  - DB `EffectiveSchemaHash`,
  - expected mapping version/dialect,
  - and whether a pack was required/missing/invalid.
- A schema mismatch for one database does not prevent DMS from serving other databases (multi-instance-safe failure mode).

## Tasks

1. Add strongly-typed configuration objects and bind them to appsettings/environment variables.
2. Standardize error/exception types for:
   - missing provisioning,
   - no mapping set available,
   - pack validation failures,
   - resource key mismatches.
3. Add structured logging for the selection and validation flow.
4. Add unit tests validating configuration combinations and resulting behaviors.

