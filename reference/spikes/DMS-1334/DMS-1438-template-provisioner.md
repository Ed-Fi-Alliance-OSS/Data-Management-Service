# DMS-1438: Add a runtime-safe CMS DMS-template provisioner

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Build the CMS-owned runtime provisioner that managed lifecycle jobs use to create and delete DMS target databases from trusted Minimal/Sample template artifacts.

- Endpoint: none; this is a backend service consumed by DMS-1439.
- Contracts: provider-neutral create/delete API in CMS backend, with PostgreSQL and SQL Server adapters in the existing CMS provider projects.
- Template behavior: `Minimal` resolves to the DMS Minimal package and `Sample` resolves to the DMS Populated package for the configured target provider and supported effective schema.
- Safety: artifact authentication, protected-name denial, expected `dms.DataStoreIdentity.SourceIdentity` ownership checks, timeout/cancellation, and secret-safe diagnostics.
- Blocker: trusted package execution waits for DMS-1270/DMS-1271 to deliver the pinned artifact manifest, producer-trust, content-profile, and compatibility contract.

### Description

DMS publishes Minimal and Populated database-template packages and has operator-oriented restore tooling, but CMS cannot safely invoke those PowerShell/Docker helpers inside an HTTP-triggered job. `ddl provision` creates an empty schema and does not implement the v3 template behavior.

This story delivers a CMS-owned runtime service that creates or deletes one managed PostgreSQL or SQL Server data store from a trusted, deployment-allowlisted DMS template.

**Scope**

- Define a provider-neutral create/delete contract in the existing CMS backend project, with implementations in the existing CMS PostgreSQL and SQL Server provider projects.
- Resolve v3 `Minimal` to a DMS Minimal package and v3 `Sample` to the equivalent DMS Populated package.
- Reuse the package identity, manifest, artifact hash, content-profile, schema-compatibility, and producer-trust contract coordinated with DMS-1271.
- Implement PostgreSQL SQL-dump and SQL Server backup restoration in the corresponding CMS provider projects.
- Add a target DMS data-store provider setting (working name `DmsDataStoreSettings.Provider`, values `postgresql` or `mssql`) independent of the provider used by the CMS catalog database.
- Implement administrative connection handling, safe identifier generation/quoting, reserved-name and protected-database denial, timeouts, cancellation, and secret-safe diagnostics.
- Use a caller-supplied expected `dms.DataStoreIdentity.SourceIdentity` to prove ownership for reconciliation and deletion.

**API surface**

No public endpoint is delivered by this story. The consuming request values are the v3 `databaseTemplate` values `Minimal` and `Sample`.

**Architecture and boundaries**

CMS owns this service because provisioning is invoked by the Management API lifecycle and is a control-plane responsibility. DMS owns the Resources, Descriptors, and Discovery APIs, template production, and the versioned artifact/database contracts consumed by the service; it does not own a Management API runtime component.

The request never supplies an artifact path, feed, package ID, database server, or administrative connection string. Those are resolved from operator configuration. The implementation stays within the current CMS project graph and must not reference DMS application projects or shell out to `pwsh`, Docker, `ddl provision`, or a repository script. DMS artifacts, manifests, `dms.DataStoreIdentity`, and `dms.EffectiveSchema` are consumed as versioned data contracts.

The PostgreSQL adapter uses a supported `psql` executable available in the CMS runtime. The SQL Server adapter executes administrative restore commands against a verified backup staged where the SQL Server host can read it. Both paths must protect credentials, honor cancellation/timeouts, verify immutable artifact bytes, clean up temporary resources on every outcome, and avoid secrets in process arguments and logs. A generic remote-upload protocol is not inferred: SQL Server deployments without server-visible backup staging are unsupported by this story.

**Dependencies**

- Completed [DMS-1255](https://edfi.atlassian.net/browse/DMS-1255) template packages for PostgreSQL and SQL Server.
- Delivery of the trusted manifest, artifact authentication, DMS-only content-profile, and compatibility contract owned by open [DMS-1271](https://edfi.atlassian.net/browse/DMS-1271). DMS-1438 must be linked as blocked by DMS-1271 in Jira.
- Existing [`Template-Management.psm1`](../../../eng/DatabaseTemplates/Template-Management.psm1) as verified provider restore sequencing: it demonstrates that `SourceIdentity` is reseeded after restore, but generates a fresh UUID. Assigning the lifecycle record's caller-supplied expected UUID is new DMS-1438 behavior, not an existing capability or runtime dependency.
- Existing CMS backend/provider project boundaries and database-provider registration conventions.
- The versioned DMS template and target-database contracts, including `dms.DataStoreIdentity` and `dms.EffectiveSchema`.

**Blockers**

- [DMS-1271](https://edfi.atlassian.net/browse/DMS-1271), transitively dependent on DMS-1270 — formal Jira blocker for the trusted artifact manifest, authentication, content-profile, and compatibility contract. DMS-1438 cannot complete trusted package execution until it is delivered.

**Out of scope**

- Producing or publishing template packages; DMS-1255 owns that pipeline.
- Bootstrap/start-script sequencing, workspace replacement, or multi-database restore; DMS-1271 owns that operator workflow.
- `ddl provision` changes unless a small existing provider primitive must be safely extracted without changing CLI behavior.
- Descriptor seeding; DMS-955 is obsolete and unrelated.
- DMS application, API, or runtime-library changes.
- CMS lifecycle persistence or public endpoints.

**Risks and implementation considerations**

- DMS-1271 is not complete. Its formal Jira blocker relationship prevents DMS-1438 completion until the trusted artifact contract lands; DMS-1438 must not execute unauthenticated PostgreSQL SQL or SQL Server backups while waiting.
- The repository does not record a provider on each ordinary data store. The one-target-provider-per-CMS-deployment constraint must be documented until a separately approved catalog change supports mixed providers.
- SQL Server restore file placement and PostgreSQL replay privileges differ operationally; the exact runtime dependencies and supported staging topology are release documentation, not late implementation choices.

**Non-binding implementation guidance**

- Process invocation and PostgreSQL credential handling are implementation decisions. The chosen approach should address quoting and injection risks, prevent credential exposure, secure temporary resources, and satisfy AC 12 and AC 16.
- SQL Server client selection and restore-command sequencing are implementation decisions. The chosen approach should provide logical-file validation, safe destination control, cancellation, redaction, cleanup, and satisfy AC 12, AC 17, and AC 18.
- Suggested option names such as `PostgresqlPsqlPath` and `MssqlBackupStagingPath` should follow the final CMS configuration conventions; their behavior is contractual, not the spelling of the property names.

### Acceptance Criteria

1. A typed async contract can create and delete one target for PostgreSQL and SQL Server and supports cancellation and configurable command timeouts.
2. Only `Minimal` and `Sample` are accepted at the contract boundary; matching is case-sensitive for v3 parity.
3. `Minimal` resolves to an allowlisted DMS Minimal package and `Sample` resolves to an allowlisted DMS Populated package for the configured target provider and supported Data Standard/effective schema.
4. Package bytes are authenticated and the manifest, artifact hash, provider, Data Standard, extension/project inventory, effective-schema metadata, DMS-only content profile, and engine compatibility are validated before target mutation.
5. Client input cannot choose or override an artifact path, feed, package ID/version, administrative connection, or protected database.
6. Target names are normalized and safely quoted. PostgreSQL `postgres`, `template0`, and `template1`; SQL Server `master`, `model`, `msdb`, and `tempdb`; and every configured CMS/identity database are rejected before destructive work.
7. Creation fails safely when the target already exists without a matching expected source identity. It does not drop or overwrite an unowned database.
8. A successful restore sets `dms.DataStoreIdentity.SourceIdentity` to the expected UUID supplied by the lifecycle record and validates the resulting DMS schema before success.
9. A retry accepts and reconciles an existing target only when the expected source identity, trusted artifact identity/hash, provider, engine compatibility, content profile, and effective schema all match and target validation is complete. It never replaces a target merely because source identity matches; a partial, incompatible, or unverifiable owned target fails safely for operator inspection.
10. Delete drops only a target whose `dms.DataStoreIdentity.SourceIdentity` matches the expected value. A missing target is an idempotent success; a mismatched or unreadable identity is a safe failure requiring inspection.
11. PostgreSQL and SQL Server implementations return typed outcome/error categories suitable for retry classification without returning secrets.
12. Administrative and generated runtime connection strings, package credentials, and decrypted secrets are never logged.
13. The provisioner has no dependency on Management API DTOs, job state, PowerShell, Docker, the DMS command-line application, or DMS runtime projects.
14. The provider-neutral contract is implemented in the existing CMS backend project and its provider adapters are implemented in the existing CMS PostgreSQL and SQL Server projects; no new CMS-to-DMS project/package reference or Docker build-context change is introduced.
15. A dedicated setting selects the target data-store provider independently of the CMS catalog provider, is validated at startup, and selects the matching CMS provider adapter. A deployment supports one configured target provider; mixed-provider ordinary data stores are out of scope until the catalog carries provider metadata.
16. PostgreSQL restoration uses a configured supported `psql` executable available in the CMS runtime. Startup validates availability; authentication and invocation do not expose credentials in arguments or logs; cancellation terminates the restore; and temporary credential/artifact resources are permission-restricted and cleaned up on every outcome.
17. SQL Server restoration uses provider-supported administrative database commands and a configured private staging location visible to the SQL Server host. CMS re-verifies the staged backup, validates its logical files and safe destination paths before restore, and cleans staging on every outcome.
18. Startup validates the selected provider's admin-secret reference, artifact catalog, runtime dependency, staging-path accessibility, and required privileges before managed routes can accept work. Documentation defines supported same-host/container/shared-volume topologies and explicitly rejects an unavailable SQL Server staging path.

### Tasks

**Implementation**

1. Finalize DMS-1438 only after DMS-1270/DMS-1271 deliver a pinned manifest, producer-trust, content-profile, and compatibility contract.
2. Add validated `DmsDataStoreSettings` and the provider-neutral provision/delete request, result, and typed-error contracts in the existing CMS backend project.
3. Add trusted artifact resolution, immutable staging, hash verification, protected-name/ownership validation, and secret-safe diagnostics.
4. Implement PostgreSQL `psql` restore and SQL Server administrative restore/staging adapters in their existing provider projects, including cancellation and cleanup.
5. Add the required container/runtime dependencies and operator documentation for supported topology, credentials, privileges, staging, cancellation, and cleanup.

**Verification**

- Unit tests for template resolution, compatibility validation, identifier/protected-target checks, typed errors, and secret redaction.
- Live PostgreSQL and SQL Server integration tests for Minimal and Sample/Populated creation, effective-schema validation, source-identity assignment, idempotent retry, owned deletion, absent deletion, mismatch refusal, cancellation, and cleanup after failure.
- Adversarial tests for forged/tampered packages, manifest mismatches, protected targets, unowned collisions, SQL/identifier injection, and contaminated non-DMS package content.
