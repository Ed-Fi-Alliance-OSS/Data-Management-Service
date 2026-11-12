# Numeric identity property tests (DMS-1684, location 6)

This implements recommended location 6 from
[DMS-1684](https://edfi.atlassian.net/browse/DMS-1684) in the existing Core NUnit unit-test
project, using its centrally pinned CsCheck 5.0.1 dependency. Backend.External grants that
test assembly internal visibility so one suite can compare both canonicalizers directly.
No production behavior, package versions, lock files, or `RelationalMappingVersion` change.

## Implemented coverage and independent oracle

`NumericIdentityPropertyScenarios.cs` generates immutable decimal descriptions: three 32-bit
coefficient words, scale 0–28, sign, and 1–6 redundant zeros. `new decimal(...)` constructs the
expected numeric value without parsing. A separate unsigned `BigInteger` coefficient supplies
base-ten digits; decimal-point insertion and fractional-zero trimming produce expected text
without either production canonicalizer or decimal formatting.

Each value has five mathematically equivalent spellings: fixed point, explicitly signed fixed
point with leading/trailing zeros, integer coefficient with a negative exponent, coefficient
with appended zeros and a compensating exponent, and a fractional significand with a signed
exponent. These transformations preserve the original coefficient × 10^-scale exactly. The
suite also asserts that the decimal parser returns the constructed value for every spelling.
It does not generate rounded approximations or infer equivalence from floating-point values.

`IdentityValueCanonicalizerPropertyTests.cs` verifies:

- Both layers independently equal the digit oracle, agree with each other, preserve the
  numeric value, and are idempotent. A grammar assertion requires fixed point, no leading
  plus or redundant zeros, and unsigned canonical zero.
- Numeric document identity construction preserves the supplied JSONPath and uses the
  expected canonical value.
- Synthetic descriptor identity, descriptor suffix, nested descriptor, and bracket-suffix
  paths preserve exact text with either numeric flag. Inputs include numeric-looking text,
  mixed-case Unicode URI/text, whitespace, and empty text. Ordinary nonnumeric paths remain
  unchanged with both explicit false and the default flag.
- Unparseable numeric text stays unchanged in both layers and numeric identity construction:
  generated malformed suffixes/exponents, comma decimal separators, nonfinite names, empty
  text, and both signs of one beyond the decimal coefficient limit.

`DecimalValueCanonicalizerPropertyTests.cs` verifies `NormalizeScale` preserves numeric
value, removes every insignificant fractional zero, collapses signed zero, and is idempotent
at the representation level. It inspects the flags word from `decimal.GetBits`, because
ordinary decimal equality ignores both scale and the sign of zero.

Both fixtures run with `fr-FR` current culture to expose accidental culture-dependent parsing
or formatting. Descriptor values are compared verbatim; these tests do not define descriptor
URI equality or replace database comparison with C# case folding.

## Bounds, distributions, and execution

The category contains 31 NUnit cases and 3,100 generated evaluations per successful run:

| Property | Explicit cases | Evaluations |
| --- | ---: | ---: |
| Canonical text: seven shapes × both signs | 14 | 1,400 |
| Scale normalization: seven shapes × both signs | 14 | 1,400 |
| Descriptor/nonnumeric preservation: both numeric flags | 2 | 200 |
| Invalid numeric text preservation | 1 | 100 |

The seven shapes are full-width integers, full-width fractional decimals, scale 28, maximum
coefficient at scale zero, minimum nonzero magnitude (10^-28), zero at generated nonzero
scale, and a coefficient guaranteed to have a removable trailing zero. Both signs of each
boundary class are forced instead of relying on random selection. Every canonical-text
sample checks all five spellings (7,000 spelling checks in that property alone).

Every case runs 100 samples on one thread with no generator filtering/discards. CsCheck can
still report skipped candidates during failure shrinking; that is distinct from successful-run
generator filtering. Reported counts are explicit scenario distributions, not measured random
histograms. Arrays and identity elements are constructed inside each evaluation/shrink attempt;
NUnit setup would execute only once per case. There is no database or Docker dependency.

```bash
dotnet restore src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --locked-mode

dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore \
  --filter 'Category=NumericIdentityProperty' --logger 'console;verbosity=normal'

dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-restore
```

Failures include a seed, shrink counts, and a System.Text.Json serialization of the scenario.
Retain the complete failure output, source revision, NUnit parameters, and CsCheck version.
Replay using `CsCheck_Seed` with the same generator and test filter. Shrinking may find a
further reduced case on replay; the reported case is not a proof of global minimality.
For a real defect, retain an ordinary NUnit regression with the reduced semantic input as
well as its supporting seed. Fixed `iter: 100` is not overridden by `CsCheck_Iter`.

## Mutation and replay evidence

Four temporary production edits were tested individually with `-p:RunAnalyzers=false` and
restored. Each selected run executed exactly one property case and failed through NUnit:

| Mutation | Selected case | Failure seed | Shrinks / attempts |
| --- | --- | --- | ---: |
| Core uses `G29` formatting | Canonical text / minimumUnit / False | `bd_MdOPUGOt1` | 8 / 100 |
| Backend uses `G29` formatting | Canonical text / minimumUnit / False | `10KtlsUlnq-1` | 4 / 100 |
| Descriptor guard no longer applies when numeric is true | Descriptor preservation / True | `bHYtZiZBkQW1` | 5 / 100 |
| `NormalizeScale` returns its input unchanged | Scale / trailingZero / False | `ce9XA4fs7181` | 4 / 100 |

Both format mutations emitted `1E-28` instead of fixed point. The descriptor mutation changed
numeric-looking descriptor text. The scale mutation left scale 13 where the digit oracle
required scale 12 for this reported counterexample:

```json
{"Low":3203390,"Middle":0,"High":0,"Scale":13,"Negative":false,"Zeros":3}
```

With the scale mutation still present, this replay failed again:

```bash
CsCheck_Seed=ce9XA4fs7181 dotnet test \
  src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --no-build --no-restore \
  --filter 'Category=NumericIdentityProperty&Name~It_removes_insignificant&Name~trailingZero&Name~False' \
  --logger 'console;verbosity=normal'
```

The replay reported two further shrinks and this final scenario (seed `8LZXQtlKXwr1`):

```json
{"Low":6994820,"Middle":0,"High":0,"Scale":27,"Negative":false,"Zeros":6}
```

The expected scale was 26 and the mutated implementation retained 27. These failures are
sensitivity checks, not discovered production defects. No deliberate mutation is retained.

## Validation and adoption

The initial analyzer-enabled focused run passed all 31 cases in 0.82 seconds of reported test
run time, excluding build time. After restoring all mutations, the full analyzer-enabled Core
suite passed 9,404 tests with zero failures or skips in 15 seconds. The final assembly also
passed all 31 new cases with `CsCheck_Seed=ce9XA4fs7181` using `--no-build --no-restore`.
Locked restore, CSharpier checking of all three new C# files and the changed project file,
and `git diff --check` passed. No production defects were observed, and no provider or
Docker-stack E2E tests were run for this unit-test addition.

Keep this bounded category in normal PR unit runs alongside the existing deterministic
examples. The main maintenance cost is the digit-based oracle and explicit spelling rules.
The measured local runtime supports that choice but does not establish CI timing guarantees.

This coverage deliberately stops at exactly representable System.Decimal values. It does not
claim equivalence for excess-precision inputs that round, floating-point conversions,
underflow, or database types with different precision/scale limits. It does not validate
provider collation or generated SQL. A broader follow-up could seed the same exact decimal
scenarios into a known-valid numeric-identity resource in the API integration harness, then
verify reference lookup and identity matching on PostgreSQL and SQL Server. Such a follow-up
must restrict generated values to each mapped column's precision/scale and use isolated leased
databases and existing provider configuration; descriptor equality remains a separate
provider-backed contract. No provider follow-up is required to run these unit properties.
