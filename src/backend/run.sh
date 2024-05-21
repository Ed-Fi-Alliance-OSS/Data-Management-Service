# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Use this script for building a PostgreSQL container that is pre-loaded
# with the Data Management Service schema.

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "Initializing PostgreSQL database directory..."
  initdb $PGDATA
fi

[ $? -ne 0 ] && exit || echo "Starting PostgreSQL service..."

echo "host    all             all             172.17.0.1/32            trust" >> "$PGDATA/pg_hba.conf"

chown -R postgres:postgres "$PGDATA"

pg_ctl -D "$PGDATA" -o "-c listen_addresses='*'" -w start

[ $? -ne 0 ] && exit || echo "Waiting for PostgreSQL to start..."

until pg_isready -h ${POSTGRES_HOST} -p ${POSTGRES_PORT} -U ${POSTGRES_USER}; do
  echo "Waiting for PostgreSQL to start..."
  sleep 2
done

echo "PostgreSQL is ready. Install Data Management Service schema."

/app/EdFi.DataManagementService.Backend.Installer \
    postgresql \
    "host=${POSTGRES_HOST};port=${POSTGRES_PORT};username=${POSTGRES_USER};database=edfi_datamanagementservice;"

echo "Shutdown PostgreSQL"
pg_ctl -D $(psql -Xtc 'show data_directory') stop
