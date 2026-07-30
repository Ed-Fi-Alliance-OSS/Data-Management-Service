---
jira: DMS-1311
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Target Selection

## Design References

- **Configuration and projection target selection**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection
- **Projection administration**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration
- **Durable lifecycle**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle
- **Projection health and deployment-owned CDC readiness**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness

The referenced design sections define target selection, validation, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Implement the DMS configuration and target-resolution layer used by the projection
workers, lifecycle administration, cache reads, and health reporting.

## Dependencies

- Configuration and target-resolution scaffolding may proceed alongside 18-00. Integrated
  lifecycle, activation-preflight, enqueue-trigger, and provider-prerequisite validation
  depends on the 18-00 schema.
- Supplies target contexts to E18 stories 18-04 through 18-06 and target observations to
  E19.

## Implementation Scope

- Add strongly typed configuration binding and validation.
- Add target normalization, resolution, refresh, and replacement lifecycle services.
- Validate durable lifecycle/work state and fail closed for configuration/database-state
  mismatch. Removing a target pauses processing without deleting work or disabling
  tracking.
- Define the guarded new-empty `Disabled -> Tracking` and repeatable offline
  activation/deactivation command/result contracts, including their eligibility and
  preflight classifications. The new-empty contract requires a one-time writer-blocking
  `dms.Document` lock, an exclusive state-row lock, clear-latch and empty
  canonical/cache/work checks, and racing-insert safety; 18-04 owns the shared
  administrative-mutex adapter and guarded workflow execution. Runtime target
  configuration never changes lifecycle. Require proof that the projection is
  internal-only and reject the simple toggle for an active or historical downstream
  consumer/CDC binding.
- Replace scan/audit tuning with queue poll, page, concurrency, backoff, seeding
  high-water, and direct-fill settings.
- Validate RCSI and server-level `nested triggers` for SQL Server when initializing or
  replacing a resolved target execution context and before activation from `Disabled`.
  A disabled or unreadable prerequisite fails only projection/cache eligibility, emits
  explicit diagnostics, and never changes the setting. The correction-and-restart
  workflow after an initialization-time failure is supported only when lifecycle is
  `Disabled`. A failure observed in any other lifecycle is outside the supported v1
  contract, which defines neither recovery nor renewed projection-health or CDC-readiness
  guarantees. An activation-preflight failure changes no lifecycle state and can be
  retried after correction. Activation validation is command-local; target-context
  initialization owns process-local validation. V1 does not continuously recheck active
  targets or add a write-side stamp guard; changing either prerequisite after successful
  validation while the target is active, including its effects and recovery, is outside
  the supported v1 contract.
- Add supported appsettings examples that link to the authoritative design.
- Add target-scoped diagnostics needed by health reporting.

## Acceptance Evidence

- Configuration, validation, and command-contract tests cover the states, requests, and
  preflight classifications enumerated by the referenced design sections.
- Provider integration tests cover target prerequisite validation and isolation, including
  initialization failure that remains ineligible for the lifetime of that execution
  context, successful validation by a newly initialized `Disabled` context after
  correction, and unsupported-incident classification for any other lifecycle.
- Preflight and contract tests cover legal/illegal transition requests, `Resetting`
  mismatch, target pause/resume, nonempty activation rejection, and
  active/historical-binding rejection, including SQL Server prerequisite-failure result
  classifications.
- Appsettings and diagnostics are verified against the implementation and defer behavioral
  explanation to the design owner.

## Not Assigned to This Story

- Projector scheduling, cache reads, and health aggregation are assigned to later E18
  stories.
- The administrative-mutex adapter, guarded transition execution, and provider
  concurrency/session-loss tests are assigned to 18-04.
- Durable CDC binding and connector lifecycle are assigned to E19.

## Clarifying Questions and Answers

### Questions 1

1. Does 18-01 own live database preflight evaluation for lifecycle commands, including lifecycle/latch/fingerprint/inventory/nonempty-state observations and rejection classification, or only the request/result DTOs and enums while 18-04 owns all live preflight queries?
2. What exact shape should the required internal-only proof object have before E19 durable CDC binding state exists, and should 18-01 define a downstream-publication-history abstraction that returns internal-only, active, historical, possible, or unknown?
3. Does 18-01 need an active target-resolution refresh loop or timer independent of request traffic, or should it expose registry refresh hooks and leave the supervisor interval implementation to 18-04?
4. Which CMS data-store fields constitute replacement connection metadata that must create a new target-context generation: connection string only, provider/data-store type, route context, tenant membership, or any changed `DataStore` field?
5. Should provider inventory validation in 18-01 assert the exact 18-00 generated object and constraint/index/trigger names, or semantic capability equivalence for the required cache, work, state, identity, and enqueue inventory?

### Answers 1

1. 18-01 should own the command/request/result contracts, result classifications, and pure classifier rules, plus the normal target-context observations it already needs for resolution, inventory, fingerprint, lifecycle/latch, and provider-prerequisite diagnostics. 18-04 should own command-time live preflight queries that must run under the administrative mutex, writer fence, table locks, exclusive state-row lock, or bounded clear/seed workflow, including nonempty canonical/cache/work checks and the final lifecycle/latch rechecks before mutation. 18-04 should call the 18-01 classifiers to produce the contract result.
2. Define a trusted downstream-publication-history abstraction in 18-01 and make internal-only proof the successful observation from that abstraction, not a caller-supplied boolean or operator assertion. The result shape should include target key, observed physical-source fingerprint when available, status (`InternalOnly`, `Active`, `Historical`, `Possible`, `Unknown`), evidence source/generation identifier, observed time, and sanitized diagnostic text. Offline activation/deactivation require status `InternalOnly` for the same normalized target key and the same currently resolved physical-source fingerprint. When the command request supplies an expected physical-source fingerprint, both the current target observation and the downstream-publication-history observation must match that expected value. Every other status, missing fingerprint for a resolved target, or fingerprint mismatch rejects. Until E19 supplies durable binding state, the production default should return `Unknown`, with tests using an explicit fake provider. The story should later phrase the proof as this trusted observation object.
3. 18-01 should not add an independent hosted refresh timer. It should expose an immutable registry snapshot plus explicit refresh hooks for startup, existing CMS refresh notifications, and supervisor-triggered refresh. 18-04 owns the bounded supervisor interval and invokes those hooks for unresolved targets and replacement detection.
4. Create a new target-context generation only when the resolved execution metadata for the same configured target changes: the effective provider token used by the execution context or the connection factory input, including the effective connection string. Do not create a new generation for route-context/display metadata, tenant membership churn, or arbitrary `DataStore` field changes that do not affect the provider or connection used by the execution context. If the configured key no longer resolves, report the target as unresolved rather than treating it as a replacement generation.
5. 18-01 should validate the exact 18-00/data-model physical contract, including stable table, constraint, index, trigger, and PostgreSQL function names, required singleton rows, required columns, key order, FK/cascade behavior, lifecycle constraint, enabled enqueue trigger/function inventory, and provider prerequisites. Do not accept renamed or hand-built "equivalent" inventory as valid. Semantic checks should supplement the exact-name checks so a correctly named but wrong object is rejected.

### Questions 2

1. The story requires a resolved target to be compatible with the process provider and to classify provider mismatch, but the current CMS `DataStoreType` appears to be an operator category rather than `postgresql`/`sqlserver`; should 18-01 infer the target provider solely from the process `AppSettings:Datastore`, or does it need new/read provider metadata before provider-mismatch classification is taskable?
2. For a configured non-default `TenantKey` that is not currently loaded or returned by `IDataStoreProvider.LoadTenants()`, should the 18-01 refresh hook call `LoadDataStores(tenantKey)` directly so an already-configured late-created tenant can resolve, or should retry be limited to tenants already known to the provider cache?
3. Are the lifecycle command/request/result contracts in 18-01 intended to be internal C# classifier models only, or should they be stable JSON-facing administrative DTOs now, including property names, enum string values, and fingerprint serialization tests?

### Answers 2

1. 18-01 should not infer the target provider solely from process `AppSettings:Datastore`, and it should not map CMS `DataStoreType` to a relational provider. Make provider compatibility taskable by adding or reading an explicit normalized relational provider token on resolved data-store metadata, with values `postgresql` or `sqlserver`. A missing or unknown token leaves the target resolved but projection/cache-ineligible with a provider-metadata diagnostic; a token that differs from the process `AppSettings:Datastore` is the provider-mismatch classification. The target execution context and physical-source fingerprint should use the normalized token only after it matches the process provider. Until CMS exposes this metadata, real CMS provider-mismatch integration is not taskable; classifier tests may use fake resolved metadata.
2. The 18-01 refresh hook should call `LoadDataStores(tenantKey)` directly for each configured non-default tenant key, even when that tenant is not returned by `LoadTenants()` and is not already present in the provider cache. `LoadTenants()`, `TenantExists`, and loaded-tenant enumeration are cache/startup aids, not membership gates for explicitly configured DocumentCache targets. A failed or empty tenant-specific load leaves that configured target `Unresolved` with retry diagnostics, and the existing CMS refresh notification or 18-04 supervisor-triggered refresh retries it.
3. Define stable JSON-facing administrative contracts in 18-01 now, without adding an HTTP endpoint or command runner in this story. The shared request/result DTOs should specify property names, enum string values, normalized target-key serialization, opaque fingerprint string serialization, rejection classifications, and `System.Text.Json` tests. Internal classifier models may wrap these DTOs, but the DTOs are the contract consumed by 18-04 administrative execution and later E19/bootstrap command surfaces.
