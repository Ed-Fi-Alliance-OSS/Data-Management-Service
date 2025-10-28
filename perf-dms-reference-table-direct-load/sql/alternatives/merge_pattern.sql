SET application_name = 'perf_merge_pattern';
SET track_functions = 'pl';
SET client_min_messages = 'warning';

-- Alternative 2: MERGE Pattern using INSERT ON CONFLICT
-- Uses PostgreSQL's UPSERT capabilities to avoid separate DELETE/INSERT

-- Create a unique constraint for efficient conflict detection
-- Note: This would need to be added to the actual schema
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uk_reference_parent_referential'
    ) THEN
        ALTER TABLE dms.Reference
        ADD CONSTRAINT uk_reference_parent_referential
        UNIQUE (ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey);
    END IF;
END$$;

-- Alternative stored procedure using MERGE pattern
CREATE OR REPLACE FUNCTION dms.InsertReferences_Merge(
    parentDocumentIds BIGINT[],
    parentDocumentPartitionKeys SMALLINT[],
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (ReferentialId UUID)
LANGUAGE plpgsql AS
$$
DECLARE
    invalid_row_count INTEGER;
BEGIN
    IF
        array_length(parentDocumentIds, 1) IS DISTINCT FROM array_length(parentDocumentPartitionKeys, 1)
        OR array_length(parentDocumentIds, 1) IS DISTINCT FROM array_length(referentialIds, 1)
        OR array_length(referentialIds, 1) IS DISTINCT FROM array_length(referentialPartitionKeys, 1)
    THEN
        RAISE EXCEPTION 'All parameter arrays must have identical lengths';
    END IF;

    CREATE TEMP TABLE IF NOT EXISTS dms_temp_reference_staging (
        ParentDocumentId BIGINT,
        ParentDocumentPartitionKey SMALLINT,
        ReferentialId UUID,
        ReferentialPartitionKey SMALLINT,
        PRIMARY KEY (ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey)
    ) ON COMMIT DELETE ROWS;

    TRUNCATE dms_temp_reference_staging;

    INSERT INTO dms_temp_reference_staging (
        ParentDocumentId,
        ParentDocumentPartitionKey,
        ReferentialId,
        ReferentialPartitionKey
    )
    SELECT DISTINCT
        pd.parent_document_id,
        pd.parent_document_partition_key,
        rf.referential_id,
        rf.referential_partition_key
    FROM
        unnest(parentDocumentIds, parentDocumentPartitionKeys)
        WITH ORDINALITY AS pd(parent_document_id, parent_document_partition_key, ord)
    JOIN
        unnest(referentialIds, referentialPartitionKeys)
        WITH ORDINALITY AS rf(referential_id, referential_partition_key, ord)
        USING (ord);

    IF NOT FOUND THEN
        RETURN;
    END IF;

    WITH missing_alias AS (
        SELECT s.ReferentialId
        FROM dms_temp_reference_staging s
        LEFT JOIN dms.Alias a ON
            s.ReferentialId = a.ReferentialId
            AND s.ReferentialPartitionKey = a.ReferentialPartitionKey
        WHERE a.DocumentId IS NULL
        GROUP BY s.ReferentialId
    )
    SELECT count(*) INTO invalid_row_count FROM missing_alias;

    IF invalid_row_count > 0 THEN
        RETURN QUERY
        SELECT ReferentialId FROM (
            SELECT DISTINCT s.ReferentialId
            FROM dms_temp_reference_staging s
            LEFT JOIN dms.Alias a ON
                s.ReferentialId = a.ReferentialId
                AND s.ReferentialPartitionKey = a.ReferentialPartitionKey
            WHERE a.DocumentId IS NULL
        ) invalid_ids;
        TRUNCATE dms_temp_reference_staging;
        RETURN;
    END IF;

    WITH parents AS (
        SELECT DISTINCT ParentDocumentId, ParentDocumentPartitionKey
        FROM dms_temp_reference_staging
    )
    DELETE FROM dms.Reference r
    USING parents p
    WHERE r.ParentDocumentId = p.ParentDocumentId
      AND r.ParentDocumentPartitionKey = p.ParentDocumentPartitionKey
      AND NOT EXISTS (
          SELECT 1
          FROM dms_temp_reference_staging s
          WHERE s.ParentDocumentId = r.ParentDocumentId
            AND s.ParentDocumentPartitionKey = r.ParentDocumentPartitionKey
            AND s.ReferentialId = r.ReferentialId
            AND s.ReferentialPartitionKey = r.ReferentialPartitionKey
      );

    INSERT INTO dms.Reference (
        ParentDocumentId,
        ParentDocumentPartitionKey,
        ReferentialId,
        ReferentialPartitionKey
    )
    SELECT
        s.ParentDocumentId,
        s.ParentDocumentPartitionKey,
        s.ReferentialId,
        s.ReferentialPartitionKey
    FROM dms_temp_reference_staging s
    ON CONFLICT (ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey)
    DO NOTHING;

    TRUNCATE dms_temp_reference_staging;
    RETURN;
END;
$$;

-- Test the MERGE pattern
\timing on

INSERT INTO dms.perf_test_results (test_name, test_type, start_time, test_parameters)
VALUES (
    'merge_pattern',
    'single_document_update',
    NOW(),
    jsonb_build_object('approach', 'merge', 'description', 'Uses INSERT ON CONFLICT for upsert')
) RETURNING test_id AS test_id \gset

-- Deterministic fixture-driven inputs for repeatability
\set fixture_target 'single_doc_standard'
\set mutation_target 'batch_100_mixed'

\unset fixture_target_name
\unset fixture_parent_document_ids
\unset fixture_parent_partition_keys
\unset fixture_reference_ids
\unset fixture_reference_partition_keys
\unset fixture_reference_count

\unset mutation_target_name
\unset mutation_reference_ids
\unset mutation_reference_partition_keys
\unset mutation_reference_count

SELECT
    target_name,
    parent_document_ids,
    parent_partition_keys,
    reference_ids,
    reference_partition_keys,
    reference_count
FROM dms.perf_reference_targets
WHERE target_name = :'fixture_target'
\gset fixture_

\if :{?fixture_target_name}
\else
    \echo 'ERROR: Fixture target ' :fixture_target ' not found in dms.perf_reference_targets.' >&2
    \quit 1
\endif

SELECT
    target_name,
    reference_ids,
    reference_partition_keys,
    reference_count
FROM dms.perf_reference_targets
WHERE target_name = :'mutation_target'
\gset mutation_

\if :{?mutation_target_name}
\else
    \echo 'ERROR: Mutation fixture ' :mutation_target ' not found in dms.perf_reference_targets.' >&2
    \quit 1
\endif

SELECT
    (:'fixture_parent_document_ids'::bigint[])[1]     AS doc_id,
    (:'fixture_parent_partition_keys'::smallint[])[1] AS doc_partition_key
\gset doc_

WITH mutation_source AS (
    SELECT
        (:'mutation_reference_ids'::uuid[])[n] AS ref_id,
        (:'mutation_reference_partition_keys'::smallint[])[n] AS ref_key,
        n
    FROM generate_subscripts(:'mutation_reference_ids'::uuid[], 1) AS n
    WHERE (:'mutation_reference_ids'::uuid[])[n] IS NOT NULL
),
limited AS (
    SELECT ref_id, ref_key
    FROM mutation_source
    ORDER BY n
    LIMIT :fixture_reference_count
)
SELECT
    array_agg(:doc_doc_id::bigint)              AS parent_ids,
    array_agg(:doc_doc_partition_key::smallint) AS parent_partition_keys,
    array_agg(ref_id)                           AS ref_ids,
    array_agg(ref_key)                          AS ref_partition_keys
FROM limited
\gset new_

\if :'new_ref_ids' = ''
    \echo 'ERROR: Unable to build deterministic reference set for mutation target ' :mutation_target >&2
    \quit 1
\endif

SELECT COALESCE(array_length(:'new_ref_ids'::uuid[], 1), 0) AS ref_count \gset

UPDATE dms.perf_test_results
SET dead_tuples_before = (
    SELECT n_dead_tup
    FROM pg_stat_user_tables
    WHERE schemaname = 'dms' AND relname = 'reference'
)
WHERE test_id = :test_id;


DROP TABLE IF EXISTS dms_temp_latency;
CREATE TEMP TABLE dms_temp_latency (
    duration_ms NUMERIC
) ON COMMIT DROP;

-- Run performance test
DO $$
DECLARE
    start_time timestamp;
    end_time timestamp;
    total_duration interval := '0'::interval;
    operation_duration_ms NUMERIC;
    original_parent_ids bigint[];
    original_parent_keys smallint[];
    original_ref_ids uuid[];
    original_ref_keys smallint[];
    mutated_parent_ids bigint[];
    mutated_parent_keys smallint[];
    mutated_ref_ids uuid[];
    mutated_ref_keys smallint[];
BEGIN
    original_parent_ids := :'fixture_parent_document_ids'::bigint[];
    original_parent_keys := :'fixture_parent_partition_keys'::smallint[];
    original_ref_ids := :'fixture_reference_ids'::uuid[];
    original_ref_keys := :'fixture_reference_partition_keys'::smallint[];
    mutated_parent_ids := :'new_parent_ids'::bigint[];
    mutated_parent_keys := :'new_parent_partition_keys'::smallint[];
    mutated_ref_ids := :'new_ref_ids'::uuid[];
    mutated_ref_keys := :'new_ref_partition_keys'::smallint[];

    FOR i IN 1..100 LOOP
        start_time := clock_timestamp();

        PERFORM dms.InsertReferences_Merge(
            CASE WHEN i % 2 = 1 THEN mutated_parent_ids ELSE original_parent_ids END,
            CASE WHEN i % 2 = 1 THEN mutated_parent_keys ELSE original_parent_keys END,
            CASE WHEN i % 2 = 1 THEN mutated_ref_ids ELSE original_ref_ids END,
            CASE WHEN i % 2 = 1 THEN mutated_ref_keys ELSE original_ref_keys END
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
        avg_latency_ms = EXTRACT(EPOCH FROM total_duration / 100) * 1000,
        rows_affected = :ref_count
    WHERE test_id = :test_id;

END$$;

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
    dead_tuples_after = (
        SELECT n_dead_tup
        FROM pg_stat_user_tables
        WHERE schemaname = 'dms' AND relname = 'reference'
    ),
    table_size_mb = (SELECT pg_relation_size('dms.reference') / 1024.0 / 1024.0),
    index_size_mb = (SELECT pg_indexes_size('dms.reference') / 1024.0 / 1024.0),
    additional_metrics = coalesce(additional_metrics, '{}'::jsonb)
        || jsonb_build_object(
            'latency_call_count', (SELECT call_count FROM latency_stats),
            'latency_histogram', COALESCE((SELECT histogram FROM histogram), '{}'::jsonb),
            'refs_per_operation', :ref_count
        )
WHERE test_id = :test_id;

-- Validate functional correctness against expected reference set
WITH expected AS (
    SELECT
        ref_id,
        ref_key
    FROM
        unnest(:'ref_ids'::uuid[]) WITH ORDINALITY AS r(ref_id, ord)
    JOIN
        unnest(:'ref_partition_keys'::smallint[]) WITH ORDINALITY AS k(ref_key, ord)
        USING (ord)
),
actual AS (
    SELECT
        r.ReferentialId AS ref_id,
        r.ReferentialPartitionKey AS ref_key
    FROM dms.Reference r
    WHERE r.ParentDocumentId = :doc_id
      AND r.ParentDocumentPartitionKey = :doc_partition_key
)
SELECT
    test_name,
    round(min_latency_ms::numeric, 2) AS min_latency_ms,
    round(avg_latency_ms::numeric, 2) AS avg_latency_ms,
    round(max_latency_ms::numeric, 2) AS max_latency_ms,
    round(p95_latency_ms::numeric, 2) AS p95_latency_ms,
    round(p99_latency_ms::numeric, 2) AS p99_latency_ms,
    rows_affected,
    dead_tuples_after AS dead_tuples,
    round(table_size_mb::numeric, 2) AS table_size_mb,
    round(index_size_mb::numeric, 2) AS index_size_mb,
    (SELECT count(*) FROM (SELECT * FROM expected EXCEPT SELECT * FROM actual) missing) AS missing_refs,
    (SELECT count(*) FROM (SELECT * FROM actual EXCEPT SELECT * FROM expected) extra_refs) AS extra_refs
FROM dms.perf_test_results
WHERE test_id = :test_id;
