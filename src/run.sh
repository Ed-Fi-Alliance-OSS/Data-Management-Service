#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

envsubst </app/appsettings.template.json >/app/appsettings.json

until pg_isready -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_ADMIN_USER}; do
    echo "Waiting for PostgreSQL to start..."
    sleep 2
done

echo "PostgreSQL is ready."

if [ "$NEED_DATABASE_SETUP" = true ]; then

    DATABASE="edfi_datamanagementservice"

    echo "Installing Data Management Service schema."
    dotnet Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c "host=${POSTGRES_HOST};port=${POSTGRES_PORT};username=${POSTGRES_ADMIN_USER};password=${POSTGRES_ADMIN_PASSWORD};database=${DATABASE};"

    export NEED_DATABASE_SETUP=false
    export PGPASSWORD=$POSTGRES_ADMIN_PASSWORD

    sleep 5

    if [[ "${QUERY_HANDLER,,}" == "opensearch" ]]; then

        echo "Set up debezium publication."
        DATABASE="edfi_datamanagementservice"
        TABLE_NAME="document"

        DB_EXISTS=$(psql -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_ADMIN_USER} -tAc "SELECT 1 FROM pg_database WHERE datname='$DATABASE'")

        if [ "$DB_EXISTS" == "1" ]; then
            echo "Database '$DATABASE' exists."

            TABLE_EXISTS=$(psql -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_ADMIN_USER} -d $DATABASE -tAc "SELECT 1 FROM information_schema.tables WHERE table_name='$TABLE_NAME'")

            if [ "$TABLE_EXISTS" == "1" ]; then

                echo "Table '$TABLE_NAME' exists."

                psql --username ${POSTGRES_ADMIN_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        CREATE PUBLICATION to_debezium WITH (publish = 'insert, update, delete, truncate', publish_via_partition_root = true);
EOSQL
                psql --username ${POSTGRES_ADMIN_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        SELECT pg_create_logical_replication_slot('debezium', 'pgoutput');
EOSQL
                psql --username ${POSTGRES_ADMIN_USER} --port ${POSTGRES_PORT} --dbname $DATABASE -h ${POSTGRES_HOST} <<-EOSQL
        ALTER PUBLICATION to_debezium ADD TABLE dms.document;
EOSQL

            else
                echo "Table '$TABLE_NAME' does not exist in the database '$DATABASE'."
            fi
        else
            echo "Database '$DATABASE' does not exist."
        fi

    fi

else
    echo "Skipping Data Management Service schema installation."
fi

dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll
