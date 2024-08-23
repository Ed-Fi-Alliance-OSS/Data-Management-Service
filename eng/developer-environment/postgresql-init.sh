#!/bin/sh

echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf

psql -U $POSTGRES_USER -c "CREATE DATABASE ${POSTGRES_DB_NAME};"
