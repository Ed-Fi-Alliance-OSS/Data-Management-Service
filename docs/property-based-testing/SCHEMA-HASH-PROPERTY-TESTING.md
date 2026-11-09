# Canonical JSON and schema hash property tests (DMS-1684, location 2)

This implements recommended location 2 from [DMS-1684](https://edfi.atlassian.net/browse/DMS-1684),
using the CsCheck 5.0.1 dependency already installed for [location 1](PROFILE-PROPERTY-TESTING.md).
The tests run in the existing Core NUnit unit-test project. No production code, package
versions, database setup, or `RelationalMappingVersion` changes are required.

## Coverage and independent oracles

| Test file | Coverage |
| --- | --- |
| `Utilities/CanonicalJsonSerializerPropertyTests.cs` | Recursive object-property permutations, ordinal Unicode names, escaped strings, nulls, empty containers, nested arrays, exact canonical UTF-8/string/stream agreement, canonicalization idempotence, source immutability and detached output containers. An independent `JsonDocument`/string-composition oracle sorts object members and retains array order and numeric tokens. Incremental hashing must equal whole-buffer `SHA256.HashData` of the expected bytes. |
| `Utilities/CanonicalJsonSerializerPropertyTests.cs` (numeric spellings) | Generated integers, fractional trailing zeros, exponent notation and negative zero in ordered arrays containing nulls, strings and nested arrays. Assert exact tokens and order, without treating distinct numeric spellings as interchangeable. |
| `Utilities/CanonicalJsonSerializerPropertyTests.cs` (buffer boundaries) | Exact canonical lengths 16,383/16,384/16,385 and 32,767/32,768/32,769 bytes. Each length exercises both one large string and 700 small strings followed by padding, covering buffer growth and incremental flushing. Payload characters vary while canonical byte length remains fixed. |
| `Startup/ApiSchemaInputNormalizerPropertyTests.cs` | Zero, one or four extensions and one to three resources/abstract resources. Independently construct the expected document with OpenAPI omitted. Change each of the three documented OpenAPI paths separately, and verify hash neutrality for changed and absent payloads. Check ordinal extension sorting, normalization idempotence, input immutability and nested output ownership. Identically named properties at other paths must survive. |
| `Startup/EffectiveSchemaHashProviderPropertyTests.cs` | Independent manifest composition with whole-buffer SHA-256 and the test canonical oracle. Exercise recursive property permutations, generated extension permutations, reversed extension inputs and normalization. Test the provider directly so normalizer sorting cannot hide a provider regression. Mutations of core/extension array order, numeric spelling, nested OpenAPI-like names and project versions must match the independently recomputed manifest hash. |

The manifest oracle uses the current hash/mapping version constants without changing them.
It includes the core project in ordinal project sorting and checks exact lowercase SHA-256
output. It never calls the production metadata extractor or canonical serializer. These
properties do not assert universal collision-freedom or arbitrary JSON equivalence.

## Bounds and execution

```bash
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit \
  --no-restore --filter 'Category=SchemaHashProperty' --logger 'console;verbosity=normal'

dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore
```

The category also runs in ordinary unfiltered unit-test execution. There are 40 NUnit cases
and 3,500 generated evaluations per successful run:

- Four JSON shapes × 200 samples: scalar, object, array and forced nested arrays/objects.
- Four numeric spellings × 100 samples.
- Six exact byte lengths × two token layouts × 25 samples.
- Three extension counts × three independently changed OpenAPI paths × 100 samples.
- Three extension counts × 100 manifest/permutation samples.
- Two change targets (core/extension) × four content changes × 100 samples.

Generators use immutable strings and records with immutable arrays. Fresh JSON nodes and
subjects are created inside every evaluation/shrink attempt; NUnit setup would run only
once per parameterized case. Each random container has at most three children, with at
most three generated container levels; the forced nested shape wraps and repeats that
bounded tree. Schema inputs have at most four extensions and three resources/abstract
resources per project. No generator filters/discards inputs. Sampling uses one thread.
Each test prints its explicit shape, boundary, extension-count or change-path distribution.

## Failure reporting and replay

CsCheck failures include the seed, shrink counts and printed immutable input, alongside
FluentAssertions' exact mismatch. Keep the test parameters, source revision, CsCheck 5.0.1
version and full failure output together. Replay with `CsCheck_Seed` and the failing test
filter, as in the experiment below. Fixed `iter` arguments bound PR execution;
`CsCheck_Iter` does not override them. Seed interpretation depends on unchanged generators
and framework version. Convert any real defect to a deterministic NUnit regression using
the reduced semantic input, not just its seed.

## Validation results

The initial focused run passed all 40 tests in 3.17 seconds of reported test time, excluding
build time. The unfiltered Core suite passed 9,269 tests with zero failures or skips in
14 seconds of reported test time. Both used the repository's normal analyzers. These are
local observations, not CI runtime guarantees. CSharpier formatted the five new C# files.
No production defects were observed.

Three temporary production mutations verified that meaningful properties fail:

| Temporary mutation | Result |
| --- | --- |
| Omit `Array.Sort` in `CanonicalJsonSerializer.RentSortedProperties` | The object-shape property failed; CsCheck reported seed `ei9Wnvofzlp1`, seven shrinks and 200 attempts. |
| Omit removal of `projectSchema.openApiBaseDocuments` | All three extension-count cases for that path failed against the independently constructed expected schema. |
| Reverse the effective hash provider's ordinal project comparison | Both nonempty-extension cases failed against the manifest oracle; the core-only case passed, as expected. |

The sorting mutation was applied again and replayed with:

```bash
CsCheck_Seed=ei9Wnvofzlp1 dotnet test \
  src/dms/core/EdFi.DataManagementService.Core.Tests.Unit \
  --no-restore -p:RunAnalyzers=false \
  --filter 'FullyQualifiedName~It_canonicalizes_recursive_permutations&Name~object' \
  --logger 'console;verbosity=normal'
```

Replay failed again, with one further shrink (reported seed `6DfqrSWgswf1`). The reduced
input from that run was `rotation=12` and:

```json
{"-0":{"ordinary-0":{"ordinary-0":null,"é-1":932,"😀-2":false}},"-1":{"é-0":true,"Z-1":[]}}
```

Its nested `é-0`/`Z-1` properties expose missing recursive ordinal sorting. This is the
observed reduced input, not a claim of a globally minimal counterexample. Mutation runs
used `-p:RunAnalyzers=false` to avoid repeating expensive analyzer work on deliberately
broken source; the successful initial run and final unfiltered validation use normal
analyzers. All temporary production edits were restored before final validation. No
mutation is retained in the commit. A final analyzer-enabled run of all 40 property cases
with `CsCheck_Seed=ei9Wnvofzlp1` passed in 3.16 seconds against the restored implementation,
confirming that the recorded failure seed passes with correct behavior.

## Limits and adoption

These are serializer/normalizer/hash unit contracts, not full schema validity or relational
DDL validation. The generated schemas satisfy the normalizer and hash provider's metadata
contracts; they are not inputs for compiling a full relational model. Buffer cases force
canonical byte lengths, not particular ArrayPool allocations or writer-internal calls.

Keep these bounded tests alongside existing examples. Exact output and manifest oracles
make failures actionable; maintaining the schema builders and independent manifest contract
is the main cost. No production defect is implied by deliberately broken implementations.
Other DMS-1684 locations remain outside this change.
