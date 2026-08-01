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
- Relational writes now resolve and lock the target as the first logical statement of the write
  session's first-phase command. There is no pre-session target lookup. A fallback is an ordered set of
  commands on that same session and transaction; it never re-observes the target.

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

Re-measured after first-phase production adoption on live PostgreSQL and SQL Server through the
write-session command recorder, using the
`focused/stable-key-update-semantics` fixture (an `Ed-Fi.School` with an `addresses` child collection
and no document or descriptor references), with no authorization strategies configured and no etag
precondition. **Both providers produced identical counts for every row.**

| Scenario | Session commands | BEGIN | COMMIT | ROLLBACK | `dms.ReferentialIdentity` reads | `DocumentUuid` reads | Hydration batches |
| --- | --- | --- | --- | --- | --- | --- | --- |
| POST create, 2 collection rows | 2 | 1 | 1 | 0 | 1 | 0 | 1 |
| PUT changed, 2 rows to 3 | 2 | 1 | 1 | 0 | 0 | 1 | 1 |
| POST resolving to an existing document, 2 rows to 3 | 2 | 1 | 1 | 0 | 1 | 0 | 1 |
| PUT missing target | 1 | 1 | 0 | 1 | 0 | 1 | 1 |

These are the counts after second-command DML adoption; the pre-adoption values were 6, 4, 4, and 1. See
"Production Second-Command Adoption" below.

Reading these numbers:

- These are all commands for the request: no target lookup occurs before the session begins.
- The first command for all four scenarios is the production first phase. Its first logical statement
  captures and locks the target, and its later logical statements consume only that captured decision.
  The ordinary path co-batches stored authorization (vacuous here), reference lookup (absent here), and
  current-state hydration. On create and missing-target paths hydration owns empty result sets rather
  than re-observing a target.
- The POST create stream is: first-phase composite; then one DML-mode composite carrying the
  `dms.Document` insert, the root insert, the collection insert, and the `ContentVersion` read. No
  `CollectionItemId` reservation is issued, because each collection row produces its own key inline.
- The PUT and POST-as-update streams are: first-phase composite; then one DML-mode composite carrying the
  collection insert for the single added row and the `ContentVersion` read. The root row is unchanged so
  it is not rewritten. POST-as-update performs its only target resolution inside the transaction.
- Missing PUT executes only the first-phase command, maps the absent capture to the unchanged not-exists
  result, and rolls back.
- Reference resolution issues no command in these scenarios because the fixture body carries no
  references. Live production-first-phase reference variants were measured separately:

| Provider and reference shape | Session commands | BEGIN | COMMIT | ROLLBACK | Result |
| --- | --- | --- | --- | --- | --- |
| PostgreSQL array lookup, missing reference | 1 | 1 | 0 | 1 | capture, lookup, and vacuous hydration in one command |
| SQL Server scalar small-list lookup, missing reference | 1 | 1 | 0 | 1 | capture, lookup, and vacuous hydration in one command |
| SQL Server table-valued lookup at 2000 references | 2 | 1 | 0 | 1 | capture, then standalone TVP lookup on the same transaction |

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

Still estimates, because no live command-count characterization covers them yet: DELETE, the guarded
no-op path, GET-by-id, descriptor writes, most authorization and precondition variants, and the counts
that scale with collection-table count. The focused AUTH1 tests below prove precedence, not a complete
count matrix.

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

`RelationalDocumentStoreRepository.ExecuteWriteGuardRails` →
`DefaultRelationalWriteExecutor.ExecuteAsyncInternal` → production relational first phase. Unless a
row says otherwise, these estimates assume no request references.

| Variant | Commands | Sequence |
| --- | --- | --- |
| Create, no collections, no authorization, no precondition | 4 | first-phase capture/lock plus vacuous hydration; `INSERT dms."Document" ... RETURNING`; root insert; `SELECT "ContentVersion" ... FOR UPDATE` |
| Create, proposed relationship authorization | 4 | the `AUTH1` check remains prefixed onto the `dms.Document` insert by `RelationalWriteNoProfilePersister.BuildAuthorizedInsertDocumentCommand`; no extra command |
| Create, proposed namespace authorization | 5 | `ProposedNamespaceAuthorizationOrchestrator` remains a later command |
| Create, N collection tables with new rows | 4 + 2 x (row groups across all collection tables) | collection reservation and persistence are unchanged in this slice; five collections is roughly 14 |
| Resolves to an existing document, no authorization | 1 + persist | first-phase capture/lock and hydration; per-table DML; `ContentVersion` |
| Resolves to an existing document, stored authorization | 1 + persist when embeddable | stored namespace then stored relationship are logical statements in the first-phase command; a structured parameter or command-budget boundary selects ordered same-session segments |

The old collection-less count of 6 plus COMMIT explained the ticket's original 7 round trips. The
first-phase slice removes the separate pre-session lookup and folds capture, stored authorization,
reference lookup, and hydration into one command when the provider shape and parameter budget permit.

### PUT

| Variant | Commands | Sequence |
| --- | --- | --- |
| Changed, no collections, no authorization, no precondition | 3 | first-phase capture/lock and hydration; root `UPDATE`; `SELECT "ContentVersion" ... FOR UPDATE` |
| Changed, stored authorization | 3 when embeddable | stored namespace then stored relationship join the first phase; structured parameters or a command-budget boundary use ordered segments |
| Changed, proposed authorization | +1 to +2 | proposed namespace and proposed relationship are each their own command; the POST-create inline path does not apply to PUT |
| Guarded no-op, no proposed authorization | 1 | first phase holds the capture lock; the exact same-session lock proof replaces the freshness query |
| With collections | + 2 x row groups | the same N+1 shape as POST |
| Missing target | 1 | the first-phase capture returns absent and the session rolls back |

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
| POST create, no collections | 4 | 4 | 2 | 2 | Stricter than the draft. The draft's four trips assumed separate reference-resolution, existence, authorization, and insert trips; reference resolution and target resolution are both `dms.ReferentialIdentity` lookups and merge, and the authorization/insert/child/`ContentVersion` statements all merge behind the abort device. |
| POST create, 5 collections | 4 | ~14 | 2 | 2 | Stricter. Child rows carry `CollectionItemId` from an inline sequence expression, so no table pays a reservation trip. |
| POST-as-update | 4 | 3 to 5 | 2 | 3 | Carrier-dependent. One command must serve both the create and the update branch, which requires carrying the initial target-or-missing decision across statements. |
| PUT changed | 3 | 3 to 5 | 2 | 2 | Equal to or better than the draft. PUT's target must exist, so it never needs the create/update fork. |
| PUT guarded no-op, no proposed authorization | 3 | 1 to 3 | 1 | 1 | Stricter. The freshness re-read becomes redundant once the target row is held under lock from the observing statement through commit. |
| PUT guarded no-op, proposed authorization configured | 3 | 2 to 4 | 2 | 2 | Proposed authorization runs before the no-op decision in the current executor and must continue to. |
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

The command builder enforces the rule mechanically on the single-command path: after capture, target
dependent SQL consumes only the provider carrier. The ordered-segment path does not attempt to carry a
SQL Server batch local across commands. It decodes the captured `DocumentId`, binds that exact value to
each later standalone command, and keeps the original row lock held on the same transaction.

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
session just executed. The guarded no-op validator requires the same session and exact agreement on
`DocumentId` and captured `ContentVersion`. A missing or mismatched proof is an invariant failure; it
never falls back to a freshness query. A consequence to note: existing-target PUT and POST-as-update
take the row lock on every request rather than only when authorization is configured, so concurrent
no-ops on the same document may contend where they previously did not.

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

The lifecycle is *capture, establish, restore*. Every SQL Server composite command — not only one holding
several logical statements — begins by capturing the caller's `XACT_ABORT` and `NOCOUNT` values from
`@@OPTIONS` into batch-local variables and then sets both on; after the final logical statement it
restores each to its captured value. The gate is the command, not its logical statement count, because a
logical statement count does not bound the emitted statement count: a data-modifying statement carries an
appended sentinel, the captured-target statement emits a declaration and two selects, and deterministic
packing makes a command holding one logical statement ordinary at a parameter-budget boundary.

Two measured facts drive that shape, both observed on SQL Server 2025:

- **Without the options, a failing one-logical-statement batch does not abort.** A primary-key violation
  aborts only the offending statement, execution continues through the following statement, and the
  transaction remains committable.
- **An option's lifetime depends on how the client transports the command.** A parameterized command
  travels through `sp_executesql`, a procedure context whose SET options SQL Server restores on exit, so
  the establishment dies with the command. A parameterless command travels as a plain batch with no such
  context, so `SET XACT_ABORT ON` would persist on the connection and let a *later ordinary* command's
  constraint violation doom the transaction and detach the client transaction object — a failure that
  never passes through the composite execution boundary and so would never be reported to the session.

An earlier draft of this section called the lifecycle *establish, never restore*, on the grounds that a
trailing restore never executes after an abort. That premise is true and the conclusion does not follow:
the abort path is the one case where leaving the options set is harmless, because the request is already
failing, rollback tolerance covers an already-completed transaction, and disposal returns the connection
to the pool. The path that needs restoring is the successful one, which is exactly the path a trailing
restore reaches. Restoring the *captured* value rather than forcing `OFF` preserves an ambient
`XACT_ABORT ON` that a caller established for its own reasons.

`SET NOCOUNT ON` is safe because no write path reads an affected-row count; delete success is determined
from returned rows. It is not required for result-stream decoding: SqlClient does not surface a
data-modifying statement's row-count completion as a result set, so the decoder steps correctly without
it. Independently, the client library resets session options when a pooled connection is handed to the
next borrower, which is verified by test rather than relied upon.

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

## Live-Provider Gate Outcomes

Both pre-adoption gates were run against live providers before any production write path consumes a
composite command. **Both passed on both providers.** PostgreSQL 16.3; SQL Server 2025 RTM-CU7
(`ProductVersion` 17.0.4065.4), verified from the servers.

```powershell
# PostgreSQL
$env:ConnectionStrings__DatabaseConnection = "host=localhost;port=5432;username=postgres;database=edfi_dms_backend_integration"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration `
  --filter "FullyQualifiedName~Given_A_Postgresql_Composite_Command_Against_A_Live_Provider"

# SQL Server 2025, per AGENTS.md
$env:ConnectionStrings__MssqlAdmin = "Server=localhost,14333;User Id=sa;Password=<password>;TrustServerCertificate=true"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration `
  --filter "FullyQualifiedName~Given_A_Mssql_Composite_Command_Against_A_Live_Provider"
```

Results: PostgreSQL 9 passed, 0 failed. SQL Server 7 passed, 0 failed.

### Gate 1 — Ordered Failure Attribution

A three-statement composite was executed with the failure placed in the **first**, a **middle**, and the
**last** logical statement. On both providers the reported ordinal and label identified the statement
that actually failed, and the provider exception arrived unchanged (PostgreSQL SQLSTATE `22012`, SQL
Server error `8134`).

Two findings the gate produced:

- **The failure stage is provider-dependent and is diagnostic only; the ordinal is the invariant.**
  Npgsql raises at the reader-open boundary for the first statement and at the result-set advance for
  later ones. SqlClient instead hands back a reader, lets the advance succeed onto the failing
  statement's result set, and raises when its rows are read. Nothing may assert the stage as a
  cross-provider invariant.
- **Attribution is correct on SQL Server precisely because every logical statement emits exactly one
  result set.** Without that, advancing could skip past the failing statement onto a later one and
  misattribute. This is the empirical justification for the sentinel rule rather than an argument for it.

### Gate 2 — Captured-Target Carrier

PostgreSQL: the locking clause is accepted in the capture CTE position; the captured value is published
to dependent statements in the same command; an absent target is captured as absent, so dependents
observe no target rather than re-observing the row.

SQL Server: the batch-local declaration is emitted ahead of the capture, the captured value is published
to dependent statements, and an absent target is captured as absent.

Two findings the gate produced:

- **PostgreSQL `is_local` reverts the captured *value*, not the setting's existence.** After both commit
  and rollback, and on the next pooled borrower, `current_setting(name, true)` returns the empty string
  rather than NULL, because referencing a custom GUC defines a session placeholder. The captured document
  id does not survive, and the carrier's own expressions correctly yield NULL and false, which is what
  every dependent statement consumes. The spec's phrasing "the carrier is absent after commit and
  rollback" is satisfied in the sense of *no captured target is observable*, not *the setting is unset*.
  Tests assert the promise (the derived expressions) rather than the placeholder representation.
- **SQL Server needs no revert at all.** A batch-local cannot outlive its batch, so a later command on
  the same transaction fails to compile rather than observing stale state. That is verified directly, and
  it means neither provider requires a cleanup statement.

One incidental gate observation drove the transaction-state work that preceded production adoption:
after an `XACT_ABORT`-aborted batch, SQL Server may already have rolled the transaction back and detached
the client-side transaction object, so an unconditional rollback can throw. The shared session is now
narrowly tolerant only when a reported provider failure and transaction-state probe prove that exact
case.

### Production First-Phase Adoption

The first production slice now combines target capture and lock, stored namespace authorization,
stored relationship authorization, reference resolution, and current-state hydration. The ordinary
path is one composite command in that exact order. Target-dependent POST immediate results, SQL Server
structured parameters, a non-embeddable reference lookup, or a combined parameter budget that does not
fit select a conservative ordered-segment path before the candidate builder is executed. Every segment
uses the same write session and transaction; target-dependent standalone commands bind the decoded
captured `DocumentId`.

Live verification after adoption:

- PostgreSQL command-stream characterization: 8 discovered, 8 passed, 0 failed, 0 skipped.
- PostgreSQL production array-reference first phase: 1 discovered, 1 passed, 0 failed, 0 skipped.
- PostgreSQL stored/proposed AUTH1 precedence: 2 discovered, 2 passed, 0 failed, 0 skipped.
- SQL Server command-stream characterization: 8 discovered, 8 passed, 0 failed, 0 skipped.
- SQL Server scalar and TVP production reference forms: 2 discovered, 2 passed, 0 failed, 0 skipped.
- SQL Server stored/proposed AUTH1 precedence: 2 discovered, 2 passed, 0 failed, 0 skipped.
- SQL Server structured stored-relationship fallback at 2000 claims: 1 discovered, 1 passed, 0 failed,
  0 skipped.

The AUTH1 tests prove the observable denial order: stored authorization still wins over reference
failure, and proposed authorization still wins over a stale precondition where the existing executor
contract requires it. The final targets above are unchanged; this is only the first-phase adoption.

### Production Second-Command Adoption

The second command now exists in two modes behind one implementation, because one rule decides both
whether it exists and what it holds. Authorization-only mode was adopted first. DML mode adds, behind
the same two proposed `AUTH1` statements, the `dms.Document` insert, the resource tables' deletes and
upserts in resolved dependency order, and the committed `ContentVersion` read.

Statement order is owned by a planner that both the co-batched command and the per-statement path
consume, so which statements a write owes — and the order it owes them in — cannot diverge between the
two transports. The per-statement path splits a statement's rows at the table's compiled bulk-insert row
cap; the co-batched path packs the same statements against the command's parameter budget.

Measured after adoption, both providers, through the write-session command recorder, on the
`focused/stable-key-update-semantics` fixture:

| Scenario | Before | After |
| --- | --- | --- |
| POST create, 2 collection rows | 6 | 2 |
| PUT changed, 2 rows to 3 | 4 | 2 |
| POST resolving to an existing document, 2 rows to 3 | 4 | 2 |
| PUT missing target | 1 | 1 |

Those meet the agreed targets for POST create, PUT changed, and POST-as-update.

Live verification after adoption:

- PostgreSQL command-stream characterization: 8 passed, 0 failed.
- PostgreSQL relational write family (multi-batch collections, aligned extension scopes, rollback
  safety, create/update baselines): 54 passed, 0 failed.
- SQL Server command-stream characterization and relational write family: 40 passed, 0 failed.
- Backend unit suite: 2279 passed, 0 failed.

#### Deviations Recorded By This Slice

1. **The created `DocumentId` is re-derived by a scalar subquery, not a common table expression.** The
   design proposed a CTE keyed on the unique `DocumentUuid`. A CTE belongs to the statement that declares
   it and cannot be referenced from a following statement's `VALUES` list, so the portable equivalent is
   a scalar subquery on `dms.Document` in the position the bind marker occupied. It compiles on both
   dialects in every position the marker did. The uuid is bound once per command and reused by every
   statement that derives the id.
2. **The shared collection-key reservation precedes the authorization statements.** Its values must be
   bound into the command that also carries the DML, so it cannot join that command, and it must run
   before it. It creates no artifact — a consumed sequence value is not a row — so invariant 5 is
   unaffected. This is the bounded dependency case's one extra command, not one per table.
3. **A relationship check that cannot co-batch runs its ordered segment before the DML statements are
   built.** SQL Server structured claims are the case that reaches it. Authorization therefore still
   strictly precedes any created artifact, at the cost of one more command on that path.
4. **The proposed relationship `AUTH1` is its own statement rather than a prefix on the `dms.Document`
   insert.** With both checks co-batched ahead of the insert in the same command, the prefix form is
   redundant; the ordering invariant it existed to satisfy is now satisfied by statement position.
5. **Per-command parameter-count characterizations became per-provider statement and command counts.**
   Asserting the parameter count of "the insert command" stops being well defined once statements share a
   command. The replacements assert the packing algorithm's exact output for each provider's fixed
   inputs — PostgreSQL reaches the row cap first, SQL Server the parameter cap — plus the invariant that
   neither count grows with the number of tables.
6. **The write-session recorder's hydration classifier is now read-only-based.** It previously identified
   the hydration batch as the only command touching more than one resource table, a premise co-batching
   removes; it now identifies it as the command that touches them without modifying any of them.

### Executor Test Removal And Replacement Audit

The executor fixture now uses a sequential first-phase fake to keep later executor orchestration tests
focused. Production SQL construction, ordered-segment behavior, result-stream decoding, and lock-proof
validation are owned by `Given_The_Composite_Relational_Write_First_Phase` and the composite command
fixtures. The audit of every removed or renamed executor test is:

| Removed test | Disposition | Equivalent coverage |
| --- | --- | --- |
| `It_locks_existing_put_target_before_returning_stored_relationship_no_claims` | Replaced | `It_returns_stored_relationship_no_claims_for_an_existing_put_without_a_second_observation`; `It_accepts_an_exact_guarded_no_op_lock_proof_from_the_current_session` |
| `It_returns_not_exists_when_put_target_disappears_before_stored_relationship_no_claims` | Obsolete race | Capture and row lock are one statement, so an observed target cannot disappear before stored authorization; covered by `It_returns_missing_put_without_decoding_absent_hydration_as_current_state` and the existing-target no-claims replacement above |
| `It_uses_the_session_loaded_content_version_when_guarding_unchanged_put_requests` | Renamed/replaced | `It_uses_the_session_observed_content_version_when_guarding_unchanged_put_requests`; exact proof agreement is covered by `It_rejects_a_guarded_no_op_lock_proof_that_disagrees_with_the_target` |
| `It_uses_the_session_loaded_content_version_when_guarding_unchanged_post_as_update_requests` | Renamed/replaced | `It_uses_the_session_observed_content_version_when_guarding_unchanged_post_as_update_requests`; exact proof agreement is covered by `It_rejects_a_guarded_no_op_lock_proof_that_disagrees_with_the_target` |
| `It_returns_not_exists_when_the_existing_put_target_disappears_before_current_state_load` | Obsolete race, invariant replacement | A locked target cannot disappear; a malformed empty hydration result is covered by `It_throws_when_current_state_hydration_returns_no_metadata_for_a_locked_put_target` and `It_rejects_missing_hydration_metadata_for_a_captured_target` |
| `It_returns_if_match_failure_for_put_before_reference_resolution_when_the_current_etag_mismatches` | Renamed/replaced | `It_returns_if_match_failure_for_put_before_reference_failures_when_the_current_etag_mismatches` |
| `It_returns_if_match_failure_for_post_as_update_before_reference_resolution_when_the_current_etag_mismatches` | Renamed/replaced | `It_returns_if_match_failure_for_post_as_update_before_reference_failures_when_the_current_etag_mismatches` |
| `It_locks_the_observed_post_target_for_stored_authorization_without_a_second_observation` | Replaced | `It_returns_stored_relationship_no_claims_for_an_observed_post_target_without_a_second_observation`; production proof construction is covered by `It_resolves_an_existing_target_and_hydrates_the_captured_content_version` |
| `It_retries_the_post_observation_once_when_the_observed_target_vanishes_before_the_lock` | Obsolete race | Capture and lock are atomic; POST branch selection is covered by `It_selects_the_existing_post_authorization_plan_after_capture` and `It_selects_the_post_create_immediate_result_before_reference_or_hydration_execution` |
| `It_returns_write_conflict_when_the_observed_post_target_disappears_before_current_state_load` | Obsolete race, invariant replacement | A locked target cannot disappear; malformed hydration is covered by `It_throws_when_current_state_hydration_returns_no_metadata_for_a_locked_post_target` |
| `It_returns_a_stale_no_op_compare_outcome_when_guarded_freshness_is_lost` | Obsolete race | Guarded no-op no longer re-reads freshness; same-session and exact-value proof requirements are covered by the four `guarded_no_op_lock_proof` tests |
| `It_returns_if_match_failure_with_a_stale_no_op_compare_outcome_when_guarded_freshness_is_lost` | Obsolete race | Etag comparison uses the captured/hydrated version, while the same-session proof prevents post-observation staleness; covered by the PUT etag replacement and proof tests |
| `It_returns_write_conflict_not_if_match_failure_for_wildcard_stale_no_op_compare` | Obsolete race | No stale freshness compare remains; wildcard precedence remains covered by the existing guarded no-op and precondition tests, with proof invariants covered directly |
| `It_never_reaches_the_stale_no_op_check_for_a_post_as_update_no_op_body_under_if_none_match_wildcard` | Renamed/replaced | `It_never_reaches_the_guarded_no_op_check_for_a_post_as_update_no_op_body_under_if_none_match_wildcard` |
| `It_never_reaches_the_stale_no_op_check_for_a_put_no_op_body_under_if_none_match_wildcard` | Renamed/replaced | `It_never_reaches_the_guarded_no_op_check_for_a_put_no_op_body_under_if_none_match_wildcard` |
| `It_returns_stale_no_op_write_conflict_for_profiled_put_when_freshness_is_lost` | Obsolete race | Profile merge still feeds guarded no-op; freshness is now proven by the same-session capture-lock proof and its direct invariant tests |
| `It_returns_stale_no_op_write_conflict_for_profiled_post_as_update_when_freshness_is_lost` | Obsolete race | Profile merge still feeds guarded no-op; freshness is now proven by the same-session capture-lock proof and its direct invariant tests |
| `It_returns_if_match_failure_for_profiled_stale_post_as_update_no_op_compares` | Renamed/replaced | `It_returns_if_match_failure_for_profiled_post_as_update_when_the_current_etag_mismatches` |

No retained same-name executor test dropped a caller-visible assertion without an equivalent. The
tests that stopped inspecting obsolete resolver/freshness calls now assert the same result and ordering
through the sequential first-phase boundary; the production-first-phase fixture supplies the direct
replacement for the removed internal-call assertions.

## Follow-On Work

- **Retiring the per-statement write path.** `RelationalWriteNoProfilePersister`,
  `ProposedNamespaceAuthorizationOrchestrator`, and `ProposedRelationshipAuthorizationOrchestrator` no
  longer serve a production request: the second command does. They remain because the executor's
  orchestration fixture substitutes the sequential shape through them, which is what keeps its precedence
  and result-shape assertions arranged the way they always were. Removing them means repointing that
  fixture's arrangements at the second-command seam, and the same is true of the stale-no-op retry path
  already recorded on this story. Both are their own change, not a test-cleanup afterthought.
- Co-batching the authorized GET-by-id path, including the post-hydration read boundary.
- `NpgsqlBatch` for per-statement plan caching on PostgreSQL, already an acceptance criterion on
  DMS-1065 (`reference/design/backend-redesign/epics/14-authorization/16-further-performance-optimizations.md`).
