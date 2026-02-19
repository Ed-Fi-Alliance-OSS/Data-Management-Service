---
title: Backend Redesign Jira Index
---

# Backend Redesign: Jira Index

This index links design documents under `reference/design/backend-redesign/epics/` to their Jira issues.

## Epics

- `DMS-922` — Effective Schema Fingerprinting & `dms.ResourceKey` Seeding — `reference/design/backend-redesign/epics/00-effective-schema-hash/EPIC.md`
  - `DMS-923` — Load and Normalize `ApiSchema.json` Inputs — `reference/design/backend-redesign/epics/00-effective-schema-hash/00-schema-loader.md`
  - `DMS-924` — Deterministic Canonical JSON Serialization — `reference/design/backend-redesign/epics/00-effective-schema-hash/01-canonical-json.md`
  - `DMS-925` — Compute `EffectiveSchemaHash` — `reference/design/backend-redesign/epics/00-effective-schema-hash/02-effective-schema-hash.md`
  - `DMS-926` — Deterministic `dms.ResourceKey` Seed Mapping + Seed Hash — `reference/design/backend-redesign/epics/00-effective-schema-hash/03-resourcekey-seed.md`
  - `DMS-927` — Emit `effective-schema.manifest.json` — `reference/design/backend-redesign/epics/00-effective-schema-hash/04-effective-schema-manifest.md`
  - `DMS-947` — Startup-Time ApiSchema + Mapping Initialization — `reference/design/backend-redesign/epics/00-effective-schema-hash/05-startup-schema-and-mapping-init.md`

- `DMS-928` — Derived Relational Model — `reference/design/backend-redesign/epics/01-relational-model/EPIC.md`
  - `DMS-929` — Derive Base Tables/Columns from JSON Schema — `reference/design/backend-redesign/epics/01-relational-model/00-base-schema-traversal.md`
  - `DMS-930` — Bind References/Descriptors + Derive Constraints — `reference/design/backend-redesign/epics/01-relational-model/01-reference-and-constraints.md`
  - `DMS-931` — Apply Naming Rules + `relational.nameOverrides` — `reference/design/backend-redesign/epics/01-relational-model/02-naming-and-overrides.md`
  - `DMS-932` — Model `_ext` Extension Tables — `reference/design/backend-redesign/epics/01-relational-model/03-ext-mapping.md`
  - `DMS-933` — Derive Abstract Identity Table + Union View Models — `reference/design/backend-redesign/epics/01-relational-model/04-abstract-union-views.md`
  - `DMS-934` — Emit `relational-model.manifest.json` — `reference/design/backend-redesign/epics/01-relational-model/05-relational-model-manifest.md`
  - `DMS-942` — Map Descriptor Resources to `dms.Descriptor` (No Per-Descriptor Tables) — `reference/design/backend-redesign/epics/01-relational-model/06-descriptor-resource-mapping.md`
  - `DMS-945` — Derive Index + Trigger Inventory (DDL Intent) — `reference/design/backend-redesign/epics/01-relational-model/07-index-and-trigger-inventory.md`
  - `DMS-1033` — Build `DerivedRelationalModelSet` from the Effective Schema Set — `reference/design/backend-redesign/epics/01-relational-model/08-derived-relational-model-set-builder.md`

- `DMS-935` — DDL Emission (PostgreSQL + SQL Server) — `reference/design/backend-redesign/epics/02-ddl-emission/EPIC.md`
  - `DMS-936` — SQL Dialect Abstraction + Writer — `reference/design/backend-redesign/epics/02-ddl-emission/00-dialect-abstraction.md`
  - `DMS-937` — Emit Core `dms.*` DDL (Including Update-Tracking Triggers) — `reference/design/backend-redesign/epics/02-ddl-emission/01-core-dms-ddl.md`
  - `DMS-938` — Emit Project Schemas + Resource/Extension Tables + Abstract Identity Tables/Views — `reference/design/backend-redesign/epics/02-ddl-emission/02-project-and-resource-ddl.md`
  - `DMS-939` — Emit Seed + Fingerprint Recording SQL (Insert-if-Missing + Validate) — `reference/design/backend-redesign/epics/02-ddl-emission/03-seed-and-fingerprint-ddl.md`
  - `DMS-940` — SQL Canonicalization + Deterministic Ordering for DDL — `reference/design/backend-redesign/epics/02-ddl-emission/04-sql-canonicalization.md`
  - `DMS-943` — Emit ODS-Parity `dms.Descriptor` DDL (Descriptor Resources Stored in `dms`) — `reference/design/backend-redesign/epics/02-ddl-emission/05-descriptor-ddl.md`
  - `DMS-946` — Engine UUIDv5 Helper Function (PostgreSQL + SQL Server) — `reference/design/backend-redesign/epics/02-ddl-emission/06-uuidv5-function.md`

- `DMS-948` — Provisioning Workflow (Create-Only) — `reference/design/backend-redesign/epics/03-provisioning-workflow/EPIC.md`
  - `DMS-950` — CLI Command — `ddl emit` — `reference/design/backend-redesign/epics/03-provisioning-workflow/00-ddl-emit-command.md`
  - `DMS-951` — CLI Command — `ddl provision` (Create-Only) — `reference/design/backend-redesign/epics/03-provisioning-workflow/01-ddl-provision-command.md`
  - `DMS-952` — Provision Preflight + Idempotency + Diagnostics — `reference/design/backend-redesign/epics/03-provisioning-workflow/02-preflight-and-idempotency.md`
  - `DMS-953` — Emit `ddl.manifest.json` (Deterministic DDL Summary) — `reference/design/backend-redesign/epics/03-provisioning-workflow/03-ddl-manifest.md`
  - `DMS-954` — Remove Legacy `EdFi.DataManagementService.SchemaGenerator` — `reference/design/backend-redesign/epics/03-provisioning-workflow/04-remove-legacy-schemagenerator.md`
  - `DMS-955` — Optional Descriptor Seeding During Provisioning (`ddl provision --seed-descriptors`) — `reference/design/backend-redesign/epics/03-provisioning-workflow/05-seed-descriptors.md`

- `DMS-956` — Verification Harness (Determinism + DB-Apply) — `reference/design/backend-redesign/epics/04-verification-harness/EPIC.md`
  - `DMS-957` — Fixture Layout + Runner (`expected/` vs `actual/`) — `reference/design/backend-redesign/epics/04-verification-harness/00-fixture-runner.md`
  - `DMS-958` — Unit/Contract Tests (Determinism + Fail-Fast Rules) — `reference/design/backend-redesign/epics/04-verification-harness/01-contract-tests.md`
  - `DMS-959` — Snapshot Tests for Small Fixtures (No DB) — `reference/design/backend-redesign/epics/04-verification-harness/02-snapshot-tests.md`
  - `DMS-960` — Authoritative Golden Directory Comparisons (No DB) — `reference/design/backend-redesign/epics/04-verification-harness/03-authoritative-goldens.md`
  - `DMS-961` — DB-Apply Smoke Tests (Docker Compose; PGSQL + MSSQL) — `reference/design/backend-redesign/epics/04-verification-harness/04-db-apply-smoke.md`
  - `DMS-962` — Runtime Compatibility Gate Tests (Mapping Set ↔ DB Validation) — `reference/design/backend-redesign/epics/04-verification-harness/05-runtime-compatibility-gate.md`

- `DMS-963` — Mapping Pack (`.mpack`) Generation and Consumption (Optional AOT Mode) — `reference/design/backend-redesign/epics/05-mpack-generation/EPIC.md`
  - `DMS-964` — Protobuf Contracts for PackFormatVersion=1 — `reference/design/backend-redesign/epics/05-mpack-generation/00-protobuf-contracts.md`
  - `DMS-965` — Payload Object Graph + Deterministic Ordering Rules — `reference/design/backend-redesign/epics/05-mpack-generation/01-pack-payload-shape.md`
  - `DMS-966` — CLI Command — `pack build` Emits `.mpack` — `reference/design/backend-redesign/epics/05-mpack-generation/03-pack-build-cli.md`
  - `DMS-967` — Emit `pack.manifest.json` and `mappingset.manifest.json` — `reference/design/backend-redesign/epics/05-mpack-generation/04-pack-manifests.md`
  - `DMS-968` — Pack Loader/Validator + Mapping Set Selection — `reference/design/backend-redesign/epics/05-mpack-generation/05-pack-loader-validation.md`
  - `DMS-969` — Pack ↔ Runtime Compilation Equivalence Tests — `reference/design/backend-redesign/epics/05-mpack-generation/06-pack-equivalence-tests.md`
  - `DMS-970` — CLI Command — `pack manifest` (Inspect/Validate Existing `.mpack`) — `reference/design/backend-redesign/epics/05-mpack-generation/07-pack-manifest-command.md`

- `DMS-974` — Runtime Schema Validation & Mapping Set Selection — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/EPIC.md`
  - `DMS-975` — Read and Cache DB Fingerprint (`dms.EffectiveSchema`) — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/00-read-effective-schema.md`
  - `DMS-976` — Validate `dms.ResourceKey` Seed Mapping (Fast + Slow Path) — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/01-resourcekey-validation.md`
  - `DMS-977` — Select Mapping Set by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/02-mapping-set-selection.md`
  - `DMS-978` — Configuration + Fail-Fast Behaviors for Schema/Pack Selection — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/03-config-and-failure-modes.md`
  - `DMS-979` — Remove In-Process Schema Reload / Hot Reload — `reference/design/backend-redesign/epics/06-runtime-mapping-selection/04-remove-hot-reload.md`

- `DMS-980` — Relational Write Path (POST/PUT) — `reference/design/backend-redesign/epics/07-relational-write-path/EPIC.md`
  - `DMS-981` — Core Emits Concrete JSON Locations for Document References — `reference/design/backend-redesign/epics/07-relational-write-path/00-core-extraction-location.md`
  - `DMS-982` — Bulk Reference and Descriptor Resolution (Write-Time Validation) — `reference/design/backend-redesign/epics/07-relational-write-path/01-reference-and-descriptor-resolution.md`
  - `DMS-983` — Flatten JSON into Relational Row Buffers (Root + Children + `_ext`) — `reference/design/backend-redesign/epics/07-relational-write-path/02-flattening-executor.md`
  - `DMS-984` — Persist Row Buffers with Replace Semantics (Batching, Limits, Transactions) — `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`
  - `DMS-985` — Populate Propagated Reference Identity Columns (No Edge Table) — `reference/design/backend-redesign/epics/07-relational-write-path/04-propagated-reference-identity-columns.md`
  - `DMS-986` — Map DB Constraint Errors to DMS Write Error Shapes — `reference/design/backend-redesign/epics/07-relational-write-path/05-write-error-mapping.md`
  - `DMS-987` — Descriptor POST/PUT Writes Maintain `dms.Descriptor` (No Per-Descriptor Tables) — `reference/design/backend-redesign/epics/07-relational-write-path/06-descriptor-writes.md`

- `DMS-988` — Relational Read Path (GET + Query) — `reference/design/backend-redesign/epics/08-relational-read-path/EPIC.md`
  - `DMS-989` — Hydrate Relational Rows Using Multi-Result Queries — `reference/design/backend-redesign/epics/08-relational-read-path/00-hydrate-multiresult.md`
  - `DMS-990` — Reconstitute JSON from Hydrated Rows (Including `_ext`) — `reference/design/backend-redesign/epics/08-relational-read-path/01-json-reconstitution.md`
  - `DMS-991` — Reconstitute Reference Identity Values from Local Propagated Columns — `reference/design/backend-redesign/epics/08-relational-read-path/02-reference-identity-projection.md`
  - `DMS-992` — Project Descriptor URIs from `dms.Descriptor` — `reference/design/backend-redesign/epics/08-relational-read-path/03-descriptor-projection.md`
  - `DMS-993` — Execute Root-Table Queries with Deterministic Paging — `reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md`
  - `DMS-994` — Serve Descriptor GET/Query Endpoints from `dms.Descriptor` (No Per-Descriptor Tables) — `reference/design/backend-redesign/epics/08-relational-read-path/05-descriptor-endpoints.md`

- `DMS-995` — Strict Identity Maintenance & Concurrency — `reference/design/backend-redesign/epics/09-identity-concurrency/EPIC.md`
  - `DMS-996` — Implement Deadlock Retry Policy for Cascade/Trigger Writes — `reference/design/backend-redesign/epics/09-identity-concurrency/00-locking-and-retry.md`
  - `DMS-997` — Maintain `dms.ReferentialIdentity` (Primary + Superclass Alias Rows) — `reference/design/backend-redesign/epics/09-identity-concurrency/01-referentialidentity-maintenance.md`
  - `DMS-998` — Detect Identity Projection Changes Reliably — `reference/design/backend-redesign/epics/09-identity-concurrency/02-identity-change-detection.md`
  - `DMS-999` — Identity Propagation via Cascades/Triggers (No Closure Traversal) — `reference/design/backend-redesign/epics/09-identity-concurrency/03-identity-propagation.md`
  - `DMS-1000` — Invalidate Identity Resolution Caches After Commit — `reference/design/backend-redesign/epics/09-identity-concurrency/04-cache-invalidation.md`

- `DMS-1001` — Update Tracking (`_etag/_lastModifiedDate`) + Change Queries (`ChangeVersion`) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/EPIC.md`
  - `DMS-1002` — Emit Stamping Triggers for `dms.Document` (Content + Identity Stamps) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/00-token-stamping.md`
  - `DMS-1003` — Journaling Contract (Triggers Own Journal Writes) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/01-journaling-contract.md`
  - `DMS-1004` — Serve `_etag`, `_lastModifiedDate`, and `ChangeVersion` from Stored Stamps — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/02-derived-metadata.md`
  - `DMS-1005` — Enforce `If-Match` Using Stored Representation Stamps — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/03-if-match.md`
  - `DMS-1006` — Change Query Candidate Selection (Journal-Driven) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/04-change-query-selection.md`
  - `DMS-1007` — Change Query API Endpoints (Optional / Future-Facing) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/05-change-query-api.md`
  - `DMS-1008` — Ensure Descriptor Writes Stamp and Journal Correctly (`dms.Descriptor`) — `reference/design/backend-redesign/epics/10-update-tracking-change-queries/06-descriptor-stamping.md`

- `DMS-1009` — Delete Path & Conflict Diagnostics — `reference/design/backend-redesign/epics/11-delete-path/EPIC.md`
  - `DMS-1010` — Implement Delete-by-Id for Relational Store — `reference/design/backend-redesign/epics/11-delete-path/00-delete-by-id.md`
  - `DMS-1011` — Map FK Violations to Delete Conflict Responses — `reference/design/backend-redesign/epics/11-delete-path/01-conflict-mapping.md`
  - `DMS-1012` — Provide “Who References Me?” Diagnostics Without a Reverse-Edge Table — `reference/design/backend-redesign/epics/11-delete-path/02-referencing-diagnostics.md`
  - `DMS-1013` — Delete Path Tests (pgsql + mssql) — `reference/design/backend-redesign/epics/11-delete-path/03-delete-tests.md`

- `DMS-1014` — Operational Guardrails, Repair Tools, and Observability — `reference/design/backend-redesign/epics/12-ops-guardrails/EPIC.md`
  - `DMS-1015` — Audit/Repair Tool for `dms.ReferentialIdentity` — `reference/design/backend-redesign/epics/12-ops-guardrails/00-referentialidentity-audit-repair.md`
  - `DMS-1016` — Sampling-Based Integrity Watchdog (ReferentialIdentity + Journals) — `reference/design/backend-redesign/epics/12-ops-guardrails/01-referentialidentity-watchdog.md`
  - `DMS-1017` — Instrumentation for Cascades, Stamps/Journals, and Retries — `reference/design/backend-redesign/epics/12-ops-guardrails/02-instrumentation.md`
  - `DMS-1018` — Guardrails for Identity-Update Fan-out and Retry Behavior — `reference/design/backend-redesign/epics/12-ops-guardrails/03-guardrails.md`
  - `DMS-1019` — Benchmark Harness for Read/Write Hot Paths — `reference/design/backend-redesign/epics/12-ops-guardrails/04-performance-benchmarks.md`

- `DMS-1020` — Test Strategy & Migration (Runtime + E2E) — `reference/design/backend-redesign/epics/13-test-migration/EPIC.md`
  - `DMS-1021` — Update E2E Workflow for Per-Schema Provisioning (No Hot Reload) — `reference/design/backend-redesign/epics/13-test-migration/00-e2e-environment-updates.md`
  - `DMS-1022` — Runtime Integration Tests for Relational Backend (CRUD + Query) — `reference/design/backend-redesign/epics/13-test-migration/01-backend-integration-tests.md`
  - `DMS-1023` — Cross-Engine Parity Tests and Shared Fixtures — `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md`
  - `DMS-1024` — Update Developer Docs and Runbooks — `reference/design/backend-redesign/epics/13-test-migration/03-developer-docs.md`
  - `DMS-1025` — Descriptor Integration Coverage (Writes, Queries, Seeding) — `reference/design/backend-redesign/epics/13-test-migration/04-descriptor-tests.md`

- `DMS-1029` — Authorization Design Spike (Relational Primary Store) — `reference/design/backend-redesign/epics/14-authorization/EPIC.md`
  - `DMS-1026` — Authorization Design Spike (Relational Primary Store) — `reference/design/backend-redesign/epics/14-authorization/00-auth-placeholder.md`

- `DMS-1027` — Runtime Plan Compilation + Caching (Shared with AOT Packs) — `reference/design/backend-redesign/epics/15-plan-compilation/EPIC.md`
  - `TBD` — Plan SQL Foundations (Shared Canonical Writer + Dialect Helpers) — `reference/design/backend-redesign/epics/15-plan-compilation/01-plan-sql-foundations.md`
  - `TBD` — Plan Contracts + Deterministic Bindings (Parameter Naming, Ordering, Metadata) — `reference/design/backend-redesign/epics/15-plan-compilation/02-plan-contracts-and-deterministic-bindings.md`
  - `DMS-1028` — Thin Slice — Runtime Plan Compilation + Caching (Root-Only) — `reference/design/backend-redesign/epics/15-plan-compilation/03-thin-slice-runtime-plan-compilation-and-cache.md`
  - `TBD` — Compile Write Plans for Child/Extension Tables (Replace Semantics + Batching) — `reference/design/backend-redesign/epics/15-plan-compilation/04-write-plan-compiler-collections-and-extensions.md`
  - `TBD` — Compile Hydration Read Plans (`SelectByKeysetSql`) for All Tables — `reference/design/backend-redesign/epics/15-plan-compilation/05-read-plan-compiler-hydration.md`
  - `TBD` — Compile Projection Plans (Descriptor URI + Identity Projection) — `reference/design/backend-redesign/epics/15-plan-compilation/06-projection-plan-compilers.md`
