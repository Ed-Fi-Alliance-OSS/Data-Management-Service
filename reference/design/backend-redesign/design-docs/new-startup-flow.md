# Backend Redesign: New Startup Flow (Startup-Time Schema + Mapping Initialization)

## Status

Draft. This document describes an implementation-oriented startup lifecycle that moves:

- ApiSchema loading/validation/merge into **DMS Core startup**, and
- mapping set initialization (runtime compile and/or `.mpack` load) into **application startup**,

so request handling never triggers schema or mapping compilation work.

This is aligned with the backend redesign constraint:
“schema updates are validated, not applied” (no hot reload).

## Goals

1. **Fail fast at startup** when:
   - ApiSchemas cannot be loaded/validated, or
   - any configured database instance is not provisioned for the same effective schema, or
   - mapping packs are missing/invalid (when packs are required), or
   - runtime compilation fails (when compilation is allowed/required).
2. **No per-request schema work**: requests should not lazily load ApiSchemas or build merged/derived schema
   views.
3. **No per-request mapping work**: requests should not load/compile mapping sets or validate
   `dms.ResourceKey`.
4. **Keep ApiSchema ownership in Core**: Core remains the source of truth for loaded ApiSchemas and any merged
   “effective schema” view that downstream components consume.
5. **Backend remains backend-specific**: pack loading/plan compilation remains in backend projects under
   `src/dms/backend/*`, but is triggered by startup orchestration rather than first request.

## Non-goals

- Supporting multiple concurrently-active effective schema versions in a single DMS host process.
- Supporting in-process schema reload/hot-reload for relational backends.
- Defining the full runtime compiler and pack store implementation (covered elsewhere); this document focuses
  on lifecycle and dependencies.

## Current State (Today)

In the current DMS Core request pipeline:

- `ApiSchemaProvider.GetApiSchemaNodes()` performs an **initial lazy load** the first time it is accessed.
- `ProvideApiSchemaMiddleware` performs the core+extension merge and primes `ICompiledSchemaCache` using a
  `VersionedLazy` keyed by `ApiSchemaProvider.ReloadId`.
- Backend repositories are invoked by Core handlers (e.g., `UpsertHandler`) and currently do not
  participate in any mapping pack or runtime compilation lifecycle.

This means the first request can pay:

- schema file/assembly IO + validation,
- schema merge costs,
- JSON schema compilation (validation cache priming),
- (future) mapping pack load/compile and DB fingerprint validation.

## Proposed Startup Lifecycle (High Level)

The new lifecycle splits startup into explicit phases:

1. **Load DMS instances** (already done today) so we know all connection strings to validate.
2. **Load + validate ApiSchemas** in Core (startup-time, one-time).
3. **Build the effective schema view** in Core (startup-time, one-time):
   - apply extension merges,
   - build `ApiSchemaDocuments`,
   - prime `ICompiledSchemaCache` (and any other Core caches derived from schema).
4. **Initialize backend mappings** (startup-time):
   - compute/resolve expected `EffectiveSchemaHash` for the running schema set,
   - load `.mpack` for the configured dialect/version OR compile mapping sets from the loaded ApiSchemas,
   - for each configured DMS instance database:
     - read the database fingerprint (`dms.EffectiveSchema`, `dms.SchemaComponent`),
     - validate `ResourceKeySeedHash/Count` (fast path) and cache `ResourceKeyId` maps,
     - fail fast on mismatch.

After these phases, request processing becomes purely “consume cached schema + cached mapping”.

### Recommended ordering in the ASP.NET host

In `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`, the intended ordering
becomes:

1. `InitializeDmsInstances(app)` (already present)
2. Optional DB deploy (`InitializeDatabase(app)`; already present)
3. **New**: `InitializeApiSchemas(app)` (Core)
4. **New**: `InitializeBackendMappings(app)` (backend-specific)
5. Other warmups (`RetrieveAndCacheClaimSets`, OIDC metadata warmup, etc.)
6. Start request routing

ApiSchema loading can occur before or after DB deploy. Mapping initialization must occur after instances are
loaded and after DBs are provisioned (if provisioning is done on startup).

## Core Changes (ApiSchema as a Startup Contract)

### 1) Eager schema load and validation

Core already has the machinery to load and validate schemas. The only lifecycle change required is ensuring
that `IApiSchemaProvider.GetApiSchemaNodes()` is invoked at startup and treated as fatal if it fails.

Key behavior:

- ApiSchema source remains configurable (filesystem path vs embedded assemblies).
- Validation failures become “startup failures” rather than “first-request failures”.
- `ApiSchemaProvider.ReloadId` becomes stable for the lifetime of the process (unless reload is still enabled
  for non-relational modes).

### 2) Build and cache the merged “effective schema” view once

Today, extension-to-core merge logic lives in `ProvideApiSchemaMiddleware`. Under the new lifecycle, Core
should provide a startup-built singleton that contains:

- merged effective `ApiSchemaDocumentNodes` (core JSON with extension nodes merged),
- `ApiSchemaDocuments` wrapper,
- any derived caches currently primed in middleware (e.g., `ICompiledSchemaCache`).

The middleware then becomes a cheap “attach precomputed documents to `RequestInfo`” step, and the pipeline
no longer performs schema IO or merge work.

Suggested shape (conceptual):

```csharp
public interface IEffectiveApiSchemaProvider
{
  ApiSchemaDocuments Documents { get; }
  Guid ReloadId { get; }
}
```

Implementation can reuse the existing merge code with minimal refactoring (extract merge into a service
callable both at startup and by the middleware, if the middleware remains for compatibility).

### 3) Schema reload/upload behavior

The backend redesign assumes no hot reload. Under this startup-based flow:

- Schema reload endpoints are either disabled for relational backends, or
- reload triggers a full re-initialization (schema + mappings + DB validation) with an atomic swap, or a
  “restart required” response.

This document assumes “restart required” for relational backends; atomic reload is a separate,
higher-complexity design.

## Backend Changes (Startup-Time Mapping Initialization)

### Responsibilities

Backend mapping initialization is responsible for ensuring that, before serving traffic:

1. A mapping set exists for the running effective schema and configured SQL dialect:
   - from `.mpack` load, or
   - from runtime compilation.
2. Every configured database instance is provisioned for the same effective schema (fingerprint validation).
3. The database’s `dms.ResourceKey` mapping is valid for the mapping set, and bidirectional maps are cached.

### Inputs and dependencies

At startup, the backend initializer needs:

- **Effective schema** inputs from Core:
  - merged schema documents (or raw nodes) that the runtime compiler can consume,
  - `EffectiveSchemaHash` (computed deterministically from the same inputs as DDL generation / pack
    generation).
- **Database instances** from Core:
  - list of `DmsInstance` records (possibly per tenant),
  - connection strings.
- **Pack store / compiler** backend implementation:
  - `.mpack` loader (filesystem, embedded resource, or other store),
  - runtime compiler (optional, depending on mode),
  - DB fingerprint reader and `ResourceKey` validator.

### Triggering the backend at startup (without Core→Backend coupling)

Core should not reference backend-specific implementations. The recommended pattern is:

- Define a small “startup task” interface in a shared layer that both Core host and backend can reference
  (likely `Core.External`).
- Backend projects register one or more implementations into DI.
- The ASP.NET host (frontend) invokes a Core-owned “startup orchestrator” which enumerates and runs
  registered tasks in a defined order.

Conceptual interface:

```csharp
public interface IDmsStartupTask
{
  int Order { get; }
  Task ExecuteAsync(CancellationToken ct);
}
```

Example tasks:

- `LoadAndValidateApiSchemaTask` (Core)
- `BuildEffectiveApiSchemaTask` (Core)
- `InitializeRelationalMappingsTask` (backend-specific)

The orchestrator can live in Core so the host only needs one call at startup.

### Modes: pack-only vs compile-enabled

Startup mapping initialization should support at least these modes (configurable):

1. **Pack-only**:
   - `.mpack` is required and runtime compilation is disabled.
   - Startup fails if the pack cannot be found or fails validation.
2. **Compile-only**:
   - runtime compilation is required; packs are ignored.
   - Startup fails if compilation fails.
3. **Pack-preferred with compile fallback**:
   - try `.mpack`; if missing, compile.
   - still validate the loaded/compiled mapping set against the database fingerprint.

The design should make the selected mode visible in logs and health checks.

## End-to-End Startup Sequence (Detailed)

For a single host process serving a single effective schema set:

1. Host loads configuration.
2. Host loads DMS instances (single-tenant or multi-tenant).
3. Core loads ApiSchemas:
   - filesystem or assembly source,
   - validate core + extensions,
   - fail startup on invalid schemas.
4. Core builds effective schema view:
   - merge extension nodes into core as required,
   - create `ApiSchemaDocuments`,
   - prime `ICompiledSchemaCache` (and any other schema-derived caches).
5. Core computes (or retrieves from the effective schema builder) the deterministic:
   - `EffectiveSchemaHash`,
   - `ResourceKey` seed list and `ResourceKeySeedHash/Count` (if using the relational redesign contract).
6. Backend mapping initialization:
   - resolve mapping pack key: `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`,
   - load `.mpack` or compile mapping set,
   - validate pack payload invariants (including recompute seed hash from embedded seed list),
   - for each configured `DmsInstance`:
     - connect and read `dms.EffectiveSchema` (and `dms.SchemaComponent` if required),
     - validate `EffectiveSchemaHash` matches,
     - validate `ResourceKeySeedHash/Count` fast path (and diff `dms.ResourceKey` only on mismatch),
     - cache `QualifiedResourceName ↔ ResourceKeyId` maps,
     - (optional) validate dialect compatibility.
7. Startup completes; host begins serving traffic.

## Failure Modes and Policy

The startup-based flow forces explicit decisions about “what is fatal”.

- **Fatal**:
  - ApiSchema load/validation failure,
  - mapping pack load failure when pack-only mode is enabled,
  - mapping compilation failure when compile is required,
  - any configured database instance fails startup validation (connectivity failure, wrong
    `EffectiveSchemaHash`, missing `dms.EffectiveSchema`, invalid `ResourceKeySeedHash/Count`).

Startup validation is all-or-nothing: if any database instance fails validation, the service startup fails.
Logs must identify each failing instance (tenant, instance id/name) and the specific validation failure.

### Container-oriented “fail fast”

For the startup tasks defined in this document, “fail fast” should not mean “return 500 forever”.
It should mean “do not become ready and do not accept traffic”.

Recommended behavior for deterministic startup failures
(invalid ApiSchema, missing/invalid mapping pack, schema fingerprint mismatch):

- Emit a `Critical` log with a stable error code and a clear remediation hint.
- Terminate the process with a non-zero exit code without serving requests.

If the implementation uses a background initializer that can overlap with the server listening socket, add a
request gate:

- readiness remains failing until initialization completes successfully, and
- requests received while not ready return `503 Service Unavailable` (not 500).

## Multi-tenancy Considerations

This design assumes:

- a single DMS host process is configured with one ApiSchema set (core + extensions),
- all tenants and all instances served by that process are provisioned for that same effective schema.

If different tenants require different ApiSchema sets, the design must be extended to support multiple schema
providers and per-request selection based on resolved instance/tenant. That is out of scope for this startup
flow.

## Observability

Startup should emit structured logs and metrics for:

- schema load duration and failures,
- effective schema hash and mapping version,
- mapping initialization mode (pack-only vs compile),
- pack load/validation duration,
- per-instance DB fingerprint validation results:
  - instance id/name,
  - tenant (if applicable),
  - effective hash observed vs expected,
  - seed hash/count observed vs expected,
  - number of resource keys cached.

Health checks should reflect readiness:
“schemas loaded + mappings initialized + all required instances validated”.

## Implementation Plan

Implement the full startup-time flow in one change set:

1. Add a startup orchestrator that runs ordered startup tasks (Core-owned orchestration; backend registers
   tasks).
2. Move ApiSchema load/validation to startup and fail fast on any load/validation failure.
3. Build and cache the merged effective schema view at startup and update request middleware to consume it.
4. Stub for computing `EffectiveSchemaHash` and seed hash/count at
   startup.
5. Stub for initialize mapping sets at startup (pack load and/or runtime compile) for the configured
   dialect/version.
6. Stub to validate every configured database instance at startup (fingerprint + `ResourceKeySeedHash/Count`)
   and
   cache `ResourceKeyId` maps.
7. Remove request-time schema/mapping initialization and disable schema reload for relational backends.
