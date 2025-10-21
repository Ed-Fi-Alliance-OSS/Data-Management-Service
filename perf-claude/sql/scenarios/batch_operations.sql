-- Batch Operations Test Scenario
-- Tests updating references for multiple documents in a single transaction using deterministic fixtures.

\timing on
\set ON_ERROR_STOP on

SET application_name = 'perf_batch_operations';
SET track_functions = 'pl';
SET client_min_messages = 'warning';

\set fixture_target 'batch_100_mixed'

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
\set batch_size :fixture_document_count
\set refs_per_doc :fixture_min_per_document
\set total_refs :fixture_reference_count

\if :batch_size = 0
    \echo 'ERROR: Fixture target ' :fixture_target ' does not include any documents.' >&2
    \quit 1
\endif

INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'batch_operations_current',
    'batch_update',
    NOW(),
    jsonb_build_object(
        'batch_size', :batch_size,
        'refs_per_doc', :refs_per_doc,
        'total_refs', :total_refs,
        'fixture_target', :'fixture_target'
    )
) RETURNING test_id AS test_id \gset

\echo 'Fixture information:'
SELECT
    :batch_size::INT AS document_count,
    :refs_per_doc::INT AS min_refs_per_doc,
    :total_refs::INT AS total_reference_rows;

\echo 'Validating fixture array lengths...'
SELECT
    array_length(:'parent_ids'::bigint[], 1) AS parent_count,
    array_length(:'ref_ids'::uuid[], 1) AS ref_count,
    array_length(:'parent_partition_keys'::smallint[], 1) AS parent_key_count,
    array_length(:'ref_partition_keys'::smallint[], 1) AS ref_key_count,
    :total_refs::INT AS expected_count
\gset array_check

\if :array_check.parent_count != :array_check.ref_count \or :array_check.parent_key_count != :array_check.ref_key_count
    \echo 'ERROR: Fixture arrays for target ' :fixture_target ' are misaligned.' >&2
    \quit 1
\endif

\if :array_check.parent_count != :array_check.expected_count
    \echo 'WARNING: Expected ' :array_check.expected_count ' rows but fixture provided ' :array_check.parent_count '.' >&2
\endif

DO $$
DECLARE
    start_time TIMESTAMP;
    end_time TIMESTAMP;
    dead_tuples_before INTEGER;
    dead_tuples_after INTEGER;
    effective_rows INTEGER := :total_refs;
BEGIN
    SELECT n_dead_tup INTO dead_tuples_before
    FROM pg_stat_user_tables
    WHERE schemaname = 'dms' AND relname = 'reference';

    start_time := clock_timestamp();

    BEGIN
        PERFORM dms.InsertReferences(
            :'parent_ids'::bigint[],
            :'parent_partition_keys'::smallint[],
            :'ref_ids'::uuid[],
            :'ref_partition_keys'::smallint[]
        );
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'Batch operation failed: %', SQLERRM;
        effective_rows := 0;
    END;

    end_time := clock_timestamp();

    SELECT n_dead_tup INTO dead_tuples_after
    FROM pg_stat_user_tables
    WHERE schemaname = 'dms' AND relname = 'reference';

    UPDATE dms.perf_test_results
    SET
        end_time = NOW(),
        avg_latency_ms = EXTRACT(EPOCH FROM (end_time - start_time)) * 1000,
        rows_affected = effective_rows,
        dead_tuples_before = dead_tuples_before,
        dead_tuples_after = dead_tuples_after,
        table_size_mb = (SELECT pg_relation_size('dms.reference') / 1024.0 / 1024.0),
        index_size_mb = (SELECT pg_indexes_size('dms.reference') / 1024.0 / 1024.0),
        additional_metrics = jsonb_build_object(
            'batch_size', :batch_size,
            'refs_per_doc', :refs_per_doc,
            'new_dead_tuples', dead_tuples_after - dead_tuples_before,
            'ms_per_document', CASE
                WHEN :batch_size::INT = 0 THEN NULL
                ELSE EXTRACT(EPOCH FROM (end_time - start_time)) * 1000 / :batch_size::INT
            END,
            'fixture_target', :'fixture_target',
            'fixture_description', (
                SELECT description
                FROM dms.perf_reference_targets
                WHERE target_name = :'fixture_target'
            ),
            'fixture_reference_count', :total_refs,
            'fixture_requested_per_document', :fixture_requested_per_document,
            'fixture_min_per_document', :fixture_min_per_document,
            'fixture_max_per_document', :fixture_max_per_document
        )
    WHERE test_id = :test_id;
END$$;

SELECT
    test_name,
    test_type,
    round(avg_latency_ms::numeric, 2) AS total_ms,
    rows_affected,
    round((additional_metrics->>'ms_per_document')::numeric, 2) AS ms_per_doc,
    (additional_metrics->>'new_dead_tuples')::INT AS new_dead_tuples,
    round(table_size_mb::numeric, 2) AS table_size_mb,
    round(index_size_mb::numeric, 2) AS index_size_mb
FROM dms.perf_test_results
WHERE test_id = :test_id;
