# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Explicit live Admission/Lifecycle selection only; excluded from Contract's Cdc*.Tests.ps1 glob.
# Uses the shipped wrappers on an exclusively owned local stack. Retains private files on failure.
param(
    [ValidateSet('Postgresql', 'Mssql')][string] $Provider = 'Postgresql',
    [ValidateSet('Setup', 'Lifecycle')][string] $Procedure = 'Setup'
)

Describe '<Provider> live runbook <Procedure>' -ForEach @(@{ Provider = $Provider; Procedure = $Procedure }) {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'cdc-runbook-snippets.ps1')
        $script:repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $script:repo 'eng/docker-compose/env-utility.psm1') -DisableNameChecking
        if ($env:CDC_RUNBOOK_OWNED_STACK -ne '1') { throw 'EnvironmentUnavailable: CDC_RUNBOOK_OWNED_STACK=1 requires an exclusively owned disposable local stack.' }
        if ($IsWindows) { throw 'EnvironmentUnavailable: live runbook setup requires Linux ownership checks.' }
        if (Test-Path (Join-Path $script:repo 'eng/docker-compose/.bootstrap')) { throw 'EnvironmentUnavailable: archive an already retired bootstrap workspace before starting the fresh fixture.' }
        $existing = @(& docker ps -aq --filter label=com.docker.compose.project=dms-local)
        if ($LASTEXITCODE -ne 0 -or $existing.Count) { throw 'EnvironmentUnavailable: the live fixture requires no existing dms-local containers.' }
        $volumes = @(& docker volume ls -q --filter label=com.docker.compose.project=dms-local)
        if ($LASTEXITCODE -ne 0 -or $volumes.Count) { throw 'EnvironmentUnavailable: retire and remove the previous owned volumes before preparing fresh credentials.' }
        $published = @(& docker ps -aq --filter label=com.docker.compose.project=dms-published)
        $publishedVolumes = @(& docker volume ls -q --filter label=com.docker.compose.project=dms-published)
        if ($LASTEXITCODE -ne 0 -or $published.Count -or $publishedVolumes.Count) { throw 'EnvironmentUnavailable: published fixture requires no existing dms-published resources.' }
        $script:originalLocation = Get-Location
        Set-Location $script:repo
        $script:results = [Collections.Generic.List[object]]::new()
        function Invoke-PrivateScript {
            param([string] $Id, [string] $Code, [int] $TimeoutSeconds = 600)
            $path = Join-Path $script:fixture ($Id + '.ps1')
            $Code | Set-Content -LiteralPath $path
            & chmod 600 $path
            $result = Invoke-NativeCommandWithInput -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $path) -InputText '' -TimeoutSeconds $TimeoutSeconds
            $result.StandardOutput | Set-Content (Join-Path $script:fixture "$Id.stdout")
            $result.StandardError | Set-Content (Join-Path $script:fixture "$Id.stderr")
            & chmod 600 (Join-Path $script:fixture "$Id.stdout") (Join-Path $script:fixture "$Id.stderr")
            $result.FailureKind | Should -Be 'None' -Because "the private $Id process must complete"
            return $result
        }
    }

    BeforeEach {
        foreach ($ownedProject in @('dms-local', 'dms-published')) {
            $remaining = @(& docker ps -aq --filter "label=com.docker.compose.project=$ownedProject")
            if ($LASTEXITCODE -ne 0 -or $remaining.Count) { throw 'A preceding live case retained its deployment; preserve it for diagnosis instead of preparing another environment.' }
            $volumes = @(& docker volume ls -q --filter "label=com.docker.compose.project=$ownedProject")
            if ($LASTEXITCODE -ne 0 -or $volumes.Count) { throw 'A preceding live case retained its volumes; preserve them instead of generating new credentials.' }
        }
    }

    It 'CDC-DOC <Id>' -ForEach @(
        if ($Procedure -eq 'Lifecycle') {
            @{ Id = 'cdc-managed-start'; E2e = $false; Published = $false; SqlServer = ($Provider -eq 'Mssql') }
        } elseif ($Provider -eq 'Postgresql') {
            @{ Id = 'cdc-pg-bootstrap-local'; E2e = $false; Published = $false; SqlServer = $false }
            @{ Id = 'cdc-pg-e2e-setup'; E2e = $true; Published = $false; SqlServer = $false }
        } else {
            @{ Id = 'cdc-sqlserver-bootstrap-local'; E2e = $false; Published = $false; SqlServer = $true }
            @{ Id = 'cdc-sqlserver-bootstrap-published'; E2e = $false; Published = $true; SqlServer = $true }
            @{ Id = 'cdc-sqlserver-e2e-setup'; E2e = $true; Published = $false; SqlServer = $true }
        }
    ) {
        $prefix = if ($SqlServer) { 'cdc-sqlserver' } else { 'cdc-pg' }
        $providerName = if ($SqlServer) { 'sqlserver' } else { 'postgresql' }
        $stateName = if ($SqlServer) { 'state-sqlserver' } else { 'state-pg' }
        $project = if ($Published) { 'dms-published' } else { 'dms-local' }
        $script:fixture = Join-Path ([IO.Path]::GetTempPath()) ('cdc-runbook-' + [guid]::NewGuid().ToString('N'))
        $local = Join-Path $script:fixture '.local/cdc'
        $null = New-Item -ItemType Directory -Path $local -Force
        & chmod 700 $script:fixture (Split-Path $local) $local
        $source = if ($E2e) { '.env.e2e' } else { '.env.example' }
        $environmentName = if ($E2e) { '.env.e2e' } else { '.env' }
        $environmentFile = Join-Path $script:fixture $environmentName
        Copy-Item (Join-Path $script:repo "eng/docker-compose/$source") $environmentFile
        $password = [guid]::NewGuid().ToString('N') + 'Ab1!'
        $connectorPassword = [guid]::NewGuid().ToString('N') + 'Ab1!'
        $environmentText = Get-Content $environmentFile -Raw
        $fixtureValues = @{ POSTGRES_USER = 'postgres'; POSTGRES_PASSWORD = $password; POSTGRES_PORT = '5435'; CDC_DATABASE_PASSWORD = $connectorPassword }
        $providerImage = if ($SqlServer) { $env:CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE } else { $env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE }
        $providerImage | Should -Not -BeNullOrEmpty
        $imageVariable = if ($SqlServer) { 'MSSQL_IMAGE' } else { 'POSTGRES_IMAGE' }
        $fixtureValues[$imageVariable] = $providerImage
        $databaseContainer = if ($SqlServer) { 'dms-mssql' } else { 'dms-postgresql' }
        $providerImageId = & docker image inspect $providerImage --format '{{.Id}}'
        $LASTEXITCODE | Should -Be 0
        $providerImageId | Should -Match '^sha256:[a-f0-9]{64}$'
        # Cold plugin scans need headroom on high-core qualification hosts. Keep the
        # declared worker policy and Compose heap identical from initial creation.
        if ($Procedure -eq 'Lifecycle') { $fixtureValues.CDC_WORKER_HEAP_MIB = '1024' }
        if ($SqlServer) {
            $fixtureValues.MSSQL_SA_PASSWORD = $password; $fixtureValues.MSSQL_PORT = '1435'
            $fixtureValues.DMS_DATASTORE = 'mssql'; $fixtureValues.DMS_CONFIG_DATASTORE = 'mssql'
        }
        if ($Published) {
            # Fixture-packaged branch images exercise the published wrapper with matching code/schema.
            $fixtureValues.DMS_IMAGE_TAG = 'cdc-runbook-' + [guid]::NewGuid().ToString('N')
            foreach ($pair in @{ 'ed-fi-api-local' = 'edfialliance/ed-fi-api'; 'ed-fi-api-config-local' = 'edfialliance/ed-fi-api-configuration-service' }.GetEnumerator()) {
                & docker tag ($pair.Key + ':latest') ($pair.Value + ':' + $fixtureValues.DMS_IMAGE_TAG)
                $LASTEXITCODE | Should -Be 0
            }
        }
        foreach ($pair in $fixtureValues.GetEnumerator()) {
            $pattern = '(?m)^' + [regex]::Escape($pair.Key) + '=.*$'
            $assignment = "$($pair.Key)=$($pair.Value)"
            if ([regex]::IsMatch($environmentText, $pattern)) { $environmentText = [regex]::Replace($environmentText, $pattern, $assignment) }
            else { $environmentText += "`n$assignment`n" }
        }
        $environmentText | Set-Content $environmentFile
        & chmod 600 $environmentFile
        $values = ReadValuesFromEnvFile $environmentFile
        $database = if ($E2e) { $values.E2E_DATABASE_NAME } else { 'edfi_cdc' }
        $suffix = if ($E2e) { '-e2e' } else { '' }
        $settingsPath = Join-Path $local "$providerName$suffix.json"
        $statePath = Join-Path $local "$stateName$suffix"
        # Exact infrastructure snippet, with its declared environment-path substitution.
        $infra = (Get-CdcRunbookCode "$prefix-infrastructure").Replace('./eng/docker-compose/.env', $environmentFile)
        if ($Published) { $infra = $infra.Replace('start-local-dms.ps1', 'start-published-dms.ps1') }
        (Invoke-PrivateScript "$prefix-infrastructure" $infra).ExitCode | Should -Be 0
        (& docker inspect $databaseContainer --format '{{.Config.Image}}') | Should -Be $providerImage
        $LASTEXITCODE | Should -Be 0
        (& docker inspect $databaseContainer --format '{{.Image}}') | Should -Be $providerImageId
        $LASTEXITCODE | Should -Be 0
        # Deployment-owned role preparation only. No target database, capture artifact or grant repair.
        function Invoke-FixtureSql {
            param([string] $Sql, [string] $Database = 'master')
            # Container-owned secret, never a password argument or output artifact.
            return Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-mssql', 'sh', '-c',
                'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -d "$1"', 'sqlcmd', $Database) -InputText ("SET NOCOUNT ON;`n" + $Sql)
        }
        if ($SqlServer) {
            $server = (& docker inspect dms-mssql | ConvertFrom-Json)[0]
            ($server.Config.Env -ccontains "MSSQL_SA_PASSWORD=$password") | Should -BeTrue -Because 'the effective engine overlay must preserve the declared fixture credential'
            $server = $null
            $sql = "IF DB_ID(N'$database') IS NOT NULL THROW 51000, 'Target must be absent', 1; CREATE LOGIN cdc_reader WITH PASSWORD = '$connectorPassword';"
            $role = Invoke-FixtureSql $sql
        } else {
            $sql = "CREATE ROLE cdc_reader WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$connectorPassword';"
            $role = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1') -InputText $sql
        }
        $role.FailureKind | Should -Be 'None'
        $role.ExitCode | Should -Be 0
        $base = Get-Content (Join-Path $script:repo 'src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json') -Raw | ConvertFrom-Json -AsHashtable
        $base.ConfigurationServiceSettings.BaseUrl = 'http://127.0.0.1:8081'
        $base.ConfigurationServiceSettings.ClientId = $values.CONFIG_SERVICE_CLIENT_ID
        $base.ConfigurationServiceSettings.ClientSecret = $values.CONFIG_SERVICE_CLIENT_SECRET
        $base.ConfigurationServiceSettings.Scope = $values.CONFIG_SERVICE_CLIENT_SCOPE
        $base.ConfigurationServiceSettings.EncryptionKey = $values.DMS_CONFIG_DATABASE_ENCRYPTION_KEY
        $base.AppSettings.AuthenticationService = 'http://127.0.0.1:8081/connect/token'
        $base.JwtAuthentication.Authority = 'http://127.0.0.1:8081'
        $base.JwtAuthentication.MetadataAddress = 'http://127.0.0.1:8081/.well-known/openid-configuration'
        $base | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $local 'dms-base.json')
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder['Database'] = $database; $builder['Password'] = $password
        if ($SqlServer) {
            $builder['Server'] = '127.0.0.1,1435'; $builder['User Id'] = 'sa'
            $builder['Encrypt'] = 'true'; $builder['TrustServerCertificate'] = 'true'; $builder['Command Timeout'] = '180'
        } else {
            $builder['Host'] = '127.0.0.1'; $builder['Port'] = '5435'; $builder['Username'] = 'postgres'
        }
        $inputPath = Join-Path $script:fixture 'setup-connection.txt'
        $builder.ConnectionString | Set-Content $inputPath
        & chmod 600 $inputPath (Join-Path $local 'dms-base.json')
        $settings = Get-CdcRunbookCode "$prefix-settings"
        $settings = $settings.Replace("'.local/cdc/", "'$local/").Replace("'./.local/cdc/", "'$local/")
        $settings = $settings.Replace("Join-Path `$repoRoot '$local/", "'$local/")
        $settings = $settings.Replace('./eng/docker-compose/.env', $environmentFile).Replace('edfi_cdc', $database)
        $settings = $settings.Replace("$providerName.json", "$providerName$suffix.json").Replace("$stateName'", "$stateName$suffix'")
        # Declared supported fixture budgets allow cold broker/worker health checks to finish.
        $settings = $settings.Replace('CallMilliseconds = 30000', 'CallMilliseconds = 120000').Replace('WaitMilliseconds = 300000', 'WaitMilliseconds = 600000')
        if ($Procedure -eq 'Lifecycle') { $settings = $settings.Replace('HeapBytes = 536870912', 'HeapBytes = 1073741824') }
        $settings = "function Read-Host { param([string]`$Prompt, [switch]`$MaskInput) return (Get-Content -LiteralPath '$inputPath' -Raw).Trim() }`n" + $settings
        (Invoke-PrivateScript "$prefix-settings" $settings).ExitCode | Should -Be 0
        Test-Path $settingsPath | Should -BeTrue
        # Cold infrastructure may return an unavailable observation. Retry only the
        # intact initial workflow, never a generation that authorized writers.
        $maximumAttempts = if ($E2e) { 1 } else { 3 }
        for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
            $setupId = if ($Procedure -eq 'Lifecycle') { "$prefix-bootstrap-local" } else { $Id }
            $result = Invoke-CdcRunbookLiveWrapper -Id $setupId -FixtureRoot $script:fixture
            if ($result.ExitCode -ne 1 -or $result.FailureKind -ne 'None') { break }
            $journalPath = Join-Path $statePath 'workflows/local/datastore-1/1.json'
            if (-not (Test-Path $journalPath)) { break }
            $retryJournal = Get-Content $journalPath -Raw | ConvertFrom-Json
            if (@($retryJournal.operations | Where-Object effect -eq 'AuthorizeWriterPublication').Count) { break }
        }
        $result.FailureKind | Should -Be 'None'
        $result.ExitCode | Should -Be 0 -Because 'only the shipped wrapper may authorize and launch writers'
        $inventoryPath = Join-Path $script:repo "eng/docker-compose/.cdc-deployments/$project.json"
        $inventory = Get-Content $inventoryPath -Raw | ConvertFrom-Json
        $inventory.Entries.Count | Should -Be 1
        $entry = $inventory.Entries[0]
        (ReadValuesFromEnvFile $inventory.EnvironmentFile)[$imageVariable] | Should -Be $providerImage
        (& docker inspect $databaseContainer --format '{{.Config.Image}}') | Should -Be $providerImage
        $LASTEXITCODE | Should -Be 0
        (& docker inspect $databaseContainer --format '{{.Image}}') | Should -Be $providerImageId
        $LASTEXITCODE | Should -Be 0
        $entry.StatePath | Should -Be $statePath
        $journal = Get-Content (Join-Path $statePath 'workflows/local/datastore-1/1.json') -Raw | ConvertFrom-Json
        # Publication authority is the atomic intent itself, not a later completion reply.
        @($journal.operations | Where-Object effect -eq 'AuthorizeWriterPublication').Count | Should -Be 1
        $retained = Get-Content $entry.SettingsPath -Raw | ConvertFrom-Json
        $retained.Cdc.DataStoreId | Should -Be '1'
        $retained.DataManagement.DocumentCache.Targets[0].DataStoreId | Should -Be 1
        $databaseProperty = if ($SqlServer) { 'database.names' } else { 'database.dbname' }
        $retained.Cdc.ProviderConnectionProperties.$databaseProperty | Should -Be $database
        $projects = @($retained.Cdc.Schemas | ForEach-Object { (Get-Content $_ -Raw | ConvertFrom-Json).projectSchema.projectName } | Sort-Object)
        $projects | Should -Be $(if ($E2e) { @('Ed-Fi', 'Homograph', 'Sample', 'TPDM') } else { @('Ed-Fi', 'TPDM') })
        (Invoke-WebRequest 'http://127.0.0.1:8080/health').StatusCode | Should -Be 200
        $qualified = Get-Content (Join-Path $script:repo 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') -Raw | ConvertFrom-Json
        (& docker inspect "$project-kafka-cdc-worker-1" --format '{{.Config.Image}}') | Should -Be $qualified.image
        if ($SqlServer) {
            $capture = Invoke-FixtureSql -Database $database -Sql @'
SELECT STRING_AGG(OBJECT_NAME(source_object_id), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(source_object_id)) FROM cdc.change_tables;
SELECT COUNT(*) FROM cdc.change_tables WHERE source_object_id = OBJECT_ID(N'dms.DocumentProjectionWork');
SELECT COUNT(*) FROM sys.database_principals WHERE name = N'cdc_reader' AND type = 'S' AND sid = SUSER_SID(N'cdc_reader');
EXECUTE AS LOGIN = 'cdc_reader';
SELECT CONCAT(IS_SRVROLEMEMBER('sysadmin'), ',', IS_ROLEMEMBER('db_owner'), ',',
 HAS_PERMS_BY_NAME('dms.Document', 'OBJECT', 'SELECT'), ',', HAS_PERMS_BY_NAME('dms.DocumentCache', 'OBJECT', 'SELECT'), ',',
 HAS_PERMS_BY_NAME('dms.CdcHeartbeat', 'OBJECT', 'SELECT'), ',', HAS_PERMS_BY_NAME('dms.Document', 'OBJECT', 'INSERT'), ',',
 HAS_PERMS_BY_NAME('dms.DocumentCache', 'OBJECT', 'UPDATE'), ',', HAS_PERMS_BY_NAME('dms.DocumentProjectionWork', 'OBJECT', 'SELECT'), ',',
 HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL'));
REVERT;
'@
            $capture.ExitCode | Should -Be 0
            ($capture.StandardOutput.Trim() -split '\r?\n') | Should -Be @('CdcHeartbeat,Document,DocumentCache', '0', '1', '0,0,1,1,1,0,0,0,0')
        } else {
            $capture = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', $database, '-At', '-v', 'ON_ERROR_STOP=1') -InputText "SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_publication_tables; SELECT count(*) FROM pg_publication_tables WHERE tablename = 'DocumentProjectionWork';"
            $capture.ExitCode | Should -Be 0
            ($capture.StandardOutput.Trim() -split '\r?\n') | Should -Be @('CdcHeartbeat,Document,DocumentCache', '0')
        }
        foreach ($observation in @("$prefix-status", "$prefix-watch")) {
            $configuration = if ($env:CDC_RUNBOOK_CONFIGURATION) { $env:CDC_RUNBOOK_CONFIGURATION } else { 'Release' }
            $tool = Join-Path $script:repo "src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/$configuration/net10.0/api-schema-tools"
            $code = (Get-CdcRunbookCode $observation).Replace('api-schema-tools', "& '$tool'").Replace('<retained-settings-path>', $entry.SettingsPath).Replace('<original-state-root>', $statePath).Replace('--maximum-passes 20', '--maximum-passes 2')
            $observed = Invoke-PrivateScript $observation ($code + "`nexit `$LASTEXITCODE")
            $output = $observed.StandardOutput | ConvertFrom-Json
            $observed.ExitCode | Should -Be 1
            $output.exitCode | Should -Be $observed.ExitCode
            $output.operation | Should -Be $(if ($observation -like '*watch') { 'watch' } else { 'status' })
            $output.deploymentProfile.aclIsolationProven | Should -BeFalse
            $output.data.targets[0].status.providerSetup.state | Should -Be 'Satisfied'
            $output.data.targets[0].status.connectorRuntime.state | Should -Be 'Satisfied'
            # Standalone status cannot claim the hosted worker's operational health.
            $output.data.targets[0].status.projection.state | Should -Be 'Unknown'
            $output.data.aggregate.readiness | Should -Be 'NotReady'
        }
        if ($Procedure -eq 'Lifecycle') {
            # Reuse the established deployment and custom root. Each command below is
            # extracted from its marked operator example; faults affect only this fixture.
            . (Join-Path $PSScriptRoot 'cdc-runbook-lifecycle.ps1')
            Invoke-CdcRunbookLifecycle -Entry $entry -InventoryPath $inventoryPath -StatePath $statePath -Project $project -FixtureRoot $script:fixture -Provider $Provider
            (ReadValuesFromEnvFile $inventory.EnvironmentFile)[$imageVariable] | Should -Be $providerImage
            (& docker inspect $databaseContainer --format '{{.Config.Image}}') | Should -Be $providerImage
            $LASTEXITCODE | Should -Be 0
            (& docker inspect $databaseContainer --format '{{.Image}}') | Should -Be $providerImageId
            $LASTEXITCODE | Should -Be 0
        }
        if ($E2e -and $SqlServer) {
            $snapshot = Invoke-FixtureSql -Database $values.E2E_SNAPSHOT_DATABASE_NAME -Sql 'SELECT COUNT(*) FROM dms.EffectiveSchema; SELECT COUNT(*) FROM sys.database_principals WHERE name = N''cdc_reader'';'
            $snapshot.ExitCode | Should -Be 0
            ($snapshot.StandardOutput.Trim() -split '\r?\n') | Should -Be @('1', '0')
        }
        if ($E2e) {
            $smokeCode = @"
`$env:AppSettings__DatabaseEngine = 'postgresql'
`$env:AppSettings__DmsContainerName = 'ed-fi-api'
`$env:AppSettings__DataStoreAdminConnectionString = (Get-Content -LiteralPath '$inputPath' -Raw).Trim()
`$b = [System.Data.Common.DbConnectionStringBuilder]::new()
`$b.ConnectionString = `$env:AppSettings__DataStoreAdminConnectionString
`$b['Host'] = 'dms-postgresql'; `$b['Port'] = '5432'
`$env:AppSettings__DataStoreConnectionString = `$b.ConnectionString
`$b['Database'] = '$($values.E2E_SNAPSHOT_DATABASE_NAME)'
`$env:AppSettings__DataStoreSnapshotConnectionString = `$b.ConnectionString
"@
            if ($SqlServer) {
                $smokeCode = $smokeCode.Replace("= 'postgresql'", "= 'mssql'").Replace("`$b['Host'] = 'dms-postgresql'; `$b['Port'] = '5432'", "`$b['Server'] = 'dms-mssql,1433'")
            }
            $smokeCode += "`n" + (Get-CdcRunbookCode 'cdc-pg-e2e-test').Replace('edfi_datamanagementservice_e2e', $database)
            $smoke = Invoke-PrivateScript 'cdc-pg-e2e-test' $smokeCode
            $smoke.ExitCode | Should -Be 0
            $smoke.StandardOutput | Should -Match 'Passed:\s+2'
            $smoke.StandardOutput | Should -Match 'Skipped:\s+0'
        }
        $applicationContainers = @(& docker ps -q --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=dms')
        $LASTEXITCODE | Should -Be 0
        $applicationContainers.Count | Should -Be 1
        $applicationImage = & docker inspect $applicationContainers[0] --format '{{.Image}}'
        $LASTEXITCODE | Should -Be 0
        $applicationImage | Should -Match '^sha256:[a-f0-9]{64}$'
        $configurationImage = & docker inspect ed-fi-api-config-service --format '{{.Image}}'
        $LASTEXITCODE | Should -Be 0
        $configurationImage | Should -Match '^sha256:[a-f0-9]{64}$'
        $script:results.Add(@{ TestId = "CDC-DOC $Id"; SnippetId = $Id; Outcome = 'Passed' })
        # Fixed assertions and image IDs only; no settings, SIDs, credentials or raw output.
        @{
            SnippetId = $Id; WrapperAttempts = $attempt; Provider = $providerName
            TargetId = 1; Generation = 1; Database = $database; SchemaProjects = $projects
            InitialDatabaseAbsenceChecked = $SqlServer; WriterPublicationAuthorized = $true
            LoginUserSidMatched = $SqlServer; NarrowEffectiveAccessVerified = $SqlServer
            WorkTableCaptured = $false; StatusExitCode = 1; WatchExitCode = 1
            Projection = 'Unknown'; AggregateReadiness = 'NotReady'
            ProviderImage = $providerImage; ProviderImageId = $providerImageId
            RetainedProviderImageVerified = $true; ManagedStartupImageVerified = ($Procedure -eq 'Lifecycle')
            ApplicationImage = $applicationImage
            ConfigurationImage = $configurationImage
        } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $script:fixture 'asserted-results.json')
        # Complete governed retirement while services remain reachable; never erase provenance to retry.
        $teardown = Get-CdcRunbookCode 'cdc-stack-teardown'
        if ($Published) { $teardown = $teardown.Replace('bootstrap-local-dms.ps1', 'bootstrap-published-dms.ps1') }
        (Invoke-PrivateScript 'cdc-stack-teardown' $teardown).ExitCode | Should -Be 0
        Test-Path $inventoryPath | Should -BeFalse
        $workspace = Join-Path $script:repo 'eng/docker-compose/.bootstrap'
        if (Test-Path $workspace) { Move-Item $workspace (Join-Path $script:fixture 'retired-bootstrap') }
    }

    AfterAll {
        if ($script:results -and $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY) {
            @{ Cases = @($script:results) } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY "cdc-runbook-live-$($Procedure.ToLowerInvariant()).json")
        }
        if ($script:originalLocation) { Set-Location $script:originalLocation }
    }
}
