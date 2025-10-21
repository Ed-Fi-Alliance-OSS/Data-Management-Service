-- Test Problematic Patterns
-- These tests specifically target known performance issues

\timing on
\set ON_ERROR_STOP on

SET application_name = 'perf_problematic_patterns';
SET track_functions = 'pl';
SET client_min_messages = 'warning';

\echo '================================================'
\echo 'PROBLEMATIC PATTERN TESTING'
\echo 'Testing specific scenarios that cause issues'
\echo '================================================'
\echo ''

-- Deterministic fixtures to avoid random sampling during tests
\set base_fixture 'single_doc_standard'
\set heavy_fixture 'single_doc_heavy'
\set batch_fixture 'batch_100_mixed'

\unset base_target_name
\unset base_parent_document_ids
\unset base_parent_partition_keys
\unset base_reference_ids
\unset base_reference_partition_keys
\unset base_reference_count

\unset heavy_target_name
\unset heavy_reference_ids
\unset heavy_reference_partition_keys
\unset heavy_reference_count

\unset batch_target_name
\unset batch_parent_document_ids
\unset batch_parent_partition_keys
\unset batch_reference_ids
\unset batch_reference_partition_keys
\unset batch_reference_count

SELECT
    target_name,
    parent_document_ids,
    parent_partition_keys,
    reference_ids,
    reference_partition_keys,
    reference_count
FROM dms.perf_reference_targets
WHERE target_name = :'base_fixture'
\gset base_

\if :{?base_target_name}
\else
    \echo 'ERROR: Base fixture ' :base_fixture ' not found. Run data generation first.' >&2
    \quit 1
\endif

SELECT
    target_name,
    reference_ids,
    reference_partition_keys,
    reference_count
FROM dms.perf_reference_targets
WHERE target_name = :'heavy_fixture'
\gset heavy_

\if :{?heavy_target_name}
\else
    \echo 'ERROR: Heavy fixture ' :heavy_fixture ' not found. Run data generation first.' >&2
    \quit 1
\endif

SELECT
    target_name,
    parent_document_ids,
    parent_partition_keys,
    reference_ids,
    reference_partition_keys,
    reference_count
FROM dms.perf_reference_targets
WHERE target_name = :'batch_fixture'
\gset batch_

\if :{?batch_target_name}
\else
    \echo 'ERROR: Batch fixture ' :batch_fixture ' not found. Run data generation first.' >&2
    \quit 1
\endif

-- Test 1: Frequent updates to same document (causes massive dead tuples)
\echo 'Test 1: Repeated updates to same document'
\echo '-----------------------------------------'

SELECT
    (:'base_parent_document_ids'::bigint[])[1]     AS target_doc_id,
    (:'base_parent_partition_keys'::smallint[])[1] AS target_partition_key
\gset target_

INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'repeated_same_document_updates',
    'problematic_pattern',
    NOW(),
    jsonb_build_object('pattern', 'repeated_updates', 'iterations', 100)
) RETURNING test_id AS test_id \gset

DO $$
DECLARE
    i integer;
    start_time timestamp;
    total_time interval := '0'::interval;
    refs uuid[];
    ref_keys smallint[];
    parent_ids bigint[];
    parent_keys smallint[];
    actual_refs_inserted integer := 0;
    small_refs uuid[];
    small_keys smallint[];
    medium_refs uuid[];
    medium_keys smallint[];
    large_refs uuid[];
    large_keys smallint[];
    alt_refs uuid[];
    alt_keys smallint[];
BEGIN
    small_refs := (:'heavy_reference_ids'::uuid[])[1:20];
    small_keys := (:'heavy_reference_partition_keys'::smallint[])[1:20];
    medium_refs := (:'heavy_reference_ids'::uuid[])[1:35];
    medium_keys := (:'heavy_reference_partition_keys'::smallint[])[1:35];
    large_refs := (:'heavy_reference_ids'::uuid[])[1:50];
    large_keys := (:'heavy_reference_partition_keys'::smallint[])[1:50];
    alt_refs := (:'batch_reference_ids'::uuid[])[1:50];
    alt_keys := (:'batch_reference_partition_keys'::smallint[])[1:50];

    IF small_refs IS NULL OR medium_refs IS NULL OR large_refs IS NULL OR alt_refs IS NULL THEN
        RAISE EXCEPTION 'Fixture data did not provide enough reference rows for repeated update test';
    END IF;

    RAISE NOTICE 'Starting repeated updates to document % using deterministic variations', :target_target_doc_id;

    FOR i IN 1..100 LOOP
        CASE (i % 3)
            WHEN 1 THEN
                refs := small_refs;
                ref_keys := small_keys;
            WHEN 2 THEN
                refs := medium_refs;
                ref_keys := medium_keys;
            ELSE
                refs := large_refs;
                ref_keys := large_keys;
        END CASE;

        IF i % 5 = 0 THEN
            refs := alt_refs;
            ref_keys := alt_keys;
        END IF;

        parent_ids := array_fill(:target_target_doc_id::bigint, ARRAY[array_length(refs, 1)]);
        parent_keys := array_fill(:target_target_partition_key::smallint, ARRAY[array_length(refs, 1)]);

        start_time := clock_timestamp();

        PERFORM dms.InsertReferences(
            parent_ids,
            parent_keys,
            refs,
            ref_keys
        );

        total_time := total_time + (clock_timestamp() - start_time);
        actual_refs_inserted := actual_refs_inserted + array_length(refs, 1);

        IF i % 20 = 0 THEN
            RAISE NOTICE 'Completed % updates, avg time: % ms',
                i,
                EXTRACT(EPOCH FROM total_time / i) * 1000;
        END IF;
    END LOOP;

    UPDATE dms.perf_test_results
    SET
        end_time = NOW(),
        avg_latency_ms = EXTRACT(EPOCH FROM total_time / 100) * 1000,
        rows_affected = actual_refs_inserted,
        dead_tuples_after = (
            SELECT n_dead_tup
            FROM pg_stat_user_tables
            WHERE schemaname = 'dms' AND relname = 'reference'
        )
    WHERE test_id = :test_id;
END$$;

-- Check dead tuple accumulation
SELECT
    'Dead tuples after 100 updates to same document: ' ||
    dead_tuples_after ||
    ' (avg ' || round(dead_tuples_after / 100.0, 1) || ' per update)'
FROM dms.perf_test_results
WHERE test_id = :test_id;

\echo ''

-- Test 2: Cross-partition reference lookups
\echo 'Test 2: Cross-partition reference lookups'
\echo '-----------------------------------------'

INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'cross_partition_lookups',
    'problematic_pattern',
    NOW(),
    jsonb_build_object('pattern', 'cross_partition', 'description', 'Updates spanning all partitions')
) RETURNING test_id AS test_id2 \gset

-- Create references that span all partitions
DO $$
DECLARE
    partition_key smallint;
    doc_ids bigint[];
    doc_keys smallint[];
    ref_ids uuid[];
    ref_keys smallint[];
    start_time timestamp;
    partitions_tested integer := 0;
    actual_rows integer := 0;
BEGIN
    start_time := clock_timestamp();

    -- Get documents from each partition
    FOR partition_key IN 0..15 LOOP
        WITH parent_docs AS (
            SELECT Id, DocumentPartitionKey
            FROM dms.Document
            WHERE DocumentPartitionKey = partition_key
            ORDER BY Id
            LIMIT 10
        ),
        reference_pairs AS (
            -- Mimic C# bulk array construction: repeat parent values for each reference row
            SELECT
                p.Id AS ParentDocumentId,
                p.DocumentPartitionKey AS ParentDocumentPartitionKey,
                a.ReferentialId,
                a.ReferentialPartitionKey
            FROM parent_docs p
            CROSS JOIN LATERAL (
                SELECT ReferentialId, ReferentialPartitionKey
                FROM dms.Alias
                WHERE ReferentialPartitionKey != p.DocumentPartitionKey
                ORDER BY ReferentialPartitionKey, ReferentialId
                OFFSET (partition_key * 50)
                LIMIT 50
            ) a
        )
        SELECT
            array_agg(ParentDocumentId),
            array_agg(ParentDocumentPartitionKey),
            array_agg(ReferentialId),
            array_agg(ReferentialPartitionKey)
        INTO doc_ids, doc_keys, ref_ids, ref_keys
        FROM reference_pairs;

        IF
            doc_ids IS NULL
            OR doc_keys IS NULL
            OR ref_ids IS NULL
            OR ref_keys IS NULL
            OR array_length(doc_ids, 1) != array_length(ref_ids, 1)
            OR array_length(doc_keys, 1) != array_length(ref_keys, 1)
        THEN
            CONTINUE;
        END IF;

        PERFORM dms.InsertReferences(
            doc_ids::bigint[],
            doc_keys::smallint[],
            ref_ids::uuid[],
            ref_keys::smallint[]
        );
        partitions_tested := partitions_tested + 1;
        actual_rows := actual_rows + COALESCE(array_length(doc_ids, 1), 0);
    END LOOP;

    UPDATE dms.perf_test_results
    SET
        end_time = NOW(),
        avg_latency_ms = EXTRACT(EPOCH FROM (clock_timestamp() - start_time)) * 1000,
        rows_affected = actual_rows,
        additional_metrics = jsonb_build_object(
            'partitions_tested', partitions_tested,
            'expected_partitions', 16
        )
    WHERE test_id = :test_id2;

    RAISE NOTICE 'Successfully exercised % of 16 partitions', partitions_tested;
END$$;

\echo ''

-- Test 3: Delete cascade impact
\echo 'Test 3: Delete cascade with many references'
\echo '-------------------------------------------'

SELECT
    (:'heavy_parent_document_ids'::bigint[])[1]     AS heavily_ref_doc_id,
    (:'heavy_parent_partition_keys'::smallint[])[1] AS heavily_ref_partition_key,
    (:'heavy_reference_ids'::uuid[])[1]             AS heavily_ref_uuid,
    (:'heavy_reference_partition_keys'::smallint[])[1] AS heavily_ref_key
\gset heavydoc_

-- Make many documents reference this one
DO $$
DECLARE
    referring_docs RECORD;
    count integer := 0;
BEGIN
    FOR referring_docs IN
        SELECT Id, DocumentPartitionKey
        FROM dms.Document
        WHERE Id != :heavydoc_heavily_ref_doc_id
        ORDER BY Id
        LIMIT 500
    LOOP
        INSERT INTO dms.Reference (
            ParentDocumentId,
            ParentDocumentPartitionKey,
            ReferencedDocumentId,
            ReferencedDocumentPartitionKey,
            ReferentialId,
            ReferentialPartitionKey
        ) VALUES (
            referring_docs.Id,
            referring_docs.DocumentPartitionKey,
            :heavydoc_heavily_ref_doc_id,
            :heavydoc_heavily_ref_partition_key,
            :heavydoc_heavily_ref_uuid,
            :heavydoc_heavily_ref_key
        ) ON CONFLICT DO NOTHING;

        count := count + 1;
    END LOOP;

    RAISE NOTICE 'Created % references to document %', count, :heavily_ref_doc_id;
END$$;

-- Now test deletion performance
EXPLAIN (ANALYZE, BUFFERS)
DELETE FROM dms.Document
WHERE Id = :heavily_ref_doc_id
  AND DocumentPartitionKey = :heavily_ref_partition_key;

\echo ''

-- Test 4: Large reference arrays
\echo 'Test 4: Documents with hundreds of references'
\echo '--------------------------------------------'

INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'large_reference_arrays',
    'problematic_pattern',
    NOW(),
    jsonb_build_object('pattern', 'large_arrays', 'refs_per_doc', 500)
) RETURNING test_id AS test_id3 \gset

SELECT
    (:'batch_parent_document_ids'::bigint[])[1]     AS large_doc_id,
    (:'batch_parent_partition_keys'::smallint[])[1] AS large_doc_partition_key
\gset large_

WITH large_ref_source AS (
    SELECT
        (:'batch_reference_ids'::uuid[])[idx] AS ref_id,
        (:'batch_reference_partition_keys'::smallint[])[idx] AS ref_key,
        idx
    FROM generate_subscripts(:'batch_reference_ids'::uuid[], 1) AS idx
    WHERE (:'batch_reference_ids'::uuid[])[idx] IS NOT NULL
    ORDER BY idx
    LIMIT 500
)
SELECT
    array_agg(ref_id ORDER BY idx) AS large_ref_ids,
    array_agg(ref_key ORDER BY idx) AS large_ref_keys
FROM large_ref_source
\gset large_

DO $$
DECLARE
    doc_id bigint;
    doc_key smallint;
    large_refs uuid[];
    large_keys smallint[];
    parent_ids bigint[];
    parent_keys smallint[];
    start_time timestamp;
BEGIN
    doc_id := :large_large_doc_id;
    doc_key := :large_large_doc_partition_key;
    large_refs := :'large_large_ref_ids'::uuid[];
    large_keys := :'large_large_ref_keys'::smallint[];

    IF array_length(large_refs, 1) IS DISTINCT FROM 500 THEN
        RAISE WARNING 'Expected 500 refs but got %', COALESCE(array_length(large_refs, 1), 0);
    END IF;

    start_time := clock_timestamp();

    -- Test the operation with large array
    IF
        large_refs IS NOT NULL
        AND large_keys IS NOT NULL
        AND array_length(large_refs, 1) = array_length(large_keys, 1)
    THEN
        parent_ids := array_fill(doc_id, ARRAY[array_length(large_refs, 1)]);
        parent_keys := array_fill(doc_key, ARRAY[array_length(large_keys, 1)]);

        IF parent_ids IS NULL OR parent_keys IS NULL THEN
            RETURN;
        END IF;

        PERFORM dms.InsertReferences(
            parent_ids,
            parent_keys,
            large_refs,
            large_keys
        );
    END IF;

    UPDATE dms.perf_test_results
    SET
        end_time = NOW(),
        avg_latency_ms = EXTRACT(EPOCH FROM (clock_timestamp() - start_time)) * 1000,
        rows_affected = COALESCE(array_length(large_refs, 1), 0)
    WHERE test_id = :test_id3;
END$$;

-- Summary of problematic patterns
\echo ''
\echo 'PROBLEMATIC PATTERN SUMMARY'
\echo '==========================='

SELECT
    test_name,
    round(avg_latency_ms::numeric, 2) as latency_ms,
    rows_affected,
    dead_tuples_after,
    test_parameters->>'pattern' as pattern_type,
    CASE
        WHEN avg_latency_ms > 1000 THEN 'CRITICAL'
        WHEN avg_latency_ms > 500 THEN 'HIGH'
        WHEN avg_latency_ms > 100 THEN 'MEDIUM'
        ELSE 'LOW'
    END as severity
FROM dms.perf_test_results
WHERE test_type = 'problematic_pattern'
  AND created_at > NOW() - INTERVAL '1 hour'
ORDER BY avg_latency_ms DESC;
