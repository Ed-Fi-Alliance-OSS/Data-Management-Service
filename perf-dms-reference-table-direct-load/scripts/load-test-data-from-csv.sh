#!/bin/bash

# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -euo pipefail

source "$(dirname "$0")/config.sh"

OUTPUT_DIR="${1:-${OUTPUT_DIR:-$(pwd)/../data/out}}"
ABS_OUTPUT_DIR="$(cd "$OUTPUT_DIR" 2>/dev/null && pwd)"

if [ -z "$ABS_OUTPUT_DIR" ]; then
    log_error "Unable to resolve output directory: $OUTPUT_DIR"
    exit 1
fi

DOCUMENT_CSV="$ABS_OUTPUT_DIR/document.csv"
ALIAS_CSV="$ABS_OUTPUT_DIR/alias.csv"
REFERENCE_CSV="$ABS_OUTPUT_DIR/reference.csv"

log_info "Loading CSV data from: $ABS_OUTPUT_DIR"

for file in "$DOCUMENT_CSV" "$ALIAS_CSV" "$REFERENCE_CSV"; do
    if [ ! -f "$file" ]; then
        log_error "Required file not found: $file"
        exit 1
    fi
done

log_info "Disabling autovacuum and truncating target tables..."
psql_exec <<'EOF'
DO $$
DECLARE
    target record;
BEGIN
    FOR target IN
        SELECT format('%I.%I', n.nspname, c.relname) AS fq_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'dms'
          AND (
              c.relname IN ('Document', 'Alias', 'Reference')
              OR c.relname ~ '^(document|alias|reference)_[0-9]{2}$'
          )
    LOOP
        EXECUTE format('ALTER TABLE %s SET (autovacuum_enabled = false);', target.fq_name);
    END LOOP;
END;
$$;

TRUNCATE TABLE dms.Document RESTART IDENTITY CASCADE;
EOF

log_info "Loading documents..."
PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME \
    -c "COPY dms.Document (DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, IsDescriptor, ProjectName, EdfiDoc, SecurityElements, LastModifiedTraceId) FROM STDIN WITH (FORMAT csv, HEADER true);" \
    < "$DOCUMENT_CSV"

log_info "Loading aliases..."
PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME \
    -c "COPY dms.Alias (ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey) FROM STDIN WITH (FORMAT csv, HEADER true);" \
    < "$ALIAS_CSV"

log_info "Loading references..."
PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME \
    -c "COPY dms.Reference (ParentDocumentId, ParentDocumentPartitionKey, ReferentialId, ReferentialPartitionKey) FROM STDIN WITH (FORMAT csv, HEADER true);" \
    < "$REFERENCE_CSV"

log_info "Re-enabling autovacuum and refreshing statistics..."
psql_exec <<'EOF'
DO $$
DECLARE
    target record;
BEGIN
    FOR target IN
        SELECT format('%I.%I', n.nspname, c.relname) AS fq_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'dms'
          AND (
              c.relname IN ('Document', 'Alias', 'Reference')
              OR c.relname ~ '^(document|alias|reference)_[0-9]{2}$'
          )
    LOOP
        EXECUTE format('ALTER TABLE %s SET (autovacuum_enabled = true);', target.fq_name);
    END LOOP;
END;
$$;

ANALYZE dms.Document;
ANALYZE dms.Alias;
ANALYZE dms.Reference;
EOF

log_info "Refreshing deterministic reference fixture targets..."
psql_exec <<'EOF'
SELECT dms.build_perf_reference_targets();
EOF

log_info "Load complete. Row counts:"
psql_exec <<'EOF'
SELECT
    'Document' AS table_name,
    COUNT(*) AS row_count
FROM dms.Document
UNION ALL
SELECT 'Alias', COUNT(*) FROM dms.Alias
UNION ALL
SELECT 'Reference', COUNT(*) FROM dms.Reference;
EOF
