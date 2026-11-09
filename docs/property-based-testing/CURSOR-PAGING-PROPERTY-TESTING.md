# Cursor paging property tests (DMS-1684, location 5)

This implements the unit-test portion of recommended location 5 from
[DMS-1684](https://edfi.atlassian.net/browse/DMS-1684). The existing Core NUnit project already
references centrally pinned CsCheck 5.0.1. No production behavior, package versions, or
`RelationalMappingVersion` changes are needed.

## Implemented coverage

- `PageTokenCodecPropertyTests.cs` compares encoding with an independent wire oracle:
  invariant signed decimal fields, explicit `d`/`c` markers, and standard base64 converted
  to the URL alphabet. Exact decoding and padded/unpadded equivalence cover arbitrary,
  equal, strictly inverted, minimum-bound, maximum-bound, and reversed-extreme ranges.
  Empty maxima decode to `Int64.MaxValue`; leading zeros and signed zero remain accepted.
- Corruptions independently target markers, field counts, each bound's grammar, positive
  and negative overflow in either bound, the base64 alphabet, padding, and invalid UTF-8.
  UTF-8 cases include isolated continuation bytes, overlong encodings, truncated sequences,
  encoded surrogates, and values beyond Unicode's range, embedded beside generated bounds.
  Every invalid token must return false and no range. These are rejection contracts; they
  do not prescribe which internal decoder or grammar check rejects a token.
- Continuation properties use `BigInteger` arithmetic as the successor oracle. Both modes
  explicitly exercise `Int64.MinValue`, -1, 0, 1, `Int64.MaxValue - 1`, `Int64.MaxValue`, and
  generated anchors. Each checks an arbitrary maximum, the selected anchor as the partition
  ceiling, and an unbounded maximum. The ceiling is preserved, reaching a finite ceiling
  produces an inverted continuation range, and `Int64.MaxValue` produces no token.
- `CursorRequestValidatorPropertyTests.cs` checks exact retained bounds (including inverted
  ranges), zero/interior/maximum/default page sizes, six invalid size forms, unresolved
  ordering, and input immutability. Explicit competing-error cases pin token/anchor errors
  before offset, limit, total-count conflicts, page-size syntax, and total-count syntax.
  All eight pageSize/limit/offset presence combinations are exercised without a token and
  again with an empty token, including blank and malformed values.
- `ChangeQueryPageOrderingPolicyPropertyTests.cs` uses an explicit window-shape expectation
  for primary, replica, and snapshot sources, with the legacy switch both ways. Absent,
  empty, minimum-only, maximum-only, and two-bound windows are tested against generated,
  extreme, inverted, and zero values. Direct live/snapshot resolution and target dispatch
  must agree with that expectation. Matching tokens retain their own bounds, independently
  of the change-version window, and tokens for the opposite anchor reject.

Tokens describe bounds and ordering, not authority. These unit tests cannot establish
provider authorization or execute an inverted SQL range. They preserve inverted bounds
through validation; the match-nothing and authorization execution checks belong in the
provider follow-up below and the existing API integration examples.

## Bounds, distributions, and execution

There are 104 NUnit cases and 10,400 generated evaluations per successful run. Each case
prints its explicit distribution and runs 100 samples on one thread with no discard filters.

| Property | Cases | Generated evaluations |
| --- | ---: | ---: |
| Exact wire encoding and round trip: two modes × six shapes | 12 | 1,200 |
| Empty maximum and alternate decimal spelling: two modes | 2 | 200 |
| Rejection: two modes × eight corruption families | 16 | 1,600 |
| Successor: two modes × seven anchor classes | 14 | 1,400 |
| Page sizes and retained ranges: two modes × four shapes | 8 | 800 |
| Validation precedence: two modes × seven first failures | 14 | 1,400 |
| Tokenless routing: three presence flags | 8 | 800 |
| Ordering: three sources × two switch values × five window shapes | 30 | 3,000 |

Several assertions run per evaluation, including all listed invalid forms within a corruption
family and all three ceiling forms in a successor case. Counts describe evaluations, not
individual assertions or a measured random-value histogram. Generated bounds span signed
Int64 values; important extremes and range shapes are forced rather than left to chance.
Immutable records describe inputs. Fresh dictionaries and subjects are built inside each
sample/shrink attempt; NUnit setup would run only once per test case.

```bash
dotnet restore src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --locked-mode

dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore \
  --filter 'Category=CursorPagingProperty' --logger 'console;verbosity=normal'

dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore
```

The category also runs in normal unfiltered local/CI unit tests. CsCheck failures report a
seed, shrink counts, and serialized scenario. Retain the NUnit parameters, source revision,
CsCheck version, and complete output. Replay with `CsCheck_Seed` and the same test filter.
Seeds depend on unchanged generators/framework; fixed `iter` arguments are not overridden
by `CsCheck_Iter`. Convert any real failure into an ordinary deterministic NUnit regression
using its reduced semantic input, retaining the seed as supporting evidence.

## Mutation and replay evidence

Three temporary production edits were tested with `-p:RunAnalyzers=false`, then restored:

| Mutation | Selected property | Observed result |
| --- | --- | --- |
| Remove `+ 1` from the next-token minimum | Successor, DocumentId/generated | Failed; seed `ep5Jg_Q9LWz1`, seven shrinks in 100 attempts. |
| Remove the request/token ordering-mode comparison | Validation precedence, ContentVersion/mismatched | Failed; seed `4z_vIVqEe3n1`, five shrinks in 100 attempts. The wrong implementation reported the offset conflict instead of invalid token. |
| Make snapshot resolution ignore minimum-only windows | Ordering, Snapshot/False/minimum | Failed; seed `5GN-lCc1InU1`, two shrinks in 100 attempts. |

With the successor mutation present, this replay failed again with the same seed and reduced
input, with no further shrinks:

```bash
CsCheck_Seed=ep5Jg_Q9LWz1 dotnet test \
  src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore -p:RunAnalyzers=false \
  --filter 'Category=CursorPagingProperty&Name~It_advances&Name~generated&Name~DocumentId' \
  --logger 'console;verbosity=normal'
```

The reduced input was `{"Minimum":0,"Maximum":-2040}`. The required successor token encodes
`d,1,-2040`; the broken code encoded `d,0,-2040`. This is the observed reduced input, not a
claim of global minimality. No temporary production mutation is retained. These deliberate failures are test-strength
evidence, not discovered production defects.

## Validation results and adoption

The focused analyzer-enabled run passed all 104 cases (10,400 generated evaluations) in
0.90 seconds of reported test time. After restoring the temporary mutations, the full Core
unit suite passed 9,373 tests with zero failures or skips in 16 seconds, using normal analyzers.
The final assembly also passed all 104 property cases with `CsCheck_Seed=ep5Jg_Q9LWz1` in
0.87 seconds using `--no-build --no-restore`, confirming the recorded failure seed succeeds
with the correct implementation. Build time is excluded from these test-run figures.
Locked restore, CSharpier checking of the four new C# files, and `git diff --check` passed.

Initial build feedback identified style-analyzer violations in the new test harness; these
were corrected before validation. No production defect was observed. No provider-backed
integration or Docker-stack E2E tests were run for this unit-test addition.

Keep these bounded properties in ordinary PR unit runs alongside the existing examples.
The local runtime supports that choice, but is not a benchmark or CI performance guarantee.
Maintaining the wire oracle and explicit validation/ordering expectations is the main cost;
these expectations must change deliberately when the public paging contract changes.
Database filtering, authorization enforcement, and complete walks remain separate integration
responsibilities rather than guarantees established by these unit properties.

## Provider-backed follow-up (proposed, not implemented here)

Add a bounded `CursorPagingPropertyScenario` under
`src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios/`, with PostgreSQL and
SQL Server wrappers in `Tests/Postgresql/` and `Tests/Mssql/`. Reuse the harness patterns in
`CursorPagingExecutionScenario`, `PartitionWalkCoverageScenario`, and
`CursorPartitionAuthorizationMatrixScenario`/`Support`. Existing examples already cover
seed-derived partition coverage and authorization; this follow-up would vary their linked
inputs with CsCheck rather than replacing those examples.

1. Generate immutable descriptions of 0–40 documents with known identity keys, resource
   filters, namespace/relationship authorization membership, and a deterministic sequence
   of inserts/updates that makes document-ID order differ from content-version order.
   Include all-authorized, mixed, and none-authorized populations, and authorization joins
   matching several rows for one document. Rebuild and seed an isolated leased database on
   every evaluation and shrink attempt. Read persistence-assigned IDs/content versions from
   fixture seeding infrastructure, not from the paging endpoint under test.
2. Stop writes before walking and keep data, filters, and authorization inputs fixed. Use a
   real frozen snapshot for snapshot-specific minimum-only ordering, and a quiescent fixture
   for live-source policy. In memory, independently apply authorization and resource/window
   predicates to the seeded model, then sort by the expected anchor. Compare the complete
   sequence of page IDs with this oracle, including multiplicity. Do not merely compare
   two API surfaces that could share the same filtering defect.
3. Generate page sizes 1–5 and partition counts 1–4; configure host maximum page size 2 as
   in `PartitionWalkCoverageScenario`, so populations can cross the minimum partition size
   of ten. Force empty, singleton, exact-page, partial-page, and multiple-partition cases.
   Compare sequential page concatenation with the sorted oracle. Walk each returned
   partition; require disjoint ID sets and an exact union with the oracle. Sort the union
   for comparison when partitions were consumed concurrently. Bound walk length by the
   seeded row count plus a terminal request, so a nonadvancing token fails promptly.
4. Widen/replay syntactically valid tokens while independently changing the requesting
   principal. Every returned row must still satisfy the new principal's authorization and
   the current query filters: tokens grant no authority. Include inverted ranges expecting
   an empty result and pageSize zero as a single empty-page check, not a progress walk.
5. Run a small explicit count (for example ten evaluations per provider/shape) in integration
   CI with available admin connection strings. Costs include schema provisioning, fixture
   cleanup during shrinking, authorization catalogs/relationships, snapshot setup, and
   persistence metadata capture. Follow the integration README and repository instructions
   for PostgreSQL and SQL Server 2025 connectivity; do not use Docker-stack E2E setup.

Do not apply the frozen-dataset completeness oracle while writes are changing a live source.
Existing concurrency scenarios exercise those separate, weaker contracts. Unit properties
remain suitable for every PR; provider properties need a separate runtime budget and reliable
per-sample reset before adoption.
