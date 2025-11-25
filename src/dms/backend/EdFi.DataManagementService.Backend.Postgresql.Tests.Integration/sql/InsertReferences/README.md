# InsertReferences2 SQL Test Suite

This suite exercises the `dms.InsertReferences` stored procedure to verify the behavior of the CTE-based implementation matches the existing temp-table version.

## Files

- `000_setup.sql` – idempotent helper data (document row + aliases).
- `010_pure_insert.sql` – pure insert path returns `(true, {})`.
- `020_update_noop.sql` – non-pure call that detects no changes and skips DML yet still returns success.
- `030_update_with_change.sql` – update that swaps one alias; ensures upsert/delete path still returns success.
- `040_invalid_ids.sql` – payload with an unresolved referentialId returns `(false, invalid_uuid)`.
- `050_duplicate_payload.sql` – duplicate referential pair raises the `reference_stage_pkey` violation (SQLSTATE 23505).
- `060_null_inputs.sql` – null referentialId/partition key inputs raise the 23502 constraint violation.
- `070_partition_variation.sql` – repeats the pure-insert scenario on parent partition 7 to confirm partition-targeted DML selection.
- `080_insert_delete_mix.sql` – invokes InsertReferences twice in one transaction to cover the delete/orphan cleanup branch.
- `085_partial_failure_cleanup.sql` – mixed resolved/unresolved ids returns `(false, invalid_uuid)`, leaves no references, and clears `dms.ReferenceStage`.
- `090_high_cardinality.sql` – feeds ~25 references (selected from seeded aliases) to mirror real payload sizes.
- `100_rollback_safety.sql` – forces an error after the call and verifies reference row counts remain unchanged.
- `200_pgbench_concurrency.sql` – pgbench workload file for stressing InsertReferences under concurrent sessions.

## Running

1. Ensure the desired version of `dms.InsertReferences` is deployed.
2. Set `PGPASSWORD`/connection env vars as needed, then execute the scripts in order via `psql`, e.g.:
   ```bash
   psql -h localhost -p 5432 -U postgres -d edfi_datamanagementservice -f 000_setup.sql
   psql ... -f 010_pure_insert.sql
   # ...and so on
   ```
3. Each script uses `BEGIN/ROLLBACK` (or anonymous DO blocks) so the test data is not persisted.
4. For concurrent load testing, run `200_pgbench_concurrency.sql` with pgbench. Basic example:
   ```bash
   PGPASSWORD='...' pgbench -h localhost -p 5432 -U postgres -d edfi_datamanagementservice \
       -f 200_pgbench_concurrency.sql \
       -c 16   # concurrent clients
       -j 4    # worker threads
       -t 2000 # transactions per client
       --progress=10 --no-vacuum
   ```
   Longer runs just scale those knobs. For example, a 10-minute soak with 32 clients:
   ```bash
   PGPASSWORD='...' pgbench -h localhost -p 5432 -U postgres -d edfi_datamanagementservice \
       -f 200_pgbench_concurrency.sql \
       -c 32 -j 8 -T 600 --progress=30 --no-vacuum
   ```
   Useful pgbench switches:
   - `-c/--client`: parallel sessions.
   - `-j/--jobs`: threads (usually <= cores).
   - `-t/--transactions`: count per client (total calls = clients × transactions).
   - `-T/--time`: run for N seconds instead of a fixed count.
   - `-P/--progress`: status interval, `--no-vacuum` skips pgbench’s setup tables.

The scripts print the procedure output (or NOTICEs for expected failures) so regressions in success flags, invalid-id handling, or error semantics are easy to spot.
