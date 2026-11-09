# CMS authorization metadata property tests (DMS-1684, location 4)

This implements recommended location 4 from [DMS-1684](https://edfi.atlassian.net/browse/DMS-1684)
in the existing CMS Backend NUnit unit-test project. It adds a package reference and lock-file
entry for the centrally pinned CsCheck 5.0.1. The tests call `AuthorizationMetadataResolver`
directly, including its existing internal comparer overload. No repository, database, Docker,
new production test hook, or `RelationalMappingVersion` change is needed.

## Implemented coverage and independent oracle

- `AuthorizationMetadataPropertyScenarios.cs` generates immutable node descriptions with
  parent indices, defaults, claim-set grants, and strategy overrides. Parent indices always
  precede children, preserving acyclicity during generation and shrinking. Each evaluation
  constructs fresh mutable `Claim` objects through the `Claims` setter, which populates
  parent links at every depth.
- The oracle identifies leaves from the immutable parent indices, walks each leaf's ancestors,
  and independently selects the nearest grant and nearest default for each action. It does
  not use production parent links, resolver traversal, or authorization equality/hashing.
  An empty nearest grant override falls back to defaults, not a more distant override.
  An empty nearest default masks a more distant nonempty default. Defaults never grant actions;
  an empty claim-set entry still includes a leaf with an empty authorization.
- `AuthorizationMetadataResolverPropertyTests.cs` compares exact semantic permissions for
  ordinary resolution, forced hash collisions, and reversed catalog/root/sibling/action/strategy
  order. Claim-set names match case-insensitively. Strategy names retain case and multiplicity.
  Semantic keys use sorted JSON, avoiding delimiter ambiguity, and deliberately exclude IDs.
- Authorization references must resolve within their own claim set, IDs must be positive and
  unique across the response, every emitted authorization must be used, and each claim set
  must contain exactly one authorization per distinct semantic permission set. Equivalent
  reordered permissions reuse an ID; extra strategy occurrences, extra actions, and changed
  strategy/action names remain distinct even when every authorization has the same hash.
  Catalog/traversal permutations may legitimately change IDs.
- Adding and changing a separate root branch must leave all original claims' permissions and
  inclusion unchanged, even when IDs shift. General branching scenarios also compare every
  sibling's permissions against the oracle. Serialized hierarchy content is unchanged after
  ordinary resolution. Existing literal tests retain exact ID/order and empty-catalog coverage.

## Bounds and distributions

There are 56 NUnit cases and 3,800 generated evaluations per successful focused run:

| Property | Explicit cases | Samples per case | Evaluations |
| --- | --- | --- | --- |
| Ancestor oracle, collisions, permutations | Five shapes × three metadata modes | 100 | 1,500 |
| Unrelated branch addition/change | Four nonempty shapes | 100 | 400 |
| Nearest grant/default precedence | Three grant depths × three default depths × override absent/present × default empty/nonempty | 50 | 1,800 |
| Equal and distinct permission deduplication | Reordered equivalent pair, extra strategy occurrence, extra action, changed strategy/action names | 100 | 100 |

Shapes are empty, single leaf, chain, binary branching, and generated forest. Nontrivial
trees contain 5–12 nodes; forests guarantee at least two roots. Metadata modes are defaults
without grants, empty grants for both claim sets, and mixed metadata. The catalog includes
two potentially granted names and one always-absent name. Each node has up to four unique
action names; each action has zero to three strategies drawn from six strings including
case variants, Unicode, delimiters, escapes, and newlines. The collision property forces
duplicate strategy occurrences and exactly five distinct permissions per populated claim set.

The precedence matrix guarantees conflicting ancestor metadata whenever depth permits,
including defaults above and below the grant, and a default-only action that must remain
ungranted. These boundary classes do not depend on random occurrence. Sampling uses one
thread, fixed iteration counts, and no generator discard filters. Every shrink attempt rebuilds
mutable inputs and comparer state. NUnit setup alone would not provide that isolation.

## Execution and replay

```bash
dotnet restore src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --locked-mode

dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit \
  --no-restore --filter 'Category=AuthorizationMetadataProperty' --logger 'console;verbosity=normal'

dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --no-restore
```

These tests also run in ordinary unfiltered local/CI execution. CsCheck reports a replay seed,
shrink counts, and serialized scenario with the NUnit failure. Retain the test parameters,
source revision, framework version, and complete failure output. Replay with `CsCheck_Seed`
and the same test filter; seeds depend on the generator and framework remaining unchanged.
The fixed `iter` arguments bound execution and are not overridden by `CsCheck_Iter`.
Turn any real defect into an ordinary deterministic NUnit regression built from the reduced
semantic scenario, retaining the seed as supporting evidence rather than the sole regression.

## Mutation and replay evidence

Two temporary production mutations were built with `-p:RunAnalyzers=false` and then restored:

| Mutation | Selected property | Observed result |
| --- | --- | --- |
| Replace default `TryAdd` with assignment, allowing distant defaults to overwrite nearer defaults | Ancestor model, branching/mixed | Failed; seed `dU8lflc1Y8p1`, three shrinks in 100 attempts. Replaying failed again and shrank once further. |
| Make authorization equality match any action with any other action, ignoring strategy differences | Forced-collision deduplication | Failed; seed `1IlBAUeavaI1`, three shrinks in 100 attempts. Replaying failed again and shrank once further. |

The replay commands, while the corresponding mutation was present, were:

```bash
CsCheck_Seed=dU8lflc1Y8p1 dotnet test \
  src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --no-restore --no-build \
  --filter 'Category=AuthorizationMetadataProperty&Name~branching&Name~mixed' \
  --logger 'console;verbosity=normal'

CsCheck_Seed=1IlBAUeavaI1 dotnet test \
  src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --no-restore --no-build \
  --filter 'Category=AuthorizationMetadataProperty&Name~It_deduplicates' \
  --logger 'console;verbosity=normal'
```

The collision replay reduced the strategy inputs to `["Allow","Allow","Allow"]` and reported
seed `5va17NDKbpN1`. The generated third claim has one additional `Allow` occurrence for Create;
the broken equality incorrectly reuses the first claim's authorization in both claim sets.
This is an observed reduced input, not a claim of global minimality. The defaults mutation
incorrectly replaced nonempty Read defaults on descendant leaves with an empty root default.

## Validation results

The final focused suite passed all 56 cases (3,800 generated evaluations) with normal analyzers
in 2.53 seconds of reported test time, excluding build time. After restoring both production
mutations, the unfiltered project run passed all 5,375 executed tests in 2.46 minutes with normal analyzers.
Two existing explicit timing-evidence tests were reported skipped because they require a
controlled environment. The final strategy-name/action-name collision variants were added
after that full-project build and validated in the subsequent focused run.
Both original mutation seeds also passed their selected cases against the final restored
assembly (1.03 and 0.86 seconds). Locked restore, CSharpier formatting, and `git diff --check`
passed. Only test files, the test package reference/lock file, and this report are changed.

Initial harness failures were corrected to allow an empty ID collection; the assertions still
require every emitted ID to be positive. No production defect was found. These timings are
local observations, not benchmarks or CI runtime guarantees.

## Limits and follow-up

Keep these bounded properties alongside the literal examples. The local runtime supports
including them in normal PR unit tests; it is not a CI performance guarantee. Maintenance is
concentrated in the small immutable model and explicit precedence rules. The tests intentionally
cover bounded, valid trees with unique action names per node and unique claim names. They do
not define behavior for cycles, missing/incorrect parent links, or duplicate claim-set entries.

Follow-up coverage can generate validated imported claim hierarchies and check composition
before resolution, using the same semantic oracle after import. That requires fixture builders
for import/composition constraints. Provider-backed repository tests would additionally need
database leases and seeded claim catalogs; HTTP endpoint authentication belongs in its own
suite. Tenant selection is routing/data partitioning, not a CMS authorization boundary.
Job leasing remains the separate follow-on investigation described in the Jira ticket.
