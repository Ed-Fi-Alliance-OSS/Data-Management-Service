---
jira: DMS-1091
jira_url: https://edfi.atlassian.net/browse/DMS-1091
---

# Story: Formalize Auth Startup as IDmsStartupTask

## Description

The backend redesign's startup flow (`new-startup-flow.md`) requires all initialization work to be managed through the DmsStartupOrchestrator using IDmsStartupTask implementations.

Currently, `WarmUpOidcMetadataCache()` and `RetrieveAndCacheClaimSets()` are standalone methods in Program.cs that run outside the orchestrator.

This ticket formalizes auth startup into the orchestrator per `reference/design/backend-redesign/design-docs/new-startup-flow.md`.

## Acceptance Criteria

- **OIDC metadata warm-up runs as an IDmsStartupTask**
  - A new WarmUpOidcMetadataTask implementing IDmsStartupTask exists with Order in the 400-499 range (after backend mapping initialization at 300).
  - When OIDC metadata retrieval fails, the task throws, causing the orchestrator to abort startup (process exits with non-zero code).
  - The standalone `WarmUpOidcMetadataCache()` method is removed from Program.cs.
- **Claim set caching runs as an IDmsStartupTask**
  - A new CacheClaimSetsTask implementing IDmsStartupTask exists with Order higher than the OIDC task (e.g., 410).
  - If the Claim set retrieval fails, the error gets logged. Do not prevent DMS from starting up to avoid impacting the application's availability.
  - Multi-tenant claim set loading (iterating over tenants) is preserved.
  - The standalone `RetrieveAndCacheClaimSets()` method is removed from Program.cs.
- **Startup ordering is preserved**
  - The auth startup tasks run after LoadAndBuildEffectiveSchemaTask (100) and BackendMappingInitializationTask (300), matching the ordering prescribed in `new-startup-flow.md`.
- **Existing tests pass**
  - No behavioral regression in JWT validation, claim set resolution, or authorization middleware.
  - Unit tests for DmsStartupOrchestrator cover the new tasks (task ordering, fail-fast on error).
