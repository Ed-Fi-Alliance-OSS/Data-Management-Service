# Performance Final Gate Report

Overall status: **PASS**

Latency gates are app-level p50/p95 ratios; SQL Server db CPU/elapsed values are indicative only, and per-statement plan evidence lives beside each run's results under its `plans/` directory.

## Runs

| Provider | Kind | Run id | Fixture | Subject commit | Machine | Directory |
| --- | --- | --- | --- | --- | --- | --- |
| postgresql | traditional-baseline | postgresql-primary-500k-20260902150522 | primary-500k | 5656477957eb | 60f293b3b1997ab9 | C:\perf\dms-1392-local\baseline60\postgresql-primary-500k-20260902150522 |
| postgresql | final-primary | postgresql-final-primary-primary-500k-20260902165913 | primary-500k | 675dafe1cf0d | 60f293b3b1997ab9 | C:\perf\dms-1392-local\final60-pg3\postgresql-final-primary-primary-500k-20260902165913 |
| postgresql | final-descriptors | postgresql-final-descriptors-descriptors-25k-20260902165955 | descriptors-25k | 675dafe1cf0d | 60f293b3b1997ab9 | C:\perf\dms-1392-local\final60-pg3\postgresql-final-descriptors-descriptors-25k-20260902165955 |
| mssql | traditional-baseline | mssql-primary-500k-20260902152100 | primary-500k | 5656477957eb | 60f293b3b1997ab9 | C:\perf\dms-1392-local\baseline60\mssql-primary-500k-20260902152100 |
| mssql | final-primary | mssql-final-primary-primary-500k-20260902164047 | primary-500k | 675dafe1cf0d | 60f293b3b1997ab9 | C:\perf\dms-1392-local\final60-rerun\mssql-final-primary-primary-500k-20260902164047 |
| mssql | final-descriptors | mssql-final-descriptors-descriptors-25k-20260902164246 | descriptors-25k | 675dafe1cf0d | 60f293b3b1997ab9 | C:\perf\dms-1392-local\final60-rerun\mssql-final-descriptors-descriptors-25k-20260902164246 |

## Gate outcomes

| Gate | Provider | Status |
| --- | --- | --- |
| evidence-consistency | postgresql | PASS |
| environment-comparability | postgresql | PASS |
| traditional-sql-textual | postgresql | PASS |
| traditional-shallow-regression | postgresql | PASS |
| cursor-first-entry | postgresql | PASS |
| cursor-depth-insensitivity-unfiltered | postgresql | PASS |
| cursor-depth-insensitivity-authorized | postgresql | PASS |
| cursor-depth-insensitivity-filtered | postgresql | PASS |
| cursor-depth-insensitivity-descriptor | postgresql | PASS |
| partition-count-insensitivity | postgresql | PASS |
| single-command-structure | postgresql | PASS |
| deep-offset-observation | postgresql | PASS |
| evidence-consistency | mssql | PASS |
| environment-comparability | mssql | PASS |
| traditional-sql-textual | mssql | PASS |
| traditional-shallow-regression | mssql | PASS |
| cursor-first-entry | mssql | PASS |
| cursor-depth-insensitivity-unfiltered | mssql | PASS |
| cursor-depth-insensitivity-authorized | mssql | PASS |
| cursor-depth-insensitivity-filtered | mssql | PASS |
| cursor-depth-insensitivity-descriptor | mssql | PASS |
| partition-count-insensitivity | mssql | PASS |
| single-command-structure | mssql | PASS |
| deep-offset-observation | mssql | PASS |
| provider-coverage | all | PASS |

## Gate details

### evidence-consistency (postgresql) — PASS

Baseline and final-gate runs describe the same provider, fixture, and comparison frame.

- baseline and final-gate identities are consistent

### environment-comparability (postgresql) — PASS

Cross-run latency ratios assume the same machine and pinned server as the baseline.

- machine fingerprint 60f293b3b1997ab9 and image digest match the baseline

### traditional-sql-textual (postgresql) — PASS

Existing limit/offset page-selection SQL remains textually unchanged.

- every traditional page-selection SQL hash is byte-identical to the baseline

Evidence rows: traditional-offset-zero/25, traditional-offset-zero/500, traditional-offset-shallow/25, traditional-offset-shallow/500, traditional-offset-deep/25, traditional-offset-deep/500

### traditional-shallow-regression (postgresql) — PASS

Shallow-offset traditional paging costs at most 1.20x p50 / 1.30x p95 of its pre-change baseline.

- traditional-offset-shallow/25: p50 118.613ms / 131.144ms = 0.904x (limit 1.200x) within limit
- traditional-offset-shallow/25: p95 146.445ms / 150.795ms = 0.971x (limit 1.300x) within limit
- traditional-offset-shallow/500: p50 142.567ms / 152.837ms = 0.933x (limit 1.200x) within limit
- traditional-offset-shallow/500: p95 153.252ms / 183.744ms = 0.834x (limit 1.300x) within limit

Evidence rows: traditional-offset-shallow/25, traditional-offset-shallow/500

### cursor-first-entry (postgresql) — PASS

A first cursor page costs at most 1.20x p50 / 1.30x p95 of the offset-zero baseline.

- cursor-unfiltered-first vs traditional-offset-zero/25: p50 118.637ms / 147.393ms = 0.805x (limit 1.200x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/25: p95 137.370ms / 194.165ms = 0.707x (limit 1.300x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/500: p50 149.186ms / 159.913ms = 0.933x (limit 1.200x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/500: p95 164.155ms / 222.737ms = 0.737x (limit 1.300x) within limit

Evidence rows: cursor-unfiltered-first vs traditional-offset-zero/25, cursor-unfiltered-first vs traditional-offset-zero/500

### cursor-depth-insensitivity-unfiltered (postgresql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-unfiltered-middle/25 vs first: p50 118.977ms / 118.637ms = 1.003x (limit 1.200x) within limit
- cursor-unfiltered-middle/25 vs first: p95 138.584ms / 137.370ms = 1.009x (limit 1.300x) within limit
- cursor-unfiltered-last/25 vs first: p50 120.857ms / 118.637ms = 1.019x (limit 1.200x) within limit
- cursor-unfiltered-last/25 vs first: p95 145.393ms / 137.370ms = 1.058x (limit 1.300x) within limit
- cursor-unfiltered-middle/500 vs first: p50 141.032ms / 149.186ms = 0.945x (limit 1.200x) within limit
- cursor-unfiltered-middle/500 vs first: p95 153.731ms / 164.155ms = 0.936x (limit 1.300x) within limit
- cursor-unfiltered-last/500 vs first: p50 139.942ms / 149.186ms = 0.938x (limit 1.200x) within limit
- cursor-unfiltered-last/500 vs first: p95 156.452ms / 164.155ms = 0.953x (limit 1.300x) within limit

Evidence rows: cursor-unfiltered-middle/25 vs first, cursor-unfiltered-last/25 vs first, cursor-unfiltered-middle/500 vs first, cursor-unfiltered-last/500 vs first

### cursor-depth-insensitivity-authorized (postgresql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-authorized-middle/25 vs first: p50 118.420ms / 130.264ms = 0.909x (limit 1.200x) within limit
- cursor-authorized-middle/25 vs first: p95 137.549ms / 148.366ms = 0.927x (limit 1.300x) within limit
- cursor-authorized-last/25 vs first: p50 118.281ms / 130.264ms = 0.908x (limit 1.200x) within limit
- cursor-authorized-last/25 vs first: p95 136.849ms / 148.366ms = 0.922x (limit 1.300x) within limit
- cursor-authorized-middle/500 vs first: p50 151.291ms / 143.183ms = 1.057x (limit 1.200x) within limit
- cursor-authorized-middle/500 vs first: p95 167.835ms / 155.728ms = 1.078x (limit 1.300x) within limit
- cursor-authorized-last/500 vs first: p50 141.929ms / 143.183ms = 0.991x (limit 1.200x) within limit
- cursor-authorized-last/500 vs first: p95 152.998ms / 155.728ms = 0.982x (limit 1.300x) within limit

Evidence rows: cursor-authorized-middle/25 vs first, cursor-authorized-last/25 vs first, cursor-authorized-middle/500 vs first, cursor-authorized-last/500 vs first

### cursor-depth-insensitivity-filtered (postgresql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-filtered-middle/25 vs first: p50 149.983ms / 146.646ms = 1.023x (limit 1.200x) within limit
- cursor-filtered-middle/25 vs first: p95 229.770ms / 520.322ms = 0.442x (limit 1.300x) within limit
- cursor-filtered-last/25 vs first: p50 119.928ms / 146.646ms = 0.818x (limit 1.200x) within limit
- cursor-filtered-last/25 vs first: p95 124.342ms / 520.322ms = 0.239x (limit 1.300x) within limit
- cursor-filtered-middle/500 vs first: p50 151.976ms / 206.380ms = 0.736x (limit 1.200x) within limit
- cursor-filtered-middle/500 vs first: p95 168.738ms / 246.504ms = 0.685x (limit 1.300x) within limit
- cursor-filtered-last/500 vs first: p50 146.922ms / 206.380ms = 0.712x (limit 1.200x) within limit
- cursor-filtered-last/500 vs first: p95 161.781ms / 246.504ms = 0.656x (limit 1.300x) within limit

Evidence rows: cursor-filtered-middle/25 vs first, cursor-filtered-last/25 vs first, cursor-filtered-middle/500 vs first, cursor-filtered-last/500 vs first

### cursor-depth-insensitivity-descriptor (postgresql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-descriptor-middle/25 vs first: p50 4.495ms / 5.282ms = 0.851x (limit 1.200x) within limit
- cursor-descriptor-middle/25 vs first: p95 7.136ms / 8.786ms = 0.812x (limit 1.300x) within limit
- cursor-descriptor-last/25 vs first: p50 3.532ms / 5.282ms = 0.669x (limit 1.200x) within limit
- cursor-descriptor-last/25 vs first: p95 4.862ms / 8.786ms = 0.553x (limit 1.300x) within limit
- cursor-descriptor-middle/500 vs first: p50 8.801ms / 10.181ms = 0.864x (limit 1.200x) within limit
- cursor-descriptor-middle/500 vs first: p95 14.541ms / 13.919ms = 1.045x (limit 1.300x) within limit
- cursor-descriptor-last/500 vs first: p50 7.999ms / 10.181ms = 0.786x (limit 1.200x) within limit
- cursor-descriptor-last/500 vs first: p95 11.658ms / 13.919ms = 0.838x (limit 1.300x) within limit

Evidence rows: cursor-descriptor-middle/25 vs first, cursor-descriptor-last/25 vs first, cursor-descriptor-middle/500 vs first, cursor-descriptor-last/500 vs first

### partition-count-insensitivity (postgresql) — PASS

Requesting 200 partitions costs at most 1.25x p50 of requesting 1 over the same candidate set.

- number=200 vs number=1: p50 454.603ms / 452.377ms = 1.005x (limit 1.250x) within limit

Evidence rows: partition-unfiltered-1, partition-unfiltered-200

### single-command-structure (postgresql) — PASS

Cursor hydration adds no roundtrip and partition boundary selection is one command.

- all 36 measured cells observed exactly one database command per request

Evidence rows: traditional-offset-zero/25, traditional-offset-zero/500, traditional-offset-shallow/25, traditional-offset-shallow/500, traditional-offset-deep/25, traditional-offset-deep/500, cursor-unfiltered-first/25, cursor-unfiltered-first/500, cursor-unfiltered-middle/25, cursor-unfiltered-middle/500, cursor-unfiltered-last/25, cursor-unfiltered-last/500, partition-unfiltered-1/1, partition-unfiltered-10/10, partition-unfiltered-200/200, cursor-authorized-first/25, cursor-authorized-first/500, cursor-authorized-middle/25, cursor-authorized-middle/500, cursor-authorized-last/25, cursor-authorized-last/500, partition-authorized-10/10, cursor-filtered-first/25, cursor-filtered-first/500, cursor-filtered-middle/25, cursor-filtered-middle/500, cursor-filtered-last/25, cursor-filtered-last/500, partition-filtered-10/10, cursor-descriptor-first/25, cursor-descriptor-first/500, cursor-descriptor-middle/25, cursor-descriptor-middle/500, cursor-descriptor-last/25, cursor-descriptor-last/500, partition-descriptor-10/10

### deep-offset-observation (postgresql) — PASS

Deep-offset traditional results, recorded but never gated.

- deep/25: p50 142.128ms vs baseline 152.175ms (0.934x)
- deep/500: p50 166.448ms vs baseline 170.095ms (0.979x)
- deep-offset results are recorded for comparison and are not a cursor acceptance gate

Evidence rows: traditional-offset-deep/25, traditional-offset-deep/500

### evidence-consistency (mssql) — PASS

Baseline and final-gate runs describe the same provider, fixture, and comparison frame.

- baseline and final-gate identities are consistent

### environment-comparability (mssql) — PASS

Cross-run latency ratios assume the same machine and pinned server as the baseline.

- machine fingerprint 60f293b3b1997ab9 and image digest match the baseline

### traditional-sql-textual (mssql) — PASS

Existing limit/offset page-selection SQL remains textually unchanged.

- every traditional page-selection SQL hash is byte-identical to the baseline

Evidence rows: traditional-offset-zero/25, traditional-offset-zero/500, traditional-offset-shallow/25, traditional-offset-shallow/500, traditional-offset-deep/25, traditional-offset-deep/500

### traditional-shallow-regression (mssql) — PASS

Shallow-offset traditional paging costs at most 1.20x p50 / 1.30x p95 of its pre-change baseline.

- traditional-offset-shallow/25: p50 10.136ms / 10.820ms = 0.937x (limit 1.200x) within limit
- traditional-offset-shallow/25: p95 12.680ms / 13.287ms = 0.954x (limit 1.300x) within limit
- traditional-offset-shallow/500: p50 41.205ms / 54.195ms = 0.760x (limit 1.200x) within limit
- traditional-offset-shallow/500: p95 52.379ms / 114.379ms = 0.458x (limit 1.300x) within limit

Evidence rows: traditional-offset-shallow/25, traditional-offset-shallow/500

### cursor-first-entry (mssql) — PASS

A first cursor page costs at most 1.20x p50 / 1.30x p95 of the offset-zero baseline.

- cursor-unfiltered-first vs traditional-offset-zero/25: p50 7.136ms / 13.020ms = 0.548x (limit 1.200x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/25: p95 8.780ms / 16.276ms = 0.539x (limit 1.300x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/500: p50 34.717ms / 54.268ms = 0.640x (limit 1.200x) within limit
- cursor-unfiltered-first vs traditional-offset-zero/500: p95 39.104ms / 67.394ms = 0.580x (limit 1.300x) within limit

Evidence rows: cursor-unfiltered-first vs traditional-offset-zero/25, cursor-unfiltered-first vs traditional-offset-zero/500

### cursor-depth-insensitivity-unfiltered (mssql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-unfiltered-middle/25 vs first: p50 6.631ms / 7.136ms = 0.929x (limit 1.200x) within limit
- cursor-unfiltered-middle/25 vs first: p95 8.167ms / 8.780ms = 0.930x (limit 1.300x) within limit
- cursor-unfiltered-last/25 vs first: p50 6.601ms / 7.136ms = 0.925x (limit 1.200x) within limit
- cursor-unfiltered-last/25 vs first: p95 8.708ms / 8.780ms = 0.992x (limit 1.300x) within limit
- cursor-unfiltered-middle/500 vs first: p50 36.117ms / 34.717ms = 1.040x (limit 1.200x) within limit
- cursor-unfiltered-middle/500 vs first: p95 42.605ms / 39.104ms = 1.090x (limit 1.300x) within limit
- cursor-unfiltered-last/500 vs first: p50 34.273ms / 34.717ms = 0.987x (limit 1.200x) within limit
- cursor-unfiltered-last/500 vs first: p95 39.582ms / 39.104ms = 1.012x (limit 1.300x) within limit

Evidence rows: cursor-unfiltered-middle/25 vs first, cursor-unfiltered-last/25 vs first, cursor-unfiltered-middle/500 vs first, cursor-unfiltered-last/500 vs first

### cursor-depth-insensitivity-authorized (mssql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-authorized-middle/25 vs first: p50 7.505ms / 8.481ms = 0.885x (limit 1.200x) within limit
- cursor-authorized-middle/25 vs first: p95 9.878ms / 10.947ms = 0.902x (limit 1.300x) within limit
- cursor-authorized-last/25 vs first: p50 6.549ms / 8.481ms = 0.772x (limit 1.200x) within limit
- cursor-authorized-last/25 vs first: p95 8.411ms / 10.947ms = 0.768x (limit 1.300x) within limit
- cursor-authorized-middle/500 vs first: p50 38.989ms / 39.683ms = 0.983x (limit 1.200x) within limit
- cursor-authorized-middle/500 vs first: p95 44.940ms / 44.370ms = 1.013x (limit 1.300x) within limit
- cursor-authorized-last/500 vs first: p50 37.356ms / 39.683ms = 0.941x (limit 1.200x) within limit
- cursor-authorized-last/500 vs first: p95 44.133ms / 44.370ms = 0.995x (limit 1.300x) within limit

Evidence rows: cursor-authorized-middle/25 vs first, cursor-authorized-last/25 vs first, cursor-authorized-middle/500 vs first, cursor-authorized-last/500 vs first

### cursor-depth-insensitivity-filtered (mssql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-filtered-middle/25 vs first: p50 8.352ms / 8.925ms = 0.936x (limit 1.200x) within limit
- cursor-filtered-middle/25 vs first: p95 10.315ms / 10.642ms = 0.969x (limit 1.300x) within limit
- cursor-filtered-last/25 vs first: p50 6.888ms / 8.925ms = 0.772x (limit 1.200x) within limit
- cursor-filtered-last/25 vs first: p95 9.342ms / 10.642ms = 0.878x (limit 1.300x) within limit
- cursor-filtered-middle/500 vs first: p50 38.779ms / 41.419ms = 0.936x (limit 1.200x) within limit
- cursor-filtered-middle/500 vs first: p95 48.588ms / 47.261ms = 1.028x (limit 1.300x) within limit
- cursor-filtered-last/500 vs first: p50 37.276ms / 41.419ms = 0.900x (limit 1.200x) within limit
- cursor-filtered-last/500 vs first: p95 41.556ms / 47.261ms = 0.879x (limit 1.300x) within limit

Evidence rows: cursor-filtered-middle/25 vs first, cursor-filtered-last/25 vs first, cursor-filtered-middle/500 vs first, cursor-filtered-last/500 vs first

### cursor-depth-insensitivity-descriptor (mssql) — PASS

Middle and last cursor ranges cost at most 1.20x p50 / 1.30x p95 of the first range.

- cursor-descriptor-middle/25 vs first: p50 32.944ms / 57.169ms = 0.576x (limit 1.200x) within limit
- cursor-descriptor-middle/25 vs first: p95 37.084ms / 65.429ms = 0.567x (limit 1.300x) within limit
- cursor-descriptor-last/25 vs first: p50 12.299ms / 57.169ms = 0.215x (limit 1.200x) within limit
- cursor-descriptor-last/25 vs first: p95 14.942ms / 65.429ms = 0.228x (limit 1.300x) within limit
- cursor-descriptor-middle/500 vs first: p50 36.601ms / 59.891ms = 0.611x (limit 1.200x) within limit
- cursor-descriptor-middle/500 vs first: p95 42.551ms / 68.129ms = 0.625x (limit 1.300x) within limit
- cursor-descriptor-last/500 vs first: p50 16.432ms / 59.891ms = 0.274x (limit 1.200x) within limit
- cursor-descriptor-last/500 vs first: p95 19.211ms / 68.129ms = 0.282x (limit 1.300x) within limit

Evidence rows: cursor-descriptor-middle/25 vs first, cursor-descriptor-last/25 vs first, cursor-descriptor-middle/500 vs first, cursor-descriptor-last/500 vs first

### partition-count-insensitivity (mssql) — PASS

Requesting 200 partitions costs at most 1.25x p50 of requesting 1 over the same candidate set.

- number=200 vs number=1: p50 48.211ms / 60.150ms = 0.802x (limit 1.250x) within limit

Evidence rows: partition-unfiltered-1, partition-unfiltered-200

### single-command-structure (mssql) — PASS

Cursor hydration adds no roundtrip and partition boundary selection is one command.

- all 36 measured cells observed exactly one database command per request

Evidence rows: traditional-offset-zero/25, traditional-offset-zero/500, traditional-offset-shallow/25, traditional-offset-shallow/500, traditional-offset-deep/25, traditional-offset-deep/500, cursor-unfiltered-first/25, cursor-unfiltered-first/500, cursor-unfiltered-middle/25, cursor-unfiltered-middle/500, cursor-unfiltered-last/25, cursor-unfiltered-last/500, partition-unfiltered-1/1, partition-unfiltered-10/10, partition-unfiltered-200/200, cursor-authorized-first/25, cursor-authorized-first/500, cursor-authorized-middle/25, cursor-authorized-middle/500, cursor-authorized-last/25, cursor-authorized-last/500, partition-authorized-10/10, cursor-filtered-first/25, cursor-filtered-first/500, cursor-filtered-middle/25, cursor-filtered-middle/500, cursor-filtered-last/25, cursor-filtered-last/500, partition-filtered-10/10, cursor-descriptor-first/25, cursor-descriptor-first/500, cursor-descriptor-middle/25, cursor-descriptor-middle/500, cursor-descriptor-last/25, cursor-descriptor-last/500, partition-descriptor-10/10

### deep-offset-observation (mssql) — PASS

Deep-offset traditional results, recorded but never gated.

- deep/25: p50 173.288ms vs baseline 196.759ms (0.881x)
- deep/500: p50 185.377ms vs baseline 228.702ms (0.811x)
- deep-offset results are recorded for comparison and are not a cursor acceptance gate

Evidence rows: traditional-offset-deep/25, traditional-offset-deep/500

### provider-coverage (all) — PASS

PostgreSQL and real SQL Server satisfy every gate independently.

- providers evaluated: postgresql, mssql
