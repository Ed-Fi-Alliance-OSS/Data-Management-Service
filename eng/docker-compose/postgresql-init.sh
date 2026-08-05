#!/bin/sh

echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf

# createdb, not psql -c: the database name travels as ONE quoted command argument and is never
# interpolated into SQL text, so createdb quotes the identifier for us. Interpolating it built the
# statement by string concatenation, which made authored characters SQL syntax - a name ending in
# "--<anything>" turned the rest of the statement into a SQL comment, so the database actually
# created was the truncated prefix rather than the name as authored. "--" below ends option parsing,
# so a name beginning with "-" cannot be read as a switch either.
createdb --maintenance-db=postgres -U "$POSTGRES_USER" -- "$POSTGRES_DB_NAME"
