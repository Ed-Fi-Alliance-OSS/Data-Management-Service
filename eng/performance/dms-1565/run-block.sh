#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
#
# DMS-1565 measurement block: fresh Northridge restore, Query Store setup, activity snapshot, and one
# hosted block run of Given_A_Dms1565_Northridge_Put_Latency_Block. See README.md.
#
# Usage: run-block.sh <arm: baseline|changed> <block-number> <resource-order csv> <warmup> <measured> <output-dir>
# Environment:
#   DMS1565_WORK            directory holding arm-baseline/, arm-changed/, apischema-core-only/, doclist.json
#   DMS1565_SA_PASSWORD_FILE file containing the dedicated container's SA password (never echoed)
#   DMS1565_CONTAINER       dedicated SQL Server container (default dms1565-nr-mssql)
#   DMS1565_PORT            its host port (default 1437)
#   DMS1565_BACKUP          backup path inside the container
#                           (default /backup/EdFi_DMS_Northridge_v81_20261104_MSSQL.bak)
set -euo pipefail
fail() { echo "run-block.sh: $*" >&2; exit 64; }
[ $# -eq 6 ] || fail "usage: run-block.sh <baseline|changed> <block-number> <resource-order csv> <warmup> <measured> <output-dir>"
ARM=$1; BLOCK=$2; ORDER=$3; WARMUP=$4; MEASURED=$5
WORK=${DMS1565_WORK:?set DMS1565_WORK}
PASSWORD_FILE=${DMS1565_SA_PASSWORD_FILE:?set DMS1565_SA_PASSWORD_FILE}
CONTAINER=${DMS1565_CONTAINER:-dms1565-nr-mssql}
PORT=${DMS1565_PORT:-1437}
BAK=${DMS1565_BACKUP:-/backup/EdFi_DMS_Northridge_v81_20261104_MSSQL.bak}
DB=dms1565_nr_run
HARNESS=src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Mssql/Given_A_Dms1565_Northridge_Put_Latency_Block.cs

# Validate everything before the run database is dropped.
case "$ARM" in baseline|changed) ;; *) fail "arm must be baseline or changed, got '$ARM'" ;; esac
[[ "$BLOCK" =~ ^[0-9]+$ ]] || fail "block number must be a non-negative integer, got '$BLOCK'"
[[ "$WARMUP" =~ ^[0-9]+$ ]] || fail "warmup must be a non-negative integer, got '$WARMUP'"
[[ "$MEASURED" =~ ^[1-9][0-9]*$ ]] || fail "measured must be a positive integer, got '$MEASURED'"
EXPECTED_ORDER="classPeriods,courseOfferings,sections,sessions,studentSchoolAssociations"
SORTED_ORDER=$(echo "$ORDER" | tr ',' '\n' | LC_ALL=C sort | paste -sd, -)
[ "$SORTED_ORDER" = "$EXPECTED_ORDER" ] || fail "resource order must list each of $EXPECTED_ORDER once, got '$ORDER'"
WORK=$(cd "$WORK" 2>/dev/null && pwd) || fail "DMS1565_WORK does not exist"
[ -r "$PASSWORD_FILE" ] || fail "DMS1565_SA_PASSWORD_FILE is not readable"
[ -f "$WORK/arm-$ARM/$HARNESS" ] || fail "harness missing from $WORK/arm-$ARM"
[ -f "$WORK/doclist.json" ] || fail "$WORK/doclist.json missing"
[ -f "$WORK/apischema-core-only/bootstrap-api-schema-manifest.json" ] || fail "staged ApiSchema missing under $WORK"
mkdir -p "$6" || fail "cannot create output directory '$6'"
OUT=$(cd "$6" && pwd)
LOG="$OUT/block-$BLOCK-$ARM.log"

sq() {
  SQLCMDPASSWORD=$(cat "$PASSWORD_FILE") docker exec -i -e SQLCMDPASSWORD "$CONTAINER" \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -I -W "$@"
}

activity() {
  echo "-- $1 $(date -u +%FT%TZ)"
  echo "user requests: $(sq -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.is_user_process = 1 AND r.session_id <> @@SPID;")"
  docker stats --no-stream --format '{{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}}'
}

{
  echo "arm=$ARM block=$BLOCK order=$ORDER warmup=$WARMUP measured=$MEASURED"
  echo "inputs: harness=$(shasum -a 256 < "$WORK/arm-$ARM/$HARNESS" | cut -d' ' -f1)" \
    "doclist=$(shasum -a 256 < "$WORK/doclist.json" | cut -d' ' -f1)" \
    "apischema=$(shasum -a 256 < "$WORK/apischema-core-only/ed-fi/ApiSchema.json" | cut -d' ' -f1)"
  sq -Q "IF DB_ID(N'$DB') IS NOT NULL BEGIN ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB]; END"
  sq -t 0 -Q "RESTORE DATABASE [$DB] FROM DISK = N'$BAK' WITH CHECKSUM, MOVE N'edfi_datamanagementservice' TO N'/var/opt/mssql/data/$DB.mdf', MOVE N'edfi_datamanagementservice_log' TO N'/var/opt/mssql/data/${DB}_log.ldf';" | tail -1
  # NEW_BROKER rather than ENABLE_BROKER: another restore of the same backup on this server owns the broker id.
  sq -Q "ALTER DATABASE [$DB] SET RECOVERY SIMPLE; ALTER AUTHORIZATION ON DATABASE::[$DB] TO sa; ALTER DATABASE [$DB] SET NEW_BROKER WITH ROLLBACK IMMEDIATE;"
  sq -d $DB -Q "SET NOCOUNT ON; UPDATE dms.DataStoreIdentity SET SourceIdentity = NEWID();
    ALTER DATABASE CURRENT SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL, MAX_STORAGE_SIZE_MB = 2048, SIZE_BASED_CLEANUP_MODE = OFF, INTERVAL_LENGTH_MINUTES = 60, DATA_FLUSH_INTERVAL_SECONDS = 60);
    ALTER DATABASE CURRENT SET QUERY_STORE CLEAR;
    ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
    SELECT EffectiveSchemaHash, ResourceKeyCount FROM dms.EffectiveSchema;
    SELECT name, compatibility_level, is_read_committed_snapshot_on, snapshot_isolation_state_desc, recovery_model_desc FROM sys.databases WHERE name = DB_NAME();
    SELECT actual_state_desc, query_capture_mode_desc, max_storage_size_mb FROM sys.database_query_store_options;"
  activity before
} > "$LOG" 2>&1

set +e
(
  cd "$WORK/arm-$ARM"
  CONNECTION="Server=localhost,$PORT;Database=$DB;User Id=sa;Password=$(cat "$PASSWORD_FILE");TrustServerCertificate=true;Max Pool Size=50"
  DMS1565_NR_CONNECTION="$CONNECTION" \
  DMS1565_APISCHEMA_DIR="$WORK/apischema-core-only" DMS1565_DOCLIST="$WORK/doclist.json" \
  DMS1565_ARM=$ARM DMS1565_BLOCK=$BLOCK DMS1565_RESOURCE_ORDER=$ORDER DMS1565_WARMUP=$WARMUP DMS1565_MEASURED=$MEASURED \
  DMS1565_OUTPUT="$OUT/block-$BLOCK-$ARM.json" \
  ConnectionStrings__MssqlAdmin="Server=localhost,$PORT;User Id=sa;Password=$(cat "$PASSWORD_FILE");TrustServerCertificate=true" \
  dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration -c Release --no-build \
    --filter "FullyQualifiedName~Dms1565_Northridge_Put_Latency_Block.It_measures_one_block" --logger "console;verbosity=normal"
) >> "$LOG" 2>&1
RC=$?
set -e
activity after >> "$LOG" 2>&1
echo "rc=$RC" >> "$LOG"
grep -E "Passed!|Failed!|Total tests" "$LOG" | tail -2
exit $RC
