$sourcePort="8083"
$sinkPort="8084"

Invoke-RestMethod -Method Delete -uri http://localhost:$sourcePort/connectors/fulfillment-connector

Start-Sleep 1

Invoke-RestMethod -Method Post -InFile .\postgresql_connector.json `
    -uri http://localhost:$sourcePort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sourcePort/connectors/fulfillment-connector

##

Invoke-RestMethod -Method Delete -uri http://localhost:$sinkPort/connectors/search-engine

Start-Sleep 1

Invoke-RestMethod -Method Post -InFile .\opensearch_connector.json `
    -uri http://localhost:$sinkPort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sinkPort/connectors/search-engine



# In postgresql, I edited the pg_hba.conf to add the kafka source connector host name,
# and postgresql.conf to change WAL to logical.
# /var/lib/postgresql/data
# host    replication    postgres         kafka-connect-source    trust
# wal_level = logical
# Might have been able to run `ALTER SYSTEM SET wal_level = logical;`
# These changes can be made in our DMS postgres image.

# See https://debezium.io/documentation/reference/stable/connectors/postgresql.html
# for more information on setting up a separate user account and other considerations.
#


# echo "host    replication    postgres         kafka-connect-source    trust" >> /var/lib/postgresql/data/pg_hba.conf
# echo "wal_level = logical" >> /var/lib/postgresql/data/postgresql.conf



# The two connectors are somehow sharing information. Do we really need two different running instances anyway?
