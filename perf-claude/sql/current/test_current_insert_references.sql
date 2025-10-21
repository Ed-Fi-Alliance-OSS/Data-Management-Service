-- Test the current InsertReferences implementation (DELETE then INSERT pattern)
-- This mimics what DMS currently does using deterministic fixture data.

\timing on
\set ON_ERROR_STOP on

SET application_name = 'perf_current_insert_references';
SET track_functions = 'pl';
SET client_min_messages = 'warning';

\set num_updates 100
\set fixture_target 'single_doc_standard'

\unset fixture_target_name
\unset fixture_description
\unset fixture_parent_document_ids
\unset fixture_parent_partition_keys
\unset fixture_reference_ids
\unset fixture_reference_partition_keys
\unset fixture_document_count
\unset fixture_reference_count
\unset fixture_requested_per_document
\unset fixture_min_per_document
\unset fixture_max_per_document

SELECT
    target_name,
    description,
    parent_document_ids,
    parent_partition_keys,
    reference_ids,
    reference_partition_keys,
    document_count,
    reference_count,
    requested_per_document,
    min_per_document,
    max_per_document
FROM dms.perf_reference_targets
WHERE target_name = :'fixture_target'
\gset fixture_

\if :{?fixture_target_name}
\else
    \echo 'ERROR: Fixture target ' :fixture_target ' not found in dms.perf_reference_targets. Run the data generation script to populate fixtures.' >&2
    \quit 1
\endif

\set parent_ids :'fixture_parent_document_ids'
\set parent_partition_keys :'fixture_parent_partition_keys'
\set ref_ids :'fixture_reference_ids'
\set ref_partition_keys :'fixture_reference_partition_keys'
\set ref_count :fixture_reference_count

SELECT
    (:'parent_ids'::bigint[])[1] AS doc_id,
    (:'parent_partition_keys'::smallint[])[1] AS doc_partition_key
\gset doc_

\if :'doc_doc_id' = ''
    \echo 'ERROR: Fixture target ' :fixture_target ' did not provide a parent document id.' >&2
    \quit 1
\endif

\set doc_id :doc_doc_id
\set doc_partition_key :doc_doc_partition_key
\set refs_per_doc :ref_count

-- Record test start
INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'current_insert_references',
    'single_document_update',
    NOW(),
    jsonb_build_object(
        'num_updates', :num_updates,
        'fixture_target', :'fixture_target',
        'fixture_reference_count', :ref_count
    )
) RETURNING test_id AS test_id \gset

-- Record current state
SELECT
    COUNT(*) AS refs_before,
    (SELECT n_dead_tup FROM pg_stat_user_tables WHERE schemaname = 'dms' AND relname = 'reference') AS dead_tuples_before
FROM dms.Reference
WHERE ParentDocumentId = :doc_id
  AND ParentDocumentPartitionKey = :doc_partition_key
\gset

DROP TABLE IF EXISTS dms_temp_latency;
CREATE TEMP TABLE dms_temp_latency (
    duration_ms NUMERIC
) ON COMMIT DROP;

-- Capture explain plan once
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT dms.InsertReferences(
    :'parent_ids'::bigint[],
    :'parent_partition_keys'::smallint[],
    :'ref_ids'::uuid[],
    :'ref_partition_keys'::smallint[]
) AS explain_json \gset

-- Execute the operation multiple times to capture latency
DO $$
DECLARE
    start_time TIMESTAMP;
    end_time TIMESTAMP;
    total_duration INTERVAL := '0'::INTERVAL;
    operation_duration_ms NUMERIC;
    i INTEGER;
BEGIN
    FOR i IN 1..:num_updates LOOP
        start_time := clock_timestamp();

        PERFORM dms.InsertReferences(
            :'parent_ids'::bigint[],
            :'parent_partition_keys'::smallint[],
            :'ref_ids'::uuid[],
            :'ref_partition_keys'::smallint[]
        );

        end_time := clock_timestamp();
        total_duration := total_duration + (end_time - start_time);
        operation_duration_ms := EXTRACT(EPOCH FROM (end_time - start_time)) * 1000;

        INSERT INTO dms_temp_latency(duration_ms)
        VALUES (operation_duration_ms);

        PERFORM pg_sleep(0.01);
    END LOOP;

    UPDATE dms.perf_test_results
    SET
        end_time = NOW(),
        avg_latency_ms = EXTRACT(EPOCH FROM total_duration / :num_updates) * 1000,
        rows_affected = :ref_count
    WHERE test_id = :test_id;
END$$;

-- Record final state and enrich metrics
WITH latency_stats AS (
    SELECT
        AVG(duration_ms) AS avg_ms,
        MIN(duration_ms) AS min_ms,
        MAX(duration_ms) AS max_ms,
        percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms) AS p95_ms,
        percentile_cont(0.99) WITHIN GROUP (ORDER BY duration_ms) AS p99_ms,
        COUNT(*) AS call_count
    FROM dms_temp_latency
),
histogram AS (
    SELECT jsonb_object_agg(bucket, bucket_count) AS histogram
    FROM (
        SELECT bucket, COUNT(*) AS bucket_count
        FROM (
            SELECT CASE
                WHEN duration_ms < 10  THEN '00-10ms'
                WHEN duration_ms < 25  THEN '10-25ms'
                WHEN duration_ms < 50  THEN '25-50ms'
                WHEN duration_ms < 100 THEN '50-100ms'
                WHEN duration_ms < 250 THEN '100-250ms'
                WHEN duration_ms < 500 THEN '250-500ms'
                WHEN duration_ms < 1000 THEN '500-1000ms'
                ELSE '>1000ms'
            END AS bucket
            FROM dms_temp_latency
        ) buckets
        GROUP BY bucket
    ) ordered
)
UPDATE dms.perf_test_results
SET
    avg_latency_ms = (SELECT avg_ms FROM latency_stats),
    min_latency_ms = (SELECT min_ms FROM latency_stats),
    max_latency_ms = (SELECT max_ms FROM latency_stats),
    p95_latency_ms = (SELECT p95_ms FROM latency_stats),
    p99_latency_ms = (SELECT p99_ms FROM latency_stats),
    dead_tuples_before = :dead_tuples_before,
    dead_tuples_after = (SELECT n_dead_tup FROM pg_stat_user_tables WHERE schemaname = 'dms' AND relname = 'reference'),
    table_size_mb = (SELECT pg_relation_size('dms.reference') / 1024.0 / 1024.0),
    index_size_mb = (SELECT pg_indexes_size('dms.reference') / 1024.0 / 1024.0),
    explain_plan = :'explain_json'::jsonb,
    additional_metrics = COALESCE(additional_metrics, '{}'::jsonb)
        || jsonb_build_object(
            'latency_call_count', (SELECT call_count FROM latency_stats),
            'latency_histogram', COALESCE((SELECT histogram FROM histogram), '{}'::jsonb),
            'fixture_target', :'fixture_target',
            'fixture_reference_count', :ref_count,
            'fixture_description', (
                SELECT description
                FROM dms.perf_reference_targets
                WHERE target_name = :'fixture_target'
            ),
            'fixture_requested_per_document', :fixture_requested_per_document,
            'fixture_min_per_document', :fixture_min_per_document,
            'fixture_max_per_document', :fixture_max_per_document
        )
WHERE test_id = :test_id;

-- Show results
SELECT
    test_name,
    round(min_latency_ms::numeric, 2) AS min_latency_ms,
    round(avg_latency_ms::numeric, 2) AS avg_latency_ms,
    round(max_latency_ms::numeric, 2) AS max_latency_ms,
    round(p95_latency_ms::numeric, 2) AS p95_latency_ms,
    round(p99_latency_ms::numeric, 2) AS p99_latency_ms,
    rows_affected,
    dead_tuples_after - :dead_tuples_before AS new_dead_tuples,
    round(table_size_mb::numeric, 2) AS table_size_mb,
    round(index_size_mb::numeric, 2) AS index_size_mb
FROM dms.perf_test_results
WHERE test_id = :test_id;
