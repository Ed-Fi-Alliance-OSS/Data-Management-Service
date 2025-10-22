#!/bin/bash

source $(dirname "$0")/config.sh

log_warn "This will completely destroy the test database: $DB_NAME"
read -p "Are you sure? (yes/no): " confirm

if [ "$confirm" != "yes" ]; then
    log_info "Teardown cancelled"
    exit 0
fi

log_info "Tearing down test database: $DB_NAME"

# Drop database
PGPASSWORD=$DB_PASSWORD psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d postgres <<EOF
-- Terminate all connections to the database
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = '$DB_NAME' AND pid <> pg_backend_pid();

-- Drop the database
DROP DATABASE IF EXISTS $DB_NAME;
EOF

log_info "Database $DB_NAME has been dropped"