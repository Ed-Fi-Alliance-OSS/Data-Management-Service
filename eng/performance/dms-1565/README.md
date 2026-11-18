# DMS-1565 root update shape measurements

Evidence and tooling for DMS-1565: when an existing document's identity is unchanged, the relational
write path now leaves the root identity-projection columns out of the `UPDATE` SET list, so SQL Server
no longer compiles and opens the incoming `ON UPDATE CASCADE` branches. An allowed identity change keeps
the full SET list. Raw captures (plans, replay SQL, block JSON, logs) are attached to the ticket (see
[Evidence](#evidence)); this folder holds the report and the tooling that produced it.

## Results

All numbers below were measured on one developer host (Apple Silicon, SQL Server 2025 17.0.5005.3 amd64
under emulation in Docker, 5 GB container cap, 3.5 GB buffer pool) against the published Northridge v81
SQL Server dataset. Only the relative comparison between the two builds is claimed. Absolute
milliseconds are not comparable with the Volume campaign in the ticket, and nothing here validates the
ticket's projected 14 ms to 9 ms Session improvement or production throughput.

### PUT latency

Baseline `0fd5300f0` on `main` versus the changed build: this ticket's change as it stood before review.
The evidence set's `task3b/arm-identities.txt` records its branch revision. Twelve blocks (six interleaved
baseline/changed pairs, two runs of three pairs), each a fresh restore and a fresh host, concurrency 1, 50
warmup and 300 measured PUTs per resource per block, every PUT changing one non-key root column. Pooled
over 1,800 requests per arm and resource.

The measured revision predates the review changes that followed it: the reduced update is now rendered
by the compiler's own UPDATE emitter, resolved once per root plan, and bound with only its own parameters
on the standalone transport. Rather than repeating the campaign, the branch's final code was checked
against it with one short block (`run-block.sh changed 1 <order> 0 5`, the campaign's harness, a fresh
restore). For each of the five resources, the root UPDATE Query Store recorded on the final code,
parameter declaration included, is identical to one the campaign recorded in every one of its six
changed blocks. Each resource's campaign captures share a single statement body and parameter list;
they differ only in `nvarchar` parameter lengths, which vary with the values sent. The final code
therefore sends SQL Server the same statements the campaign measured.

| Resource | Median ms (baseline → changed) | p95 ms | Pairs with lower median | Regression threshold |
| --- | --- | --- | --- | --- |
| sessions | 11.63 → 5.29 (−54.5%) | 15.79 → 8.28 | 6 of 6 | 15.6% |
| courseOfferings | 9.10 → 5.45 (−40.1%) | 13.93 → 8.84 | 6 of 6 | 7.4% |
| sections | 9.37 → 5.35 (−42.9%) | 68.70 → 8.12 | 6 of 6 | 8.0% |
| studentSchoolAssociations | 5.89 → 5.45 (−7.4%) | 8.88 → 8.18 | 5 of 6 | 15.9% |
| classPeriods | 4.51 → 4.18 (−7.2%) | 6.88 → 5.84 | 5 of 6 | 10.3% |

No resource is flagged. A resource is flagged when its pooled relative median change exceeds
`max(0.05, 2 × s / m)`, where `s` is the pooled within-arm sample standard deviation of block medians and
`m` the mean baseline block median, or when its p95 rises by more than 10%. Sessions, course offerings
and sections are lower in every pair. Student school associations and class periods show no regression,
but their improvement is within block-to-block noise and should be read as inconclusive.

Block medians per pair, baseline → changed, in ms. The two runs of three pairs are blocks 1–6 and
7–12; the second run was added after the first left the student school association and class period
comparisons inconclusive, and both are reported in full.

| Pair (blocks) | sessions | courseOfferings | sections | studentSchoolAssociations | classPeriods |
| --- | --- | --- | --- | --- | --- |
| 1, 2 | 12.76 → 6.05 | 9.18 → 5.54 | 9.73 → 6.02 | 5.71 → 4.79 | 4.52 → 3.88 |
| 3, 4 | 11.66 → 5.31 | 9.04 → 5.94 | 9.52 → 5.13 | 5.88 → 5.97 | 4.60 → 4.09 |
| 5, 6 | 10.82 → 4.36 | 9.50 → 5.16 | 9.44 → 5.16 | 6.16 → 6.02 | 4.50 → 4.65 |
| 7, 8 | 13.13 → 6.14 | 9.04 → 5.88 | 9.30 → 5.69 | 5.44 → 4.77 | 4.34 → 4.05 |
| 9, 10 | 11.04 → 5.22 | 8.81 → 5.69 | 9.16 → 5.66 | 5.77 → 5.16 | 4.43 → 3.91 |
| 11, 12 | 10.78 → 4.33 | 8.92 → 4.90 | 9.01 → 4.76 | 6.22 → 5.83 | 4.66 → 4.45 |

### Root UPDATE statement (Query Store, measured window)

Inclusive per-execution averages of the root `UPDATE [edfi].[<Table>]` statement over the 300 measured
requests (Query Store statement figures, which may include nested trigger work), mean of the six blocks
per arm. The execution delta was exactly 300 in every block and resource.

| Resource | Duration µs | CPU µs | Logical reads | Distinct plans | Compiles in measured window |
| --- | --- | --- | --- | --- | --- |
| sessions | 6,005 → 539 | 5,992 → 530 | 94.3 → 54.3 | 2 → 1 | 0 → 0 |
| courseOfferings | 4,279 → 565 | 4,273 → 562 | 82.5 → 54.5 | 24 → 12 | 14 → 6 |
| sections | 3,478 → 588 | 3,473 → 585 | 101.2 → 59.2 | 28 → 2 | 20 → 1 |
| studentSchoolAssociations | 688 → 638 | 685 → 635 | 79.5 → 60.5 | 2 → 2 | 1 → 1 |
| classPeriods | 591 → 589 | 581 → 579 | 72.3 → 50.2 | 2 → 2 | 1 → 1 |

The full SET list declares string identity parameters as `nvarchar(<value length>)`, so the baseline
compiles one plan per distinct identity-value length. Course offerings keep length-sized parameters in
the changed build because `Course_CourseCode` is a non-identity column of that table.

### Plan shape

For the identity-unchanged Session update on the restored dataset (Session 81487, with 86 course
offerings and 688 sections), replaying each build's exact root statement with `SET STATISTICS XML ON`
inside a rolled-back transaction gave 300 operators over 22 tables for the full SET list and 13
operators over 4 tables for the reduced one, with 34 versus 8 root-statement logical reads. In the
captured baseline executions the CourseOffering and Section update operators opened with zero rows and
their seeks did not execute. The reduced plan still performs the outgoing School, SchoolYearType and
Descriptor checks, and the 11 stamp-trigger statements (19 logical reads) are identical in both builds.
The ticket's 208 operators over 19 tables came from a schema with 139 root-to-Document foreign keys
removed and from a different revision; that difference is not explained here.

## Protocol

- **Builds.** Each arm is a `git archive` export of its commit, built in Release with its own `bin`/`obj`,
  plus a byte-identical copy of `Given_A_Dms1565_Northridge_Put_Latency_Block.cs` in
  `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/`.
- **Host.** The fixture subclasses `MssqlApiIntegrationTestBase`, so requests go through the production
  HTTP pipeline in process (`WebApplicationFactory<Program>`) with that suite's JWT, claim-set and
  instance doubles and `BypassAuthorization` enabled. There is no network hop between client and host.
- **Pool ownership.** The suite's stub data-store provider never publishes data-store ownership, so
  `MssqlConnectionAcquisition` treats every pool identity as retired and clears it when its last lease
  ends, costing fresh logins on every request. The fixture publishes ownership for the configured
  primary once per host start, as `ConfigurationServiceDataStoreProvider` does in production. In the
  pilot's GET latency diagnostic, which is not part of the campaign, a GET by id took about 150 ms without
  it and about 2.4 ms with it (`task3b/pilot/ladder.json` through `ladder6.json` in the evidence set).
- **Data.** Before every block the published backup is restored into `dms1565_nr_run` (`WITH CHECKSUM`),
  set to recovery SIMPLE, owned by `sa`, given a new broker id, and has `SourceIdentity` rotated.
  Query Store is enabled (`READ_WRITE`, capture `ALL`, no size-based cleanup), cleared, and the database's
  procedure cache cleared. Read-committed snapshot stays off as backed up; production provisioning turns it
  on. The schema is the stock supported one in both arms, with all 143 root-to-`dms.Document` foreign
  keys present.
- **Schema compatibility.** SchemaTools at the artifact's recorded revision `efc9079a0`, at `0fd5300f0`
  and in the changed build compute effective schema hash `a0d39468…` for the core-only
  `EdFi.DataStandard52.ApiSchema` 1.0.335 and emit byte-identical DDL. The restored catalog matches a fresh
  `ddl provision` of the baseline (`catalog-dump.sql`, Configuration Service objects excluded, module text
  compared without CR and trailing newlines). The host validates the stored hash at startup, and each
  block's smoke GET must return 200.
- **Workload.** A deterministic document list: all 26 sessions and 83 class periods, and 200 each of
  course offerings and sections (those with dependents first) and student school associations. Bodies
  are prepared from GETs before timing. Each visit toggles one root column: `totalInstructionalDays`
  between v and v+1, or sets and removes `localCourseTitle`, `sectionName`, `officialAttendancePeriod`
  and `repeatGradeIndicator` (null throughout the dataset). The resource order rotates per pair.
- **Timing.** Client-side `Stopwatch` from sending the PUT through reading the whole response body. Any
  status other than 200 or 204 fails the block.
- **Statement attribution.** Query Store rows with `object_id = 0` whose text contains
  `UPDATE [edfi].[<Table>]` (trigger statements excluded). Snapshots are taken after warmup and after
  the measured requests, each following `sp_query_store_flush_db`. Totals sum `count_executions × avg`
  over every plan, execution type and interval row, and are differenced. Compile counts are differenced per
  query id, with new ids starting at zero. Instance compilation counters are recorded but not used.

## Reproducing

1. Download and verify the SQL Server v81 artifact as described in `eng/northridge/README.md`
   (archive sha256 `21a24f61…2786`, backup sha256 `38b071b1…8049`).
2. Start a dedicated SQL Server 2025 container with the extracted backup mounted read-only at `/backup`
   and a disk volume large enough for two restores (about 21 GB each), for example host port 1437 and a
   5 GB memory cap. Keep its SA password in a file and point `DMS1565_SA_PASSWORD_FILE` at it.
3. Prepare `$DMS1565_WORK`:
   - `git archive 0fd5300f0 | tar -x -C "$DMS1565_WORK/arm-baseline"`, and the same into `arm-changed` for
     the commit that merged this ticket, which is the first to add this README:
     `git log --diff-filter=A --format=%H -- eng/performance/dms-1565/README.md | tail -1`. That commit
     sends the measured statements (see above). If it also carries later `main` changes that move the
     effective schema hash, the analysis rejects the campaign.
   - Copy `Given_A_Dms1565_Northridge_Put_Latency_Block.cs` into each arm's
     `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/` and run
     `dotnet build src/dms/tests/EdFi.DataManagementService.Tests.Integration -c Release` in each arm.
   - `./stage-apischema.sh "$DMS1565_WORK/apischema-core-only"`. It copies the package unmodified and
     prints the `ApiSchema.json` sha256, `1051c5c3d1b2a3e488460a08a82f65a22fc49802b361df66dff8195b5aac5e73`.
   - Restore the backup once into a scratch database (as `run-block.sh` does) and run
     `./make-doclist.sh <database> "$DMS1565_WORK/doclist.json"`. The campaign's list has sha256
     `e231a71106f1b56109789f6166bc0dfb736ac946e994ef86fd5a2345b4924d3b`.
4. Run the two campaigns into the same output directory, then analyze all twelve blocks together:

   ```sh
   ./run-campaign.sh <output-dir> 1    # blocks 1-6: three baseline/changed pairs
   ./run-campaign.sh <output-dir> 7    # blocks 7-12: the second run of three pairs
   ./analyze_campaign.py <output-dir>  # requires exactly blocks 1-12 (pass a count to analyze fewer)
   ```

   Each campaign refuses to start unless both arms hold a byte-identical harness, every block log records
   the sha256 of its harness, document list and `ApiSchema.json`, and the analysis refuses captures that
   fail validation. `run-block.sh` refuses to start when the output directory already holds that block's
   JSON or log, so a failed retry cannot leave an earlier attempt's capture for the analysis. It connects
   the harness to the host port the SQL Server container publishes, and fails if `DMS1565_PORT` names a
   different one. The published captures keep the two runs
   in `task3b/run1/` and `task3b/run2/`, and `task3b/all12/` holds their combined analysis.

## Evidence

The raw captures behind every number above are attached to
[DMS-1565](https://edfi.atlassian.net/browse/DMS-1565) as `DMS-1565-evidence.zip` (attachment 41420,
sha256 `07eb6c49baa9a18c9b24e2a596839e5af26e278060b64381f793ec8edfbdc901`). The archive's
`DMS-1565/evidence-manifest.sha256` lists every file with its sha256 (195 files; manifest sha256
`1b1f94d686a074d7b69dd1e852fc98f3dc34ae5f8193d5ee4fe950269d0de0cd`), so
`shasum -a 256 -c evidence-manifest.sha256` run in `DMS-1565/` verifies the set:

- `task3b/run1/` and `task3b/run2/`: `block-<n>-<arm>.json` (every latency sample and the raw Query Store
  snapshots) and `block-<n>-<arm>.log` (restore, schema hash, database options, activity) for blocks
  1–12.
- `task3b/all12/analysis.json` (sha256 `75900d6d…6c26`) and `block-medians.csv` (sha256 `15c1ef5c…4f32`):
  the analysis output reported here.
- `task3b/validation/`: the analyzer's negative controls and a relative-output runner check.
- `task3a/plans/`: both arms' Session plans. `baseline/last-actual/000.sqlplan` and
  `changed/last-actual/000.sqlplan` are the root `UPDATE [edfi].[Session]` actual plans; the
  `query-store/` folders hold the compiled plans, `summary.json` and `analysis.json` the operator and
  table counts per arm, and `recorded-commands-last-put.sql` each arm's last Session PUT.
- `task3a/replay/`: the replayed root statements (`baseline.sql`, `changed.sql`), each run's
  `SET STATISTICS XML` output, and `replay-summary.json` with the 300 and 13 operator counts.
- `task3a/`, otherwise: the restore and catalog comparison, and the SchemaTools hash and DDL checksums for
  `efc9079a0`, `0fd5300f0` and the changed build.

The results were produced with the executed copies of these scripts, kept in the evidence set. The
committed versions add input validation, parameterized paths and the container port check. The committed
harness differs from the executed one by formatting and by dropping a GET latency diagnostic that the
campaign never ran.

## Limitations and open items

- One emulated host, in-process HTTP and doubled authorization, so these are relative results.
- Student school association and class period improvements are inside the noise threshold.
- The campaign does not reproduce the ticket's Volume configuration (139 foreign keys removed, other
  resources and concurrency); the operator-count difference from the ticket is unexplained.
