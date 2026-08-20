# Code Review: DMS-1381 — SQL Server concurrent-write deadlocks in generated `_Stamp` triggers

**Branch:** DMS-1381
**Base:** main (isolated from `2d226982a`, the last merged PR commit)
**Date:** 2026-08-20
**Commits reviewed:** 14 branch-owned
**Round:** 5 (prior rounds indexed from `.claude/review-logs/DMS-1381.md`; nothing already applied/rejected is re-raised)

**Verification run for this review:** `dotnet build` clean (exit 0) · `dotnet csharpier check src/dms` clean (2250 files) · `Backend.Ddl.Tests.Unit` 1277 passed / 0 failed / 1 skipped.

---

| Category           | Resume                                                                        | High | Medium | Low   |
|--------------------|-------------------------------------------------------------------------------|------|--------|-------|
| Correctness        | Sibling EdOrg trigger re-fires ungated on the mirror stamp; two reader gaps    | 0    | 1      | 2     |
| Test coverage gaps | Contention fixture's durability assertion is vacuous when the breaker trips    | 0    | 1      | 0     |
| Maintainability    | Multi-row workset test hard-codes the seeded Contact count                     | 0    | 0      | 1     |
| **Total**          |                                                                               | **0**| **2**  | **3** |

---

## Correctness risks / gaps

### 1. [Medium] The mirror stamp re-fires `TR_<EdOrg>_AuthHierarchy_Update`, which is ungated and does a no-op `DELETE` + `MERGE` against the shared `auth` hierarchy table — **FIXED**

> **Resolution.** `AuthTriggerBodyEmitter.EmitHierarchicalUpdateBody` now wraps both steps in
> `IF UPDATE(<parent id>) OR … BEGIN … END`, on SQL Server only, ranging over exactly the
> `DenormalizedParentIdColumn`s the body's own predicates read. See *Resolutions* at the end of this
> file for the verification evidence.

**Not introduced by this branch.** The trigger is pre-existing and this PR neither adds nor worsens it. It is reported because it is reached by the exact statement this PR hinted, it is the exact failure pattern the ticket's root-cause section describes, and it makes the new invariant 6 claim more than the emitter delivers.

**What's wrong.** `RelationalModelDdlEmitter.cs:1989` emits the mirror stamp as a real `UPDATE` on the resource root table:

```sql
UPDATE r SET r.[ContentVersion] = s.[ContentVersion], r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]
FROM [edfi].[LocalEducationAgency] r WITH (FORCESEEK)
INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];
```

Nested triggers (server option `nested triggers`, default ON — DMS reads it at `CdcSqlServerHeartbeatDatabaseProvider.cs:2133`) mean that statement fires *every other* `AFTER UPDATE` trigger on that table, not only the stamping trigger the new guard protects. On EdOrg root tables one of those is `TR_<EdOrg>_AuthHierarchy_Update`, and its body carries **no gate at all** — no `IF EXISTS`, no `UPDATE(col)` pre-filter:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/ddl-emission/expected/mssql/auth-edorg-hierarchy.sql:196-262` — straight into `DELETE tbd FROM [auth].[EducationOrganizationIdToEducationOrganizationId] … EXCEPT … CROSS JOIN …` then `MERGE INTO [auth].[EducationOrganizationIdToEducationOrganizationId]`.

The two sibling triggers on the same table *are* gated in exactly the way this ticket argues for — `TR_…_AbstractIdentity` at `:91` (`IF NOT EXISTS (SELECT 1 FROM deleted) … ELSE IF (UPDATE([EducationOrganizationId]))`) and `TR_…_ReferentialIdentity` at `:265`. `AuthHierarchy_Update` is the outlier.

**Impact.** On a stamp-only firing the `DELETE`'s workset is provably empty (its predicate requires `StateEducationAgency_EducationOrganizationId` to have changed, and the stamp `SET` list touches neither hierarchy column), but the optimizer still resolves the joins against `auth.EducationOrganizationIdToEducationOrganizationId` to discover that — the same "scan a shared table to prove there is nothing to do" mechanism as Cycle 1. The `MERGE`'s `USING` is worse: its source sub-select joins `inserted` to the hierarchy table on `new.[EducationOrganizationId] = tuples.[SourceEducationOrganizationId]`, which matches real rows, so the match pass runs for real on every firing.

Reachability is not hypothetical: on a **root EdOrg insert**, `@stamped` is pre-populated outside the guard, `IF EXISTS (SELECT 1 FROM @stamped)` is true, the mirror `UPDATE` runs, and `AuthHierarchy_Update` fires. So every EducationOrganization create performs a no-op hierarchy `DELETE` + `MERGE` on the shared `auth` table. DS 5.2 emits 5 of these triggers (`grep -c "AuthHierarchy_Update]" src/dms/backend/Fixtures/authoritative/ds-5.2/expected/mssql.sql` → 5). A concurrent Populated-Template load creates EdOrgs in bulk, which is where the ticket's field failure came from.

**Recommendation — pick one, both are small:**

- *Fix it here*: gate the emitted body with the shape its two siblings already use —
  `IF UPDATE([EducationOrganizationId]) OR UPDATE([<parent>_EducationOrganizationId]) BEGIN … END`.
  `UPDATE()` is false for both on a stamp-only re-firing, and the inner `EXCEPT`/`WHERE` predicates remain authoritative, so this can only skip a firing whose workset was already empty — the identical safety argument the new guard already carries at `RelationalModelDdlEmitter.cs:1942`.
- *Or narrow the claim*: `change-queries.md:1288` (invariant 6) currently reads as a general property — "the root-resource and descriptor content stamps MUST be **skipped outright** … when the firing cannot change anything". Scope it explicitly to the stamping triggers and state that other generated triggers on the same table still re-fire on the mirror `UPDATE`, so the next reader does not infer the class is closed.

Doing neither leaves the ticket's open question ("Whether Cycle 3 is the last one") answered in the code but not in the doc.

### 2. [Low] `DeadlockGraphReader` silently drops a deadlock event whose `<value>` is empty — **FIXED**

**What's wrong.** `DeadlockGraphReader.cs:281-291` splits every `xml_deadlock_report` payload two ways:

- `DeadlockGraphsIn` keeps values that have a `<deadlock>` child element;
- `UnparsedGraphsIn` keeps values with **no** `<deadlock>` child **and** `!string.IsNullOrWhiteSpace(value.Value)`.

A value that is empty or whitespace satisfies neither. It lands in neither `Graphs` nor `Signatures`, never increments `AttributedGraphCount`, and cannot be caught by `IncompletePayloadReason` (`:301-323`) — `eventCount` and `totalEventsProcessed` both count it, so no eviction signal fires and `IsInconclusive` stays `false`.

**Impact.** A capture that is missing a graph reports itself as complete with a shorter signature list. That is exactly the failure mode the type's own contract refuses (`:14-17`, `:43-48`): on the baseline side of a differential comparison, under-reporting presents itself as a fix. Narrow — it needs an `xml_report` that renders empty — but the whole point of the truncation/eviction checks is that this class of loss must never be silent.

**Recommendation.** Delete the `&& !string.IsNullOrWhiteSpace(value.Value)` clause at `:290`. The empty string then reaches `SignaturesOf`, whose `XElement.Parse("")` throws `XmlException` and returns `UnparsableGraphSignature` — the behavior the surrounding code already relies on. Add a `[TestCase]` for a `<value />` payload alongside the existing ones in `Given_Mssql_DeadlockGraphSignatures.cs:106-120`.

### 3. [Low] `AttributedGraphCount` counts unparsed payloads as attributed to the leased database — **FIXED**

**What's wrong.** `DeadlockGraphReader.cs:118-123` increments `attributedGraphCount` for every unparsed payload unconditionally, while parsed graphs (`:109-115`) only count after `TargetsDatabase` succeeds. A payload the reader could not reduce to XML carries no `currentdbname` and no qualified `objectname`, so it is by definition *not* attributable.

**Impact.** `ReportDeadlockCaptureAsync` prints it as "`{N}` attributed to the leased database" (`MssqlConcurrentWriteLoadTestBase.cs:301-303`). That number is what a reader compares across a baseline and a candidate run; an unparsed foreign graph inflates it on whichever side happened to see one. Nothing is lost by not counting it — the graph still lands in `Graphs`, and it still contributes `UnparsableGraphSignature` to `Signatures`.

**Recommendation.** Drop the `attributedGraphCount++` at `:121`. One line; the signature and evidence behavior is unchanged.

---

## Test coverage gaps

### 4. [Medium] `Given_Mssql_StampTriggerContention`'s only assertion is vacuous when the circuit breaker trips — **FIXED**

> **Resolution.** `AssertLoadReachedTheDatabase` added and called from both cases. See *Resolutions*
> at the end of this file.

**What's missing.** Both cases end with `persisted.Should().Be(result.Accepted)` (`Given_Mssql_StampTriggerContention.cs:98-104` and `:137-143`). Nothing asserts that the load actually reached the database. The ticket's trap #2 is precisely this: "One deadlock opens [the circuit breaker] and the next ~398 requests fail without reaching the database". In that run `Accepted == 0` and `persisted == 0`, so `0 == 0` passes green while the fixture measured nothing.

**Why it matters.** This fixture exists to produce a *comparable* report on a baseline and a candidate. A run that measured nothing must not be indistinguishable from a clean run — the same principle the author applied rigorously to `DeadlockGraphReader`, where an incomplete capture is forced to `InconclusiveReason` rather than a short list. The sibling fixture already carries the guard: `Given_Mssql_FirstPhaseContentionUnderConcurrentWrites.cs:155-160` asserts `lockWaits.WaitingTasks > 0` with exactly this reasoning ("the load must actually make writers wait…"). And `CaptureLockWaitsAsync`'s own doc comment argues for the same use — "contention the retry pipeline absorbed into successful responses still shows up here, which is what makes it usable as the precondition for an assertion about response status" (`MssqlConcurrentWriteLoadTestBase.cs:331-334`) — yet this fixture captures `LockWaits`, prints it, and asserts nothing about it.

Note this is *not* the `deadlockGraphs > 0` precondition the fixture deliberately omits (`:27-31`), and it does not reintroduce it: lock waits are present on both the unfixed and the fixed side, deadlocks are not.

**Recommendation.** Add one line to each case (or fold into a shared helper), ahead of the equality:

```csharp
result.Accepted.Should().BeGreaterThan(0,
    "a run the circuit breaker rejected before it reached the database measures the breaker, "
    + "not the trigger, and would satisfy the durability check trivially");
```

Suggested name if split out: `It_should_reject_a_load_that_never_reached_the_database`. Asserting `result.LockWaits.WaitingTasks > 0` instead would match the sibling exactly and is the stronger form; `Accepted > 0` is the smaller edit and closes the vacuity.

---

## Maintainability risks

### 5. [Low] `It_should_stamp_every_document_when_one_statement_produces_a_multi_row_mirror_workset` hard-codes the seeded `Contact` count — **FIXED**

**What's wrong.** `MssqlGeneratedDdlAuthoritativeSmokeTests.cs:781-787` runs `UPDATE [edfi].[Contact] SET [FirstName] = @firstName;` with no `WHERE`, then asserts `affectedRows.Should().Be(documentIds.Count)` — where `documentIds` is `[_seedData.ContactDocumentId, _seedData.OtherContactDocumentId]` plus the 10 rows the test inserts. That equals 12 only while `SeedSmokeRowsAsync` creates exactly two `edfi.Contact` rows (`:2908`, `:2921`). The per-test `_database.ResetAsync()` in `[SetUp]` (`:154-159`) rules out cross-test contamination, so the coupling is purely to the seed.

**Impact.** A future seed that adds a third Contact fails this test with `"the statement must reach every Contact for the workset to be multi-row"` — a message that points the reader at the workset when the actual cause is a stale list. The test's real intent (workset size > 1, every member stamped) is unaffected by the seed's cardinality.

**Recommendation.** Build the list from the table instead of from the seed constants, immediately before the `UPDATE`:

```csharp
List<long> documentIds = await ReadAllContactDocumentIdsAsync(); // SELECT [DocumentId] FROM [edfi].[Contact]
```

then keep `affectedRows.Should().Be(documentIds.Count)` and add `documentIds.Count.Should().BeGreaterThan(2)` to preserve the "deliberately larger than two" property the comment relies on.

---

## Resolutions

All five findings are fixed on the branch.

### Finding 1 — `AuthTriggerBodyEmitter.EmitHierarchicalUpdateBody`

`EmitHierarchicalUpdateBody` now wraps both the tuple `DELETE` step and the tuple `MERGE` step in
`IF UPDATE(<parent id>) OR … BEGIN … END`, ranging over exactly the `DenormalizedParentIdColumn`s
that the body's own null-safe predicates read. Those predicates stay authoritative, so the gate can
only skip a firing whose tuple set would have been empty — the same safety argument the stamping
guard already carries.

Scoped to SQL Server (`dialect.Rules.Dialect != SqlDialect.Mssql` falls through unchanged) because
`UPDATE(col)` has no PostgreSQL equivalent and, per the ticket, PostgreSQL does not take the
index-key and range locks this closes. Entities with no parent FK also fall through, so the leaf
shape cannot emit an empty `IF`.

- **Goldens regenerated:** the `auth-edorg-hierarchy` ddl-emission golden plus the three
  authoritative `mssql.sql` files and their manifests, and the SchemaTools
  `provisioned-schema.mssql.manifest.json` (regenerated against the local `dms-mssql`; exactly the
  5 `AuthHierarchy_Update` definitions changed, nothing else). **Zero PostgreSQL golden drift**,
  matching the branch's existing position that this is SQL Server locking behavior.
- **Gates emitted in DS 5.2:** 5, including the three-parent
  `IF UPDATE([EducationServiceCenter_EducationServiceCenterId]) OR UPDATE([ParentLocalEducationAgency_LocalEducationAgencyId]) OR UPDATE([StateEducationAgency_StateEducationAgencyId])`.
- **New test:** `Given_DdlEmitter_With_AuthEdOrgHierarchy_For_Mssql.It_should_gate_the_hierarchical_update_trigger_on_its_parent_id_columns`
  pins the gate, that both steps sit inside it, and that it does not widen to the identity column.
- **Mutation-verified:** inverting the dialect check so SQL Server takes the ungated path and
  regenerating with `UPDATE_GOLDENS=1` fails **only** the new test — the other 1277 pass, goldens
  included. That is the gap: the golden absorbs the regression, so the assertion had to be
  independent of it.
- **Runtime-verified:** all 25 `Given_A_Provisioned_Mssql_Database_With_Auth_EdOrg_Hierarchy_Triggers`
  tests pass against `dms-mssql` with the gate deployed — value→value, value→null, null→value,
  multi-row, mixed-transition, and same-value no-op reparenting. Those cases also compile-cover the
  multi-parent `OR` separator, which the single-parent unit fixture cannot reach.

No design-doc change: neither `auth.md` nor the epic doc reproduces this trigger's body, and
`change-queries.md` invariant 6 is correctly scoped to the content stamps. The rationale lives in
the emitter comment and the new test.

### Finding 4 — `Given_Mssql_StampTriggerContention.AssertLoadReachedTheDatabase`

Both cases now call `AssertLoadReachedTheDatabase(result)` before the durability equality. It
asserts `Accepted > 0` and `LockWaits.WaitingTasks > 0` under one `AssertionScope`, so a run the
circuit breaker rejected before it reached SQL Server fails loudly instead of satisfying
`persisted == Accepted` as `0 == 0`.

Lock waits rather than deadlock graphs, deliberately: contention is present on both the unfixed and
the fixed side while deadlocks are not, so this stays runnable on a candidate where the cycles are
gone — unlike the `deadlockGraphs > 0` precondition the fixture documents omitting. It also survives
a run the retry pipeline absorbed entirely into successful responses. Same guard the sibling
`Given_Mssql_FirstPhaseContentionUnderConcurrentWrites` already carries.

Not executed here: the fixture is `[Explicit]` and drives a deliberate deadlock storm. The change is
compile- and format-verified only; the query, columns and load shape are untouched.

### Findings 2 and 3 — `DeadlockGraphReader`

`UnparsedGraphsIn` is now the exact complement of `DeadlockGraphsIn`: the
`!string.IsNullOrWhiteSpace(value.Value)` clause is gone, so an `xml_deadlock_report` whose payload
rendered empty no longer satisfies neither walk. It reaches `SignaturesOf`, fails to parse, and
lands on `UnparsableGraphSignature` like every other payload the reader cannot reduce to tuples —
which is what keeps a capture that is missing a graph from reporting itself complete.

The `attributedGraphCount++` on that same walk is removed. A payload that did not parse carries
neither a `currentdbname` nor a qualified `objectname`, so it cannot be attributed to the leased
database, and `ReportDeadlockCaptureAsync` prints that number as "N attributed to the leased
database". Nothing is lost: the graph still lands in `Graphs` as evidence and still contributes a
signature, which is the half a differential comparison must not under-report.

New coverage in `Given_Mssql_DeadlockGraphSignatures`:
`It_reports_an_event_payload_with_no_content_as_unparsable` with `<value />`, `<value></value>` and
whitespace-only cases, plus an `AttributedGraphCount == 0` assertion added to the existing
not-a-graph test. The test writes its own `value` element rather than going through the
`DeadlockEvent` builder, which puts the graph on its own line and so can never produce a genuinely
empty one — a first pass at the test did go through the builder and silently exercised the
whitespace path three times.

Both fixes mutation-verified against 18 tests in that fixture:

| Mutation | Fails | Passes |
|---|---|---|
| restore `&& !string.IsNullOrWhiteSpace(value.Value)` | exactly the 3 new cases | 15 |
| restore `attributedGraphCount++` | the 4 tests asserting the count | 14 |

### Finding 5 — multi-row workset test

`documentIds` now comes from `ReadAllContactDocumentIdsAsync()` instead of the seed constants plus
the insert loop, so the expectation tracks what the seed actually created. `Count > 2` now carries
the multi-row claim the hard-coded list used to imply. The `affectedRows` equality is kept but
rescoped in its reason string: with the list read from the table it is a consistency check that the
rows asserted below are the rows the statement stamped, not an independent claim about workset size.

---

## Verified clean (checked this round, no finding)

Recorded so a later round does not re-derive them:

- **Guard column set** — `EmitMssqlDescriptorUpdateColumnDisjunction` and `EmitMssqlDescriptorColumnDiffDisjunction` both iterate `_descriptorStoredColumns` (`CoreDdlEmitter.cs:1978`, `:2005`); the resource shape passes the same `storedColumns` to both. No emitted guard anywhere lists a stamp column (958 guards across all `mssql.sql` goldens, 0 matches for `UPDATE([ContentVersion])` / `UPDATE([ContentLastModifiedAt])`).
- **DELETE arm of the guard** is execution-covered by a pre-existing test — `DELETE FROM [dms].[Descriptor]` then `afterResourceDelete.ContentVersion.Should().BeGreaterThan(before.ContentVersion)` (`MssqlGeneratedDdlAuthoritativeSmokeTests.cs:2397-2405`); dropping the `NOT EXISTS (SELECT 1 FROM inserted)` disjunct fails it.
- **FK-cascade arm** is covered as the emitter comment claims — `It_should_stamp_indirect_Identity_propagation_changes_via_native_fk_cascades_without_disabling_constraints` asserts `afterCourseOffering.ContentVersion` advances after a cascade writes `Session_SessionName` into the root (`:1177`).
- **`_ext` shape unguarded-ness** is not only golden-pinned — `It_should_stamp_root_extension_inserts_without_touching_identity_stamps` (`:894`) would fail if an `_ext` trigger were ever classified as a root shape. The `ext` fixture emits 5 stamping triggers and exactly 2 guards, which is correct.
- **FORCESEEK satisfiability** is checked across all 17 mssql-emitting fixtures including the three authoritative ones — `AuthoritativeDdlGoldenTests.cs` subclasses `DdlGoldenFixtureTestBase`, which now runs `MssqlForceSeekInvariant`. `ReadMssqlPrimaryKeyLeadColumns`'s failure modes (no PK match, truncated body) all fail loudly rather than passing silently.
- **Recursion termination** on a root insert: mirror `UPDATE` re-fires the stamp trigger, the re-firing's `@stamped` pre-population inserts nothing (`del.[DocumentId] IS NOT NULL`), so `IF EXISTS (SELECT 1 FROM @stamped)` is false and it stops at depth 2.
- **Sibling triggers on the root table** — `TR_<Root>_ReferentialIdentity` is gated `IF NOT EXISTS (SELECT 1 FROM deleted) … ELSE IF (UPDATE([<identity>]))`, so it does nothing on a stamp-only re-firing. Only `AuthHierarchy_Update` is ungated (finding 1).
- **CI reachability** — `Given_Mssql_DeadlockGraphSignatures`'s `ApiIntegration` + `MssqlIntegration` categories match the filter at `.github/workflows/on-dms-pullrequest.yml:826`, and `MssqlCiShards` applies only to the `Backend.Mssql.Tests.Integration` assembly, so no shard category is needed.
- **No stale artifacts** — no `OPTION (RECOMPILE)` remains in emitted SQL (only in two explanatory comments), no reference to the retired `Given_Mssql_ConcurrentChartOfAccountCreates`, no `FORCESEEK` leaked into any PostgreSQL golden. `DMS-1400` at `MssqlConcurrentWriteLoadTestBase.cs:419` is pre-existing and out of scope.
