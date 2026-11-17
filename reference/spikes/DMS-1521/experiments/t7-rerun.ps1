# DMS-1521 task 7: re-run the adapted (type d) classes under inline and baseline on one engine.
param([Parameter(Mandatory = $true)][ValidateSet('pg', 'mssql')] [string] $Engine)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$out = "C:\perf\dms-1521\t7-rerun-$Engine"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:DOTNET_SYSTEM_NET_DISABLEIPV6 = '1'
if ($Engine -eq 'pg') {
    $name = 'dms-1521-t7-postgresql'
    docker rm -f $name 2>$null | Out-Null
    docker run -d --name $name -e POSTGRES_HOST_AUTH_METHOD=trust --memory 8g -p 127.0.0.1:15523:5432 `
        'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873' postgres `
        -c max_locks_per_transaction=256 -c wal_level=logical -c max_replication_slots=10 | Out-Null
    do { Start-Sleep 2; docker exec $name pg_isready -U postgres *> $null } until ($LASTEXITCODE -eq 0)
    $env:ConnectionStrings__DatabaseConnection = 'host=127.0.0.1;port=15523;username=postgres;database=edfi_dms_t7;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService;NoResetOnClose=true;Include Error Detail=true;'
    $project = 'src\dms\backend\EdFi.DataManagementService.Backend.Postgresql.Tests.Integration'
    $filter = 'FullyQualifiedName~.Given_A_Pgsql_Query_Keyset_That_Returns_Its_Selected_Ids.'
}
else {
    $name = 'dms-1521-t7-mssql'
    $password = 'Dms1521_T7!'
    docker rm -f $name 2>$null | Out-Null
    docker run -d --name $name -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" -e MSSQL_AGENT_ENABLED=true `
        -e MSSQL_MEMORY_LIMIT_MB=4096 -e MSSQL_PID=Developer --memory 8g -p 127.0.0.1:14353:1433 `
        'mcr.microsoft.com/mssql/server@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a' | Out-Null
    do { Start-Sleep 3; docker exec $name /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $password -Q 'SELECT 1' -b *> $null } until ($LASTEXITCODE -eq 0)
    $env:ConnectionStrings__MssqlAdmin = "Server=127.0.0.1,14353;User Id=sa;Password=$password;TrustServerCertificate=true;"
    $env:MSSQL_GENERATED_DDL_LEASE_STRATEGY = 'backup-restore'
    $project = 'src\dms\backend\EdFi.DataManagementService.Backend.Mssql.Tests.Integration'
    $filter = 'FullyQualifiedName~.Given_A_Mssql_Query_Keyset_That_Returns_Its_Selected_Ids.'
}
try {
    foreach ($variant in 'inline', 'baseline') {
        $env:DMS1521_KEYSET_VARIANT = $variant
        & dotnet test "$repo\$project" --no-build --nologo --filter $filter --results-directory $out `
            --logger "trx;LogFileName=$variant.trx" *> "$out\$variant.console.log"
        "$variant exit $LASTEXITCODE :: " + (Select-String -Path "$out\$variant.console.log" -Pattern '(Passed|Failed)!.*' | Select-Object -Last 1).Line
    }
}
finally { docker rm -f $name | Out-Null }
