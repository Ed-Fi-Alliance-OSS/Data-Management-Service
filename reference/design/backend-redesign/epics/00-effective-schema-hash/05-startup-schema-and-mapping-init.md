---
jira: DMS-947
jira_url: https://edfi.atlassian.net/browse/DMS-947
---

# Story: Implement New Startup Flow (Phase 1: Eager Core Schema Initialization + Stubs)

## Description

Implement the first, unblocked slice of `reference/design/backend-redesign/design-docs/new-startup-flow.md`
as a refactoring of the existing DMS codebase:

- Move ApiSchema loading + extension merge + schema cache priming out of request handling and into
  application startup.
- Keep the service behaving the same after the refactor (all existing endpoints and tests continue to
  work).
- Create explicit, named code locations (interfaces + DI registrations) for future work to plug in:
  - deterministic normalization of ApiSchema.json inputs,
  - `EffectiveSchemaHash` computation,
  - `dms.ResourceKey` seed derivation and validation,
  - backend mapping initialization (pack loading and/or runtime compilation).
- Remove ApiSchema upload/reload behavior, since schema becomes a startup-only contract and in-process schema
  changes would invalidate the “no per-request schema work” and “validated, not applied” constraints.

This story is intentionally “Phase 1”: it establishes the startup lifecycle and stubs without requiring any
relational-backend redesign work to be complete.

## Scope

### Required refactor (behavior-preserving)

- Introduce a host-invoked startup orchestrator that runs ordered startup tasks before routing is enabled
  (see `new-startup-flow.md` “Recommended ordering in the ASP.NET host”).
- Move the effective schema construction currently performed in
  `EdFi.DataManagementService.Core.Middleware.ProvideApiSchemaMiddleware` into a Core startup-built
  singleton service (e.g., `IEffectiveApiSchemaProvider`).
- Update `ProvideApiSchemaMiddleware` to attach precomputed schema artifacts to `RequestInfo`

### Remove upload/reload (intentional behavior change)

- Remove ApiSchema upload/reload endpoints and any runtime schema mutation paths.

### Explicit stubs (no behavior change yet)

Add stub services and wire them into the startup context (registered in DI, invoked by the orchestrator,
but implemented as no-ops and not required by request handling yet):

- `IApiSchemaInputNormalizer` (no-op): placeholder for deterministic ApiSchema.json normalization rules
  described in `data-model.md`.
- `IEffectiveSchemaHashProvider` (stub): placeholder for deterministic `EffectiveSchemaHash` computation.
- `IResourceKeySeedProvider` (stub): placeholder for producing the deterministic resource key seed list and
  seed hash/count.
- `IBackendMappingInitializer` (no-op): placeholder for backend mapping pack loading/runtime compilation and
  per-instance validation.

## Acceptance Criteria

- Existing DMS behavior remains unchanged:
  - existing API routes continue to work,
  - unit/integration tests continue to pass.
- ApiSchema upload/reload is removed/disabled:
  - schema upload endpoints are no longer exposed,
  - schema reload/mutation cannot occur in-process,
  - any previous upload/reload configuration is removed.
- On application startup (in the ASP.NET host), Core eagerly:
  - loads ApiSchemas from the configured source (filesystem/assemblies),
  - validates schemas and fails startup on validation failure,
  - builds a merged effective schema view (core + extensions) once,
  - primes `ICompiledSchemaCache` (and any other currently middleware-primed caches) once.
- During request handling:
  - no schema file/assembly IO occurs,
  - no extension merge work occurs,
  - `ProvideApiSchemaMiddleware` only attaches the cached schema artifacts to `RequestInfo`.
- The stubs exist and are discoverable in code:
  - each stub has a stable interface name and DI registration location,
  - each stub is invoked by the startup orchestrator (so future work has a clear insertion point),
  - each stub is implemented as a no-op that cannot break existing runtime behavior.
- Startup ordering is explicit and tested (unit tests):
  - orchestrator runs tasks in deterministic order,
  - failures in schema load/validation abort startup.

## Tasks

1. Add a Core-owned startup orchestration API invoked by the host (e.g., `IDmsStartupTask` + ordered
   execution + `RunAsync()` entry point).
2. Extract the effective schema merge logic from `ProvideApiSchemaMiddleware` into a reusable Core service
   and build it at startup:
   - eager call to `IApiSchemaProvider.GetApiSchemaNodes()`,
   - clone/merge extension nodes into core deterministically (reuse existing logic),
   - construct `ApiSchemaDocuments`,
   - prime `ICompiledSchemaCache`.
3. Refactor `ProvideApiSchemaMiddleware` to use the startup-built provider, with a compatibility fallback
   to the current lazy path if needed.
4. Remove/disable ApiSchema upload/reload:
   - remove the upload/reload endpoints and any associated services that mutate in-process schema state,
   - ensure the removal is covered by tests (e.g., route not found / explicit “not supported” response),
   - update documentation/configuration accordingly.
5. Add the stub interfaces + no-op implementations for:
   - ApiSchema input normalization,
   - `EffectiveSchemaHash` computation,
   - resource key seed derivation,
   - backend mapping initialization.
6. Add/adjust unit tests to prove startup orchestration and that request handling does not re-run schema
   merge work when startup has completed.
