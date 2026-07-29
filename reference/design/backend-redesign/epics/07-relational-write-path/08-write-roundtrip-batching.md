---
jira: DMS-1332
jira_url: https://edfi.atlassian.net/browse/DMS-1332
---

# Story: Batch Write-Path Database Statements Into a Single Round Trip

## Purpose And Relationship To `auth.md`

`reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" is a
**pre-implementation draft**. It was written before the relational write path existed and its
per-operation round-trip counts do not describe the implemented pipeline. This document supersedes
those counts. The `auth.md` section is intentionally left in place: it remains the historical design
rationale, and its proof-of-concept is still the reference for the database-side abort device
(`dms.throw_error` on PostgreSQL, an intentional `CAST(... AS INT)` conversion error on SQL Server,
and the `AUTH1` payload carrying the failing check's index).

The first required task of DMS-1332 is this documented verification pass: compare the draft blueprint
against current behavior for POST, PUT, DELETE, and GET-by-id; record implementable merges and
technically justified deviations; and establish agreed targets. The draft's 3/4/2 targets for
PUT/POST/DELETE are starting points, not requirements.

## Counting Convention

**A target or a recorded count is the number of `DbCommand` executions issued for the whole request,
excluding the provider's BEGIN and COMMIT.**

- BEGIN and COMMIT are recorded and reported separately and are never folded into a target.
- BEGIN is provider-dependent and will be **measured, not assumed**. Npgsql defers `BEGIN` onto the
  first command of the transaction; `SqlConnection.BeginTransactionAsync` is expected to issue its
  own exchange. Neither is asserted here.
- COMMIT is a separate exchange on both providers and DMS-1332 does not remove it. See
  "COMMIT is a permanent floor" below.
- A command may carry many SQL statements and many result sets; that is still one command.
- Today `RelationalDocumentStoreRepository.ResolveTargetContextAsync` issues a command on a separate
  scoped executor *before* the write session exists. Such commands are counted and flagged as
  pre-session in the current-state tables below. After target resolution moves into the write session
  there is no pre-session command and the distinction disappears from the target tables.

### Estimate Versus Measured

Every count in the "current state" tables below is a **code-inspected estimate** derived by reading
the call graph, except the scenarios listed under "Measured baseline", which have been observed on
live PostgreSQL and SQL Server.

An estimate may not be described as measured until the write session's command recorder observes
every in-session command (see "Instrumentation gap" below) and live characterization tests on both
providers confirm it. The recorder became complete under DMS-1332; the remaining variants are
promoted as their characterization coverage lands, and the final matrix is recorded at the end of
the story.

### Measured Baseline

Observed on live PostgreSQL and SQL Server through the write-session command recorder, using the
`focused/stable-key-update-semantics` fixture (an `Ed-Fi.School` with an `addresses` child collection
and no document or descriptor references), with no authorization strategies configured and no etag
precondition. **Both providers produced identical counts for every row.**

| Scenario | Session commands | BEGIN | COMMIT | ROLLBACK | In-session `dms.ReferentialIdentity` reads | Hydration batches |
| --- | --- | --- | --- | --- | --- | --- |
| POST create, 2 collection rows | 6 | 1 | 1 | 0 | 1 | 0 |
| PUT changed, 2 rows to 3 | 4 | 1 | 1 | 0 | 0 | 1 |
| POST resolving to an existing document, 2 rows to 3 | 4 | 1 | 1 | 0 | 0 | 1 |

Reading these numbers:

- They count commands **issued on the write session**. Each also pays one pre-session target-lookup
  command, issued on a separate connection outside the write transaction, which the session recorder
  cannot see. Total DB commands per request are therefore one higher than the session count.
- The POST create stream is: in-session target lookup, `dms.Document` insert, root insert,
  `CollectionItemId` reservation, collection insert, `ContentVersion` read.
- The PUT and POST-as-update streams are: hydration batch, `CollectionItemId` reservation, collection
  insert for the single added row, `ContentVersion` read. The root row is unchanged so it is not
  rewritten.
- **POST-as-update issues no in-session target lookup.** The executor re-resolves a POST target
  in-session only when the incoming target context is `CreateNew` or the request carries an etag
  precondition; here the pre-session lookup already resolved an existing document. So the only target
  resolution for that request happened outside the write transaction.
- Reference resolution issues no command in these scenarios because the fixture body carries no
  references; the resolver skips the adapter when every lookup is already satisfied. Its routing
  through the session is covered by unit-level assertions rather than by these counts.

Reproduce with:

```powershell
# PostgreSQL
$env:ConnectionStrings__DatabaseConnection = "host=localhost;port=5432;username=postgres;database=edfi_dms_backend_integration"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration `
  --filter "FullyQualifiedName~Write_Session_Command_Stream"

# SQL Server 2025, per AGENTS.md
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=<password>;TrustServerCertificate=true"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration `
  --filter "FullyQualifiedName~Write_Session_Command_Stream"
```

Still estimates, because no live characterization covers them yet: DELETE, the guarded no-op path,
GET-by-id, descriptor writes, every authorization and precondition variant, and the counts that scale
with collection-table count.

## Measurement Method

Command counts are observed through a decorator over `IRelationalWriteSession` that records each
`RelationalCommand` passed to `CreateCommand`. The pattern already exists in
`PostgresqlRelationalWriteMultiBatchCollectionTests` and `MssqlRelationalWriteMultiBatchCollectionTests`.

Assertions are on **exact** command counts and the ordered logical-statement kinds, never on timing.
An upper-bound assertion would not catch a regression back to a per-table N+1, which is the specific
failure this story exists to prevent.

### Instrumentation Gap (Closed)

Three in-session call sites originally bypassed `IRelationalWriteSession.CreateCommand` and were
invisible to the recorder: reference resolution
(`IReferenceResolverAdapterFactory.CreateSessionAdapter`), the in-session POST target lookup
(`IRelationalWriteTargetLookupResolver.ResolveForPostAsync`), and current-state hydration
(`ISessionDocumentHydrator.HydrateAsync`). Each took a raw `DbConnection`/`DbTransaction` pair, so a
count assertion written against the recorder would have measured a subset and passed silently.

All three now take the session or the session's command executor. Consumers that hold an
`IRelationalCommandExecutor` are covered because the default
`IRelationalWriteSession.CreateCommandExecutor` binds to the instance it is invoked on, so a session
decorator sees their commands too. Hydration is created through `CreateCommand` by way of a
command-factory overload on `HydrationExecutor`; its keyset parameters are still bound by the plan
layer after creation, so the recorder captures that one command's text with an empty declared
parameter list.

`RelationalWriteSessionCommandRecorder` in `Backend.Tests.Common` is the shared recorder, and
`WriteSessionCommandStreamScenarios` holds the provider-neutral expectations each engine adapter
asserts against.

## Current State

All counts are code-inspected estimates and exclude BEGIN and COMMIT.

### POST

`RelationalDocumentStoreRepository.ExecuteWriteGuardRails` → `ResolveTargetContextAsync`
(pre-session) → `DefaultRelationalWriteExecutor.ExecuteAsyncInternal`.

| Variant | Commands | Sequence |
| --- | --- | --- |
| Create, no collections, no authorization, no precondition | 6 | pre-session `ResolveForPostAsync`; reference resolution; in-session `ResolvePostTargetAsync` (a duplicate of the first); `INSERT dms."Document" ... RETURNING`; root insert; `SELECT "ContentVersion" ... FOR UPDATE` |
| Create, proposed relationship authorization | 6 | the `AUTH1` check is already prefixed onto the `dms.Document` insert by `RelationalWriteNoProfilePersister.BuildAuthorizedInsertDocumentCommand`; no extra command |
| Create, proposed namespace authorization | 7 | `ProposedNamespaceAuthorizationOrchestrator` issues its own command |
| Create, N collection tables with new rows | 6 + 2 x (row groups across all collection tables) | each collection table pays one `CollectionItemIdSequence` reservation command plus one insert command per parameter-cap group; five collections is roughly 16 |
| Resolves to an existing document, no authorization | 5 + persist | pre-session lookup; reference resolution; current-state hydration; per-table DML; `ContentVersion` |
| Resolves to an existing document, stored authorization | +3 | a third `ResolveForPostAsync` inside `StoredRelationshipAuthorizationOrchestrator`, then `TryLockExistingTargetAsync`, then stored namespace and stored relationship as separate commands |

The collection-less create at 6 commands plus COMMIT reproduces the 7 round trips reported on the
ticket, and the five-collection create reproduces the reported 18.

### PUT

| Variant | Commands | Sequence |
| --- | --- | --- |
| Changed, no collections, no authorization, no precondition | 5 | pre-session `ResolveForPutAsync`; reference resolution; current-state hydration; root `UPDATE`; `SELECT "ContentVersion" ... FOR UPDATE` |
| Changed, stored authorization | 8 | plus `TryLockExistingTargetAsync`, stored namespace, stored relationship |
| Changed, proposed authorization | +1 to +2 | proposed namespace and proposed relationship are each their own command; the POST-create inline path does not apply to PUT |
| Guarded no-op | 4 to 7 | as above minus persist, plus `RelationalWriteFreshnessChecker.IsCurrentAsync`, a second `FOR UPDATE` read of the same row |
| With collections | + 2 x row groups | the same N+1 shape as POST |
| Missing target | 1 to 2 | the pre-session lookup returns not-found and no session is opened on the plain path |

### DELETE

`RelationalDocumentStoreRepository.DeleteDocumentByIdAsync`, which does not use the write executor.

| Variant | Commands | Sequence |
| --- | --- | --- |
| No authorization, no precondition | 3 | `TryResolveDeleteTargetAsync`; `TryLockDeleteTargetAsync`; combined root and `dms.Document` delete |
| Stored namespace and relationship authorization | 5 | one command each |
| Specific-tag `If-Match` | 3 | already a client-side compare against the locked `ContentVersion` |
| Blocked by an inbound foreign key | 3 | the violation surfaces on the delete command and is mapped by constraint name |

### GET-by-id

| Variant | Commands |
| --- | --- |
| No authorization | 2 — already at the draft target |
| Namespace authorization only | 4 (adds the authorization command and `ShouldRetryPostHydrationReadBoundaryAsync`) |
| Namespace and relationship authorization | 5 |

### Descriptor Writes

`DescriptorWriteHandler` is a separate path with no flattener, no child tables, and no collection
reservations. Its counts are recorded when the recorder is complete.

## Draft, Current, And Target

Targets exclude BEGIN and COMMIT. Two target columns are given because the PostgreSQL
captured-decision carrier is spike-gated (see "Same-state observation" below). If the carrier does
not survive its spike, the conservative column is the agreed target and the difference is recorded
here as a technically justified deviation.

| Operation | Draft | Current (estimate) | Target, carrier proven | Target, carrier fallback | Deviation from draft and why |
| --- | --- | --- | --- | --- | --- |
| POST create, no collections | 4 | 6 | 2 | 2 | Stricter than the draft. The draft's four trips assumed separate reference-resolution, existence, authorization, and insert trips; reference resolution and target resolution are both `dms.ReferentialIdentity` lookups and merge, and the authorization/insert/child/`ContentVersion` statements all merge behind the abort device. |
| POST create, 5 collections | 4 | ~16 | 2 | 2 | Stricter. Child rows carry `CollectionItemId` from an inline sequence expression, so no table pays a reservation trip. |
| POST-as-update | 4 | 5 to 9 | 2 | 3 | Carrier-dependent. One command must serve both the create and the update branch, which requires carrying the initial target-or-missing decision across statements. |
| PUT changed | 3 | 5 to 8 | 2 | 2 | Equal to or better than the draft. PUT's target must exist, so it never needs the create/update fork. |
| PUT guarded no-op, no proposed authorization | 3 | 4 to 7 | 1 | 1 | Stricter. The freshness re-read becomes redundant once the target row is held under lock from the observing statement through commit. |
| PUT guarded no-op, proposed authorization configured | 3 | 4 to 7 | 2 | 2 | Proposed authorization runs before the no-op decision in the current executor and must continue to. |
| DELETE | 2 | 3 to 5 | 1 | 2 | Carrier-dependent, and stricter than the draft when proven. |
| GET-by-id, no authorization | 2 | 2 | 2 | 2 | Already at target; unchanged. |
| GET-by-id, authorized | 2 | 4 to 5 | unchanged | unchanged | Verification-only deviation; see below. |
| Descriptor POST/PUT/DELETE | — | recorded later | unchanged | unchanged | Verification-only deviation; see below. |

### Bounded Dependency Case

One condition adds a single command, and only when present: a collection-aligned extension scope with
unmatched inserts, whose rows need the parent collection row's generated `CollectionItemId`. Where
those ids cannot be supplied inline because a dependent statement in the same command consumes them,
**one shared cross-table reservation command** is issued — never one per table. POST create,
POST-as-update, and PUT each cost one more command in that case.

### Path Classes

Error and short-circuit paths never execute a normal-path count.

| Class | Commands |
| --- | --- |
| Not found (PUT or DELETE missing target) | 1 |
| Authorization denial on stored values | 1 |
| Authorization denial on proposed values | 2 |
| Immediate precondition failure (no authorization configured) | 1 |
| Deferred precondition failure, proposed authorization configured | 2 |
| Deferred precondition failure, no proposed authorization | 1 |
| Deferred missing-document-reference failure, proposed authorization configured | 2 |
| Deferred missing-document-reference failure, no proposed authorization | 1 |
| Guarded no-op, proposed authorization configured | 2 |
| Guarded no-op, no proposed authorization | 1 |
| Any coincident combination of the no-DML conditions above, proposed authorization configured | 2 |
| Immediate reference-resolution failure | 1 |
| Race or retry | one attempt's count per attempt; the existing two-attempt loop is unchanged |
| Parameter overflow | per the packing algorithm below; never a function of table count |

### Second-Command Emission Rule

A single rule governs whether the second command exists and what it contains, so no situation can
drift out of alignment with the others:

> The second command is emitted **if and only if** proposed authorization is configured **or**
> data-modifying statements are required. Its mode is DML mode **if and only if** data-modifying
> statements are required; otherwise it is authorization-only mode.

Authorization-only mode contains exactly the proposed `AUTH1` statements that DML mode would have
contained, in the same order — namespace before relationship — and nothing else. Deferred precondition
failures, deferred reference failures, and guarded no-ops are all no-DML situations, so a request that
is several of them at once still issues one authorization-only command rather than one per condition.

Proposed authorization cannot be hoisted into the first command. Its statements bind values taken from
the finalized merged root row (`RelationalWriteFinalizedRootRow.Build`, consumed by
`ProposedNamespaceValueExtractor` and `RelationshipAuthorizationProposedValueExtractor`), and that row
does not exist until the first command's hydration result sets are decoded and the merge runs. The
two-command floor for authorized writes is therefore structural.

## Verification-Only Deviations

### Authorized GET-by-id: 4 to 5 Commands Against a Draft Target of 2

Unauthenticated GET-by-id is already at the draft target of two commands. The additional commands on
the authorized path are the stored namespace check, the stored relationship check, and
`ShouldRetryPostHydrationReadBoundaryAsync`, which re-resolves the target after hydration to prove the
served representation matches the state that was authorized.

That recheck is a deliberate correctness device, not accidental cost. Collapsing it requires its own
design, and doing so inside a write-path story would put read-path correctness at risk. DMS-1332
therefore records the deviation and changes nothing on this path; the read path must not regress and
read regression coverage is run. A follow-on story is recommended for co-batching authorized
GET-by-id.

### Descriptor Writes: Verification Only

`DescriptorWriteHandler` deliberately does not flow through the generic write executor
(`06-descriptor-writes.md`), has no child tables, and performs no collection-id reservations — so none
of the acceptance criteria that motivate this story (reference-resolution, authorization, document,
root, and child-table statement batching, and the child-table reservation N+1) apply to it. Its counts
are recorded for completeness. `DescriptorWriteHandler` is not modified by DMS-1332.

## Correctness Invariants That Constrain Batching

1. One transaction per request; rollback on any failure. Commit-phase failure must continue to avoid
   an explicit rollback and rely on disposal.
2. Target locking precedes same-state observation. The locking statement is the first statement of any
   command that also reads current state or evaluates stored authorization.
3. Stored authorization strictly precedes proposed authorization, and namespace AND-composes before
   the relationship OR-group on both sides. Because a command aborts at the first `AUTH1`, statement
   order *is* precedence order; reordering silently changes which denial the client sees.
4. Authorization versus precondition. `GetEtagPreconditionEvaluation` defers the etag precondition past
   proposed authorization whenever any authorization is configured, and
   `TryBuildDeferredPreconditionFailureResult` must keep admitting both `If-Match` and `If-None-Match`.
   Batching must never let a precondition be evaluated earlier than it is today.
5. Create artifacts only after proposed authorization. The `dms.Document` insert stays textually after
   the proposed `AUTH1` statements in the same command.
6. Immutable-identity failure outranks proposed authorization.
   `RelationalWriteIdentityStability.TryBuildFailureResult` is pure in-process work over the merged
   root row and stays ahead of any authorization statement being sent.
7. Guarded no-op freshness. The observed `ContentVersion` must still be current before a no-op success
   is returned; see "Lock-based no-op freshness" below.
8. Stable collection identity. Matched rows keep their `CollectionItemId`; only unmatched inserts get
   new ids; the temporary-negative-ordinal pass stays ordered before the final contiguous update.
9. Dependency order and unresolved collection ids. The command builder consumes the resolved order
   produced by the existing deferral loop and fails loudly rather than reordering.
10. Deletes before upserts; children before parents on delete; parents before children on insert.
11. The resource root row is deleted before `dms.Document`, so the tombstone trigger can still read
    `DocumentUuid` (see `transactions-and-concurrency.md` §"Delete Path" and DMS-1180).
12. Cancellation tokens are preserved. A merged command cannot be cancelled between its statements;
    this is an accepted consequence.
13. Transient classification and the whole-attempt retry are unchanged. A deadlock inside a merged
    command rolls back and replays the entire transaction.
14. API responses are unchanged: result shapes, status codes, ProblemDetails, `_etag`, and
    `_lastModifiedDate`.

### Same-State Observation

Under READ COMMITTED, **each statement** takes its own snapshot on both providers. One network command
is one round trip, not one same-state observation. A later statement that repeats a target predicate
is therefore a fresh observation, not a reuse of the first statement's decision, and a concurrent
create landing in between would let a request classified as a create run stored-value authorization
against a row it never locked, let hydration observe a target the locking statement never saw, or let
a missing-target DELETE authorize or mutate a newly appeared row.

Every statement after the locking statement must therefore consume the **captured** outcome of that
statement rather than re-observe:

- PostgreSQL captures into a transaction-local setting with
  `set_config('dms.<name>', <value>, is_local => true)`, read back with
  `current_setting('dms.<name>', true)`. It needs no schema object and no DDL, and PostgreSQL reverts
  it automatically at transaction end on both commit and rollback. Isolation level is not raised.
- SQL Server captures into batch-local variables declared in the same batch, which are scoped to it by
  construction. Their names are reserved so the parameter allocator cannot collide with them.

The command builder enforces the rule mechanically: after the capturing statement is emitted, any
statement that references the target other than through the captured expression is a build-time error.

Holding the lock from the capturing statement also makes child-table reads *more* consistent than they
are today: a concurrent writer to any child row must bump `dms.Document.ContentVersion` through its
`*_Stamp` trigger, which requires the row lock already held, so it blocks until the request finishes.

The PostgreSQL carrier is **spike-gated**. If a live-provider spike shows it does not behave as
described, the conservative fallback targets apply — POST-as-update 3 and DELETE 2 — and that becomes
the recorded deviation. Correct same-state behavior outranks the lower count.

### Lock-Based No-Op Freshness

`03-persist-and-batch.md` requires revalidating the observed `ContentVersion` before returning a no-op
success, with stale compares surfaced as a write conflict rather than success. Once the target row is
held under `FOR UPDATE` / `UPDLOCK, HOLDLOCK` from the observing statement through commit, no other
transaction can change that row in between, so the criterion is satisfied without a second read.

This applies only where the lock is provably held in the current session. That is expressed as a type,
not a flag: a locked-target value is constructible only from the result of the locking statement the
session just executed, and the no-op path accepts only that type. Paths that did not lock retain the
existing freshness query. A consequence to note: existing-target PUT and POST-as-update take the row
lock on every request rather than only when authorization is configured, so concurrent no-ops on the
same document may contend where they previously did not.

## Error Attribution

The existing error contract is already independent of statement position for every mapping this story
requires, so no new error-framing protocol is introduced:

- Authorization failures carry their own attribution. The relationship and namespace payload codecs
  encode a discriminator and a strategy index, transported as PostgreSQL `SqlState = "AUTH1"` and as
  the SQL Server conversion-error message, and `RelationalAuthorizationAuth1Dispatcher` routes on the
  discriminator. Every check merged into one command must be assigned a distinct emitted `AUTH1` index.
- Constraint violations are resolved by the violated constraint name from provider metadata, not by
  which call site issued the statement, so natural-key conflicts continue to map to 409 and the
  `If-None-Match` create race continues to map to a write conflict.
- Everything else already falls through to an unknown-failure result, so no attribution is lost.

For diagnostics and for reading successful results, merged commands use a deterministic result-stream
protocol:

- Merged commands execute through a reader and step result sets explicitly. Executing them without a
  reader consumes every statement and loses all position information.
- Every logical statement emits exactly one result set. Statements that already return data satisfy
  this; pure DML statements are followed by a small sentinel select.
- Sentinels rather than `RETURNING` / `OUTPUT` are used for resource tables, because SQL Server rejects
  `OUTPUT` without `INTO` on a table with enabled triggers and every resource root and child table
  carries an emitted `*_Stamp` trigger. `dms.Document` currently has no trigger, which is why the
  existing ordered delete can use `OUTPUT DELETED.[DocumentId]`; that is a property of the current DDL
  and is not relied on generally.
- With one result set per statement, a failure raised while opening the reader identifies the first
  statement, and a failure raised while advancing to result set *k* identifies statement *k*.
- The provider exception is never wrapped or replaced. The logical statement identifier is diagnostic
  metadata only; no existing mapping decision changes, and a previously unmapped database failure still
  produces the same unknown-failure response.

This protocol is **spike-gated**. A live first/middle/last failure spike on both providers runs before
any production call site adopts merged commands. If it does not hold, the batching scope is reduced to
statement runs that share one error-mapping treatment rather than the claim being softened.

## Provider Limits, Packing, And Session Options

### Parameter Ceilings And Packing

SQL Server allows 2098 usable parameters per command (`MssqlCommandLimits.MaxUserParametersPerCommand`,
the documented 2100 less the two slots `sp_executesql` consumes for its own arguments); PostgreSQL
allows 65535. Both apply a 1000-row policy cap.

`BulkInsertBatchingInfo.MaxRowsPerBatch` is computed **per table** in `PlanWriteBatchingConventions`.
Merging tables into one command makes a per-table budget unsound, so the command builder tracks the
budget at command level and packs deterministically:

- An atomic unit is one table's contiguous run of rows for one statement kind, split at the dialect row
  cap into row groups. A row group is never split.
- Units are appended in the existing resolved dependency order.
- The current command is sealed and a new one opened when appending the next unit would exceed the
  command's remaining parameter budget, the dialect row cap for the statement being appended, or a
  dependency boundary.

`ceil(total parameters / budget)` is not asserted as an equality, because atomic units, row width, row
caps, and dependency boundaries all constrain packing. Tests assert the exact output of the algorithm
for fixed inputs, plus three invariants: no per-table N+1; adding tables whose rows fit the remaining
budget does not increase the command count; and command count is monotonic non-decreasing in total
bound parameters.

PostgreSQL normally reaches the row cap first and SQL Server the parameter cap first; both are covered.

### Sequence Values

`nextval('"dms"."CollectionItemIdSequence"')` and `NEXT VALUE FOR [dms].[CollectionItemIdSequence]` are
both legal inside `INSERT ... SELECT`, so a collection table with no dependent rows needs no reservation
command. Where a dependent statement in the same command consumes the parent's ids, the existing bulk
reservation shapes are retained but issued once for all tables.

### Generated Document Identity

`dms.Document.DocumentId` is an identity column, so unlike `CollectionItemId` it cannot be reserved in
advance without a DDL change, which is out of scope. Within one command the value is produced and
consumed server-side: later statements re-derive it through a common table expression keyed on the
unique `DocumentUuid`, a shape that compiles on both dialects. The SQL Server local-variable form is
retained only as a measured optimization.

### SQL Server Session Options

`SET XACT_ABORT` is **session state**, not command state. With it off, which is the default, a
constraint violation in a multi-statement batch aborts only the offending statement and execution
continues; PostgreSQL always aborts the transaction. Merging DML therefore requires it on.

The lifecycle is *establish, never restore*: every SQL Server command containing more than one
statement begins with `SET XACT_ABORT ON;` and `SET NOCOUNT ON;` inside the command text itself. That
costs no extra round trip and no path depends on a previous command having set it, so nothing needs to
be unset. `SET NOCOUNT ON` is safe because no write path reads an affected-row count; delete success is
determined from returned rows. Independently, the client library resets session options when a pooled
connection is handed to the next borrower, which is verified by test rather than relied upon.

A related hazard must be handled in the same change: when `XACT_ABORT ON` causes the server to roll
back, the client-side transaction object can be detached, and an unconditional rollback then throws.
The session's rollback is made tolerant of that single case only — narrowly, after a database failure
has been explicitly reported on that session for the current failure, and only when a provider-specific
probe proves the transaction is already completed. Cancellation, connection failures, unrelated
invalid-operation failures, and the commit-after-rollback and rollback-after-commit guards all continue
to throw. PostgreSQL behavior is unchanged: an aborted transaction always accepts a rollback.

## COMMIT Is A Permanent Floor

DMS-1332 does not remove the COMMIT round trip. Under the request-scoped explicit transaction boundary
established by DMS-984, commit is an independent exchange on both providers; removing it would mean
autocommit, which is incompatible with "one transaction per request, rollback on any failure". The
instrumentation on the ticket puts COMMIT at roughly 1.5 ms, about a quarter of database session time,
so this is a material and permanent floor and the targets above are stated with it excluded rather than
counted around.

## Measurement Gate

Merged command text varies with the combination of per-table row counts, so the space of distinct
statement texts is far larger than today's. On PostgreSQL, statements are auto-prepared only after
repeated identical text, so high-cardinality merged commands may never be prepared and could trade
saved wire time for repeated planning; on SQL Server the analogue is plan-cache pressure. Sentinel
statements and speculative hydration add further per-command work.

Acceptance is therefore **warm, steady-state, end-to-end write latency on both providers** — not command
counts, and not a cold plan inspection. If a regression appears, deterministic row-count bucketing may
be applied within this story. Adopting a provider-specific batch API remains the separate follow-on
already recorded on DMS-1065.

## Follow-On Work

- Co-batching the authorized GET-by-id path, including the post-hydration read boundary.
- `NpgsqlBatch` for per-statement plan caching on PostgreSQL, already an acceptance criterion on
  DMS-1065 (`reference/design/backend-redesign/epics/14-authorization/16-further-performance-optimizations.md`).
