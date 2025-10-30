# DMS Reference Table Performance Testing Suite

This directory contains scripts and tools for testing the performance of the DMS three-table database design, specifically focusing on the References table performance issues.

## Directory Structure

```
perf-claude/
├── scripts/           # Bash scripts for setup, monitoring, execution
├── sql/              # SQL scripts
│   ├── current/      # Current implementation extracted from DMS
│   ├── alternatives/ # Alternative implementations to test
│   └── scenarios/    # Different test scenarios
├── data/             # Test data generation scripts
├── results/          # Performance test results
├── monitoring/       # Monitoring queries and tools
└── load-tests/       # Concurrent load testing scripts
```

> ⚠️ **Important**: The harness currently re-creates the Document, Alias, and Reference tables with local DDL embedded in this directory. If the canonical deploy scripts under `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts` change, re-run tests with that in mind or update the harness accordingly.

## Setup

### Virtual Environment
The Python data generation scripts require a virtual environment with specific dependencies:

```bash
# The venv is already set up. To recreate it:
python3 -m venv venv

# Install dependencies
./venv/bin/pip install -r requirements.txt
```

### Python Dependencies
- `asyncpg>=0.27.0` - PostgreSQL async driver
- `asyncio` - Async I/O support
- `uuid7` - For generating UUIDv7 identifiers

## Quick Start

1. Set up test database: `./scripts/setup-test-db.sh --force`
2. Generate test data (deterministic CSV + COPY load): `./scripts/generate-test-data.sh`
3. Run baseline tests: `./scripts/run-baseline-tests.sh`
4. Run alternative implementations: `./scripts/test-alternatives.sh`
5. Generate report: `./scripts/generate-report.sh`

All scripts capture metrics under `perf-claude/results/<timestamp>` (with optional per-scenario suffixes). The timestamp is generated once per run so the snapshot files, SQL output, and reports stay in the same folder. Set the environment variable `RESULTS_TIMESTAMP=20240101_120000` before invoking any scripts if you want to aggregate multiple runs into a single directory.

### Metrics and Snapshots

- `run-all-tests.sh` automatically collects database statistics **before** and **after** a test run. Look for `before_tests_pg_stats.txt` and `after_tests_pg_stats.txt` in the run’s results directory to inspect table/vacuum state and database-level counters without polluting the workload.
- Continuous polling is no longer enabled by default. If you still need live sampling, run `scripts/monitor-performance.sh` manually; adjust `MONITOR_INTERVAL` in `scripts/config.sh` to a coarse cadence (default 10 s) before invoking it.

### Deterministic CSV Pipeline (Fast Path)

Use the external generator when you want reproducible data files that can be bulk loaded with `COPY`:

```bash
# 1. Create & load the dataset (defaults: 100k docs, 20M refs, 16 partitions)
./scripts/generate-test-data.sh --mode csv

# Optional: generate CSVs only, then load later
./venv/bin/python3 ./data/generate_deterministic_data.py --output ./data/out
./scripts/load-test-data-from-csv.sh ./data/out
```

Environment variables `NUM_DOCUMENTS`, `NUM_REFERENCES`, `AVG_REFS_PER_DOC`, and `NUM_PARTITIONS` are honored by both commands. The generator produces the same dataset on every run, matching the partition-key logic used by the service.

### Dataset Generation Modes

- **CSV pipeline (default)** – deterministic CSV generation followed by `COPY`. Fastest and fully repeatable. Invoked via `./scripts/generate-test-data.sh --mode csv`.
- **SQL pipeline** – in-database generator that now loads references in configurable chunks and records progress. Useful when external tools are unavailable. Invoke with `./scripts/generate-test-data.sh --mode sql [--resume] [--chunk-size <docs>]`.
- **Resume support** – rerun the SQL pipeline with `--resume` to continue after interruptions. Each chunk deletes and reloads its document range to remain idempotent.

## Deterministic Reference Fixtures

Every data-generation pathway now refreshes the table `dms.perf_reference_targets`, which provides repeatable reference payloads for the SQL test scenarios. The table is rebuilt automatically by both the CSV loader and the SQL generator via `SELECT dms.build_perf_reference_targets();`.

### Bundled fixtures

| Target name          | Description                                                 | Intended usage                    |
|----------------------|-------------------------------------------------------------|-----------------------------------|
| `single_doc_standard`| Document 2 with the first 50 references (average document). | Baseline single-document testing. |
| `single_doc_heavy`   | Document 1 with the first 200 references (heavy document).  | Stress heavy reference loads.     |
| `batch_100_mixed`    | Documents 1–100 with up to 20 references each.              | Batch `InsertReferences` testing. |

The test scripts load these fixtures directly:

- `sql/current/test_current_insert_references.sql` consumes `single_doc_standard`.
- `sql/scenarios/batch_operations.sql` consumes `batch_100_mixed`.

### Managing fixtures

- Regenerate fixtures at any time with `psql -c "SELECT dms.build_perf_reference_targets();"` on the test database.
- To add new scenarios, edit the function `dms.build_perf_reference_targets()` in `scripts/setup-test-db.sh` (it delegates to `dms.insert_reference_target_for_docs`) and rerun the command above.
- Custom fixtures can be inspected via `SELECT * FROM dms.perf_reference_targets ORDER BY created_at DESC;`.

### Deterministic Workloads

- All SQL scenario files now read their parent/reference arrays directly from the fixture table. The old `ORDER BY random()` lookups have been removed, so every run reuses the same inputs and avoids full-table random scans.
- The Python concurrency harness seeds its samples from the same fixture data: it pulls the first 1 000 documents and 10 000 aliases ordered by partition/id, shuffles them with a fixed seed, and uses a per-session RNG (`seed = 12345 + sessionId`) when choosing documents or reference batches.
- To vary the data set, update the fixture builder or adjust the seeded limits in `load-tests/concurrent_load_test.py`. Because the seeds are fixed, rerunning a test with the same configuration reproduces identical document/reference selections.

## Configuration

Edit `scripts/config.sh` to set:
- Database connection parameters
- Test data volume settings (honored by CSV and SQL loaders)
- Performance thresholds

## Observability Artifacts

Every scenario run now generates before/after snapshots alongside the raw `psql` output under `results/<timestamp>/`:

- `*_output.txt` – console output from the SQL or load test script (timings, EXPLAIN plans, assertions).
- `*_before_observability.txt` – baseline counters immediately after resetting `pg_stat_*` views; values should be zeroed.
- `*_after_observability.txt` – post-run counters including:
  - `pg_stat_statements` for every query executed during the scenario (calls, total time, shared/temp IO).
  - `pg_stat_user_functions` scoped to the `dms` schema to surface PL/pgSQL function cost (`InsertReferences`, alternatives, helpers).
  - `pg_statio_user_tables` for `Document`, `Alias`, and `Reference` to compare heap/index hit ratios.
  - `pg_stat_wal` (if available) to show WAL volume emitted by the run.
  - `pgstattuple_approx` (if available) for `dms.Reference`—reports totals plus per-partition dead-tuple and free-space percentages.
  - `pg_stat_io` (if available) for per-backend read/write time across client backends and background writers.
  - Current wait events for connections tagged with `application_name` starting with `perf_`.
- All harness sessions automatically set `application_name = 'perf_<scenario>'` and `track_functions = 'pl'`, so the database views and logs neatly group activity by workload without extra manual setup.

Analyze the delta between the before/after files to understand how many shared hits vs reads, temp spills, and WAL bytes each scenario generated. Because the harness resets counters before each test, the `after` snapshot already reflects the workload cost without further subtraction.

> **Extension requirement**: `pgstattuple_approx` lives in the `pgstattuple` extension, which ships with the PostgreSQL contrib modules but is not installed by default. Make sure the matching `postgresql-contrib` package is present for your server version, then enable it with `CREATE EXTENSION pgstattuple;` before running the harness if you want the bloat metrics.
> The function operates only on physical partitions; the harness automatically iterates the `dms.Reference` partitions and aggregates the results.

## Test Scenarios

1. **Single Document Update**: Update references for one document
2. **Batch Updates**: Update 1000 documents in a transaction
3. **Concurrent Updates**: Multiple sessions updating different documents
4. **Cross-Partition**: Updates spanning multiple partitions
5. **Heavy Read/Write**: Mixed workload simulation

## Key Findings

Performance issues identified:
- DELETE-then-INSERT pattern in InsertReferences causes massive overhead
- Reverse lookups rely on the referenced-document covering index; monitor IX_Reference_ReferencedDocument and Alias hydration logic
- Lock contention during bulk operations
- No differential updates for references
