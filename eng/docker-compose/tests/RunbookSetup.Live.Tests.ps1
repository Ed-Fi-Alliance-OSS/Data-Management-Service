# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Explicit live Admission selection only; excluded from the Contract's Cdc*.Tests.ps1 glob.
# Uses the shipped wrappers on an exclusively owned local stack. Retains private files on failure.
Describe 'Postgresql live runbook setup' {
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
        $remaining = @(& docker ps -aq --filter label=com.docker.compose.project=dms-local)
        if ($LASTEXITCODE -ne 0 -or $remaining.Count) { throw 'A preceding live case retained its deployment; preserve it for diagnosis instead of preparing another environment.' }
        $volumes = @(& docker volume ls -q --filter label=com.docker.compose.project=dms-local)
        if ($LASTEXITCODE -ne 0 -or $volumes.Count) { throw 'A preceding live case retained its volumes; preserve them instead of generating new credentials.' }
    }

    It 'CDC-DOC <Id>' -ForEach @(
        @{ Id = 'cdc-pg-bootstrap-local'; E2e = $false },
        @{ Id = 'cdc-pg-e2e-setup'; E2e = $true }
    ) {
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
        foreach ($pair in @{ POSTGRES_USER = 'postgres'; POSTGRES_PASSWORD = $password; POSTGRES_PORT = '5435'; CDC_DATABASE_PASSWORD = $connectorPassword }.GetEnumerator()) {
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
        $settingsPath = Join-Path $local "postgresql$suffix.json"
        $statePath = Join-Path $local "state-pg$suffix"
        # Exact infrastructure snippet, with its declared environment-path substitution.
        $infra = (Get-CdcRunbookCode 'cdc-pg-infrastructure').Replace('./eng/docker-compose/.env', $environmentFile)
        (Invoke-PrivateScript 'cdc-pg-infrastructure' $infra).ExitCode | Should -Be 0
        # Deployment-owned role preparation only. No target database, capture artifact or grant repair.
        $sql = "CREATE ROLE cdc_reader WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$connectorPassword';"
        $role = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1') -InputText $sql
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
        $builder['Host'] = '127.0.0.1'; $builder['Port'] = '5435'; $builder['Database'] = $database
        $builder['Username'] = 'postgres'; $builder['Password'] = $password
        $inputPath = Join-Path $script:fixture 'setup-connection.txt'
        $builder.ConnectionString | Set-Content $inputPath
        & chmod 600 $inputPath (Join-Path $local 'dms-base.json')
        $settings = Get-CdcRunbookCode 'cdc-pg-settings'
        $settings = $settings.Replace("'.local/cdc/", "'$local/").Replace("'./.local/cdc/", "'$local/")
        $settings = $settings.Replace("Join-Path `$repoRoot '$local/", "'$local/")
        $settings = $settings.Replace('./eng/docker-compose/.env', $environmentFile).Replace('edfi_cdc', $database)
        $settings = $settings.Replace('postgresql.json', "postgresql$suffix.json").Replace("state-pg'", "state-pg$suffix'")
        # Declared supported fixture budgets allow cold broker/worker health checks to finish.
        $settings = $settings.Replace('CallMilliseconds = 30000', 'CallMilliseconds = 120000').Replace('WaitMilliseconds = 300000', 'WaitMilliseconds = 600000')
        $settings = "function Read-Host { param([string]`$Prompt, [switch]`$MaskInput) return (Get-Content -LiteralPath '$inputPath' -Raw).Trim() }`n" + $settings
        (Invoke-PrivateScript 'cdc-pg-settings' $settings).ExitCode | Should -Be 0
        Test-Path $settingsPath | Should -BeTrue
        # Cold infrastructure may return an unavailable observation. Retry only the
        # intact initial workflow, never a generation that authorized writers.
        $maximumAttempts = if ($E2e) { 1 } else { 3 }
        for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
            $result = Invoke-CdcRunbookLiveWrapper -Id $Id -FixtureRoot $script:fixture
            if ($result.ExitCode -ne 1 -or $result.FailureKind -ne 'None') { break }
            $journalPath = Join-Path $statePath 'workflows/local/datastore-1/1.json'
            if (-not (Test-Path $journalPath)) { break }
            $retryJournal = Get-Content $journalPath -Raw | ConvertFrom-Json
            if (@($retryJournal.operations | Where-Object effect -eq 'AuthorizeWriterPublication').Count) { break }
        }
        $result.FailureKind | Should -Be 'None'
        $result.ExitCode | Should -Be 0 -Because 'only the shipped wrapper may authorize and launch writers'
        $inventoryPath = Join-Path $script:repo 'eng/docker-compose/.cdc-deployments/dms-local.json'
        $inventory = Get-Content $inventoryPath -Raw | ConvertFrom-Json
        $inventory.Entries.Count | Should -Be 1
        $entry = $inventory.Entries[0]
        $entry.StatePath | Should -Be $statePath
        $journal = Get-Content (Join-Path $statePath 'workflows/local/datastore-1/1.json') -Raw | ConvertFrom-Json
        # Publication authority is the atomic intent itself, not a later completion reply.
        @($journal.operations | Where-Object effect -eq 'AuthorizeWriterPublication').Count | Should -Be 1
        $retained = Get-Content $entry.SettingsPath -Raw | ConvertFrom-Json
        $retained.Cdc.DataStoreId | Should -Be '1'
        $retained.DataManagement.DocumentCache.Targets[0].DataStoreId | Should -Be 1
        $retained.Cdc.ProviderConnectionProperties.'database.dbname' | Should -Be $database
        $projects = @($retained.Cdc.Schemas | ForEach-Object { (Get-Content $_ -Raw | ConvertFrom-Json).projectSchema.projectName } | Sort-Object)
        $projects | Should -Be $(if ($E2e) { @('Ed-Fi', 'Homograph', 'Sample', 'TPDM') } else { @('Ed-Fi', 'TPDM') })
        (Invoke-WebRequest 'http://127.0.0.1:8080/health').StatusCode | Should -Be 200
        $qualified = Get-Content (Join-Path $script:repo 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') -Raw | ConvertFrom-Json
        (& docker inspect dms-local-kafka-cdc-worker-1 --format '{{.Config.Image}}') | Should -Be $qualified.image
        $capture = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', $database, '-At', '-v', 'ON_ERROR_STOP=1') -InputText "SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_publication_tables; SELECT count(*) FROM pg_publication_tables WHERE tablename = 'DocumentProjectionWork';"
        $capture.ExitCode | Should -Be 0
        ($capture.StandardOutput.Trim() -split '\r?\n') | Should -Be @('CdcHeartbeat,Document,DocumentCache', '0')
        foreach ($observation in @('cdc-pg-status', 'cdc-pg-watch')) {
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
            $smokeCode += "`n" + (Get-CdcRunbookCode 'cdc-pg-e2e-test').Replace('edfi_datamanagementservice_e2e', $database)
            $smoke = Invoke-PrivateScript 'cdc-pg-e2e-test' $smokeCode
            $smoke.ExitCode | Should -Be 0
            $smoke.StandardOutput | Should -Match 'Passed:\s+2'
            $smoke.StandardOutput | Should -Match 'Skipped:\s+0'
        }
        $script:results.Add(@{ TestId = "CDC-DOC $Id"; SnippetId = $Id; Outcome = 'Passed' })
        # Complete governed retirement while services remain reachable; never erase provenance to retry.
        $teardown = Get-CdcRunbookCode 'cdc-stack-teardown'
        (Invoke-PrivateScript 'cdc-stack-teardown' $teardown).ExitCode | Should -Be 0
        Test-Path $inventoryPath | Should -BeFalse
        $workspace = Join-Path $script:repo 'eng/docker-compose/.bootstrap'
        if (Test-Path $workspace) { Move-Item $workspace (Join-Path $script:fixture 'retired-bootstrap') }
    }

    AfterAll {
        if ($script:results -and $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY) {
            @{ Cases = @($script:results) } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'cdc-runbook-live-setup.json')
        }
        if ($script:originalLocation) { Set-Location $script:originalLocation }
    }
}
