#!/bin/sh

echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf

# createdb, not psql -c: the database name travels as ONE quoted command argument and is never
# interpolated into SQL text, so createdb quotes the identifier for us. Interpolating it built the
# statement by string concatenation, which made authored characters SQL syntax - notably
# POSTGRES_DB_NAME=edfi_configurationservice--comment, where "--comment" is a SQL comment and the
# statement therefore created the reserved Configuration Service database instead. "--" ends option
# parsing so a name beginning with "-" cannot be read as a switch.
createdb --maintenance-db=postgres -U "$POSTGRES_USER" -- "$POSTGRES_DB_NAME"
