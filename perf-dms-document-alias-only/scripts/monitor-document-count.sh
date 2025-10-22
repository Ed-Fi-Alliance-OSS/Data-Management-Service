#!/bin/bash

# DMS Table Row Count Monitor
# Monitors the row count of the dms.Document and dms.Alias tables every 10 seconds

# Database connection parameters
DB_HOST="localhost"
DB_PORT="5432"
DB_NAME="edfi_datamanagementservice"
DB_USER="postgres"
DB_PASSWORD="abcdefgh1!"

# Export password for psql to use
export PGPASSWORD="$DB_PASSWORD"

echo "Starting DMS table monitor..."
echo "Database: $DB_NAME"
echo "Tables: dms.Document, dms.Alias"
echo "Polling interval: 10 seconds"
echo "Press Ctrl+C to stop"
echo "----------------------------------------"

while true; do
    # Get current timestamp
    TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')

    # Query the row counts for both tables
    RESULT=$(psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -t -c "
        SELECT
            (SELECT COUNT(*) FROM dms.Document) as document_count,
            (SELECT COUNT(*) FROM dms.Alias) as alias_count;
    ")

    # Check if query was successful
    if [ $? -eq 0 ]; then
        # Trim whitespace and extract counts
        RESULT=$(echo "$RESULT" | xargs)
        DOC_COUNT=$(echo "$RESULT" | awk '{print $1}')
        ALIAS_COUNT=$(echo "$RESULT" | awk '{print $3}')
        echo "[$TIMESTAMP] Document: $DOC_COUNT | Alias: $ALIAS_COUNT"
    else
        echo "[$TIMESTAMP] ERROR: Failed to query database"
    fi

    # Wait 10 seconds
    sleep 10
done
