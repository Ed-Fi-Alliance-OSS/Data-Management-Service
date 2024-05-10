#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

if [[ -z "$POSTGRES_PORT" ]]; then
  export POSTGRES_PORT=5432
fi

echo "Creating EdFi.DataManagementService database..."
psql --username "$POSTGRES_USER" --port $POSTGRES_PORT --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE DATABASE "EdFi.DataManagementService" TEMPLATE template0;
    GRANT ALL PRIVILEGES ON DATABASE "EdFi.DataManagementService" TO $POSTGRES_USER;
EOSQL

echo "Running database migration scripts..."

for FILE in `LANG=C ls /tmp/DbScripts/*.sql | sort -V`
do
    psql --no-password --username "$POSTGRES_USER" --port $POSTGRES_PORT --dbname "EdFi.DataManagementService" --file $FILE 1> /dev/null
done
