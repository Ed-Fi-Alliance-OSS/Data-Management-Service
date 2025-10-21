# Codex Session Notes

## Database Setup for Performance Testing

### Environment Notes
- PostgreSQL is running inside Docker container `dms-postgresql`.
- Default connection settings in `perf-claude/scripts/config.sh` already match the container (host `localhost`, port `5435`, user `postgres`, password `abcdefgh1!`, db `dms_perf_test`).
- The `postgresql-contrib` package is preinstalled in the container, so extensions such as `pg_stat_statements`, `pgcrypto`, and `pgstattuple` can be enabled.

### Setup Script Observations
- Initial run of `scripts/setup-test-db.sh --force` failed while defining fixture helper functions because unescaped `$$` delimiters were interpreted by Bash and replaced with the script PID (e.g., `192864`), producing multiple syntax errors in PostgreSQL. Updated the function definitions to use escaped delimiters (`\$\$`) before re-running.
- Postgres instance does not preload `pg_stat_statements`, so `pg_stat_statements_reset()` raises `pg_stat_statements must be loaded via shared_preload_libraries`. Wrapped the reset call in a guarded `DO` block that logs a notice instead of failing the setup.
- Applied `ALTER SYSTEM SET shared_preload_libraries = 'pg_stat_statements'` inside the `dms-postgresql` container and restarted it so the extension preloads; reran setup and confirmed the warning is gone.
- Setup can fail to drop `dms_perf_test` when clients (e.g., DBeaver) keep connections open. Terminated them via `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='dms_perf_test';` before rerunning with `--force`.
- Frequent checkpoint warnings during reference loads were mitigated by increasing WAL headroom: `ALTER SYSTEM SET max_wal_size = '8GB';` followed by `docker restart dms-postgresql`. Verified with `SHOW max_wal_size;`.
- Temporarily commented out the `COPY dms.Reference` block in `load-test-data-from-csv.sh` while diagnosing load performance; restored after verifying logs (see notes below).
- After cancelling the CSV loader mid-run, residual sessions can linger; run `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='dms_perf_test';` before restarting the load to avoid drop/lock failures.
- Restored the reference COPY once logs looked clean; the full CSV pipeline now loads 20M references but took ~61 minutes under `time ./generate-test-data.sh --mode csv`. Adjust CLI timeout when re-running long loads.
- Renamed parameters in `dms.insert_reference_target_for_docs` to avoid ambiguity with column names during `ON CONFLICT` updates and recreated the function in-place, then re-ran `SELECT dms.build_perf_reference_targets();` to refresh fixtures.
- Updated `reset_observability_counters` for PostgreSQL 16 by replacing the unsupported `pg_stat_reset_shared('func')` call with per-function `pg_stat_reset_single_function_counters` loops; WAL/IO resets remain guarded.
- Captured a full backup after the successful 20M-reference load at `/home/brad/work/dms-root/perf-claude-large-files/dms_perf_test_backup.dump`; restore from this artifact before rerunning baselines to avoid lengthy reloads.
- Converted the pg_stat_statements snapshot query in `scripts/config.sh` to use PostgreSQL 16 columns (`total_exec_time`, `mean_exec_time`) so observability captures stop erroring in the server logs.
- Updated the pgstattuple observability snapshot for PostgreSQL 16: aliased the lateral call to match the current column names (`approx_free_space`, etc.) to eliminate "column ... does not exist" log noise.
- Added `perf-claude/results/` to `.gitignore` and cleaned previously committed artifacts so future performance runs keep results local only.
- Long-running bulk loads exceed the default harness timeout; when invoking `generate-test-data.sh` through the CLI, pass a larger `timeout_ms` (e.g., `18000000` for ~5 h) instead of altering the script.
- PostgreSQL 16 renamed `pg_stat_user_tables.tablename` → `relname`; updated every script and SQL scenario accordingly so stats snapshots stop failing.
- Concurrent-load reporting now orders by `(test_parameters->>'sessions')::int` to avoid referencing a non-existent alias during ORDER BY.
