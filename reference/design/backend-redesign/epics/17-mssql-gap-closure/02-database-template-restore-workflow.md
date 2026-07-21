---
jira: DMS-1271
jira_url: https://edfi.atlassian.net/browse/DMS-1271
---

# Story: Add a Database-Template Restore Branch to Bootstrap

## Description

Add an explicit bootstrap path that materializes a DMS datastore from a published database-template package
before CMS, identity, schema provisioning, or DMS startup touches the environment.

Template restore is distinct from API seed delivery. Do not overload `-LoadSeedData`: that switch means
BulkLoadClient delivery through the running API, while restore replaces or recreates a physical database from
a package artifact.

`DMS-1255` supplies the baseline template packages and the `-DbOnly` startup phase used by this story. This
story owns the narrow producer-and-consumer extension that adds a machine-readable restore manifest outside
the database artifact and authenticates the completed package as trusted executable input. The shared
template tooling captures the live metadata immediately before backup, creates and hashes the database
artifact, packages the completed manifest beside the `.sql` or `.bak`, and signs or attests the exact package
bytes for an approved producer identity; general package publication remains under `DMS-1255` ownership. This
extension also proves that the package contains DMS datastore state only; a database template is never a CMS
or identity-state backup.

## Dependencies

- Delivered prerequisite: `DMS-1255` supplies the published template packages and `-DbOnly` startup slice.
- Active completion blocker: `DMS-1270` supplies the separate-CMS topology contract required by target-safety
  and topology-matrix acceptance coverage.

## Restore Sequence

The wrapper performs the following sequence in restore mode:

1. Resolve the database-template package, authenticate the exact `.nupkg` bytes against an approved producer
   identity, and validate its package identity, restore manifest, artifact hash, engine, Data Standard version,
   template kind, DMS-only content profile, and internally consistent effective-schema metadata. Normalize and
   validate the selected target name, including the reserved-database denylist and the separate-CMS exclusion,
   before extracting a restore artifact, creating workspace state, or starting Docker.
2. Materialize the requested schema, claims, and seed handoff into a new candidate workspace that is not the
   active `.bootstrap` tree and is never bind-mounted. Invoke the authoritative preparation phases so
   `prepare-dms-schema.ps1`, rather than the wrapper, computes the expected `EffectiveSchemaHash` and records
   any engine-defined physical-schema version in the candidate manifest.
3. Compare the package manifest with the candidate bootstrap manifest. The Data Standard version, selected
   core/extension project inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational mapping
   version, engine, and any engine-defined physical-schema version must match exactly. On mismatch, remove the
   candidate and leave the active workspace and selected database untouched.
4. Prove that the entire selected DMS stack is stopped before starting database preflight. A failed or
   indeterminate stop proof leaves the active workspace and target untouched.
5. Run `start-*-dms.ps1 -DbOnly` with an engine-dispatched restore-preflight override that starts only the
   database service and waits for readiness without creating or selecting the requested target. PostgreSQL
   must replace `POSTGRES_DB_NAME` with a generated, non-target preflight database for this first startup so
   `postgresql-init.sh` cannot create the selected target when it initializes a fresh volume. MSSQL uses its
   administrative database without materializing the target.
6. Revalidate target safety against the live engine catalog. Restore the artifact into a generated scratch
   database, validate its effective schema and DMS-only catalog contents, and remove the scratch database.
   Package, candidate-workspace, stop-proof, target-safety, or scratch-validation failure ends preflight,
   removes the candidate, scratch database, and generated preflight database, and leaves both the active
   workspace and selected database unchanged, including preserving an absent target as absent.
7. Stop the database-only slice and again prove that the complete selected stack is stopped. The verified
   candidate and scratch result do not authorize workspace replacement while any stack service is running.
8. Begin the commit stage. If the active workspace is absent or differs from the verified candidate, replace
   the complete `.bootstrap` tree with the candidate. If it already matches, discard the candidate and reuse
   the complete active tree. Never rewrite only a schema or manifest subtree.
9. Run `start-*-dms.ps1 -DbOnly` again. Immediately before any destructive target operation, repeat the
   reserved-name, separate-CMS, running-service, and live-catalog validation, then replace the explicitly
   selected physical DMS datastore database from the same hash-verified artifact validated in scratch. Before
   any service can select the restored target, replace the template's copied
   `dms.DataStoreIdentity.SourceIdentity` with a newly generated UUID and verify that it differs from the
   package value. If an existing target has CDC binding/artifact state, require the governed new-generation
   recovery workflow rather than silently preserving or rewriting that binding.
10. Run `start-*-dms.ps1 -InfraOnly` to initialize identity and CMS.
11. Run `configure-local-data-store.ps1` for the restored datastore.
12. Skip `provision-dms-schema.ps1` for that datastore because the template already contains the generated
   schema and seed data.
13. Run `start-*-dms.ps1 -DmsOnly`.
14. Run supplemental API seed only when an explicit, compatible option requests it.

The wrapper owns when restore happens. An engine-dispatched restore module owns how the package is restored.

## Engine-Specific Restore

- Both engines first materialize the package into a generated, non-user-selectable scratch database. The
  scratch name uses a safe product prefix plus an unpredictable suffix, passes the same reserved-name checks,
  and is removed on success or failure. Selected-target replacement cannot begin until scratch validation
  succeeds.
- The PostgreSQL restore-preflight start must not expose the selected target as `POSTGRES_DB_NAME` because
  `postgresql-init.sh` creates that database while initializing a fresh volume. It uses a generated preflight
  database instead, connects through an administrative database for catalog and cleanup operations, and
  removes the generated database on success or failure. Tests must prove that a target absent before preflight
  remains absent after every preflight failure. MSSQL preflight similarly connects through an administrative
  database and does not create the target.
- PostgreSQL extracts the SQL artifact from the authenticated, privately staged package, replays it into the
  scratch database, validates it, then drops/recreates the selected datastore database and replays the same
  immutable, hash-verified SQL bytes into the target.
- MSSQL extracts the `.bak` from the authenticated, privately staged package, validates logical file metadata,
  and performs `RESTORE ... WITH MOVE` into the scratch database. After validation, it restores the same
  immutable, hash-verified backup bytes into the selected datastore with target-specific `MOVE` paths and
  `REPLACE`.
- Feed resolution and an explicit `-PackageDirectory` path are both supported so the workflow can be tested
  before or without publication to the configured feed. Both paths use the same package-authentication gate;
  prepublication packages must be signed or attested by a configured development trust anchor.
- The external restore manifest contains the package id/version, engine, Data Standard version, template kind,
  selected core/extension project inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational
  mapping version, engine version, database compatibility level, physical `DocumentJson` column type, any
  engine-defined physical-schema version such as `MssqlPhysicalSchemaVersion`, the fixed `DmsDatastoreOnly`
  content profile, a canonical schema/object inventory and its SHA-256, artifact filename, and artifact
  SHA-256.
- Immediately before backup, the producer reads schema fields from `dms.EffectiveSchema` and provider fields
  from the live catalog. It fails unless the source is a dedicated DMS datastore database whose canonical
  inventory contains only the exact DMS-owned objects derived from the authoritative DDL and selected project
  inventory: the `dms` schema, effective-schema-derived resource schemas, the generated `auth` companion schema
  when present, the generated `tracked_changes_<project>` companion schemas for the selected projects, and an
  explicit provider-specific support-object allowlist. Similar-looking schema names and objects not present in
  the authoritative inventory are not implicitly allowed. In particular, `dmscs`, Configuration
  Service/OpenIddict tables, copied clients or keys, and unexpected database principals are forbidden. This
  gate also applies to direct local use of the shared template helper, whose default database name may otherwise
  identify the shared local database.
- The consumer rejects an unauthenticated package or a missing, malformed, mismatched, or hash-invalid manifest
  before extracting the restore artifact, starting the database service, or changing the active workspace.
  After `-DbOnly`, it independently derives the scratch database's canonical inventory, compares it with the
  manifest, verifies `dms.EffectiveSchema` against both the manifest and candidate bootstrap manifest, and
  enforces the DMS-only exclusions before touching the target. Feed and `-PackageDirectory` packages use
  identical producer/consumer checks; local origin is not a trust bypass.
- Any failure through scratch validation is a preflight failure: transient candidate, preflight-database, and
  scratch state is removed, while the active `.bootstrap` tree and selected physical database remain
  unchanged. A target that did not exist before preflight remains absent.
- A legacy package without the external manifest is not eligible for target replacement. If legacy-package
  compatibility is later required, it must use a safe scratch restore followed by `dms.EffectiveSchema`
  validation before any destructive operation against the selected datastore.

## Package Trust Boundary

Database-template packages are executable deployment inputs. PostgreSQL restore executes package-supplied SQL
through `psql`, so a manifest and artifact hash stored beside that SQL provide internal consistency but not
producer authenticity. Catalog validation after scratch replay cannot undo commands that affected another
database, created a server principal, or escaped through a client meta-command.

- Before extracting a database artifact or starting Docker, authenticate the exact `.nupkg` bytes with either a
  verifiable NuGet author/repository signature or a provenance attestation that cryptographically binds the
  package SHA-256, package id/version, and approved producer identity. Trust anchors and allowed producer
  identities come from repository/operator configuration outside the package being verified.
- Feed and `-PackageDirectory` resolution use the same trust policy. Restore mode has no unsigned-package
  bypass. A local prepublication workflow uses an explicitly configured development signer or attestor rather
  than treating filesystem origin as trust.
- After authentication, copy the package into a private restore workspace, extract exactly one declared
  artifact, verify its manifest hash, and prevent replacement of the staged package or artifact. Scratch and
  target restore consume those same immutable bytes so validation cannot be separated from execution by a file
  replacement race.
- Artifact and inventory hashes remain required for deterministic identity and corruption detection, but they
  never substitute for package authentication.
- Authentication, trust-policy, package-staging, or immutability failure occurs before Docker startup and leaves
  the active workspace and every database untouched.

## Topology and Target Safety

- Normalize database names case-insensitively and reject PostgreSQL `postgres`, `template0`, and `template1`,
  plus SQL Server `master`, `model`, `msdb`, and `tempdb`. Apply this denylist during parameter validation and
  again against the live catalog before scratch creation, `DROP`, or `RESTORE`; generic identifier validation
  is not a substitute for reserved-name validation.
- Fail before workspace replacement unless the complete selected stack is proven stopped after scratch
  validation; checking only whether CMS or DMS points at the eventual restore target is insufficient because
  either service may still have `.bootstrap` content bind-mounted.
- Fail before target restore if CMS or DMS is running against the target. Failure to prove either safety
  condition during preflight leaves the existing workspace and database untouched; the same checks run again
  immediately before target replacement so a post-commit race cannot authorize database mutation.
- In shared-database mode, restore only a package proven to have the `DmsDatastoreOnly` content profile, and do
  so in the `-DbOnly` window before CMS creates a fresh schema and clients. No CMS or identity record from the
  package may survive into the initialized environment.
- In separate-database mode, restore only the selected DMS datastore; never replace the CMS database.
- One invocation restores one physical database. Additional school-year, district, or tenant datastores are
  restored by explicit repeated invocations, followed by datastore registration/configuration.
- Empty schema databases continue to use `provision-dms-schema.ps1`; template-backed databases use restore.

If the requested Data Standard or extension set differs from active staged state, prepare and validate a
complete candidate first, then replace the complete workspace only after the package cross-check and stop proof
above. Partial manifest invalidation or subtree replacement is not permitted because schema, claims, and seed
state form one handoff and may still be bind-mounted.

## Acceptance Criteria

- Bootstrap exposes an explicit restore option separate from `-LoadSeedData`.
- Restore authenticates the exact package bytes, validates the external manifest and artifact hash, safely
  prepares the complete workspace, then follows `-DbOnly -> scratch validation -> stop proof -> complete
  workspace replacement -> -DbOnly -> target restore -> -InfraOnly -> configure -> -DmsOnly`; it skips schema
  provisioning only for restored targets.
- Before Docker startup or active-workspace replacement, restore proves that the package's Data Standard,
  core/extension inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational mapping version, and
  any engine-defined physical-schema version match the authoritative candidate workspace.
- PostgreSQL SQL replay and MSSQL backup restore both materialize Minimal and Populated packages correctly.
- Every restored independent target receives a new `dms.DataStoreIdentity.SourceIdentity` before CMS/DMS
  startup; repeated restores do not reuse the package identity, while an existing CDC-bound target requires
  explicit new-generation recovery.
- Target validation rejects every reserved PostgreSQL/SQL Server database and prevents an MSSQL restore from
  replacing a separate CMS database before any workspace or database mutation.
- Producers reject shared or contaminated sources while accepting the authoritative DMS-owned `auth` and
  `tracked_changes_<project>` companion schemas. Consumers scratch-restore and reject packages containing
  `dmscs`, CMS/OpenIddict state, unexpected principals, lookalike companion schemas, or objects outside the
  manifest's exact DMS-only inventory before replacing the target, including packages supplied through
  `-PackageDirectory`.
- Package authentication, resolution, staging, or external-manifest validation failures occur before Docker
  startup. Every preflight failure through scratch validation leaves the active workspace and selected target
  untouched, including on a fresh PostgreSQL volume where the selected target did not exist. Target selection
  or restore failures stop before CMS/DMS startup.
- Existing non-restore bootstrap behavior is unchanged.
- A mismatched or incomplete workspace is replaced only after the complete stack is proven stopped; the whole
  `.bootstrap` tree is removed and schema, claims, and seed handoff state are regenerated together.
- Tests cover wrapper sequencing, candidate/package hash and project-inventory mismatches, valid trusted feed
  and `-PackageDirectory` packages, unsigned and untrusted signers/attestors, package tampering after signing,
  immutable staging for scratch/target reuse, reserved database names for both engines, local-package
  resolution, engine-specific scratch and target command construction, package authentication plus
  manifest/artifact validation before Docker activity, acceptance of authoritative `auth` and
  `tracked_changes_<project>` objects, rejection of lookalike/unexpected objects, DMS-only producer and consumer
  gates, scratch cleanup, generated preflight-database cleanup, fresh-volume PostgreSQL target-absence
  preservation, failed stop proof, the database-only preflight stop/restart boundary, full-workspace replacement,
  bind-mount safety, shared/separate topology behavior, default targeting, repeated single-target operation,
  source-identity replacement and CDC-bound-target rejection, and the distinction between restore and API
  seed delivery.
- Live validation covers PostgreSQL and MSSQL with Minimal and Populated templates and supported Data Standard
  versions, including different extension selections, package-authentication failure before replay, companion
  `auth`/`tracked_changes_<project>` inventory validation, effective-schema validation, contaminated-package
  rejection before target replacement, and served data.

## Non-Goals

- A persisted multi-database restore control plane or resumable workflow.
- Restoring multiple physical databases implicitly from CMS routing state.
- Replacing API-based supplemental seed delivery.
- General package publication workflow ownership; `DMS-1255` retains that ownership while this story adds only
  the restore-manifest producer/consumer contract required for safe restore.

## Design References

- [`01-local-database-topology-parity.md`](01-local-database-topology-parity.md)
- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md)
