#!/bin/bash

# Resolve repository roots
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export SCRIPT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Database connection defaults (override with environment variables)
export DB_HOST="${DB_HOST:-localhost}"
export DB_PORT="${DB_PORT:-5432}"
export DB_NAME="${DB_NAME:-edfi_datamanagementservice}"
export DB_USER="${DB_USER:-postgres}"
export DB_PASSWORD="${DB_PASSWORD:-${PGPASSWORD:-abcdefgh!}}"

# Default CSV output directory for generators
export OUTPUT_DIR="${OUTPUT_DIR:-${SCRIPT_ROOT}/data/out}"

# Convenience colors for log output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

psql_exec() {
    PGPASSWORD="$DB_PASSWORD" \
        psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" "$@"
}
