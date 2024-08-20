#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

until pg_isready -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_USER}; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready."

export PGPASSWORD=$POSTGRES_PASSWORD
DATABASE="edfi_datamanagementservice"
TABLE_NAME="document"

DB_EXISTS=$(psql -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_USER} -tAc "SELECT 1 FROM pg_database WHERE datname='$DATABASE'")

if [ "$DB_EXISTS" == "1" ]; then
    echo "Database '$DATABASE' exists."

    TABLE_EXISTS=$(psql -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_USER} -d $DATABASE -tAc "SELECT 1 FROM information_schema.tables WHERE table_name='$TABLE_NAME'")

    if [ "$TABLE_EXISTS" == "1" ]; then

    echo "Table '$TABLE_NAME' exists."

psql --username ${POSTGRES_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        CREATE PUBLICATION to_debezium WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);
EOSQL
psql --username ${POSTGRES_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        SELECT pg_create_logical_replication_slot('debezium', 'pgoutput');
EOSQL
psql --username ${POSTGRES_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        ALTER PUBLICATION to_debezium ADD TABLE dms.document;
EOSQL

    else
        echo "Table '$TABLE_NAME' does not exist in the database '$DATABASE'."
    fi
else
    echo "Database '$DATABASE' does not exist."
fi

