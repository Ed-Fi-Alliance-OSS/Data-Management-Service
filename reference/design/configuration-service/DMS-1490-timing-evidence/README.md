# DMS-1490 token endpoint timing evidence

`POST /connect/token` now answers an unknown client the way it answers a known client with a wrong
secret: the same `invalid_client` error, after verifying the supplied secret against a placeholder
hash from the registered `IClientSecretHasher`. This folder holds the timing evidence for the
question an observer could ask: can an absent client id still be told apart from an existing one by
response time?

Nothing here runs in ordinary CI. The collectors are `[Explicit]` NUnit fixtures:
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTimingEvidenceTests.cs` (in-process);
- `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Timing/TokenEndpointTimingEvidenceTests.cs` (HTTP).

The analysis is `analyze_timing.py`, which uses numpy only and is at method version `1`.

## Result: SQL Server, current code (`step1-mssql/`)

| | |
| --- | --- |
| Code measured | commit `a5c4b7570c9ff93346f4c3171605e4017c89f5a6`, Release build, Configuration Service as a host process |
| Database | SQL Server LocalDB 17.0.4025.3, freshly created database, 210,000 PBKDF2 iterations |
| Collector | `Given_token_endpoint_requests_measured_for_timing_evidence`: one keep-alive HTTP client, no concurrency |
| Sample plan, fixed before collection | 1,200 warm-up rounds, then 2,000 measured rounds. Each round sends one request per arm (`absent`, `wrong_secret`, `unapproved_correct_secret`) in an order shuffled by seed 501 |
| Estimand | median(absent) − median(wrong_secret) |
| **Estimate** | **−17.6 µs**, 95% moving-block bootstrap CI **[−55.9, +10.8]** (L = 13; [−55.0, +10.05] at 2L = 26) |
| Decision | **EQUIVALENT** under the pre-registered rule |
| Descriptive | unapproved − wrong_secret: +20.3 µs, CI [−18.9, +54.3] |

**The pre-registered rule.** The rule is in `step1-mssql/pre-registration-mssql.txt`, written before collection. The result is EQUIVALENT only if all of these hold:
- the CI at L lies inside ±250 µs;
- the CI at 2L lies inside ±250 µs;
- `diagnostics.py` raises no warning flag;
- the absent − wrong_secret point difference in each half of the run lies inside ±250 µs.

The result is NOT EQUIVALENT if the CI is disjoint from ±250 µs, and INCONCLUSIVE otherwise.

**How the conditions came out:**
- **Diagnostics:** no flag raised. D1 (level drift) was 1.028 against a limit of 1.10; D2 (autocorrelation at lag 26) was 0.040 against 0.20.
- **Half differences:** −49.25 µs and +7.40 µs.
- **Power:** the power line was online at all three readings in the run log.

**What it shows and what it does not:**
- **What it shows:** this repeated-sample test detects no difference between absent and wrong-secret response times on the tested setup. The interval is conditional statistical evidence. It is not a hard maximum, and it is not a bound on individual request times.
- **One run on one host:** LocalDB on Windows ARM64, with the service as a host process.
- **The margin:** ±250 µs is the experiment's fixed margin, not an accepted security tolerance.
- **Packaging:** the files were copied byte-for-byte after the run; the commit that adds them changes no application or collector source. The private setup script, service output and runner output were not kept here.

Reproduce the analysis from this folder:

```
python -B analyze_timing.py analyze --csv step1-mssql/step1-endpoint-mssql-20261025T070738Z.csv --delta 250
python -B analyze_timing.py analyze --csv step1-mssql/step1-endpoint-mssql-20261025T070738Z.csv --delta 250 --arm-a unapproved_correct_secret
python -B diagnostics.py diagnose --csv step1-mssql/step1-endpoint-mssql-20261025T070738Z.csv --delta 250
```

## Method

- **Collection:** randomised paired rounds. Warm-up rounds are discarded but kept in their own file.
- **Interval:** a 95% percentile moving-block bootstrap over consecutive rounds, so that a round's arms travel together. The block length is L = ceil(N^(1/3)), with 5,000 resamples and analysis seed 1. A sensitivity interval at 2L is reported beside it.
- **Decision:**
  - EQUIVALENT if the CI lies inside [−δ, +δ];
  - NOT EQUIVALENT if the CI is disjoint from it;
  - otherwise INCONCLUSIVE, which is not a pass.
  - δ = 250 µs at the endpoint.
- **`diagnostics.py`:** reports warning flags and mandatory diagnostics (decile, quarter and half medians and differences, autocorrelation). A flag-free result is not, by itself, a validity finding.

## Earlier measurements

The earlier campaigns are not part of this folder. Their raw files and narrative stay in the branch history up to `a5c4b7570`:
- the first finals on code before the narrow credential lookup, where SQL Server was NOT EQUIVALENT at −377 µs;
- the A5 pilots and finals, which were a validation attempt that failed on a SQL Server regime shift.

The SQL Server result on pre-lookup code does not bear on the current code.

The two `a5-final/` sample files are kept only as fixed inputs to `diagnostics.py selftest`.

## Files

- `analyze_timing.py`: the analysis (`selftest`, `analyze`, `plan`, `fit`, `simulate`, `observer`).
- `diagnostics.py`: the warning diagnostics (`selftest`, `diagnose`).
- `step1-mssql/`: the accepted run. It holds the measured samples, warm-up samples, run header, pre-registration and run log, with `analysis/` (the outputs and `commands.log`) and `SHA256SUMS-raw.txt`, which holds the hashes taken right after collection.
- `a5-final/`: the two `diagnostics.py selftest` fixtures, unchanged.
- `SHA256SUMS.txt`: the SHA-256 and size of every file above. Files are stored without line-ending normalisation (`.gitattributes`).

```
python -B analyze_timing.py selftest
python -B diagnostics.py selftest
```
