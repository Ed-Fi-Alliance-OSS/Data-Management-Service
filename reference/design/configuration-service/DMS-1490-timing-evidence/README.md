# DMS-1490 token endpoint timing evidence

`POST /connect/token` answers an unknown client the way it answers a known client with a wrong secret
or an unapproved client: the same `invalid_client` 401. Before rejecting an unknown client it
verifies the presented secret against a placeholder hash. The registered `IClientSecretHasher` makes
that placeholder once, at startup. This page records the timing evidence for the question an
observer could ask: can an absent client id still be told apart from an existing one by response
time?

The raw samples, run logs, analysis outputs and analysis scripts are not kept in the repository; see
[Where the data is](#where-the-data-is).

## Result on the current code (step 3)

| | SQL Server (LocalDB) | PostgreSQL |
| --- | --- | --- |
| absent − wrong_secret (estimate) | **−19.05 µs** | **+0.70 µs** |
| 95% CI, L = 13 | [−44.55, +4.70] | [−23.90, +22.35] |
| 95% CI, 2L = 26 | [−43.75, +4.35] | [−23.80, +21.70] |
| Half differences | −9.95, −24.35 | +5.90, −6.35 |
| Diagnostics (D1 / D2) | 1.007 / 0.015, no flag | 1.012 / 0.014, no flag |
| Median response, wrong_secret | 13.62 ms | 14.10 ms |
| Decision under the pre-registered rule | EQUIVALENT | EQUIVALENT |
| Descriptive: unapproved − wrong_secret | +3.20 µs [−21.50, +25.60] | +0.10 µs [−24.00, +20.10] |

| | |
| --- | --- |
| Code measured | commit `38be2e413a068eb71f6f6f93aa46350495e2d8dd`, Release build, Configuration Service as a host process |
| Databases | SQL Server LocalDB 17.0.4025.3, a fresh instance and database; PostgreSQL 18.6 (`postgres:18.6-alpine`), a fresh container and database |
| Hashing | 210,000 PBKDF2 iterations |
| Collector | `Given_token_endpoint_requests_measured_for_timing_evidence`: one keep-alive HTTP client, no concurrency |
| Sample plan, fixed before collection | 1,200 warm-up rounds, then 2,000 measured rounds per database. Each round sends one request per arm (`absent`, `wrong_secret`, `unapproved_correct_secret`) in an order shuffled by seed 503 (SQL Server) or 504 (PostgreSQL) |
| Estimand | median(absent) − median(wrong_secret) |
| Runs | 2026-10-27, SQL Server then PostgreSQL, one run each, each the first attempt; power line online throughout |

**The pre-registered rule** (unchanged since step 1) gives EQUIVALENT only if all of these hold:
- the 95% CI at L lies inside ±250 µs;
- the 95% CI at 2L lies inside ±250 µs;
- `diagnostics.py` raises no warning flag;
- the absent − wrong_secret difference in each half of the run lies inside ±250 µs.

The result is NOT EQUIVALENT if the CI is disjoint from ±250 µs, and INCONCLUSIVE otherwise.

**What it shows and what it does not:**
- **The margin:** ±250 µs is this experiment's margin, not an accepted security tolerance. EQUIVALENT
  means "inside that margin", nothing more. No decision recorded on DMS-1490 says whether that
  satisfies its "statistically indistinguishable" criterion.
- **Detection:** neither interval excludes zero, so 2,000 paired rounds detected no difference on
  either database. That is not evidence that there is none. On this setup the runs cannot rule out
  that an absent client is answered up to about 45 µs faster than a wrong secret on SQL Server, or
  between about 24 µs faster and 22 µs slower on PostgreSQL.
- **The previous run:** the step 2 SQL Server run (commit `d86bb6e95`) detected −42.1 µs, CI
  [−58.0, −22.0]. Since then the credential statement returns one row for any client id. Before,
  an unknown id returned no row and skipped the API client lookup. Step 3 ran on a different LocalDB
  instance on a different day, so the two runs are not a controlled comparison: the step 2 difference
  was not reproduced on the current code, and the two intervals overlap.
- **The interval:** it is conditional statistical evidence, not a hard maximum and not a bound on
  individual request times.
- **Startup:** every measured request follows 1,200 warm-up rounds, so the runs say nothing about the
  first requests after startup. Deterministic tests show that the placeholder exists before the first
  request and that no request generates a hash.
- **One host:** Windows ARM64, LocalDB rather than a SQL Server service, a single PostgreSQL container
  and loopback HTTP.
- **Hasher:** the result holds for the default hasher, whose stored hashes and placeholder share one
  format and work factor. See the `IClientSecretHasher` contract README for a replacement that does not.

## Method

- **Collection:** randomised paired rounds. Warm-up rounds are discarded, but kept in their own file.
- **Interval:** a 95% percentile moving-block bootstrap over consecutive rounds, so that a round's arms
  travel together. Block length L = ceil(N^(1/3)), 5,000 resamples, analysis seed 1, with a
  sensitivity interval at 2L. The analysis is `analyze_timing.py`, method version `1` (numpy only).
- **Diagnostics:** `diagnostics.py` reports warning flags (D1 level drift, D2 autocorrelation at 2L)
  and the decile, quarter and half medians and differences. A flag-free result is not, by itself, a
  validity finding.
- **Malformed input:** a missing column, a row cut short or an invalid value exits with code 2 and no
  verdict.

The collectors are `[Explicit]` NUnit fixtures and never run in ordinary CI:
- `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Timing/TokenEndpointTimingEvidenceTests.cs` (HTTP);
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTimingEvidenceTests.cs` (in-process).

## Where the data is

Everything behind this page is in one archive,
[`DMS-1490-timing-evidence.zip`](https://edfi.atlassian.net/secure/attachment/41311/DMS-1490-timing-evidence.zip),
attached to [DMS-1490](https://edfi.atlassian.net/browse/DMS-1490):

| | |
| --- | --- |
| SHA-256 | `77a19b56525f1fa8fd89176117084315e1a4c8f5b9171ed192ba801c450d1229` |
| Size | 540,313 bytes |

It holds:
- `step3/`: the runs above, one folder per database. Each folder has the measured and warm-up
  samples, the run header, the run log, `analysis/` (outputs and `commands.log`) and
  `SHA256SUMS-raw.txt`, taken right after collection. The folder also has the pre-registration and
  its hash, and the two setup scripts.
- `step2-mssql/` and `step1-mssql/`: the earlier SQL Server runs on `d86bb6e95` and `a5c4b7570`,
  with `README-step1-step2.md` and `SHA256SUMS-step1-step2.txt` as they were committed.
- `a5-final/`: the two fixtures for `diagnostics.py selftest`.
- `analyze_timing.py` and `diagnostics.py`.
- `BUNDLE-SHA256SUMS.txt`: the SHA-256 and size of every file in the archive.

`SHA256SUMS.txt` beside this README repeats those checksums, so a copy of the archive can be checked
against the repository. The files removed from this folder also remain in the branch history at
`38be2e413`.

To reproduce the analysis, run these from the archive's `step3/<database>/` folder:

```
python -I -B ../../analyze_timing.py analyze --csv <samples>.csv --delta 250
python -I -B ../../analyze_timing.py analyze --csv <samples>.csv --delta 250 --arm-a unapproved_correct_secret
python -I -B ../../diagnostics.py diagnose --csv <samples>.csv --delta 250
```

Earlier campaigns, on code before the narrow credential lookup, are in the branch history up to
`a5c4b7570` and do not bear on the current code.
