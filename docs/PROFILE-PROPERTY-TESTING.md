# Profile property testing spike (DMS-1684, location 1)

This implements recommended location 1 from [DMS-1684](https://edfi.atlassian.net/browse/DMS-1684).
CsCheck 5.0.1 is centrally pinned in `src/Directory.Packages.props`, referenced only by the
existing Backend and Core unit-test projects, and recorded in their package lock files.
Tests use ordinary NUnit 4.2.2 tests and FluentAssertions. Production behavior and
`RelationalMappingVersion` are unchanged.

## Implemented coverage

| Subject / test file | Generator and independent oracle |
| --- | --- |
| `ProfileCollectionPlannerPropertyTests.cs` | Immutable stored-row descriptions, composite numeric/string identities, root or collection-parent addresses, visibility, omissions, creatability, request reorderings and inserts. An independent boundary-position model specifies the complete output sequence. Reference assertions and snapshots verify that hidden rows survive unchanged exactly once, matched entries keep their stored identities and hidden paths, and candidates are carried through unchanged. |
| `ProfileCollectionPlannerPropertyTests.cs` (contract failures) | Start with valid linked metadata and corrupt one contract at a time. Sixteen cases cover candidate/request/stored scope, parent scope and ancestor identity, duplicate current/stored/request/candidate identities, both directions of request coverage, stored coverage and both ordering contracts. Assert the exact exception type, invariant name and scope. Separately reject an unmatched non-creatable request item. |
| `ProfileSeparateTableMergeDeciderPropertyTests.cs` | Exhaust all 32 combinations of request state, stored state and physical-row existence, sampling parent identity and creatability. A declarative decision table specifies insert/update/delete/preserve/create-denied outcomes; unlisted tuples must fail closed. This includes caller-contract failures, not just valid profile combinations. |
| `ReadableProfileProjectorPropertyTests.cs` | Bounded documents with root identity/reference fields, metadata, hidden scalars, a nested object, filtered items, hidden collections and nested child collections. Build expected JSON directly from immutable descriptions. Test both member-selection modes and both value-filter modes, metadata/identity preservation and source immutability, including mutation of nested output. |
| `WritableRequestShaperPropertyTests.cs` | Reuse the immutable document descriptions, with a compiled scope catalog built fresh per sample. Independently construct the expected writable document, root identity surface, visible item identities, original request paths, nested parent identities and typed value-filter failures. Hidden submitted members are stripped without a validation failure. |

The projection/shaping oracle deliberately distinguishes readable omission of empty
collections from writable retention of empty arrays. Readable root identity references
are preserved as whole references; writable hidden references retain only resource
identity leaves. Writable metadata is not assumed to have readable preservation rules.

Both filtering tests use a `kind` field that is removed from output. They verify filtering
against the original item before member removal; they do **not** assert unconditional
projection idempotence.

## Bounds and execution

Run from the repository root:

```bash
dotnet restore src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --locked-mode
dotnet restore src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --locked-mode

dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit \
  --no-restore --filter 'Category=ProfileProperty' --logger 'console;verbosity=normal'
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit \
  --no-restore --filter 'Category=ProfileProperty' --logger 'console;verbosity=normal'
```

These tests also run in the projects' ordinary unfiltered unit-test runs, so no new CI
lane or database service is needed. Use the same filtered commands for focused PR checks.

The 88 NUnit cases perform 8,000 evaluations on a successful run:

- Planner success: 200 samples each for empty, all omitted, all hidden, all visible,
  mixed, guaranteed surplus before trailing hidden rows, and unconstrained visibility.
- Planner rejection: 200 samples. Contract corruptions: 100 samples each for 16 cases.
- Separate-table decision matrix: 50 samples for each of 32 combinations.
- Projection and shaping: 100 samples for each of 32 combinations (four collection
  shapes × two member modes × two filter modes × two subjects).

Planner descriptions contain at most eight stored rows and ten inserts. Filtering
descriptions contain at most six parent items with three children each. Empty, all-hidden,
all-visible and mixed shapes are explicit test parameters, not probabilistic coverage.
Planner output reports observed root/nested and surplus distributions; filtering output
reports each shape/mode count. For example, the initial successful planner run exercised
96 nested and 104 root cases in the guaranteed-surplus class, all 200 with surplus entries.

Sampling uses one thread per property to keep assertions and distribution accounting
simple and to avoid multiplying CI concurrency. Every sample and shrink attempt constructs
fresh JSON, metadata and subjects; NUnit `[SetUp]` would run only once per test and is not
used to hold mutable generated state. Generators do not discard inputs with `Where`.

## Failure reporting, shrinking and replay

CsCheck exceptions appear as ordinary NUnit failures with the underlying FluentAssertions
failure, a replay seed, shrink counts and a serialized immutable scenario. Keep the full
failure output, test name/parameters, source revision and CsCheck version together.

Replay a specific planner parameter case with:

```bash
CsCheck_Seed=bv8M5o3Clp_1 dotnet test \
  src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~It_preserves_hidden_rows_and_merges_visible_items_in_request_order&Name~surplus' \
  --logger 'console;verbosity=normal'
```

Replace the seed and filter with those of the failing test. `--no-build` assumes the intended
revision has already been built. Replay starts with the recorded case and may shrink it
further during the remaining evaluations, so the final reported seed can change. Fixed
iteration arguments intentionally keep normal PR runs bounded; changing `CsCheck_Iter`
alone does not override them. Keep generator definitions and framework version unchanged
for seed replay. A seed is a debugging aid, not a permanent regression specification.

For an actual discovered defect, add an ordinary deterministic `Given_` / `It_` example
using the minimized semantic input and an exact expected result in the existing subject's
test file. Keep that regression alongside the property after fixing the defect. Do not
retain only a seed whose meaning changes when the generator changes.

### Observed mutation experiment

Temporarily disabling the planner's `if (i == lastVisibleStoredIndex)` flush by replacing
its condition with `i == -2` moved surplus visible entries behind trailing hidden rows.
The `surplus` property failed in NUnit, reporting seed `bv8M5o3Clp_1`, nine shrinks and
200 total generated attempts. Its reduced stored shape was hidden/visible/hidden with a
matched middle row and ten inserts. The expected sequence ended with the trailing hidden
row; the broken sequence placed it third, ahead of the inserts. Replaying that seed with
the same mutation failed again and shrank further. The source and assemblies were restored.
The replay command above passes against the correct implementation.

The reported reduced scenario was:

```json
{
  "Rows": [
    { "Visible": false, "Keep": false, "Priority": 3, "Creatable": false },
    { "Visible": true, "Keep": true, "Priority": 4, "Creatable": true },
    { "Visible": false, "Keep": true, "Priority": 3, "Creatable": true }
  ],
  "InsertPriorities": [7, 18, 7, 7, 18, 5, 14, 6, 19, 12],
  "ParentId": 796,
  "Nested": true,
  "Payload": "é雪"
}
```

Shrinking is bounded by the sample budget and scenario constraints; this is the reduced
case observed in that run, not a claim of a globally minimal counterexample. See the
[CsCheck documentation](https://github.com/AnthonyLloyd/CsCheck) for generation and replay APIs.

This demonstrates a nontrivial property detecting an intentional production mutation,
NUnit failure propagation, shrinking and seed replay. It does not represent a newly
discovered production defect. The existing deterministic
`Given_Planner_example_5_matched_middle_trailing_hidden_insert` fixture already retains a
small example of this ordering contract.

## Results and limits

Validation also included an analyzer-enabled unfiltered run:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --no-restore
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore
```

The Backend suite passed 4,827 tests (nine seconds of reported test duration), and the
Core suite passed 9,229 tests (15 seconds). Both ran with the repository's normal analyzers
enabled and had zero failures or skips. Locked-mode restore succeeded for both projects;
CSharpier checked all five new C# files successfully. No production defects were observed.

Initial focused runs passed all generated cases. The Backend property run took about
1.7 seconds (before adding the two ancestor-mismatch variants), and the Core property run
took about 1.0 second, as reported by `dotnet test`, excluding build time. These are local
observations, not benchmarks or CI runtime guarantees. The normal analyzer-enabled build
is substantially more expensive than executing the properties.

Recommend keeping these bounded properties in the existing unit suites alongside the
example tests. Explicit scenario descriptions and direct expected outputs make failures
reviewable; linked metadata construction is the main maintenance cost. Seed replay depends
on generator stability, which is why minimized example regressions remain necessary.

These are planner and filtering unit contracts, not database persistence proofs. Follow-up
coverage can add provider-specific semantic identity equality, generated schema catalogs,
extensions, multiple sibling parent scopes in one merge, and API/provider-backed checks
that the persister applies the plans correctly. Existing example tests remain in place.
Other recommended locations in DMS-1684 are outside this implementation's scope.
