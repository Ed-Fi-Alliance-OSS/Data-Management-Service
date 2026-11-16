# DMS-1521 task 7: correctness lanes for the inline prototype (patch must already be applied and built).
param(
    [Parameter(Mandatory = $true)][ValidateSet('unit', 'pg', 'mssql')] [string] $Lane,
    [string] $Variant = 'inline',
    [string] $Tag = 't7'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$scratch = $PSScriptRoot
$out = "C:\perf\dms-1521\$Tag-$Lane-$Variant"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:DOTNET_SYSTEM_NET_DISABLEIPV6 = '1'
$env:DMS1521_KEYSET_VARIANT = $Variant
Remove-Item Env:ConnectionStrings__DatabaseConnection, Env:ConnectionStrings__MssqlAdmin, Env:ConnectionStrings__PostgresAdmin -ErrorAction SilentlyContinue

function Invoke-Suite([string] $Name, [string] $Project, [string] $Filter) {
    $started = Get-Date
    "[$started] $Name" | Tee-Object -FilePath "$out\lane.log" -Append
    $arguments = @('test', "$repo\$Project", '--no-build', '--nologo', '--results-directory', $out,
        '--logger', "trx;LogFileName=$Name.trx", '--logger', 'console;verbosity=minimal')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet @arguments *> "$out\$Name.console.log"
    $exit = $LASTEXITCODE
    $summary = Select-String -Path "$out\$Name.console.log" -Pattern '^(Passed|Failed)!.*' | Select-Object -Last 1
    "[$(Get-Date)] $Name exit $exit after $([int]((Get-Date) - $started).TotalSeconds) s :: $($summary.Line)" | Tee-Object -FilePath "$out\lane.log" -Append
}

function Invoke-LaneContainer([string] $Name, [string[]] $RunArgs, [scriptblock] $Ready) {
    docker rm -f $Name 2>$null | Out-Null
    docker run -d --name $Name @RunArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run $Name failed" }
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (& $Ready)) {
        if ((Get-Date) -gt $deadline) { throw "$Name not ready" }
        Start-Sleep -Seconds 3
    }
}

switch ($Lane) {
    'unit' {
        Invoke-Suite -Name 'plans-unit' -Project 'src\dms\backend\EdFi.DataManagementService.Backend.Plans.Tests.Unit' -Filter $null
        Invoke-Suite -Name 'backend-unit' -Project 'src\dms\backend\EdFi.DataManagementService.Backend.Tests.Unit' -Filter $null
        Invoke-Suite -Name 'frontend-relationalwritesmoke' -Project 'src\dms\frontend\EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit' -Filter 'FullyQualifiedName~RelationalWriteSmoke'
    }
    'pg' {
        $name = 'dms-1521-t7-postgresql'
        # CI's start-postgresql-test-container flags, the spike's pinned 18.6 digest; no tmpfs (local memory).
        Invoke-LaneContainer -Name $name -RunArgs @('-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '--memory', '8g', '-p', '127.0.0.1:15523:5432',
            'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873', 'postgres',
            '-c', 'max_locks_per_transaction=256', '-c', 'wal_level=logical', '-c', 'max_replication_slots=10') -Ready {
            docker exec $name pg_isready -U postgres *> $null; $LASTEXITCODE -eq 0
        }
        try {
            $env:ConnectionStrings__DatabaseConnection = 'host=127.0.0.1;port=15523;username=postgres;database=edfi_dms_t7;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DataManagementService;NoResetOnClose=true;Include Error Detail=true;'
            $env:ConnectionStrings__PostgresAdmin = 'host=127.0.0.1;port=15523;username=postgres;database=postgres;pooling=false;'
            Invoke-Suite -Name 'api-postgresql' -Project 'src\dms\tests\EdFi.DataManagementService.Tests.Integration' -Filter 'Category=ApiIntegration&Category=PostgresqlIntegration'
            Invoke-Suite -Name 'backend-postgresql-readpath' -Project 'src\dms\backend\EdFi.DataManagementService.Backend.Postgresql.Tests.Integration' -Filter (Get-Content "$scratch\t7-filter-Postgresql.txt" -Raw).Trim()
        }
        finally { docker rm -f $name | Out-Null }
    }
    'mssql' {
        $name = 'dms-1521-t7-mssql'
        $password = 'Dms1521_T7!'
        # CI's start-mssql-test-container settings (Agent on, 4 GB SQL memory), the spike's pinned 2025 digest; no tmpfs.
        Invoke-LaneContainer -Name $name -RunArgs @('-e', 'ACCEPT_EULA=Y', '-e', "MSSQL_SA_PASSWORD=$password", '-e', 'MSSQL_AGENT_ENABLED=true',
            '-e', 'MSSQL_MEMORY_LIMIT_MB=4096', '-e', 'MSSQL_PID=Developer', '--memory', '8g', '-p', '127.0.0.1:14353:1433',
            'mcr.microsoft.com/mssql/server@sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a') -Ready {
            docker exec $name /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $password -Q 'SELECT 1' -b *> $null; $LASTEXITCODE -eq 0
        }
        try {
            $env:ConnectionStrings__MssqlAdmin = "Server=127.0.0.1,14353;User Id=sa;Password=$password;TrustServerCertificate=true;"
            $env:MSSQL_GENERATED_DDL_LEASE_STRATEGY = 'backup-restore'
            $env:MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY = '2'
            Invoke-Suite -Name 'api-mssql' -Project 'src\dms\tests\EdFi.DataManagementService.Tests.Integration' -Filter 'Category=ApiIntegration&Category=MssqlIntegration'
            Invoke-Suite -Name 'backend-mssql-readpath' -Project 'src\dms\backend\EdFi.DataManagementService.Backend.Mssql.Tests.Integration' -Filter (Get-Content "$scratch\t7-filter-Mssql.txt" -Raw).Trim()
        }
        finally { docker rm -f $name | Out-Null }
    }
}
"done" | Tee-Object -FilePath "$out\lane.log" -Append
