# DMS-1490 token endpoint timing evidence

Timing evidence for the change that makes `POST /connect/token` answer an unknown client the way it answers a
known client with a wrong secret: the same `invalid_client` error and a secret verification against a placeholder
hash produced by the registered hasher. The question measured is whether an observer can still tell an absent
client id from an existing one by response time.

This folder holds raw samples, their headers, deployment provenance and every analysis output. Nothing here is a
test that runs in ordinary CI. The collectors are `[Explicit]` NUnit fixtures in
`src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTimingEvidenceTests.cs`
(in-process) and `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Timing/TokenEndpointTimingEvidenceTests.cs`
(HTTP). The analysis is `analyze_timing.py` (numpy only, method version `1`).

## Summary

| Boundary | δ (fixed) | Rounds | absent − wrong_secret, µs | 95% CI, µs | Decision |
| --- | --- | --- | --- | --- | --- |
| In-process, 20,000 iterations | 25 | 1,000 | −0.35 | [−2.10, +0.70] | EQUIVALENT |
| HTTP, PostgreSQL, 210,000 iterations | 250 | 500 | **−177.70** | **[−242.25, −122.20]** | EQUIVALENT (CI excludes 0) |
| HTTP, SQL Server (LocalDB), 210,000 iterations | 250 | 4,000 | **−377.40** | **[−403.45, −353.95]** | **NOT EQUIVALENT** |
| HTTP, PostgreSQL, pre-fix build `17e97ed6a` (control) | 250 | 500 | −19,792.85 | [−19,872.00, −19,689.64] | NOT EQUIVALENT |

- On PostgreSQL the measured median difference falls from about 19.8 ms (pre-fix build) to about 0.18 ms (fixed build).
- **A difference remains.** In both endpoint runs the absent arm's median is lower than the wrong-secret arm's. SQL Server exceeds the fixed bound; PostgreSQL is inside it, but its interval excludes zero.
- The in-process run bounds the specified median difference **in that experiment** (a fixed-cost in-memory repository and 20,000 iterations, against 210,000 over HTTP). It does not prove a zero residual and does not isolate the cause of the HTTP difference.
- The supplementary observer analysis below is an IID empirical-resampling model. Under it, the fixed builds' remaining difference is separable with about 100 resampled observations per side. That is a model result, not an observed attack.
- **AC2 is therefore not met** ("absent-client response times statistically indistinguishable from wrong-secret"). The ticket's engineering gate requires EQUIVALENT on both endpoint backends, and SQL Server is NOT EQUIVALENT.
- Any acceptance of a measured residual is a separate product decision.

## Method (pre-registered)

- **Estimand:** `median(absent) − median(wrong_secret)`, in microseconds.
- **Collection:** randomised paired rounds. Each round sends one request per arm, in an order shuffled by `System.Random(seed)`. Warm-up rounds are discarded. One keep-alive HTTP client, no concurrency.
- **Interval:** 95% percentile moving-block bootstrap over consecutive rounds (both arms of a round travel together), block length `L = ceil(N^(1/3))`, 5,000 resamples, analysis seed 1. A sensitivity interval at `2L` is reported beside it.
- **Decision:**
  - EQUIVALENT iff the CI ⊆ [−δ, +δ].
  - NOT EQUIVALENT iff the CI is disjoint from [−δ, +δ].
  - Otherwise INCONCLUSIVE, which is not a pass.
- **Bounds:** δ = 25 µs in-process and 250 µs at the endpoint. These are fixed experiment thresholds, not approved security tolerances.
- **N:** the smallest candidate in {500, 1,000, 2,000, 4,000} that meets both conditions below. Amendment A1, approved before any final run.
  - The pilot-based precision heuristic: half-width × √(200/N) ≤ δ/2.
  - All three known-effect (C2) expectations under the committed synthetic generator: EQ@0 ≥ 0.90, EQ@δ ≤ 0.05, NE@3δ ≥ 0.95.

  C2 describes operating characteristics under that synthetic model, not interval coverage for the service's real distributions. The pilot-size calibration check (`p3.0-pilot/analysis/exploratory-c2-calibration-at-pilot-size.json`) found the generator 1.5–7.6× wider than the real data.
- **Controls (in-process):** C0 skips the derivation, the original defect's shape, and must be NOT EQUIVALENT. C1 adds 3δ to the absent arm by a spin-wait and must be NOT EQUIVALENT.
- **Contrast (HTTP):** the pre-fix build in `pre-fix` contract mode, where a wrong secret answers `unauthorized_client`. It gives the defect's magnitude, not acceptance evidence.
- **One final attempt per boundary.** Pilot samples (`p3.0-pilot/`) are excluded from every decision.

| Boundary | N | L / 2L | Warm-up | Seed | Contract | Arms |
| --- | --- | --- | --- | --- | --- | --- |
| In-process | 1,000 | 10 / 20 | 50 | 201 | — | absent, wrong_secret, c0_no_derivation, c1_absent_plus_3_bounds |
| HTTP PostgreSQL | 500 | 8 / 16 | 200 | 202 | current | absent, wrong_secret, unapproved_correct_secret |
| HTTP SQL Server | 4,000 | 16 / 32 | 200 | 203 | current | same |
| HTTP PostgreSQL pre-fix contrast | 500 | 8 / 16 | 200 | 204 | pre-fix | same |

## Environment and provenance

- **Host:** ASUS Zenbook A16 UX3607OA, Windows 10.0.28000 Arm64, 18 logical CPUs, 47.6 GB, AC power, Balanced plan; .NET 10.0.13; Python 3.12.10, numpy 2.5.2.
- **Host state:** otherwise idle (≈1% CPU before the runs). No simulation or analysis overlapped any collection; the runs were sequential.
- **Collector:** commit `2c70ee881f37b6a2545d8684fd3e87e11ae06f3a`, clean worktree, Release builds. Analysis script SHA-256 `27d881d666a429f60e6cdd047d5bd258c87502491bc86b31869d500f87596b9d`. Self-check 47/47.
- **PostgreSQL (fixed):** `cs-local` stack built by `setup-local-cms.ps1` with no cache from the clean worktree. Image `sha256:fdbf7ea83f6f6164229e29fe9e3ccdae60e4b6f17867e04a8149985d199ec46c`, container `e7e7e008…`.
- **PostgreSQL (pre-fix):** the same script from a `git archive` of `17e97ed6a531ee54fcaa2fdc06820a2399b9e277` (tree `a8a6be7a…`) in an isolated folder. Image `sha256:87d59d2fbc04c40fbf6dc72558023e422cfd9a777a9e5cc7fe8f26dc7daab828`, a different image from the fixed one. Every wrong-secret response was the pre-fix `unauthorized_client`, which the collector checks per request.
- **SQL Server:** LocalDB 17.0.4025.3 (this ARM64 host cannot run the SQL Server container). The CMS ran as a host process from the Release build, DLL SHA-256 `8029c29afe84f7273597d7baf8b18caa84eb3324c36160675a14c287496416e9`, launched with an explicit environment block. No inherited configuration variables.
- **Provenance files:** `p3.2-final/provenance-*.json` record an exact allowlist of non-secret settings from the running deployment, before and after collection. The allowlist is `AppSettings__Datastore`, `AppSettings__IdentityProvider`, `AppSettings__MultiTenancy`, `IdentitySettings__ClientSecretHashingIterations` and `IdentitySettings__AllowRegistration`. They also record:
  - the container and image ids, start time and restart count (Docker);
  - the PID, start time and DLL hash (host process);
  - whether the same container or process served the whole collection.

  Every endpoint run recorded `postgresql`/`mssql`, `self-contained`, `false`, `210000`, `true`, and `true` for the same deployment throughout.
- **Pilot limitation:** the PostgreSQL pilot's runtime configuration was observed but not saved, so it cannot be re-verified. The pilot is planning data only.

## Results

Per-arm summaries, µs:

| Run | Arm | mean | SD | min | p10 | p25 | p50 | p75 | p90 | max | 1st / 2nd-half median |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| in-process | absent | 1271.2 | 186.5 | 1180.5 | 1205.0 | 1208.2 | 1213.0 | 1249.4 | 1339.0 | 4224.5 | 1212.5 / 1214.2 |
| in-process | c0_no_derivation | 0.4 | 1.0 | 0.0 | 0.1 | 0.1 | 0.1 | 0.3 | 0.7 | 15.6 | 0.2 / 0.1 |
| in-process | c1_absent_plus_3_bounds | 1350.7 | 165.9 | 1262.5 | 1281.3 | 1283.6 | 1289.2 | 1327.1 | 1442.9 | 2256.3 | 1287.5 / 1292.8 |
| in-process | wrong_secret | 1273.0 | 171.2 | 1180.8 | 1205.2 | 1208.1 | 1213.3 | 1252.7 | 1375.3 | 3516.5 | 1212.8 / 1215.2 |
| PostgreSQL | absent | 20830.8 | 626.3 | 19842.1 | 20318.7 | 20510.3 | 20711.8 | 21008.0 | 21356.4 | 26599.7 | 20706.1 / 20725.4 |
| PostgreSQL | unapproved_correct_secret | 21004.8 | 746.7 | 20024.8 | 20488.2 | 20650.5 | 20896.2 | 21194.7 | 21518.7 | 33837.8 | 20824.0 / 20953.9 |
| PostgreSQL | wrong_secret | 20992.9 | 627.3 | 19942.3 | 20470.6 | 20667.0 | 20889.4 | 21192.1 | 21593.9 | 29951.0 | 20830.3 / 20991.4 |
| SQL Server | absent | 14221.4 | 1151.7 | 13043.7 | 13442.6 | 13610.3 | 13879.6 | 14436.2 | 15276.3 | 33523.3 | 14261.5 / 13703.2 |
| SQL Server | unapproved_correct_secret | 14670.3 | 1507.9 | 13391.7 | 13807.7 | 13984.7 | 14304.2 | 14875.5 | 15818.9 | 69154.5 | 14692.2 / 14104.8 |
| SQL Server | wrong_secret | 14610.9 | 1282.7 | 13366.2 | 13749.8 | 13933.5 | 14257.0 | 14819.4 | 15707.1 | 35847.5 | 14652.2 / 14043.3 |
| pre-fix PostgreSQL | absent | 2268.7 | 474.3 | 1486.0 | 1815.5 | 1983.3 | 2210.2 | 2463.2 | 2766.7 | 8563.7 | 2285.0 / 2155.3 |
| pre-fix PostgreSQL | unapproved_correct_secret | 22128.1 | 895.7 | 20517.3 | 21354.3 | 21606.9 | 22018.9 | 22489.1 | 22960.0 | 32317.9 | 22170.5 / 21827.8 |
| pre-fix PostgreSQL | wrong_secret | 22069.5 | 658.4 | 20728.4 | 21329.6 | 21611.1 | 22003.0 | 22401.8 | 22853.5 | 24868.8 | 22139.8 / 21813.0 |

Pre-registered comparisons, with the 2L sensitivity check:

| Run | Comparison | Estimate | CI (L) | Decision | CI (2L) | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| in-process | absent − wrong_secret | −0.35 | [−2.10, +0.70] | EQUIVALENT | [−2.05, +0.75] | EQUIVALENT |
| in-process | C0 − wrong_secret | −1213.20 | [−1215.90, −1211.90] | NOT EQUIVALENT | [−1216.20, −1211.90] | NOT EQUIVALENT |
| in-process | C1 − wrong_secret | +75.90 | [+74.00, +78.10] | NOT EQUIVALENT | [+74.00, +78.40] | NOT EQUIVALENT |
| PostgreSQL | absent − wrong_secret | −177.70 | [−242.25, −122.20] | EQUIVALENT | [−239.35, −120.40] | EQUIVALENT |
| PostgreSQL | unapproved − wrong_secret | +6.70 | [−56.40, +59.25] | EQUIVALENT | [−54.15, +56.35] | EQUIVALENT |
| SQL Server | absent − wrong_secret | −377.40 | [−403.45, −353.95] | NOT EQUIVALENT | [−402.25, −353.50] | NOT EQUIVALENT |
| SQL Server | unapproved − wrong_secret | +47.20 | [+21.60, +71.10] | EQUIVALENT | [+22.20, +71.30] | EQUIVALENT |
| pre-fix PostgreSQL | absent − wrong_secret | −19792.85 | [−19872.00, −19689.64] | NOT EQUIVALENT | [−19876.55, −19679.99] | NOT EQUIVALENT |
| pre-fix PostgreSQL | unapproved − wrong_secret | +15.95 | [−57.35, +107.15] | EQUIVALENT | [−48.45, +110.80] | EQUIVALENT |

L and 2L agree on every decision. Both controls behave as required.

### Diagnostics

`p3.2-final/analysis/diagnostics-by-quarter.json` gives the absent − wrong_secret difference of medians by quarter of the run:

| Run | Q1 | Q2 | Q3 | Q4 |
| --- | --- | --- | --- | --- |
| in-process | −1.0 | −1.1 | +6.0 | −0.2 |
| PostgreSQL | −172.0 | −73.4 | −319.5 | −225.2 |
| SQL Server | −342.0 | −352.8 | −344.8 | −340.4 |
| pre-fix PostgreSQL | −19976.8 | −19814.0 | −19634.9 | −19685.2 |

- **Direction:** the absent arm's quarter median is lower in every quarter on both databases. The SQL Server quarters agree closely. The PostgreSQL quarters vary substantially (−73 to −320 µs), so a shared sign over four quarters does not exclude time effects.
- **SQL Server transient:** the first quarter is about 1 ms slower in all three arms than the rest, a common-mode settling the 200 warm-up rounds did not cover. The difference is unaffected.
- **Dependence:** per-arm serial dependence is strong on SQL Server (lag-1 ACF 0.32–0.49). The per-round A − B series shows almost none on any boundary (|lag-1| ≤ 0.06 on the fixed builds; 0.16 on the pre-fix contrast).
- **Unapproved clients:** unapproved/correct-secret clients measure within a few tens of µs of wrong-secret clients. That is consistent with the hypothesis below (a difference between "a client row exists" and "no client row"), but it does not establish it.

### Supplementary observer analysis (does not decide AC2)

This is an **IID empirical-resampling model**, not an observed attack.

- **Draws:** for each draw, Q observations are drawn with replacement from the candidate's arm and, independently, Q from the absent (reference) arm.
- **Score and threshold:** the score is the absolute difference of their medians. The threshold is the 95th percentile of the score when both sides come from the absent arm, so FPR is about 5% under H0.
- **Reported rate:** the table gives the true-positive rate when the candidate side comes from the wrong-secret arm, over 2,000 draws per Q (`analyze_timing.py`, `observer`).
- **Ignored structure:** resampling individual observations ignores the runs' serial dependence, the round effects both arms share, and settling transients.
- **Extrapolation:** Q = 1,000 and Q = 10,000 exceed the observations available per arm in some runs (500 per arm on PostgreSQL; 4,000 on SQL Server), so those columns are extrapolations of the empirical distributions.
- **Scope:** one statistic; not the strongest attacker and not a bound on one.

| Run | Q = 10 | Q = 100 | Q = 1,000 | Q = 10,000 |
| --- | --- | --- | --- | --- |
| in-process | 0.076 | 0.057 | 0.113 | 0.475 |
| PostgreSQL (fixed) | 0.150 | **0.807** | 1.000 | 1.000 |
| SQL Server (fixed) | 0.141 | **0.970** | 1.000 | 1.000 |
| pre-fix PostgreSQL | **1.000** | 1.000 | 1.000 | 1.000 |

Under this model, the pre-fix build's difference is separated at Q = 10, and the fixed builds' at roughly Q = 100
and above. These figures describe the model applied to these samples. They are not a measured attack cost, and they
do not show how many real requests an attacker would need.

## Where the remaining difference comes from

This is a read-only source inspection: hypotheses grounded in source, not a demonstrated causal measurement.

After the client lookup, `OpenIddictTokenManager.ValidateClientSecretAsync` has the same code path for both cases:
one verification, then rejection. The leading hypothesis is that the difference comes from
`GetApplicationByClientIdAsync`, which does more work when a row exists. These experiments do not isolate it. The
in-process run replaces the database with a fixed-cost repository and uses a different iteration count, so it
cannot attribute the HTTP difference.

- **SQL Server** (`Backend.Mssql/OpenIddict/Repositories/OpenIddictDataRepository.cs`, `GetApplicationInfoAsync`): an absent client ends after the first query returns no row. An existing client then runs two more queries on the same connection (scopes, data store ids) and deserialises four JSON arrays. Two extra round trips over the LocalDB named pipe would be consistent with the −340 to −380 µs, but this has not been measured separately.
- **PostgreSQL** (`Backend.Postgresql/OpenIddict/Repositories/OpenIddictDataRepository.cs`): one statement, but only an existing client produces a row. The LEFT JOIN/`GROUP BY` aggregation does work and Dapper materialises the full `ApplicationInfo` (secret hash, permission/requirement/scope/data-store arrays, protocol mappers). For an absent client the statement matches nothing. This would be consistent with the −178 µs but has not been isolated.

## Reproduction

**Collection is not fully reproducible from this folder.** The collection lane scripts are kept with the working
evidence outside this repository and are not committed. The `lane-*.log` and `provenance-*.json` files here record
what each lane did and which deployment it measured, but they are not a complete, re-runnable collection recipe. The
provenance records are author-produced: a host-process frontend DLL hash, for example, does not fingerprint every
loaded backend assembly. The analysis is reproduced from this folder:

```
python -B analyze_timing.py selftest
python -B analyze_timing.py analyze  --csv p3.2-final/final-in-process-20261024T004212Z.csv --delta 25 [--arm-a c0_no_derivation|c1_absent_plus_3_bounds]
python -B analyze_timing.py analyze  --csv p3.2-final/final-endpoint-postgresql-20261024T004408Z.csv --delta 250 [--arm-a unapproved_correct_secret]
python -B analyze_timing.py analyze  --csv p3.2-final/final-endpoint-mssql-20261024T004842Z.csv --delta 250 [--arm-a unapproved_correct_secret]
python -B analyze_timing.py analyze  --csv p3.2-final/contrast-prefix-endpoint-postgresql-20261024T004716Z.csv --delta 250 [--arm-a unapproved_correct_secret]
python -B analyze_timing.py observer --csv <any of the above>
```

`p3.2-final/analysis/commands.log` lists the analysis commands that produced the saved outputs; it does not record
the collection invocations. `SHA256SUMS.txt` has 116 entries: the size and SHA-256 of `analyze_timing.py` and of
every file under `p3.0-pilot/`, `p3.2-final/` and `a5-pilot/`. It does not list `README.md`, `.gitattributes`,
`collection/` or itself.

The files under `p3.0-pilot/`, `p3.2-final/` and `a5-pilot/` are stored without line-ending normalisation
(`.gitattributes`), so the checksums hold for a checkout. Because the collectors and the analysis wrote CRLF line endings, `git show --check`
on the commit that added them exits 2 with 26,418 "trailing whitespace" notices across 78 files. Every notice is a
CRLF line ending in the preserved data; with the CR removed, no line ends in a space or tab. The data is kept
byte-exact rather than normalised to silence the check.

## Files

- `p3.0-pilot/`: the three 200-round pilots (planning only), the failed and successful LocalDB pilot lane logs, and the pilot analysis, planning, fit, C2 simulation and exploratory outputs. The PostgreSQL pilot's Docker setup and teardown logs are not included; their SHA-256 values are `fb81927d…` and `a1e42b24…`.
- `p3.2-final/`: the final runs, the pre-fix contrast, their provenance and lane logs, and every analysis output.
- `a5-pilot/`: the two 200-round endpoint pilots of the A5 target `b2b8d5df4`, collected with the `collection/` recipe at `d75e6e2f8` (planning only; excluded from any final decision). It holds the samples, headers, provenance, lane status logs, the environment record, both targets' `target.json`, the Stage 0 and Stage 1 command records, the secret-scan report, and the planning outputs in `analysis/` with their `commands.log`. Runner output, results files, setup logs and exception text stayed in the private work folder.
- No file contains credentials, tokens, connection strings or client-identifying request content. All evidence files were scanned for the stack's configured secret values and for credential patterns before commit.
