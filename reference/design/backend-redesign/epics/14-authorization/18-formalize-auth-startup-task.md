---
jira: DMS-1091
jira_url: https://edfi.atlassian.net/browse/DMS-1091
---

# Story: Formalize Auth Startup as IDmsStartupTask

## Description

The backend redesign's startup flow (`new-startup-flow.md`) requires all initialization work to be managed through the DmsStartupOrchestrator using IDmsStartupTask implementations, with deterministic fail-fast behavior for startup failures.

Currently, `WarmUpOidcMetadataCache()` and `RetrieveAndCacheClaimSets()` are standalone methods in Program.cs that run outside the orchestrator. Additionally, `RetrieveAndCacheClaimSets` intentionally swallows errors and lets DMS start without cached claim sets, which violates the redesign's all-or-nothing startup principle.

This ticket formalizes auth startup into the orchestrator and enforces fail-fast per `reference/design/backend-redesign/design-docs/new-startup-flow.md`.

## Acceptance Criteria

- **OIDC metadata warm-up runs as an IDmsStartupTask**
  - A new WarmUpOidcMetadataTask implementing IDmsStartupTask exists with Order in the 400-499 range (after backend mapping initialization at 300).
  - When OIDC metadata retrieval fails, the task throws, causing the orchestrator to abort startup (process exits with non-zero code).
  - The standalone `WarmUpOidcMetadataCache()` method is removed from Program.cs.
- **Claim set caching runs as an IDmsStartupTask**
  - A new CacheClaimSetsTask implementing IDmsStartupTask exists with Order higher than the OIDC task (e.g., 410).
  - Claim set retrieval fails, the task throws, causing the orchestrator to abort startup (process exits with non-zero code). The current behavior of swallowing the exception is removed.
  - Multi-tenant claim set loading (iterating over tenants) is preserved.
  - The standalone `RetrieveAndCacheClaimSets()` method is removed from Program.cs.
- **Startup ordering is preserved**
  - The auth startup tasks run after LoadAndBuildEffectiveSchemaTask (100) and BackendMappingInitializationTask (300), matching the ordering prescribed in `new-startup-flow.md`.
- **Existing tests pass**
  - No behavioral regression in JWT validation, claim set resolution, or authorization middleware.
  - Unit tests for DmsStartupOrchestrator cover the new tasks (task ordering, fail-fast on error).
