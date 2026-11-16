#Requires -Version 7
# DMS-1521 review round 8 (R8-F1): does page selection reach a generic plan on main as well as under
# inline? Runs main's batch shape (temp table dropped and recreated every call) and inline's shape
# (transaction-local carrier) through Npgsql 8.0.4 with DMS's auto-prepare settings
# (NpgsqlDataSourceLifetime: Max Auto Prepare 256, Auto Prepare Min Usages 3), each sequence on one
# physical connection, then prints pg_prepared_statements. The selection is filtered, on 500,000 rows.
#
# Needs Docker, the spike's PostgreSQL image, and Npgsql 8.0.4 in the NuGet cache (restored by any
# DMS build). Starts a throwaway container on 127.0.0.1:15522 and removes it on exit.
#
#   pwsh -File reference/spikes/DMS-1521/experiments/plan-type-probe.ps1
param(
    [string] $Image = 'postgres:18.6-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873',
    [int] $Port = 15522
)
$ErrorActionPreference = 'Stop'

$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
$loggingAbstractions = 'Microsoft.Extensions.Logging.Abstractions'
if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $loggingAbstractions })) {
    Add-Type -Path (Join-Path $nuget 'microsoft.extensions.logging.abstractions/8.0.0/lib/net8.0/Microsoft.Extensions.Logging.Abstractions.dll')
}
Add-Type -Path (Join-Path $nuget 'npgsql/8.0.4/lib/net8.0/Npgsql.dll')

$container = 'dms1521-plan-type-probe'
$connectionString = "Host=127.0.0.1;Port=$Port;Username=postgres;Database=postgres;Pooling=false;Max Auto Prepare=256;Auto Prepare Min Usages=3"

function Open-ProbeConnection {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $connection = [Npgsql.NpgsqlConnection]::new($connectionString)
        try {
            $connection.Open()
            return $connection
        }
        catch {
            $connection.Dispose()
            Start-Sleep -Milliseconds 500
        }
    }

    throw 'Could not connect to the probe container.'
}

function Invoke-ProbeStatement($Connection, [string] $Sql) {
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    [void]$command.ExecuteNonQuery()
    $command.Dispose()
}

function Invoke-ProbeBatch($Connection, [string] $Sql, [long] $Offset) {
    $transaction = $Connection.BeginTransaction()
    $command = $Connection.CreateCommand()
    $command.Transaction = $transaction
    $command.CommandText = $Sql
    [void]$command.Parameters.AddWithValue('k', [int]7)
    [void]$command.Parameters.AddWithValue('limit', [long]25)
    [void]$command.Parameters.AddWithValue('offset', $Offset)
    $reader = $command.ExecuteReader()
    do {
        while ($reader.Read()) { }
    } while ($reader.NextResult())
    $reader.Dispose()
    $command.Dispose()
    $transaction.Commit()
    $transaction.Dispose()
}

function Get-PreparedStatementPlan($Connection, [string] $Label) {
    $command = $Connection.CreateCommand()
    $command.CommandText = "SELECT left(regexp_replace(statement, '\s+', ' ', 'g'), 100), generic_plans, custom_plans FROM pg_prepared_statements ORDER BY 1"
    $reader = $command.ExecuteReader()
    "== $Label"
    while ($reader.Read()) {
        '  generic {0,2}  custom {1,2}  {2}' -f $reader.GetInt64(1), $reader.GetInt64(2), $reader.GetString(0)
    }
    $reader.Dispose()
    $command.Dispose()
}

docker run --rm -d --name $container -p "127.0.0.1:${Port}:5432" -e POSTGRES_HOST_AUTH_METHOD=trust $Image | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker run $container failed" }
try {
    $setup = Open-ProbeConnection
    Invoke-ProbeStatement -Connection $setup -Sql @'
CREATE TABLE stu (DocumentId bigint PRIMARY KEY, k int NOT NULL);
INSERT INTO stu SELECT g, g % 1000 FROM generate_series(1, 500000) g;
CREATE INDEX stu_k ON stu (k, DocumentId);
ANALYZE stu;
'@
    $setup.Dispose()

    # main: the selecting statement inserts into the keyset temp table the same batch recreated.
    $mainSql = @'
DROP TABLE IF EXISTS page;
CREATE TEMP TABLE page (DocumentId bigint PRIMARY KEY, Ordinal int NULL) ON COMMIT DROP;
WITH page_ids AS (SELECT r.DocumentId FROM stu r WHERE r.k = @k ORDER BY r.DocumentId ASC LIMIT @limit OFFSET @offset) INSERT INTO page (DocumentId) SELECT DocumentId FROM page_ids RETURNING DocumentId;
SELECT p.DocumentId FROM page p ORDER BY p.DocumentId;
'@
    # inline: the same selection sets the transaction-local carrier, and a reader reads it.
    $inlineSql = @'
WITH page_ids AS (SELECT r.DocumentId FROM stu r WHERE r.k = @k ORDER BY r.DocumentId ASC LIMIT @limit OFFSET @offset) SELECT u.DocumentId FROM (SELECT set_config('dms.page_keyset', COALESCE(string_agg(CAST(p.DocumentId AS text), ',' ORDER BY p.DocumentId), ''), true) AS keyset FROM page_ids p) c CROSS JOIN LATERAL unnest(CAST(string_to_array(c.keyset, ',') AS bigint[])) AS u(DocumentId);
SELECT u.DocumentId FROM unnest(CAST(string_to_array(current_setting('dms.page_keyset', true), ',') AS bigint[])) AS u(DocumentId);
'@

    foreach ($shape in @(@{ Name = 'main'; Sql = $mainSql }, @{ Name = 'inline'; Sql = $inlineSql })) {
        $uniform = Open-ProbeConnection
        for ($execution = 1; $execution -le 16; $execution++) {
            Invoke-ProbeBatch -Connection $uniform -Sql $shape.Sql -Offset 0
        }
        Get-PreparedStatementPlan -Connection $uniform -Label "$($shape.Name), 16 x (25 @0)"
        $uniform.Dispose()

        $alternating = Open-ProbeConnection
        for ($execution = 1; $execution -le 8; $execution++) {
            Invoke-ProbeBatch -Connection $alternating -Sql $shape.Sql -Offset 0
            Invoke-ProbeBatch -Connection $alternating -Sql $shape.Sql -Offset 1000000
        }
        Get-PreparedStatementPlan -Connection $alternating -Label "$($shape.Name), 8 x (25 @0, 25 @1,000,000)"
        $alternating.Dispose()
    }
}
finally {
    docker rm -f $container | Out-Null
}
