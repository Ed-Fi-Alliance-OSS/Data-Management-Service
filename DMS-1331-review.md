# Code Review: DMS-1331 — Anchor relationship-authorization predicates on the root row

**Branch:** DMS-1331
**Base:** main
**Date:** 2026-08-17
**Commits reviewed:** 12 branch-owned (`f18409043`..`58c2156c8`; merged PRs `a4764cb70` and earlier are out of scope)
**Round:** 7 (confirmation pass over Round 6 + fresh pass over the compiler unit suite, the planner-binding fixture, the executed deep-offset evidence, and the CI shard wiring)

---

## Summary

| Category               | Resume                                                                              | High | Medium | Low  |
|------------------------|-------------------------------------------------------------------------------------|------|--------|------|
| Design/spec drift      | Timing report still prints the prototype projection its own run contradicts          | 0    | 0      | 1    |
| Test coverage gaps     | Transitive intermediate-join loop never executed at a nonzero iteration count        | 0    | 1      | 0    |
| Simplification         | Emitter re-derives claim parameterization; hand-built quoted relation in both twins   | 0    | 0      | 2    |
| **Total**              |                                                                                     | **0**| **1**  | **3**|

No correctness or maintainability findings this round. Production code (`PageDocumentIdSqlCompiler.cs`,
`RelationshipAuthorizationPeoplePathValidation.cs`) is unchanged since Round 4 and re-verified below.

**All four findings are fixed** — see [Resolutions](#resolutions) at the end. No production code was touched by
the fixes either; all seven changed files are test or test-support code.

---

## Round 6 verification

| Round 6 finding | Status | Evidence |
|---|---|---|
| AC3's ≥100k deep-offset evidence never executed | **Closed** | `DMS-1331-deep-offset-evidence.log` at repo root (untracked), 6/6, `OFFSET 100000` plans captured for both pathways |
| Claim parameter name hard-coded in the PG row-set fixture | **Closed** | `PostgresqlAnchoredAuthorizationRowSetEquivalenceTests.cs:486-490` binds `RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds` |
| Self anchor column read from two different sources | **Closed** | `RelationalQueryAuthorizationTestSupport.cs:1184-1187` — `TerminalPersonColumn` defers to `AnchorColumn` for the zero-step case |

Independently re-verified this round:

- **Production ≡ emitter, arm by arm.** Traced the Legacy arm against the pre-change methods recovered from the
  diff: Self and Direct both reduce to `r.<anchor> IN (SELECT t0.<anchor> FROM <root> t0 WHERE t0.<person> IN
  (…))` with the same person column production picked (`StoredAnchor.RootDocumentIdColumn` for Self,
  `pathSteps[0].SourceColumnName` for Direct), and the transitive arm's `AppendPathJoins(firstJoinedStepIndex: 0)`
  reproduces the old `Range(0, count-1)` loop exactly. The Legacy arm is a faithful "before".
- **No null target column can reach the new guard from production.** Every person-path `ColumnPathStep` is built
  at `PersonJoinPathResolver.cs:194` (and its `WalkToPersonResource` sibling) with both target table and column
  set; the only null-target constructions are `SecurableElementLocationResolver.cs:62,87` and
  `SecurableElementColumnPathResolver.cs:266`, none of which produce a multi-step person path. The
  out-of-diff third caller (`SingleRecordRelationshipAuthorizationSqlCompiler.cs:462-467`) is therefore
  unaffected by the tightened validation.
- **No golden carries a person-auth predicate.** `grep` over `src/dms/backend/Fixtures` for
  `EducationOrganizationIdTo{Student,Contact,Staff}DocumentId` returns nothing, so the one-fewer-alias
  allocation change cannot shift a committed snapshot.
- **Navigator extraction is a verbatim lift.** `PostgresqlQueryPlanNavigator.cs` is the deleted private block
  from `PostgresqlPeopleAuthViewQueryPlanTests.cs` with visibility widened; the fixture diff is call-site
  requalification only.
- **MSSQL shard wiring is self-enforcing.** `Given_Mssql_Ci_Shard_Guardrails.cs:47` fails any MSSQL fixture
  without exactly one `MssqlCiShards.*` category, and both new/extended MSSQL fixtures carry `Shard1` — which is
  also the smallest shard (30 category usages vs 61/45/93), so the heaviest new fixture landed in the right place.
- **Engine twins are in sync.** Test-method sets are identical between
  `Given_A_Postgresql_Anchored_Authorization_Row_Set_Equivalence` /
  `Given_A_Mssql_Anchored_Authorization_Row_Set_Equivalence` and between the two
  `..._With_Anchored_Person_Pathways` fixtures. No one-sided drift.
- **Interleave arithmetic is sound end to end.** `Stride = total/unauthorized` (5 for both presets), every
  generated table's ordinal is `row_number() OVER (ORDER BY "DocumentId")` over its own `docs` CTE and is joined
  to the student of the same ordinal, so root-row ordinal ≡ student ordinal ≡ DocumentId order. That is exactly
  what `It_should_interleave_unauthorized_rows_across_the_document_id_ordering` asserts
  (`firstPageSize / stride`).
- `dotnet build` of `Backend.Plans.Tests.Unit` and its full dependency chain: **0 warnings, 0 errors**.

Deliberately not re-raised: Round 1's navigator-schema constraint (documented at
`PostgresqlAnchoredAuthorizationQueryPlanTests.cs:42-50`, both configured subjects satisfy it), 2×PG generation
per PR, the AC1 wording scope (Jira comment 98309), the unfiltered-PG-job/MSSQL-shard items, PG/MSSQL twin
duplication, `ZeroPaddedOrdinalSql` reusing the width constant for both the fill and the `varchar(n)` cast, and
`StudentsWithOrdinalSql`'s in-SQL prefix concatenation.

---

## Test coverage gaps

### 1. Production's transitive intermediate-join loop never runs a nonzero number of iterations in any executed test — Medium

**What's wrong.** `AppendRootDocumentIdInTransitivePersonAuthViewSql` runs its intermediate-join loop from
`stepIndex = 1` to `pathSteps.Count - 2` (`PageDocumentIdSqlCompiler.cs:1219-1242`). For a two-hop path that
range is empty, so `pathJoinAliases` is empty and the loop body never executes. Every executed test of the
transitive shape uses a two-hop path:

- `RelationshipAuthorizationDifferentialSpecs.Create` (`RelationalQueryAuthorizationTestSupport.cs:663-678`)
  supplies exactly two transitive subjects — `Grade` → StudentSectionAssociation and `CourseTranscript` →
  StudentAcademicRecord — both two-hop.
- The pipeline-binding fixtures use `CourseTranscript`
  (`PostgresqlRelationalQueryAuthorizationTests.cs:3739`, MSSQL twin at the same offset) — two-hop.
- The plan and deep-offset fixtures use `Grade`
  (`PostgresqlAnchoredAuthorizationQueryPlanTests.cs:52`) — two-hop.

DS 5.2's only three-hop person path (`StudentAssessmentRegistrationBatteryPartAssociation`) appears in exactly
one place: `PageDocumentIdSqlCompilerTests.cs:1929-1971`, where its emitted predicate is compared against a
**hand-authored literal**. That literal is the sole witness for the one code path in this rewrite that has no
executed evidence.

**Evidence.**
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageDocumentIdSqlCompiler.cs:1219-1242` — the loop.
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/RelationalQueryAuthorizationTestSupport.cs:663-678`
  — the transitive spec set, both two-hop.
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageDocumentIdSqlCompilerTests.cs:1929-1971`
  — the three-hop test, expectation hand-authored.
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageDocumentIdSqlCompilerTests.cs:3407-3447`
  — `CreateThreeHopStudentPersonAuthorizationSubject`, used only there.

**Impact.** A three-hop ON-clause that reads correct but is semantically inverted, or a `pathJoinAliases`
mis-indexing that still lands in bounds, would be caught only if the hand-authored expected string happens to be
right. Nothing else in the branch disagrees with it. `StudentAssessmentRegistrationBatteryPartAssociation` is a
real DS 5.2 resource served by this compiler, so a defect there ships as a wrong authorization result set on a
GET-many, not a crash.

**Recommendation — smallest change that closes it, no new fixture and no generator work.** In the existing
three-hop test, replace the hand-authored literal with the differential emitter's own anchored predicate for the
same spec, so two independently written emitters must agree:

```csharp
var emitted = RelationshipAuthorizationDifferentialSqlEmitter.Emit(
    spec, dialect, RelationshipAuthorizationPredicateShape.Anchored, claimIdCount: 1);

plan.PageDocumentIdSql.Should().Contain(ExtractAuthorizationPredicate(emitted.PageSql));
```

This is cheap and it lands the evidence where it is missing. The emitter's `AppendPathJoins` body
(`RelationalQueryAuthorizationTestSupport.cs:1117-1129`) *is* executed against both databases today — the Legacy
arm calls it with `firstJoinedStepIndex: 0`, which runs one iteration for every two-hop spec, and the row-set
differential proves that output row-equivalent to production. Pinning production's three-hop predicate to the
same helper transfers that executed evidence to the un-executed path. Two mechanical details make it a
three-line change: `Backend.Plans.Tests.Unit` already has `InternalsVisibleTo` on `Backend.Tests.Common`
(`EdFi.DataManagementService.Backend.Tests.Common.csproj:13`), and both emitters allocate `t0`/`t1`/`t2` in the
same order for a single-person-subject spec — visible in the existing expectation at
`PageDocumentIdSqlCompilerTests.cs:1931` — so the strings match without normalization.

If you'd rather keep the literal as a readability anchor, assert both; the point is that the literal stops being
the only witness.

---

## Design/spec drift

### 2. The timing report prints the ticket's prototype projection unqualified, next to measurements that contradict it — Low

**What's wrong.** `It_should_report_before_and_after_page_timings` closes every report with:

```
prototype reference (StudentSectionAssociation, perf rig 2026-07-23): -33% at offset 0, -41% to -46% at deep offsets
```

Round 6 executed the fixture and recorded that this does not reproduce. The saved evidence log now carries the
contradiction in adjacent lines:

```
offset 100000 | ... | delta median -22.2%, fastest -22.4%      <- StudentSectionAssociation
prototype reference (...): -33% at offset 0, -41% to -46% at deep offsets

offset 50000  | ... | delta median 4.2%, fastest -5.5%          <- StudentSectionAssociation, anchored slower at the median
offset 100000 | ... | delta median 0.3%, fastest 3.8%           <- Grade
prototype reference (StudentSectionAssociation, perf rig 2026-07-23): -41% to -46% at deep offsets
```

The line is also emitted verbatim under the **Grade** heading, where it names a different resource and sits
directly under a `+0.3%` median.

**Evidence.**
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlAnchoredAuthorizationQueryPlanTests.cs:623-626`
  — the unconditional report line.
- `DMS-1331-deep-offset-evidence.log:1222-1225, 1247-1250` — measured deltas against it.

**Impact.** This report *is* the PR's AC3 evidence — the file exists at the repo root for that purpose. Anyone
pasting it forward carries a performance claim the same run disproves on the same page. The structural claim
(one root scan) and the buffer reduction (SSA 3743→3321, Grade 12421→7376 at 100k) both hold at every measured
point; only the wall-clock projection does not.

**Recommendation.** Alignment goes toward the code, not the ticket: the projection came from a different rig, so
the fixture should stop presenting it as a target. Either drop the line, or make it self-describing in the same
breath — e.g. append `"; not reproduced on non-rig hardware — see the measured deltas above, which are
offset- and resource-dependent"`. If you keep it, scope it to the resource it belongs to rather than printing it
under both headings.

---

## Simplification / dead-code opportunities

### 3. The differential emitter re-derives the claim filter from `(dialect, count)` instead of reading the parameterization the spec already carries — Low

**What's wrong.** `AppendMembershipSubquery` hard-codes both the parameter names and the dialect branch:

```csharp
if (dialect == SqlDialect.Mssql)
{
    var placeholders = Enumerable.Range(0, claimEducationOrganizationIdCount)
        .Select(static index => $"@ClaimEducationOrganizationIds_{index}");
    writer.Append($" IN ({string.Join(", ", placeholders)})");
}
else
{
    writer.Append(" = ANY(@ClaimEducationOrganizationIds)");
}
```

Every spec handed to this emitter already carries
`spec.Authorization.ClaimEducationOrganizationIdParameterization` — a **public** record exposing `Kind`,
`BaseParameterName` and `ParameterNamesInOrder`
(`AuthorizationClaimEducationOrganizationIdParameterization.cs:28-33`), built by
`RelationshipAuthorizationDifferentialSpecs.CreateQuerySpec` from
`RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds`
(`RelationalQueryAuthorizationTestSupport.cs:814-818`). Reading it instead of re-deriving would:

- delete the two hard-coded name literals (the same class of drift Round 6 fixed one level up, at the fixture's
  `ClaimParameter`);
- remove the `claimEducationOrganizationIdCount` parameter from five signatures (`Emit`, `EmitPageSql`,
  `EmitTotalCountSql`, `AppendFromAndWhere`, `AppendAuthorizationPredicate`,
  `AppendAnchoredTransitivePredicate`, `AppendMembershipSubquery`) and from all four call sites in the two
  row-set fixtures and `AnchoredAuthorizationPlanSupport`;
- cover `MssqlStructured` for free. Today the emitter would emit 2000 scalar placeholders where production emits
  `IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)` at ≥2000 claim EdOrgs
  (`AuthorizationClaimEducationOrganizationIdParameterizationFactory.cs:44,78-95`).

**Evidence.**
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/RelationalQueryAuthorizationTestSupport.cs:1134-1162`
  — the re-derivation.
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationClaimEducationOrganizationIdParameterization.cs:28-33,44,78-95`
  — the public record and the TVP threshold.
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationClaimEducationOrganizationIdSqlHelper.cs:50-102`
  — production's own switch, which this duplicates in two of three arms and omits the third.

**Impact.** No live defect: the fixtures pass a one-element claim, and any disagreement surfaces as a
"parameter not supplied" execution failure rather than a silent wrong result. The cost is that a
`RelationalAuthorizationParameterNameConstants` rename, a claim list with duplicates (the factory dedupes,
`.Count` does not), or a claim set at TVP scale each break the emitter for a reason the emitter never had to
know about.

**Recommendation.** Have `SinglePersonSubject`'s caller also pull
`spec.Authorization!.ClaimEducationOrganizationIdParameterization` and switch on its `Kind`, emitting from
`BaseParameterName` / `ParameterNamesInOrder`. `AppendClaimFilterSql` itself is `internal` to `Backend.Plans` and
not reachable here, so this stays a local switch — but one driven by the spec rather than by a re-derived count.
Net effect is fewer parameters and fewer literals, not a new abstraction.

### 4. Both pipeline-binding twins hand-build the quoted root relation where three sibling call sites use the dialect — Low

**What's wrong.** `AssertSingleRootRelationReference` builds the search token by string interpolation:

```csharp
var quotedRootRelation = $"\"edfi\".\"{rootTableName}\"";   // PG
var quotedRootRelation = $"[edfi].[{rootTableName}]";       // MSSQL
```

The three other places in this branch that count root-relation occurrences all derive the token from the dialect
instead — `PageDocumentIdSqlCompilerTests.cs:2343`,
`PostgresqlAnchoredAuthorizationRowSetEquivalenceTests.cs:407`,
`MssqlAnchoredAuthorizationRowSetEquivalenceTests.cs:411` — and
`PageDocumentIdSqlCompilerTests.cs:2340-2342` records *why* (the closing delimiter is what stops
`"edfi"."StudentSchoolAssociation"` from matching inside `"edfi"."StudentSchoolAssociationProgram"`).

**Evidence.**
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlRelationalQueryAuthorizationTests.cs:4086-4093`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlRelationalQueryAuthorizationTests.cs:4851-4858`

**Impact.** None today — the hand-built tokens are correct for both dialects and both include the closing
delimiter, and the twins agree with each other so there is no one-sided drift. It is a second source of truth
for a quoting rule that `SqlDialectFactory` already owns, and it takes the schema name as a literal where the
siblings take it from `DbTableName`.

**Recommendation.** Two lines: take `DbTableName` instead of `string`, and
`SqlDialectFactory.Create(SqlDialect.Pgsql).QualifyTable(rootTable)` (resp. `Mssql`). The callers already have
the resource name as a constant, so constructing the `DbTableName` at the two call sites is a one-liner each.

---

## Resolutions

All four findings fixed on 2026-08-17. Seven files changed, all test or test-support code — no production file was
touched.

| # | Finding | Fix |
|---|---------|-----|
| 1 | Three-hop join loop had one hand-authored witness | New `EmitAuthorizationPredicate` on the differential emitter; the three-hop unit test now cross-checks production against it on both dialects |
| 2 | Report printed an unreproduced projection | Relabelled "NOT reproduced by this fixture", scoped to the one resource the rig measured, plus a line naming shared blocks as the hardware-independent signal |
| 3 | Emitter re-derived the claim filter | Reads `spec.Authorization.ClaimEducationOrganizationIdParameterization`; `claimEducationOrganizationIdCount` gone from seven signatures, `dialect` from three more |
| 4 | Hand-built quoted root relation in both twins | `SqlDialectFactory.Create(<dialect>).QualifyTable(...)` in both `AssertSingleRootRelationReference` helpers |

### 1 — three-hop cross-check

`RelationshipAuthorizationDifferentialSqlEmitter.EmitAuthorizationPredicate` returns the bare predicate with no
page/count scaffolding, and
`It_should_emit_three_hop_transitive_student_authorization_sql_with_ordered_path_joins` now asserts production's
predicate contains it, on both dialects, alongside the pre-existing literal.

The strings compare directly for two reasons worth recording: both emitters allocate `t0`/`t1`/`t2` in the same
order for a single-person-subject spec, and `AppendRelation(PhysicalTable)` delegates to `AppendTable`
(`PlanSqlWriterExtensions.cs:46`), so the two render tables identically. The emitted assertion is also *stricter*
than the literal — the literal stops before the predicate's closing paren, the emitted one does not.

**Non-vacuity confirmed by mutation.** Swapping the ON-clause sides inside the emitter's `AppendPathJoins` failed
the new assertion on both Pgsql and Mssql:

```
" to contain "... t1 ON t0."StudentEducationOrganizationAssociation_DocumentId" = t1."DocumentId" ..."
  because the three-hop predicate the compiler emits must agree with the differential emitter's
Failed!  - Failed: 2, Passed: 0
```

The hand-authored literal still passed under that mutation, which is precisely the independence the finding was
about. Mutation reverted; suite re-run green.

### 2 — timing report

The projection is no longer presented as a target. Two changes: a new line stating that shared blocks are the
hardware-independent signal and fall at every offset, so the measured numbers above are the ones to quote; and the
prototype line relabelled `"historical prototype run for this resource (dedicated perf rig 2026-07-23, NOT
reproduced by this fixture)"` and guarded to print only under
`AnchoredAuthorizationPlanSupport.DirectSubjectResourceName`. It therefore no longer appears under the Grade
heading, where it named a different resource directly beneath a `+0.3%` median.

### 3 — claim parameterization

`SinglePersonSubject` became `SingleAuthorizationSubject`, validating the subject *and* the parameterization in the
one guard that already existed, and a local `AppendClaimFilter` mirrors production's switch over `Kind`. Effects:

- `claimEducationOrganizationIdCount` removed from seven signatures; `dialect` fell out of three more with it and is
  now needed only to create the `SqlWriter` and to pick `LIMIT/OFFSET` vs `OFFSET/FETCH`.
- Both hard-coded `@ClaimEducationOrganizationIds` literals gone — names come from `BaseParameterName` /
  `ParameterNamesInOrder`.
- Three call sites each lost a parameter (`AnchoredAuthorizationPlanSupport.EmitPageSql` and both row-set fixtures'
  local `Emit`).
- `MssqlStructured` is now an explicit `NotSupportedException` rather than silently-wrong scalar placeholders. The
  fixtures bind one scalar parameter per claim id, so the TVP shape has no binding here either; a named refusal
  beats a missing-parameter error that says nothing about the cause. This is narrower than the finding's
  "covers `MssqlStructured` for free" — emitting the TVP SQL without TVP binding would not have worked.

### Verification

| Check | Result |
|---|---|
| Full solution build | 0 errors, 2 warnings (both pre-existing Reqnroll codebehind warnings in E2E projects) |
| `Backend.Plans.Tests.Unit` | 2028/2028 — unchanged count; assertions added, not tests |
| PG `Given_A_Postgresql_Anchored_Authorization*` | 49/49 |
| PG `..._With_Anchored_Person_Pathways` | 10/10 |
| MSSQL row-set + pathways (`127.0.0.1:1435`) | 55/55 |
| `csharpier format` | 7 changed files formatted |
| `csharpier check src` | fails only on `src/Directory.Packages.props`, untouched here — same pre-existing failure Round 6 recorded |

Every count is identical to Rounds 5/6, which is what a refactor emitting byte-identical SQL should produce. The
row-set fixtures passing is itself the evidence that the parameterization now driving the emitter resolves to the
same parameter names those fixtures bind.

**Not executed:** `Given_A_Postgresql_Anchored_Authorization_Deep_Offset_Measurement`, Finding 2's site. It is
`[Explicit]` and regenerates the 150k-row DeepOffset volume in `OneTimeSetUp` (~43 minutes per Round 6). The change
there is a report string with no assertion attached, and the fixture's other three tests were executed in Round 6
against assertion code this round did not touch.
