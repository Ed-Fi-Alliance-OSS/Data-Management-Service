#!/bin/bash

# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -euo pipefail

SCRIPT_DIR="$(dirname "$0")"
source "${SCRIPT_DIR}/config.sh"

GENERATOR_MODE="${GENERATOR_MODE:-csv}"
RESUME_SQL_LOAD="${RESUME_SQL_LOAD:-false}"
OUTPUT_DIR="${OUTPUT_DIR:-$(cd "${SCRIPT_DIR}/../data" && pwd)/out}"
SQL_DOC_CHUNK_SIZE="${SQL_DOC_CHUNK_SIZE:-1000}"
SQL_CAN_LOAD_REFERENCES=true

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode)
            GENERATOR_MODE="$2"
            shift
            ;;
        --mode=*)
            GENERATOR_MODE="${1#*=}"
            ;;
        --output)
            OUTPUT_DIR="$2"
            shift
            ;;
        --output=*)
            OUTPUT_DIR="${1#*=}"
            ;;
        --resume)
            RESUME_SQL_LOAD=true
            ;;
        --chunk-size)
            SQL_DOC_CHUNK_SIZE="$2"
            shift
            ;;
        --chunk-size=*)
            SQL_DOC_CHUNK_SIZE="${1#*=}"
            ;;
        *)
            log_error "Unknown option: $1"
            exit 1
            ;;
    esac
    shift
done

normalize_mode() {
    local mode_lower
    mode_lower="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
    case "$mode_lower" in
        csv|sql)
            printf '%s' "$mode_lower"
            ;;
        *)
            log_error "Unsupported generator mode: $1 (expected csv or sql)"
            exit 1
            ;;
    esac
}

GENERATOR_MODE="$(normalize_mode "$GENERATOR_MODE")"

if ! [[ "$SQL_DOC_CHUNK_SIZE" =~ ^[0-9]+$ ]] || [ "$SQL_DOC_CHUNK_SIZE" -le 0 ]; then
    log_warn "Invalid SQL_DOC_CHUNK_SIZE (${SQL_DOC_CHUNK_SIZE}); defaulting to 1000 documents per chunk"
    SQL_DOC_CHUNK_SIZE=1000
fi

log_info "Starting test data generation using mode: ${GENERATOR_MODE}"
log_info "Target scale: ${NUM_DOCUMENTS} documents, ~${NUM_REFERENCES} references, ${NUM_PARTITIONS} partitions"
START_TIME=$(date +%s)

generate_with_csv_pipeline() {
    if ! command -v python3 >/dev/null 2>&1; then
        log_error "python3 is required for CSV generation but was not found in PATH"
        exit 1
    fi

    mkdir -p "$OUTPUT_DIR"
    log_info "Generating deterministic CSV files into: $OUTPUT_DIR"

    python3 "${SCRIPT_DIR}/../data/generate_deterministic_data.py" \
        --output "$OUTPUT_DIR" \
        --documents "$NUM_DOCUMENTS" \
        --references "$NUM_REFERENCES" \
        --avg-refs-per-doc "$AVG_REFS_PER_DOC" \
        --partitions "$NUM_PARTITIONS"

    log_info "Loading CSVs into Postgres..."
    "${SCRIPT_DIR}/load-test-data-from-csv.sh" "$OUTPUT_DIR"
}

initialize_sql_reference_loader() {
    log_info "Configuring database objects for SQL-based data generation"

    if [[ "$RESUME_SQL_LOAD" != "true" ]]; then
        log_info "Resetting target tables and helper structures (fresh load)"
        psql_exec <<'EOF'
ALTER TABLE dms.Reference SET (autovacuum_enabled = false);
ALTER TABLE dms.Alias SET (autovacuum_enabled = false);
ALTER TABLE dms.Document SET (autovacuum_enabled = false);

TRUNCATE dms.Reference;
TRUNCATE dms.Alias;
TRUNCATE dms.Document RESTART IDENTITY CASCADE;
DROP TABLE IF EXISTS dms.alias_lookup;
DROP TABLE IF EXISTS dms.reference_load_targets;
EOF
    else
        log_info "Resuming SQL reference load – existing data will be re-used for earlier chunks"
    fi

    psql_exec <<EOF
CREATE OR REPLACE FUNCTION calculate_partition_key(uuid_val UUID) RETURNS SMALLINT AS \$\$
BEGIN
    RETURN (get_byte(uuid_val::bytea, 15) % $NUM_PARTITIONS)::smallint;
END;
\$\$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION deterministic_uuid(prefix TEXT, value BIGINT) RETURNS UUID AS \$\$
    SELECT (
        substr(hash, 1, 8) || '-' ||
        substr(hash, 9, 4) || '-' ||
        substr(hash, 13, 4) || '-' ||
        substr(hash, 17, 4) || '-' ||
        substr(hash, 21, 12)
    )::uuid
    FROM (
        SELECT md5(prefix || ':' || value::text) AS hash
    ) h;
\$\$ LANGUAGE sql IMMUTABLE;
EOF

    if [[ "$RESUME_SQL_LOAD" != "true" ]]; then
        log_info "Inserting deterministic documents and aliases"
        psql_exec <<'EOF'
INSERT INTO dms.Document (
    DocumentPartitionKey,
    DocumentUuid,
    ResourceName,
    ResourceVersion,
    IsDescriptor,
    ProjectName,
    EdfiDoc,
    SecurityElements,
    LastModifiedTraceId
)
SELECT
    calculate_partition_key(deterministic_uuid('doc', i)),
    deterministic_uuid('doc', i),
    CASE (i % 10)
        WHEN 0 THEN 'students'
        WHEN 1 THEN 'studentSchoolAssociations'
        WHEN 2 THEN 'schools'
        WHEN 3 THEN 'courses'
        WHEN 4 THEN 'sections'
        WHEN 5 THEN 'studentSectionAssociations'
        WHEN 6 THEN 'staff'
        WHEN 7 THEN 'staffSchoolAssociations'
        WHEN 8 THEN 'grades'
        ELSE 'assessments'
    END,
    '5.0.0',
    false,
    'ed-fi',
    jsonb_build_object(
        'id', deterministic_uuid('doc', i),
        'studentUniqueId', 'STU' || i,
        'firstName', 'FirstName' || i,
        'lastSurname', 'LastName' || i,
        'birthDate', '2010-01-01',
        '_etag', md5('etag:' || i::text)
    ),
    jsonb_build_object(
        'Namespace', ARRAY['uri://ed-fi.org'],
        'EducationOrganization', jsonb_build_object('Id', (i % 100)::text)
    ),
    'perf-test-' || i
FROM generate_series(1, $NUM_DOCUMENTS) AS i;

INSERT INTO dms.Alias (
    ReferentialPartitionKey,
    ReferentialId,
    DocumentId,
    DocumentPartitionKey
)
SELECT
    calculate_partition_key(deterministic_uuid('alias', d.Id)),
    deterministic_uuid('alias', d.Id),
    d.Id,
    d.DocumentPartitionKey
FROM dms.Document d;
EOF
    fi

    log_info "Building helper tables for reference generation"
    psql_exec <<'EOF'
CREATE TABLE IF NOT EXISTS dms.alias_lookup (
    alias_row_number INT PRIMARY KEY,
    alias_id BIGINT NOT NULL,
    document_id BIGINT NOT NULL,
    document_partition_key SMALLINT NOT NULL,
    referential_id UUID NOT NULL,
    referential_partition_key SMALLINT NOT NULL
);

INSERT INTO dms.alias_lookup (
    alias_row_number,
    alias_id,
    document_id,
    document_partition_key,
    referential_id,
    referential_partition_key
)
SELECT *
FROM (
    SELECT
        row_number() OVER (ORDER BY Id) - 1 AS alias_row_number,
        Id,
        DocumentId,
        DocumentPartitionKey,
        ReferentialId,
        ReferentialPartitionKey
    FROM dms.Alias
) ordered
ON CONFLICT (alias_row_number) DO UPDATE
SET
    alias_id = EXCLUDED.alias_id,
    document_id = EXCLUDED.document_id,
    document_partition_key = EXCLUDED.document_partition_key,
    referential_id = EXCLUDED.referential_id,
    referential_partition_key = EXCLUDED.referential_partition_key;

CREATE TABLE IF NOT EXISTS dms.reference_load_targets (
    doc_row_number INT PRIMARY KEY,
    parent_document_id BIGINT NOT NULL,
    parent_document_partition_key SMALLINT NOT NULL,
    self_alias_row_number INT NOT NULL,
    seed BIGINT NOT NULL,
    target_refs INT NOT NULL,
    total_aliases BIGINT NOT NULL
);
EOF

    if [[ "$RESUME_SQL_LOAD" != "true" ]]; then
        log_info "Calculating reference targets per document"
        psql_exec <<'EOF'
WITH numbered_docs AS (
    SELECT
        d.Id,
        d.DocumentPartitionKey,
        row_number() OVER (ORDER BY d.Id) - 1 AS DocRowNumber,
        COALESCE(al.alias_row_number, -1) AS SelfAliasRowNumber
    FROM dms.Document d
    LEFT JOIN dms.alias_lookup al
        ON al.document_id = d.Id
        AND al.document_partition_key = d.DocumentPartitionKey
),
weights AS (
    SELECT
        nd.*,
        CASE
            WHEN (nd.DocRowNumber % 20) = 0 THEN $AVG_REFS_PER_DOC * 10
            WHEN (nd.DocRowNumber % 5) = 0 THEN $AVG_REFS_PER_DOC * 3
            ELSE $AVG_REFS_PER_DOC
        END AS Weight
    FROM numbered_docs nd
),
total_weight AS (
    SELECT SUM(Weight) AS total_weight FROM weights
),
targets AS (
    SELECT
        w.Id,
        w.DocumentPartitionKey,
        w.DocRowNumber,
        w.SelfAliasRowNumber,
        CEIL($NUM_REFERENCES::numeric * w.Weight / NULLIF(t.total_weight, 0))::int AS TargetRefs,
        (w.DocRowNumber * 8191)::bigint AS Seed
    FROM weights w
    CROSS JOIN total_weight t
)
INSERT INTO dms.reference_load_targets (
    doc_row_number,
    parent_document_id,
    parent_document_partition_key,
    self_alias_row_number,
    seed,
    target_refs,
    total_aliases
)
SELECT
    t.DocRowNumber,
    t.Id,
    t.DocumentPartitionKey,
    t.SelfAliasRowNumber,
    t.Seed,
    GREATEST(t.TargetRefs, 0),
    (SELECT COUNT(*) FROM dms.alias_lookup)
FROM targets t
ON CONFLICT (doc_row_number) DO UPDATE
SET
    parent_document_id = EXCLUDED.parent_document_id,
    parent_document_partition_key = EXCLUDED.parent_document_partition_key,
    self_alias_row_number = EXCLUDED.self_alias_row_number,
    seed = EXCLUDED.seed,
    target_refs = EXCLUDED.target_refs,
    total_aliases = EXCLUDED.total_aliases;
EOF
    fi

    psql_exec <<'EOF'
CREATE TABLE IF NOT EXISTS dms.reference_load_progress (
    process_name TEXT PRIMARY KEY,
    last_doc_row INT NOT NULL,
    total_doc_rows INT NOT NULL,
    docs_per_chunk INT NOT NULL,
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);
EOF

    TOTAL_DOC_ROWS=$(psql_exec -At <<'EOF'
SELECT COALESCE(MAX(doc_row_number) + 1, 0) FROM dms.reference_load_targets WHERE target_refs > 0;
EOF
)

    if [[ "$TOTAL_DOC_ROWS" -eq 0 ]]; then
        log_warn "No reference targets generated; skipping reference load"
        SQL_CAN_LOAD_REFERENCES=false
        return
    fi

    if [[ "$RESUME_SQL_LOAD" != "true" ]]; then
        psql_exec <<EOF
INSERT INTO dms.reference_load_progress (process_name, last_doc_row, total_doc_rows, docs_per_chunk)
VALUES ('sql_reference_load', -1, $TOTAL_DOC_ROWS, $SQL_DOC_CHUNK_SIZE)
ON CONFLICT (process_name) DO UPDATE
SET last_doc_row = EXCLUDED.last_doc_row,
    total_doc_rows = EXCLUDED.total_doc_rows,
    docs_per_chunk = EXCLUDED.docs_per_chunk,
    updated_at = NOW();
EOF
    else
        psql_exec <<EOF
INSERT INTO dms.reference_load_progress (process_name, last_doc_row, total_doc_rows, docs_per_chunk)
VALUES ('sql_reference_load', -1, $TOTAL_DOC_ROWS, $SQL_DOC_CHUNK_SIZE)
ON CONFLICT (process_name) DO NOTHING;

UPDATE dms.reference_load_progress
SET total_doc_rows = $TOTAL_DOC_ROWS,
    docs_per_chunk = $SQL_DOC_CHUNK_SIZE,
    updated_at = NOW()
WHERE process_name = 'sql_reference_load';
EOF
    fi
}

load_references_by_chunk() {
    local progress
    progress="$(psql_exec -At <<'EOF'
SELECT last_doc_row, total_doc_rows, docs_per_chunk
FROM dms.reference_load_progress
WHERE process_name = 'sql_reference_load';
EOF
)"

    if [[ -z "$progress" ]]; then
        log_error "Reference load progress record not found; run without --resume first."
        exit 1
    fi

    IFS='|' read -r LAST_DOC_ROW TOTAL_DOC_ROWS DOCS_PER_CHUNK <<<"$progress"

    local start_row=$((LAST_DOC_ROW + 1))
    local end_row
    local chunks_total=$(( (TOTAL_DOC_ROWS + DOCS_PER_CHUNK - 1) / DOCS_PER_CHUNK ))

    if [[ "$start_row" -ge "$TOTAL_DOC_ROWS" ]]; then
        log_warn "Reference data already fully loaded (last_doc_row=$LAST_DOC_ROW, total_doc_rows=$TOTAL_DOC_ROWS)"
        return
    fi

    while [[ "$start_row" -lt "$TOTAL_DOC_ROWS" ]]; do
        end_row=$(( start_row + DOCS_PER_CHUNK - 1 ))
        if [[ "$end_row" -ge "$TOTAL_DOC_ROWS" ]]; then
            end_row=$(( TOTAL_DOC_ROWS - 1 ))
        fi

        local current_chunk=$(( start_row / DOCS_PER_CHUNK + 1 ))
        log_info "Loading references for doc_row range [$start_row, $end_row] (chunk ${current_chunk}/${chunks_total})"
        psql_exec <<EOF
\\set start_row $start_row
\\set end_row $end_row
WITH target_docs AS (
    SELECT *
    FROM dms.reference_load_targets
    WHERE doc_row_number BETWEEN :start_row AND :end_row
      AND target_refs > 0
),
deleted AS (
    DELETE FROM dms.Reference r
    USING target_docs td
    WHERE r.ParentDocumentId = td.parent_document_id
      AND r.ParentDocumentPartitionKey = td.parent_document_partition_key
    RETURNING 1
),
expanded AS (
    SELECT
        td.doc_row_number,
        td.parent_document_id,
        td.parent_document_partition_key,
        td.self_alias_row_number,
        td.seed,
        td.total_aliases,
        gs.ref_seq,
        ((td.seed + gs.ref_seq) % td.total_aliases + td.total_aliases) % td.total_aliases AS candidate_alias
    FROM target_docs td
    CROSS JOIN LATERAL generate_series(0, td.target_refs - 1) AS gs(ref_seq)
),
resolved AS (
    SELECT
        e.parent_document_id,
        e.parent_document_partition_key,
        CASE
            WHEN e.candidate_alias = e.self_alias_row_number
            THEN (e.candidate_alias + 1) % e.total_aliases
            ELSE e.candidate_alias
        END AS alias_row_number
    FROM expanded e
)
INSERT INTO dms.Reference (
    ParentDocumentId,
    ParentDocumentPartitionKey,
    AliasId,
    ReferentialPartitionKey
)
SELECT
    r.parent_document_id,
    r.parent_document_partition_key,
    al.alias_id,
    al.referential_partition_key
FROM resolved r
JOIN dms.alias_lookup al ON al.alias_row_number = r.alias_row_number;

UPDATE dms.reference_load_progress
SET last_doc_row = :end_row,
    updated_at = NOW()
WHERE process_name = 'sql_reference_load';
EOF

        start_row=$(( end_row + 1 ))
    done

    log_info "Reference load complete across ${chunks_total} chunks"
}

finalize_sql_loader() {
    log_info "Re-enabling autovacuum and refreshing statistics"
    psql_exec <<'EOF'
ALTER TABLE dms.Document SET (autovacuum_enabled = true);
ALTER TABLE dms.Alias SET (autovacuum_enabled = true);
ALTER TABLE dms.Reference SET (autovacuum_enabled = true);

ANALYZE dms.Document;
ANALYZE dms.Alias;
ANALYZE dms.Reference;
EOF

    log_info "Refreshing deterministic reference fixture targets"
    psql_exec <<'EOF'
SELECT dms.build_perf_reference_targets();
EOF

    log_info "Row counts after load:"
    psql_exec <<'EOF'
SELECT 'Document' AS table_name, COUNT(*) AS row_count FROM dms.Document
UNION ALL
SELECT 'Alias', COUNT(*) FROM dms.Alias
UNION ALL
SELECT 'Reference', COUNT(*) FROM dms.Reference;
EOF
}

case "$GENERATOR_MODE" in
    csv)
        generate_with_csv_pipeline
        ;;
    sql)
        initialize_sql_reference_loader
        if [[ "$SQL_CAN_LOAD_REFERENCES" == "true" ]]; then
            load_references_by_chunk
        else
            log_warn "Skipped reference loading because no targets were generated"
        fi
        finalize_sql_loader
        ;;
esac

END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))
log_info "Test data generation finished in ${DURATION} seconds"
