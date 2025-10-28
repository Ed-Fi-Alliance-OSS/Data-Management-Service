#!/bin/bash

source $(dirname "$0")/config.sh

ALLOW_DROP=false

usage() {
    echo "Usage: $(basename "$0") [--force]"
    echo ""
    echo "  --force     Required to allow dropping and recreating the database."
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --force)
            ALLOW_DROP=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

if [[ "$ALLOW_DROP" != "true" ]]; then
    log_error "Refusing to drop database '$DB_NAME'. Re-run with --force once you confirm it is safe to recreate."
    exit 1
fi

if [[ ! "$DB_NAME" =~ ^dms_perf_ ]]; then
    log_error "Database name '$DB_NAME' does not match allowed destructive pattern '^dms_perf_'. Update config or rename for disposable use."
    exit 1
fi

log_info "Setting up test database: $DB_NAME"

# Create database if it doesn't exist
log_info "Creating database..."
PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d postgres <<EOF
DROP DATABASE IF EXISTS $DB_NAME;
CREATE DATABASE $DB_NAME;
EOF

# Create schema
log_info "Creating DMS schema..."
psql_exec <<EOF
CREATE SCHEMA IF NOT EXISTS dms;

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pgstattuple;

-- Create Document table with partitioning
CREATE TABLE IF NOT EXISTS dms.Document (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  DocumentPartitionKey SMALLINT NOT NULL,
  DocumentUuid UUID NOT NULL,
  ResourceName VARCHAR(256) NOT NULL,
  ResourceVersion VARCHAR(64) NOT NULL,
  IsDescriptor BOOLEAN NOT NULL,
  ProjectName VARCHAR(256) NOT NULL,
  EdfiDoc JSONB NOT NULL,
  SecurityElements JSONB NOT NULL,
  StudentSchoolAuthorizationEdOrgIds JSONB NULL,
  StudentEdOrgResponsibilityAuthorizationIds JSONB NULL,
  ContactStudentSchoolAuthorizationEdOrgIds JSONB NULL,
  StaffEducationOrganizationAuthorizationEdOrgIds JSONB NULL,
  CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  LastModifiedAt TIMESTAMP NOT NULL DEFAULT NOW(),
  LastModifiedTraceId VARCHAR(128) NOT NULL,
  PRIMARY KEY (DocumentPartitionKey, Id)
) PARTITION BY HASH(DocumentPartitionKey);

-- Create Document partitions
DO \$\$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..$NUM_PARTITIONS-1 LOOP
        partition_name := 'document_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Document FOR VALUES WITH (MODULUS $NUM_PARTITIONS, REMAINDER %s);',
            partition_name, i
        );
    END LOOP;
END\$\$;

-- Create Document indexes
CREATE UNIQUE INDEX IF NOT EXISTS UX_Document_DocumentUuid ON dms.Document (DocumentPartitionKey, DocumentUuid);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Document_DocumentId ON dms.Document (DocumentPartitionKey, Id);

-- Set REPLICA IDENTITY FULL for Document partitions
DO \$\$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    EXECUTE 'ALTER TABLE IF EXISTS dms.Document REPLICA IDENTITY FULL;';
    FOR i IN 0..$((NUM_PARTITIONS-1)) LOOP
        partition_name := 'document_' || to_char(i, 'FM00');
        EXECUTE format('ALTER TABLE IF EXISTS dms.%I REPLICA IDENTITY FULL;', partition_name);
    END LOOP;
END\$\$;

-- Create Alias table with partitioning
CREATE TABLE IF NOT EXISTS dms.Alias (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ReferentialPartitionKey SMALLINT NOT NULL,
  ReferentialId UUID NOT NULL,
  DocumentId BIGINT NOT NULL,
  DocumentPartitionKey SMALLINT NOT NULL,
  PRIMARY KEY (ReferentialPartitionKey, Id)
) PARTITION BY HASH(ReferentialPartitionKey);

-- Create Alias partitions
DO \$\$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..$NUM_PARTITIONS-1 LOOP
        partition_name := 'alias_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Alias FOR VALUES WITH (MODULUS $NUM_PARTITIONS, REMAINDER %s);',
            partition_name, i
        );
    END LOOP;
END\$\$;

-- Create Alias indexes
CREATE UNIQUE INDEX IF NOT EXISTS UX_Alias_ReferentialId ON dms.Alias (ReferentialPartitionKey, ReferentialId);

-- Add FK constraint for Alias
DO \$\$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'alias' AND constraint_name = 'fk_alias_document'
    ) THEN
        ALTER TABLE dms.Alias
        ADD CONSTRAINT FK_Alias_Document FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;

        CREATE INDEX IF NOT EXISTS IX_FK_Alias_Document ON dms.Alias (DocumentPartitionKey, DocumentId);
    END IF;
END\$\$;

-- Create Reference table with partitioning
CREATE TABLE IF NOT EXISTS dms.Reference (
  Id BIGINT GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1),
  ParentDocumentId BIGINT NOT NULL,
  ParentDocumentPartitionKey SMALLINT NOT NULL,
  ReferentialPartitionKey SMALLINT NOT NULL,
  AliasId BIGINT NOT NULL,
  PRIMARY KEY (ParentDocumentPartitionKey, Id)
) PARTITION BY HASH(ParentDocumentPartitionKey);

-- Create Reference partitions
DO \$\$
DECLARE
    i INT;
    partition_name TEXT;
BEGIN
    FOR i IN 0..$NUM_PARTITIONS-1 LOOP
        partition_name := 'reference_' || to_char(i, 'FM00');
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS dms.%I PARTITION OF dms.Reference FOR VALUES WITH (MODULUS $NUM_PARTITIONS, REMAINDER %s);',
            partition_name, i
        );
    END LOOP;
END\$\$;

-- Create Reference indexes
CREATE INDEX IF NOT EXISTS UX_Reference_ParentDocumentId ON dms.Reference (ParentDocumentPartitionKey, ParentDocumentId);
CREATE INDEX IF NOT EXISTS IX_Reference_AliasId ON dms.Reference (ReferentialPartitionKey, AliasId);

-- Add FK constraints for Reference
DO \$\$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'reference' AND constraint_name = 'fk_reference_parentdocument'
    ) THEN
        ALTER TABLE dms.Reference
        ADD CONSTRAINT FK_Reference_ParentDocument FOREIGN KEY (ParentDocumentPartitionKey, ParentDocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id) ON DELETE CASCADE;
    END IF;
END\$\$;

-- Reference validation enforcement via Alias FK
DO \$\$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = 'dms' AND table_name = 'reference' AND constraint_name = 'fk_reference_referencedalias'
    ) THEN
        ALTER TABLE dms.Reference
        ADD CONSTRAINT FK_Reference_ReferencedAlias FOREIGN KEY (ReferentialPartitionKey, AliasId)
        REFERENCES dms.Alias (ReferentialPartitionKey, Id) ON DELETE RESTRICT ON UPDATE CASCADE;

        CREATE INDEX IF NOT EXISTS IX_FK_Reference_ReferencedAlias ON dms.Reference (ReferentialPartitionKey, AliasId);
    END IF;
END\$\$;

-- Create the current InsertReferences stored procedure (as implemented in DMS)
CREATE OR REPLACE FUNCTION dms.InsertReferences(
    parentDocumentIds BIGINT[],
    parentDocumentPartitionKeys SMALLINT[],
    referentialIds UUID[],
    referentialPartitionKeys SMALLINT[]
) RETURNS TABLE (ReferentialId UUID)
LANGUAGE plpgsql AS
\$\$
BEGIN
    -- First clear out all the existing references, as they may have changed
    DELETE from dms.Reference r
    USING unnest(parentDocumentIds, parentDocumentPartitionKeys) as d (Id, DocumentPartitionKey)
    WHERE d.Id = r.ParentDocumentId AND d.DocumentPartitionKey = r.ParentDocumentPartitionKey;

    WITH payload AS (
        SELECT
            ids.documentId,
            ids.documentPartitionKey,
            ids.referentialId,
            ids.referentialPartitionKey,
            a.Id AS aliasId
        FROM unnest(parentDocumentIds, parentDocumentPartitionKeys, referentialIds, referentialPartitionKeys) AS
            ids(documentId, documentPartitionKey, referentialId, referentialPartitionKey)
        LEFT JOIN dms.Alias a ON
            a.ReferentialId = ids.referentialId
            AND a.ReferentialPartitionKey = ids.referentialPartitionKey
    ),
    inserted AS (
        INSERT INTO dms.Reference (
            ParentDocumentId,
            ParentDocumentPartitionKey,
            AliasId,
            ReferentialPartitionKey
        )
        SELECT
            documentId,
            documentPartitionKey,
            aliasId,
            referentialPartitionKey
        FROM payload
        WHERE aliasId IS NOT NULL
        RETURNING 1
    )
    SELECT payload.referentialId
    FROM payload
    WHERE payload.aliasId IS NULL;
    RETURN;
END;
\$\$;

-- Create performance tracking table
CREATE TABLE IF NOT EXISTS dms.perf_test_results (
    test_id SERIAL PRIMARY KEY,
    test_name TEXT NOT NULL,
    test_type TEXT NOT NULL,
    start_time TIMESTAMP NOT NULL,
    end_time TIMESTAMP NOT NULL,
    duration_ms NUMERIC GENERATED ALWAYS AS (EXTRACT(EPOCH FROM (end_time - start_time)) * 1000) STORED,
    rows_affected INTEGER,
    avg_latency_ms NUMERIC,
    max_latency_ms NUMERIC,
    min_latency_ms NUMERIC,
    p95_latency_ms NUMERIC,
    p99_latency_ms NUMERIC,
    lock_wait_time_ms NUMERIC,
    dead_tuples_before INTEGER,
    dead_tuples_after INTEGER,
    table_size_mb NUMERIC,
    index_size_mb NUMERIC,
    explain_plan JSONB,
    additional_metrics JSONB,
    test_parameters JSONB,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IX_perf_test_results_test_name ON dms.perf_test_results(test_name, start_time);
CREATE INDEX IX_perf_test_results_test_type ON dms.perf_test_results(test_type, start_time);

-- Deterministic fixture targets used by SQL test scenarios
CREATE TABLE IF NOT EXISTS dms.perf_reference_targets (
    target_name TEXT PRIMARY KEY,
    description TEXT NOT NULL,
    parent_document_ids BIGINT[] NOT NULL,
    parent_partition_keys SMALLINT[] NOT NULL,
    reference_ids UUID[] NOT NULL,
    reference_partition_keys SMALLINT[] NOT NULL,
    document_count INT NOT NULL,
    reference_count INT NOT NULL,
    requested_per_document INT NOT NULL,
    min_per_document INT NOT NULL,
    max_per_document INT NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE OR REPLACE FUNCTION dms.insert_reference_target_for_docs(
    p_target_name TEXT,
    p_description TEXT,
    p_doc_ids BIGINT[],
    p_refs_per_doc INTEGER
) RETURNS VOID
LANGUAGE plpgsql AS
\$\$
DECLARE
    parent_ids BIGINT[];
    parent_keys SMALLINT[];
    ref_ids UUID[];
    ref_keys SMALLINT[];
    doc_count INT;
    ref_count INT;
    min_refs INT;
    max_refs INT;
    requested_doc_count INT;
BEGIN
    IF p_doc_ids IS NULL OR array_length(p_doc_ids, 1) = 0 THEN
        RAISE NOTICE 'No document ids provided for target %, skipping fixture generation.', p_target_name;
        RETURN;
    END IF;

    requested_doc_count := array_length(p_doc_ids, 1);

    WITH doc_list AS (
        SELECT p_doc_ids[idx] AS doc_id, idx AS ord
        FROM generate_subscripts(p_doc_ids, 1) AS idx
    ),
    target_docs AS (
        SELECT dl.ord, d.Id, d.DocumentPartitionKey
        FROM doc_list dl
        JOIN dms.Document d ON d.Id = dl.doc_id
        ORDER BY dl.ord
    ),
    refs AS (
        SELECT
            td.ord,
            r.ParentDocumentId,
            r.ParentDocumentPartitionKey,
            r.ReferentialId,
            r.ReferentialPartitionKey,
            ROW_NUMBER() OVER (
                PARTITION BY r.ParentDocumentId
                ORDER BY r.ReferentialPartitionKey, r.ReferentialId
            ) AS rn
        FROM target_docs td
        JOIN dms.Reference r
            ON r.ParentDocumentId = td.Id
           AND r.ParentDocumentPartitionKey = td.DocumentPartitionKey
    ),
    limited AS (
        SELECT *
        FROM refs
        WHERE rn <= p_refs_per_doc
        ORDER BY ord, rn
    ),
    doc_counts AS (
        SELECT ParentDocumentId, COUNT(*) AS doc_ref_count
        FROM limited
        GROUP BY ParentDocumentId
    )
    SELECT
        array_agg(limited.ParentDocumentId ORDER BY limited.ord, limited.rn) AS parent_ids,
        array_agg(limited.ParentDocumentPartitionKey ORDER BY limited.ord, limited.rn) AS parent_keys,
        array_agg(limited.ReferentialId ORDER BY limited.ord, limited.rn) AS ref_ids,
        array_agg(limited.ReferentialPartitionKey ORDER BY limited.ord, limited.rn) AS ref_keys,
        COUNT(DISTINCT limited.ParentDocumentId) AS doc_count,
        COUNT(*) AS ref_count,
        COALESCE(MIN(doc_counts.doc_ref_count), 0) AS min_refs,
        COALESCE(MAX(doc_counts.doc_ref_count), 0) AS max_refs
    INTO parent_ids, parent_keys, ref_ids, ref_keys, doc_count, ref_count, min_refs, max_refs
    FROM limited
    LEFT JOIN doc_counts
        ON doc_counts.ParentDocumentId = limited.ParentDocumentId;

    IF doc_count IS NULL OR doc_count = 0 OR ref_count IS NULL OR ref_count = 0 THEN
        RAISE NOTICE 'No reference data available for target %, skipping fixture generation.', p_target_name;
        RETURN;
    END IF;

    IF doc_count <> requested_doc_count THEN
        RAISE NOTICE
            'Requested % documents but only % found for target %.',
            requested_doc_count,
            doc_count,
            p_target_name;
    END IF;

    INSERT INTO dms.perf_reference_targets (
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
        max_per_document,
        created_at
    )
    VALUES (
        p_target_name,
        p_description,
        parent_ids,
        parent_keys,
        ref_ids,
        ref_keys,
        doc_count,
        ref_count,
        p_refs_per_doc,
        min_refs,
        max_refs,
        NOW()
    )
    ON CONFLICT (target_name) DO UPDATE
    SET
        description = EXCLUDED.description,
        parent_document_ids = EXCLUDED.parent_document_ids,
        parent_partition_keys = EXCLUDED.parent_partition_keys,
        reference_ids = EXCLUDED.reference_ids,
        reference_partition_keys = EXCLUDED.reference_partition_keys,
        document_count = EXCLUDED.document_count,
        reference_count = EXCLUDED.reference_count,
        requested_per_document = EXCLUDED.requested_per_document,
        min_per_document = EXCLUDED.min_per_document,
        max_per_document = EXCLUDED.max_per_document,
        created_at = EXCLUDED.created_at;
END;
\$\$;

CREATE OR REPLACE FUNCTION dms.build_perf_reference_targets() RETURNS VOID
LANGUAGE plpgsql AS
\$\$
DECLARE
    doc_sample BIGINT[];
BEGIN
    TRUNCATE dms.perf_reference_targets;

    PERFORM dms.insert_reference_target_for_docs(
        'single_doc_standard',
        'Document 2 average reference load (first 50 references).',
        ARRAY[2]::BIGINT[],
        50
    );

    PERFORM dms.insert_reference_target_for_docs(
        'single_doc_heavy',
        'Document 1 heavy reference load (first 200 references).',
        ARRAY[1]::BIGINT[],
        200
    );

    SELECT array_agg(docs.id ORDER BY docs.id)
    INTO doc_sample
    FROM (
        SELECT id
        FROM dms.Document
        ORDER BY id
        LIMIT 100
    ) AS docs;

    PERFORM dms.insert_reference_target_for_docs(
        'batch_100_mixed',
        'Documents 1-100 with up to 20 references each (ordered).',
        doc_sample,
        20
    );
END;
\$\$;

-- Grant permissions
GRANT ALL ON SCHEMA dms TO $DB_USER;
GRANT ALL ON ALL TABLES IN SCHEMA dms TO $DB_USER;
GRANT ALL ON ALL SEQUENCES IN SCHEMA dms TO $DB_USER;
GRANT ALL ON ALL FUNCTIONS IN SCHEMA dms TO $DB_USER;

-- Reset statistics
SELECT pg_stat_reset();
DO \$\$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') THEN
        BEGIN
            PERFORM pg_stat_statements_reset();
        EXCEPTION
            WHEN OTHERS THEN
                RAISE NOTICE 'Skipping pg_stat_statements reset: %', SQLERRM;
        END;
    ELSE
        RAISE NOTICE 'pg_stat_statements extension not installed; skipping reset.';
    END IF;
END;
\$\$;

EOF

log_info "Database setup complete!"
log_info "Tables created: Document, Alias, Reference (each with $NUM_PARTITIONS partitions)"
log_info "Performance tracking table created: perf_test_results"
