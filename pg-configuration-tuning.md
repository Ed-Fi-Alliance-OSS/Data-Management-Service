# PostgreSQL 16 Configuration Tuning for DMS (Write‑Heavy Inserts)

This guide outlines PostgreSQL 16 settings that typically improve sustained insert throughput and stability for Ed‑Fi DMS workloads, especially the write‑hot `dms.Reference` table (delete‑then‑insert pattern, multiple FK validations, and several B‑tree index updates per row). Values below are starting points; validate with monitoring and adjust to your hardware and workload.

## WAL and Checkpoints

- `max_wal_size`: 8–16GB
  - Increases WAL headroom; reduces checkpoint frequency and write bursts during heavy churn.
- `min_wal_size`: 1–2GB
  - Keeps preallocated WAL to avoid frequent recycling.
- `checkpoint_timeout`: 10–15min
  - Avoids overly frequent timed checkpoints (default 5min).
- `checkpoint_completion_target`: 0.9–0.95
  - Spreads checkpoint I/O to reduce latency spikes.
- `wal_compression`: on
  - Lowers WAL volume at modest CPU cost.
- `wal_buffers`: auto (default) or 16–64MB for very heavy write nodes
  - Auto is generally best on PG16.
- Keep `full_page_writes` on for safety.

Monitor with `pg_stat_bgwriter` (e.g., checkpoints_timed vs checkpoints_req, buffers_checkpoint, checkpoint_write_time/sync_time) and server logs for checkpoint cadence.

## Autovacuum (Global Capacity)

- `autovacuum_max_workers`: 5–10
- `autovacuum_naptime`: 30–60s
- `autovacuum_vacuum_cost_limit`: 1500–3000
- `autovacuum_vacuum_cost_delay`: 0–5ms (0ms if I/O headroom exists)
- `autovacuum_work_mem`: 256–512MB (per worker; ensure total fits RAM)

Per‑table reloptions should complement system settings on `dms.reference_*` partitions (e.g., `autovacuum_vacuum_scale_factor=0.01–0.05`, `autovacuum_analyze_scale_factor=0.01–0.05`) to keep up with delete→insert churn.

## Memory and Caching

- `shared_buffers`: ~25% of RAM (respect container limits)
- `effective_cache_size`: ~50–75% of RAM (planner hint)
- `work_mem`: 16–64MB per query (not critical to inserts; helps read‑side sorts/joins)
- `maintenance_work_mem`: 1–2GB during planned reindex/vacuum operations

## I/O and Parallelism

- `effective_io_concurrency`: 200–300 (SSD/NVMe)
- `maintenance_io_concurrency`: 200–300 (accelerates VACUUM/INDEX I/O)
- `max_worker_processes`: 16–32 (autovac + parallel workers + background)
- `max_parallel_workers`: 8–16
- `max_parallel_workers_per_gather`: 2–4
- `max_parallel_maintenance_workers`: 2–4 (for CREATE INDEX/VACUUM where applicable)

## Replication and Logical Decoding (Debezium)

- `wal_level`: logical (required)
- `max_wal_senders`: ≥ number of logical/physical senders (e.g., 5+)
- `max_replication_slots`: ≥ number of logical consumers (e.g., 4–8)
- `max_slot_wal_keep_size`: 8–16GB (cap retained WAL if a slot lags)
- `logical_decoding_work_mem`: 128–256MB (large transactions)

Monitor logical replication lag and slot‑retained WAL to prevent unbounded growth.

## Workload‑Specific (Session/Transaction)

- `SET LOCAL synchronous_commit=off;` during bulk ingest sessions only
  - Reduces commit latency at the cost of a slightly larger crash‑recovery window.
- Use larger transactions if you make FKs `DEFERRABLE INITIALLY DEFERRED`, to batch FK checks to commit.

## Planner Hints (SSD)

- `random_page_cost`: 1.1–1.5
- `seq_page_cost`: 1.0

These improve read plans on SSD; they don’t directly affect insert throughput but help occasional reverse‑lookup reads.

## Observability and Validation

Use these views to validate the effect of tuning:

- Checkpoints/WAL
  - `SELECT * FROM pg_stat_bgwriter;`
  - `SELECT * FROM pg_stat_wal;` (PG16)
- Autovacuum/Dead tuples
  - `SELECT relname, n_live_tup, n_dead_tup, last_autovacuum, vacuum_count
     FROM pg_stat_all_tables WHERE schemaname='dms' AND relname ILIKE 'reference%';`
  - `SELECT * FROM pg_stat_progress_vacuum;`
- I/O pressure and index cache
  - `SELECT * FROM pg_stat_io;` (PG16)
  - `SELECT * FROM pg_statio_user_indexes WHERE schemaname='dms' AND relname ILIKE 'reference%';`
- Replication
  - Monitor logical slot lag and retained WAL size; ensure it stays under `max_slot_wal_keep_size`.

## Suggested Starting Profiles

Steady‑state

- WAL & Checkpoints
  - `max_wal_size=8GB`, `min_wal_size=1GB`
  - `checkpoint_timeout=15min`, `checkpoint_completion_target=0.9`
  - `wal_compression=on`
- Autovacuum
  - `autovacuum_max_workers=6`, `autovacuum_vacuum_cost_limit=2000`, `autovacuum_work_mem=256MB`
- Memory & I/O
  - `shared_buffers=25% RAM`, `effective_cache_size=60% RAM`, `effective_io_concurrency=200`, `maintenance_io_concurrency=200`
- Replication
  - `max_replication_slots=4–8`, `max_slot_wal_keep_size=8GB`, `logical_decoding_work_mem=128MB`

Bulk ingest window (temporary)

- Inherit steady‑state settings, and:
  - Session: `SET synchronous_commit=off;`
  - Optionally increase `max_wal_size=16GB` if disk allows and checkpoints remain frequent.

### Example `postgresql.conf` snippet (adjust to your RAM/SSD)

```conf
# WAL / checkpoints
max_wal_size = 8GB
min_wal_size = 1GB
checkpoint_timeout = 15min
checkpoint_completion_target = 0.9
wal_compression = on

# Autovacuum capacity
autovacuum_max_workers = 6
autovacuum_naptime = 30s
autovacuum_vacuum_cost_limit = 2000
autovacuum_vacuum_cost_delay = 2ms
autovacuum_work_mem = '256MB'

# Memory & caching
shared_buffers = '8GB'                 # ~25% RAM example
effective_cache_size = '20GB'          # planner hint example
work_mem = '32MB'
maintenance_work_mem = '2GB'

# I/O & parallelism
effective_io_concurrency = 200
maintenance_io_concurrency = 200
max_worker_processes = 24
max_parallel_workers = 12
max_parallel_workers_per_gather = 3
max_parallel_maintenance_workers = 3

# Logical replication (Debezium)
wal_level = logical
max_wal_senders = 10
max_replication_slots = 8
max_slot_wal_keep_size = 8GB
logical_decoding_work_mem = '128MB'
```

## Cautions

- Larger `max_wal_size`/`checkpoint_timeout` increase crash recovery time; choose values consistent with RTO.
- Logical replication slots can retain WAL beyond `max_wal_size`; enforce `max_slot_wal_keep_size` and monitor consumer lag.
- Increasing autovacuum workers and cost limits can saturate I/O; raise gradually and observe `pg_stat_io` and latency.
- Ensure container or VM memory limits align with `shared_buffers` and other memory allocations.

## Next Steps

1) Apply steady‑state profile and monitor for a week.
2) Adjust based on checkpoint frequency, dead tuple accumulation, and WAL growth.
3) For high‑volume loads, use the bulk profile temporarily and revert afterwards.

