#!/bin/sh

echo "host    replication    postgres         kafka-postgresql-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf

# Create the datastore database with format('%I') so the selected POSTGRES_DB_NAME is always a safely
# quoted identifier. %I handles a hyphen, space, period, or leading digit and, crucially, doubles any
# embedded double quote, so a crafted name cannot break out of the identifier and inject SQL. This runs
# as the superuser on every fresh-volume init - before, and independently of, the host-side identifier
# guard, which is not on this path at all - so the quoting must be self-contained here. The name is passed
# as a psql variable (never spliced into the SQL text) and rendered by format via :'dbname'.
psql -U "$POSTGRES_USER" -v ON_ERROR_STOP=1 -v dbname="$POSTGRES_DB_NAME" <<'EOSQL'
SELECT format('CREATE DATABASE %I', :'dbname')
\gexec
EOSQL
