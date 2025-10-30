#!/bin/bash

# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "${SCRIPT_DIR}/config.sh"

TARGET_DIR="${1:-${OUTPUT_DIR:-${SCRIPT_ROOT}/data/out}}"

if [ ! -d "$TARGET_DIR" ]; then
    log_error "Output directory not found: $TARGET_DIR"
    exit 1
fi

ABS_OUTPUT_DIR="$(cd "$TARGET_DIR" && pwd)"
DOCUMENT_CSV="${ABS_OUTPUT_DIR}/document.csv"
ALIAS_CSV="${ABS_OUTPUT_DIR}/alias.csv"
REFERENCE_CSV="${ABS_OUTPUT_DIR}/reference.csv"

for file in "$DOCUMENT_CSV" "$ALIAS_CSV" "$REFERENCE_CSV"; do
    if [ ! -f "$file" ]; then
        log_error "Required file missing: $file"
        exit 1
    fi
done

log_info "Loading CSV data from: $ABS_OUTPUT_DIR"
log_info "Target database: postgresql://${DB_USER}@${DB_HOST}:${DB_PORT}/${DB_NAME}"

restore_autovac() {
    log_info "Re-enabling autovacuum and refreshing statistics"
    psql_exec <<'SQL'
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
SQL
}

trap restore_autovac EXIT

log_info "Disabling autovacuum and truncating target tables"
psql_exec <<'SQL'
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

TRUNCATE TABLE dms.Reference RESTART IDENTITY;
TRUNCATE TABLE dms.Alias RESTART IDENTITY CASCADE;
TRUNCATE TABLE dms.Document RESTART IDENTITY CASCADE;
SQL

log_info "Loading dms.Document"
psql_exec -c "COPY dms.Document (DocumentPartitionKey, DocumentUuid, ResourceName, ResourceVersion, IsDescriptor, ProjectName, EdfiDoc, LastModifiedTraceId) FROM STDIN WITH (FORMAT csv, HEADER true);" < "$DOCUMENT_CSV"

log_info "Loading dms.Alias"
psql_exec -c "COPY dms.Alias (ReferentialPartitionKey, ReferentialId, DocumentId, DocumentPartitionKey) FROM STDIN WITH (FORMAT csv, HEADER true);" < "$ALIAS_CSV"

log_info "Loading dms.Reference"
psql_exec -c "COPY dms.Reference (ParentDocumentId, ParentDocumentPartitionKey, AliasId, ReferentialPartitionKey, ReferencedDocumentId, ReferencedDocumentPartitionKey) FROM STDIN WITH (FORMAT csv, HEADER true);" < "$REFERENCE_CSV"

psql_exec <<'SQL'
SELECT setval(pg_get_serial_sequence('dms.Document', 'Id'), COALESCE((SELECT MAX(Id) FROM dms.Document), 0));
SELECT setval(pg_get_serial_sequence('dms.Alias', 'Id'), COALESCE((SELECT MAX(Id) FROM dms.Alias), 0));
SELECT setval(pg_get_serial_sequence('dms.Reference', 'Id'), COALESCE((SELECT MAX(Id) FROM dms.Reference), 0));
SQL

log_info "Row counts"
psql_exec <<'SQL'
SELECT 'Document' AS table_name, COUNT(*) AS row_count FROM dms.Document
UNION ALL
SELECT 'Alias', COUNT(*) FROM dms.Alias
UNION ALL
SELECT 'Reference', COUNT(*) FROM dms.Reference;
SQL

trap - EXIT
restore_autovac

log_info "Load complete"
