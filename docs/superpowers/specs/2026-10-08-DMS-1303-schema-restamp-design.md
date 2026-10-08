# DMS-1303: SchemaTools schema fingerprint re-stamp

**Status:** Conversational design approved on 2026-10-08; written specification awaiting approval.
**Baseline:** Branch `DMS-1303`, commit `368b6cd7bca5e43ef601db5e2a488b4170236c19`.
**Outcome:** An operator replaces the manual fingerprint re-key after an independently validated, data-preserving physical migration with one supported SchemaTools command.

## Authority and approved decisions

[DMS-1303](https://edfi.atlassian.net/browse/DMS-1303) governs the outcome and four acceptance criteria. The [DDL design](../../../reference/design/backend-redesign/design-docs/ddl-generation.md#provision-semantics-create-only-no-migrations) continues to govern ordinary provisioning. [AGENTS.md](../../../AGENTS.md) governs release cadence and implementation conventions. The [human pre-spec](../../../.plans/DMS-1303.pre-spec.md), [nano pre-spec](../../../.plans/DMS-1303.pre-spec.nano.md), and [resolution report](../../../.plans/DMS-1303-implementation-pre-spec-review.md) are supporting analysis, not requirements.

The user approved the following choices during brainstorming:

- Support physical migrations with unchanged ApiSchema format, ResourceKey mappings, seed fingerprints, and stored component metadata. Make no promise of a named release upgrade.
- Validate matching stamps without writes: valid matches succeed without confirmation; corrupt matches fail without writes. This is the explicitly approved corruption exception to AC3.
- Keep `ddl provision` as a separate documented follow-up.
- Preserve `AppliedAt` as the original provisioning timestamp.
- Use the standalone, computed-target design described below. Conversational approval authorizes this specification; written approval remains the gate to its planning handoff.

## Scope and exclusions

One invocation targets one existing PostgreSQL or SQL Server database. It changes only `dms.EffectiveSchema.EffectiveSchemaHash` and the corresponding keys in `dms.SchemaComponent`.

The operator must independently establish that the migration DDL is complete and compatible with the target binary and its exact core/extension schema inputs. Serialize the whole procedure against DMS, projection/CDC workers, provisioning jobs, and other administrative commands. The confirmation flag is an operator assertion, not a physical-schema check.

Exclude database creation, physical migration execution, metadata/seed repair, changed resource/project inventories or versions, format conversions, automatic provisioning, runtime cache invalidation, and a migration framework or catalog-diff engine. Preserve documents, IDs, versions, timestamps, ResourceKey rows, SourceIdentity, cache rows, lifecycle/latches, and work queues. Do not claim CDC readiness or continuity.

Re-stamping does not make the reprovision-only DMS-1401, DMS-1404, or natural-key workstreams migratable. It does not repair same-hash stale templates. Keep `RelationalMappingVersion = "v3"`; no bump or rollback belongs to this story. No cleanup of representation-restamp, introspection, or carry-forward tooling is justified by usage analysis.

## Chosen design and rationale

Add `ddl re-stamp` beside `ddl emit` and `ddl provision`. Compute expected metadata using `IApiSchemaFileLoader` and `EffectiveSchemaSetBuilder`; do not generate the full DDL merely to re-key metadata.

This reuses the existing hash and seed contracts and gives one atomic result. A raw-hash mode would lose the accompanying compatibility checks. Metadata rewriting would require resource-ID migration rules. Automatic provisioning would introduce a separate transaction and a committed-stamp/provision-failed outcome. Those alternatives add responsibilities outside the approved boundary.

### Operator contract

```text
api-schema-tools ddl re-stamp --schema <core> [<extension> ...]
  --connection-string <target> --dialect pgsql|mssql
  [--timeout <seconds>] [--migration-completed]
```

Retain the existing aliases `-s`, `-c`, `-d`, and `-t`. Require schema inputs, an explicitly named target database, and one supported dialect. Timeout must be positive, defaults to 300 seconds, and applies per database command, including lock acquisition; connection timeout remains a provider connection-string setting.

`--migration-completed` confirms that migration DDL and its physical validation are complete and the procedure is operationally serialized. Require it before any re-key DML; a valid matching invocation does not require it. Offer no raw-hash, create-database, force-repair, or managed-CDC workflow options.

Use a deployment-supplied administrative connection with the required metadata lock/read privileges, parent UPDATE privilege, and child DELETE/INSERT privileges. The command creates no principals or grants. Insufficient permissions fail without a committed re-key.

Exit `0` distinguishes “re-stamp committed” from “already stamped; no changes.” The changed result reports previous/target hashes and component count only after commit. Both results explain their metadata-only meaning and the separate provision/restart workflow. Exit `1` reports actionable input, validation, provider, timeout, cancellation, or transaction failure; never describe an uncertain commit as a confirmed rollback.

### Components and data flow

| Component | Responsibility / change |
|---|---|
| SchemaTools `Program.cs` and new `Commands/DdlRestampCommand.cs` | Register and parse the command, load target inputs, invoke the service, present results/errors. |
| Existing loader and `EffectiveSchemaSetBuilder` | Produce target hash, format, seed count/hash, ResourceKey entries, and component metadata together. Their contracts remain unchanged. |
| Focused SchemaTools re-stamp transaction service and provider SQL | Own one target connection/transaction, decisive reads, locking, validation, and re-key. Use existing Npgsql/SqlClient and ADO.NET patterns with parameterized values. |
| `EffectiveSchemaFingerprintContract`, `EffectiveSchemaTableDefinition`, and `SeedValidator` | Reuse field validation, identifiers, and exact row comparisons. Add transition-specific comparisons without weakening ordinary hash-matching validation. |
| SchemaTools unit/provider integration suites and API integration harness | Verify command behavior, atomicity, preservation, and runtime admission with a real migrated fixture. |
| SchemaTools README, `docs/RELATIONAL-BACKEND.md`, and `ddl-generation.md` | Own command examples, the migration runbook, and the bounded administrative exception. |

Do not reuse `PreflightSeedValidation` or `ExecuteInTransaction` unchanged: they own separate connections, and ordinary preflight rejects the intentional old hash and permits a fresh-database path. Keep provider differences narrowly contained rather than refactoring the provisioning subsystem.

### Transaction and validation

1. Load/normalize the exact target inputs and build expected metadata before database mutation.
2. Connect to the existing target and begin one transaction. Missing database/table/required columns produce a clear error; never initialize them.
3. Acquire transaction-scoped locks in the fixed order `EffectiveSchema`, `ResourceKey`, `SchemaComponent`, preventing concurrent metadata writes or stale child capture. Use PostgreSQL `SHARE ROW EXCLUSIVE` table locks and SQL Server `TABLOCKX, HOLDLOCK` reads. These cover only the three metadata tables; operational exclusion remains necessary for the rest of the migration. Provider semantics: [PostgreSQL table locks](https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-TABLES), [SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17).
4. Read exactly one singleton, with ID `1`, and all ResourceKey/component rows on that same transaction. Apply shared stored/expected field-shape validation. Compare target format ordinally, seed count and binary seed hash exactly, ResourceKey IDs/names/versions exactly, and component endpoints/names/versions/extension flags exactly. Every component must belong to the observed parent hash; reject missing, extra, duplicate, or orphaned rows. Stored components do not contain ProjectHash, so these checks cannot prove schema-content or physical equality.
5. If the valid parent hash matches, return success with no DML, timestamp change, seed insertion, or provision refresh. Validation and locking alone do not violate the no-write contract.
6. Otherwise require confirmation. Capture the validated children, delete them under the observed hash, and update only the existing parent hash using both singleton ID and expected old hash. Require exactly one updated row. Reinsert the captured child payloads under the target hash, preserving every non-key value and the FK. Do not delete/recreate the parent or disable constraints.
7. Commit after every step succeeds. Preserve format, seed fields, `AppliedAt`, and all unrelated state.

## Failure handling and regression safeguards

Validation or execution failures before commit roll back the complete re-key. Dispose connection/transaction resources on every path. An interruption during commit can leave the client uncertain whether the atomic transaction committed: return an outcome-unknown error and direct the operator to inspect/rerun with identical inputs while still offline. A retry either observes the preserved old stamp or validates the target stamp and performs the no-op. Do not automatically retry administrative mutations.

Normalize database, path, component, and other externally derived strings before diagnostics. Never emit connection strings, credentials, SQL parameter payloads, or unsanitized provider exception text. Reuse existing safe formatting where suitable; do not forward raw exceptions through the common logger unchanged.

Ordinary `ddl provision`, generated DDL guards, and runtime fingerprint/ResourceKey validation retain their mismatch rejection. A matching stamp is not physical certification. DMS restart is required because existing startup/first-use fingerprint and seed verdicts are cached.

## AC-to-design-to-verification mapping

All verification below is future implementation work; no local tests, builds, or database operations were performed during specification development.

| Criterion | Explicit behavior and affected components | Meaningful verification |
|---|---|---|
| **AC1: one command after migration; runtime preflight passes** | Command/builder/service replace the manual parent/child re-key within the approved compatible-metadata boundary. Restarted DMS uses the target inputs and accepts the fingerprint/seeds. | On both engines, provision a source fixture, preserve existing data, apply documented additive migration DDL, invoke the command, run separate provision, and boot a fresh target host. Assert exact target hash/child payloads, successful runtime admission, and an API write/read of the new field. A fabricated old hash alone is insufficient. |
| **AC2: PostgreSQL and SQL Server** | Both provider adapters implement the same checks and atomic transition with their quoting, parameters, and locks. | Exercise the complete CLI path on real provider databases, including core plus extension components, FK integrity, exact preservation, and rollback after child deletion, parent update, and reinsertion failures. |
| **AC3: matching no-op; absent row error** | Valid matching metadata succeeds without confirmation or DML. Corruption fails read-only. An absent singleton/table fails without creation or seeding. | Compare complete parent/child snapshots including `AppliedAt`; use DML rejection/recording to prove no writes rather than only equal final values. Cover missing database/table/row, malformed/multiple singleton rows, incompatible format/seed fields, tampered ResourceKey/components, and omitted confirmation on a changed hash. Assert nonzero errors and unchanged state. |
| **AC4: migration docs reference command** | README owns both-provider syntax and migration sequence. Relational docs sections 3/7 and DDL design describe the explicit exception while preserving ordinary guards, fresh-database development, and no hot reload. | Check examples/help against the shipped parser. Exercise the documented fixture sequence on both providers. Replace instructions to perform the fingerprint re-key manually with command references; retain the independently required physical DDL. |

Use a small source/target fixture pair with identical format, resource/component inventories, and versions: add one optional bounded-length string scalar to an existing test resource. Generate source/target hashes from their actual schema files, and document the provider-specific nullable-column migration and prerequisites for the target provision refresh. Supply the same fully materialized schema inputs to the CLI and runtime; the existing API fixture loader augments DDL-only inputs, which otherwise changes their hashes. This is a controlled migration example, not a release-upgrade recipe.

Before re-stamping, assert that ordinary target `ddl provision` rejects the old hash and the newly started target runtime returns HTTP 503. Afterward, restart the host and verify admission and the affected API operation. Also retain negative evidence that an unmigrated shape can pass metadata checks after a stamp yet fail an API operation using the missing field. This proves the documented limitation, not an obligation to add physical drift detection. Coordinate competing same-target invocations deterministically; assert one re-key followed by a valid no-op, or an explicit conflict/failure with intact state. Use targeted provider failure injection through existing test seams, not public failpoint flags or a general corruption simulator. Follow repository NUnit/FluentAssertions/FakeItEasy conventions; avoid timer-order assumptions.

## Dependencies, deviations, and handoff

Required capabilities already exist: schema loading/building, field/row validators, Npgsql/SqlClient, provider test databases, and the in-process API harness. Relevant former epic siblings are coordination context, not implementation prerequisites. The separate representation-restamp command, catalog introspection consumers, and Northridge metadata-preserving copy workflow remain active and distinct.

The story requires an explicit administrative exception to the older blanket cross-hash prohibition; ordinary provisioning still cannot perform that transition. The unchanged-metadata support restriction and corrupt-match error are approved clarifications. Computed-only targeting and separate provisioning are permitted story choices. No other story/design expansion is approved.

The report's C1/C4 transaction and concurrency corrections, C5 documentation reconciliation, and C6 real-migration verification are incorporated. H/N have no substantive behavioral discrepancy. Historical v2/per-change bump guidance yields to current v3 release policy; PostgreSQL platform-reference drift remains with DMS-1447. [PR #1121](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1121) remains historical evidence, not a complete upgrade to this baseline.

**Remaining gate:** approval of this written specification. No unresolved design blocker remains. Both-provider execution evidence and the fixture migration are implementation verification obligations. A complete, validated source-to-target recipe would be required before advertising any named production release upgrade; that promise is excluded here.

**Handoff:** after written approval, use `superpowers:writing-plans` with this specification, the pinned baseline, and the three supporting artifacts. The plan must preserve these decisions and map work/verification to AC1–AC4. This brainstorming task creates no implementation plan or product changes.
