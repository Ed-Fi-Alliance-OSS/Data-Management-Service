# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Selected only by the live fixture, after production bootstrap. No lifecycle implementation.
function Invoke-CdcRunbookLifecycle {
    param($Entry, [string] $InventoryPath, [string] $StatePath, [string] $Project, [string] $FixtureRoot, [ValidateSet('Postgresql', 'Mssql')][string] $Provider = 'Postgresql')

    $configuration = if ($env:CDC_RUNBOOK_CONFIGURATION) { $env:CDC_RUNBOOK_CONFIGURATION } else { 'Release' }
    $tool = Join-Path $script:repo "src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/$configuration/net10.0/api-schema-tools"
    $settingsBytes = [IO.File]::ReadAllBytes($Entry.SettingsPath)
    $settingsHash = (Get-FileHash $Entry.SettingsPath).Hash
    $bindingPath = @(Get-ChildItem (Join-Path $StatePath 'bindings') -Filter '*.json' -Recurse).FullName
    @($bindingPath).Count | Should -Be 1
    $bindingHash = (Get-FileHash $bindingPath).Hash
    $commandSettingsPath = $Entry.SettingsPath
    $results = [Collections.Generic.List[object]]::new()
    function Invoke-MarkedCommand {
        param([string] $Id)
        $code = (Get-CdcRunbookCode $Id).Replace('api-schema-tools', "& '$tool'").Replace('<retained-settings-path>', $commandSettingsPath).Replace('<original-state-root>', $StatePath)
        $result = Invoke-PrivateScript $Id ($code + "`nexit `$LASTEXITCODE")
        $json = $result.StandardOutput | ConvertFrom-Json
        $json.exitCode | Should -Be $result.ExitCode
        $expectedOperation = switch ($Id) {
            'cdc-intact-restart' { 'restart' }
            'cdc-intact-resume' { 'resume' }
            'cdc-disclosure-containment-result' { 'stop' }
            default { 'validate' }
        }
        $json.operation | Should -Be $expectedOperation
        return $json
    }
    $inventoryCode = (Get-CdcRunbookCode 'cdc-state-inventory').Replace('<original-state-root>', $StatePath).Replace('<retained-settings-path>', $Entry.SettingsPath).Replace('<deployment-inventory-path>', $InventoryPath).Replace('<retained-bootstrap-root>', (Join-Path $script:repo 'eng/docker-compose/.bootstrap'))
    (Invoke-PrivateScript 'cdc-state-inventory' $inventoryCode).ExitCode | Should -Be 0

    # A successful complete shared-worker stop is recorded separately from a binding result.
    (Invoke-CdcRunbookLiveWrapper -Id 'cdc-managed-stop' -FixtureRoot $FixtureRoot).ExitCode | Should -Be 0
    $stopped = Get-Content $InventoryPath -Raw | ConvertFrom-Json
    $stopped.Phase | Should -Be 'Stopped'
    $stopped.Entries[0].StatePath | Should -Be $StatePath
    (& docker inspect "$Project-kafka-cdc-worker-1" --format '{{.State.Running}}') | Should -Be 'false'
    $LASTEXITCODE | Should -Be 0
    (Get-FileHash $Entry.SettingsPath).Hash | Should -Be $settingsHash
    (Invoke-CdcRunbookLiveWrapper -Id 'cdc-managed-start' -FixtureRoot $FixtureRoot).ExitCode | Should -Be 0
    (Get-Content $InventoryPath -Raw | ConvertFrom-Json).Phase | Should -Be 'Active'
    (Get-FileHash $Entry.SettingsPath).Hash | Should -Be $settingsHash
    (Invoke-WebRequest 'http://127.0.0.1:8080/health').StatusCode | Should -Be 200
    foreach ($id in @('cdc-intact-restart', 'cdc-intact-resume')) {
        $result = Invoke-MarkedCommand $id
        $result.exitCode | Should -Be 0
        $result.succeeded | Should -BeTrue
        $result.data.ready | Should -BeTrue
        $results.Add(@{ SnippetId = $id; ExitCode = $result.exitCode; Ready = $result.data.ready })
    }
    $validation = Invoke-MarkedCommand 'cdc-validate'
    # Standalone validation observes runtime availability separately from lifecycle processing.
    $validation.exitCode | Should -Be 1
    $validation.succeeded | Should -BeFalse
    $validation.data.preStartEligible | Should -BeTrue
    $validation.data.publicationReady | Should -BeFalse
    @($validation.diagnostics | Where-Object { $_.component -eq 'Projection' -and $_.failure -eq 'Unavailable' }).Count | Should -BeGreaterThan 0
    $results.Add(@{ SnippetId = 'cdc-validate'; ExitCode = $validation.exitCode; Succeeded = $validation.succeeded; PreStartEligible = $validation.data.preStartEligible; PublicationReady = $validation.data.publicationReady; Diagnostics = @($validation.diagnostics | Select-Object component, failure) })

    $stop = Invoke-MarkedCommand 'cdc-disclosure-containment-result'
    $stop.exitCode | Should -Be 0
    $stop.data.targetShutdownVerified | Should -BeTrue
    (Get-Content $InventoryPath -Raw | ConvertFrom-Json).Phase | Should -Be 'Active'
    (& docker inspect "$Project-kafka-cdc-worker-1" --format '{{.State.Running}}') | Should -Be 'true'
    $LASTEXITCODE | Should -Be 0
    $settings = Get-Content $Entry.SettingsPath -Raw | ConvertFrom-Json
    # The initial inventory object predates the lifecycle handoff's connector-name
    # readback. Use the verified result and compare with freshly persisted inventory.
    $connectorName = $stop.binding.connectorName
    $connectorName | Should -Not -BeNullOrEmpty
    (Get-Content $InventoryPath -Raw | ConvertFrom-Json).Entries[0].ConnectorName | Should -Be $connectorName
    $connectorUri = $settings.Cdc.ConnectEndpoint.TrimEnd('/') + '/connectors/' + $connectorName
    $offset = Invoke-RestMethod -Uri "$connectorUri/offsets" -TimeoutSec 10 | ConvertTo-Json -Depth 30 -Compress
    $results.Add(@{ SnippetId = 'cdc-disclosure-containment-result'; ExitCode = 0; TargetShutdownVerified = $true; SharedWorkerRunning = $true })
    # Reversible fixture faults: restoration is test isolation, never an operator recovery recipe.
    foreach ($directory in @('bindings', 'workflows', 'source-history')) {
        $path = @(Get-ChildItem (Join-Path $StatePath $directory) -Filter '*.json' -Recurse).FullName
        @($path).Count | Should -Be 1
        $original = [IO.File]::ReadAllBytes($path)
        foreach ($fault in @('missing', 'corrupt', 'unsafe-permissions', 'contradictory')) {
            try {
                switch ($fault) {
                    missing { Remove-Item $path }
                    corrupt { [IO.File]::WriteAllText($path, '{private-fixture-sentinel') }
                    unsafe-permissions { & chmod 640 $path }
                    contradictory {
                        $node = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json -AsHashtable
                        if ($directory -eq 'bindings') { $node.partitionCount = 2 }
                        elseif ($directory -eq 'source-history') { $node.physicalSourceFingerprint = 'sha256:' + ('f' * 64) }
                        else { $node.target.instanceKey = 'contradictory' }
                        $node | ConvertTo-Json -Depth 100 | Set-Content $path
                    }
                }
                foreach ($id in @('cdc-provenance-rejection', 'cdc-intact-resume')) {
                    $result = Invoke-MarkedCommand $id
                    $result.exitCode | Should -Be 1
                    $result.succeeded | Should -BeFalse
                    @($result.diagnostics).Count | Should -BeGreaterThan 0
                    ($result | ConvertTo-Json -Depth 100) | Should -Not -Match 'private-fixture-sentinel'
                    $status = Invoke-RestMethod -Uri "$connectorUri/status" -TimeoutSec 10
                    $status.connector.state | Should -Be 'STOPPED'
                    @($status.tasks).Count | Should -Be 0
                    (Invoke-RestMethod -Uri "$connectorUri/offsets" -TimeoutSec 10 | ConvertTo-Json -Depth 30 -Compress) | Should -Be $offset
                    $results.Add(@{ SnippetId = $id; Scenario = "$directory-$fault"; ExitCode = $result.exitCode; Diagnostics = @($result.diagnostics | Select-Object component, failure) })
                }
            }
            finally {
                [IO.File]::WriteAllBytes($path, $original)
                & chmod 600 $path
            }
        }
    }
    # Existing physical-source rejection seam, exercised through the marked commands.
    # These independent fixture databases are destroyed with the owned stack; no
    # operator adoption, source identity repair, or replacement workflow is offered.
    function Invoke-OwnedSql {
        param([string] $Database, [string] $Sql)
        $result = if ($Provider -eq 'Postgresql') {
            Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-d', $Database, '-At', '-v', 'ON_ERROR_STOP=1') -InputText $Sql
        } else {
            # sqlcmd defaults differ from SqlClient; indexed projection objects require this SET option.
            Invoke-FixtureSql -Database $Database -Sql ("SET QUOTED_IDENTIFIER ON;`n" + $Sql)
        }
        $result.FailureKind | Should -Be 'None'
        $result.ExitCode | Should -Be 0
        return $result.StandardOutput.Trim()
    }
    $sourceDatabase = if ($Provider -eq 'Postgresql') { $settings.Cdc.ProviderConnectionProperties.'database.dbname' } else { $settings.Cdc.ProviderConnectionProperties.'database.names' }
    $countSql = if ($Provider -eq 'Postgresql') { 'SELECT count(*) FROM dms."Document";' } else { 'SELECT count(*) FROM dms.[Document];' }
    $identitySql = if ($Provider -eq 'Postgresql') { 'SELECT "SourceIdentity" FROM dms."DataStoreIdentity";' } else { 'SELECT CONVERT(varchar(36), SourceIdentity) FROM dms.DataStoreIdentity;' }
    (Invoke-OwnedSql $sourceDatabase $countSql) | Should -Be '0'
    if ($Provider -eq 'Postgresql') {
        $dump = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', 'dms-postgresql', 'pg_dump', '-U', 'postgres', $sourceDatabase) -InputText ''
        $dump.FailureKind | Should -Be 'None'
        $dump.ExitCode | Should -Be 0
    } else {
        # Fixture-only clones, with a different source identity, exercise rejection.
        # Restoring a backup is not an operator continuity/recovery procedure.
        $backupPath = '/var/opt/mssql/data/rejected_' + [guid]::NewGuid().ToString('N') + '.bak'
        $null = Invoke-OwnedSql 'master' "BACKUP DATABASE [$sourceDatabase] TO DISK = N'$backupPath' WITH COPY_ONLY, INIT;"
        $logicalData = Invoke-OwnedSql $sourceDatabase "SELECT name FROM sys.database_files WHERE type = 0;"
        $logicalLog = Invoke-OwnedSql $sourceDatabase "SELECT name FROM sys.database_files WHERE type = 1;"
    }
    $originalSource = Invoke-OwnedSql $sourceDatabase $identitySql
    foreach ($populated in @($false, $true)) {
        $replacement = 'rejected_' + [guid]::NewGuid().ToString('N')
        if ($Provider -eq 'Postgresql') {
            $null = Invoke-OwnedSql 'postgres' "CREATE DATABASE $replacement;"
            $null = Invoke-OwnedSql $replacement $dump.StandardOutput
            $null = Invoke-OwnedSql $replacement 'UPDATE dms."DataStoreIdentity" SET "SourceIdentity" = gen_random_uuid();'
            if ($populated) {
                $null = Invoke-OwnedSql $replacement 'INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId") SELECT gen_random_uuid(), "ResourceKeyId" FROM dms."ResourceKey" LIMIT 1;'
            }
        } else {
            $null = Invoke-OwnedSql 'master' "RESTORE DATABASE [$replacement] FROM DISK = N'$backupPath' WITH MOVE N'$logicalData' TO N'/var/opt/mssql/data/$replacement.mdf', MOVE N'$logicalLog' TO N'/var/opt/mssql/data/$replacement.ldf';"
            $null = Invoke-OwnedSql $replacement 'UPDATE dms.DataStoreIdentity SET SourceIdentity = NEWID();'
            if ($populated) {
                $null = Invoke-OwnedSql $replacement 'INSERT INTO dms.Document (DocumentUuid, ResourceKeyId) SELECT TOP (1) NEWID(), ResourceKeyId FROM dms.ResourceKey;'
            }
        }
        (Invoke-OwnedSql $replacement $countSql) | Should -Be $(if ($populated) { '1' } else { '0' })
        $beforeSource = Invoke-OwnedSql $replacement $identitySql
        $beforeSource | Should -Not -Be $originalSource
        $altered = [Text.Encoding]::UTF8.GetString($settingsBytes) | ConvertFrom-Json -AsHashtable
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        # Invoke CLR accessors: PowerShell's IDictionary adapter otherwise creates a
        # literal ConnectionString key, leaving subsequent database edits unused.
        $connection.set_ConnectionString($altered.Cdc.SetupConnectionString)
        $connection['Database'] = $replacement
        $altered.Cdc.SetupConnectionString = $connection.get_ConnectionString()
        $commandSettingsPath = Join-Path $FixtureRoot 'rejected-source-settings.json'
        $altered | ConvertTo-Json -Depth 100 | Set-Content $commandSettingsPath
        & chmod 600 $commandSettingsPath
        foreach ($id in @('cdc-provenance-rejection', 'cdc-intact-resume')) {
            $result = Invoke-MarkedCommand $id
            $result.exitCode | Should -Be 1
            $result.succeeded | Should -BeFalse
            @($result.diagnostics | Where-Object { $_.component -eq 'ProviderSetup' -and $_.failure -eq 'ValidationFailed' }).Count | Should -BeGreaterThan 0
            if ($id -eq 'cdc-provenance-rejection') { $result.data.preStartEligible | Should -BeFalse }
            $results.Add(@{ SnippetId = $id; Scenario = $(if ($populated) { 'populated-independent-source' } else { 'empty-independent-source' }); ExitCode = 1; Diagnostics = @($result.diagnostics | Select-Object component, failure) })
        }
        (Invoke-OwnedSql $replacement $identitySql) | Should -Be $beforeSource
        (Invoke-OwnedSql $sourceDatabase $identitySql) | Should -Be $originalSource
        (Invoke-RestMethod -Uri "$connectorUri/offsets" -TimeoutSec 10 | ConvertTo-Json -Depth 30 -Compress) | Should -Be $offset
        (Invoke-RestMethod -Uri "$connectorUri/status" -TimeoutSec 10).connector.state | Should -Be 'STOPPED'
    }
    $commandSettingsPath = $Entry.SettingsPath
    (Get-FileHash $bindingPath).Hash | Should -Be $bindingHash
    (Get-FileHash $Entry.SettingsPath).Hash | Should -Be $settingsHash
    $journal = Get-Content (Join-Path $StatePath 'workflows/local/datastore-1/1.json') -Raw | ConvertFrom-Json
    @($journal.operations | Where-Object effect -eq 'AuthorizeWriterPublication').Count | Should -Be 1
    $images = @{}
    $containers = @{ Provider = $(if ($Provider -eq 'Postgresql') { 'dms-postgresql' } else { 'dms-mssql' }); Broker = 'dms-kafka1'; Worker = "$Project-kafka-cdc-worker-1" }
    foreach ($name in $containers.Keys) {
        $image = & docker inspect $containers[$name] --format '{{.Image}}'
        $LASTEXITCODE | Should -Be 0
        $image | Should -Match '^sha256:[a-f0-9]{64}$'
        $images[$name] = $image
    }
    if ($env:CDC_RUNBOOK_EVIDENCE_DIRECTORY) {
        @{
            Provider = $Provider; TestId = 'CDC-DOC cdc-managed-start'
            Images = $images
            SnippetIds = @('cdc-state-inventory', 'cdc-managed-stop', 'cdc-managed-start', 'cdc-intact-restart', 'cdc-intact-resume', 'cdc-validate', 'cdc-provenance-rejection', 'cdc-disclosure-containment-result')
            SharedWorkerStopped = $true; RetainedSettingsUnchanged = $true; CustomStateRootPreserved = $true
            BindingUnchanged = $true; RejectedResumeOffsetsUnchanged = $true; InitialWriterIntentCount = 1
            Results = @($results)
        } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'managed-lifecycle-runbook.json')
    }
}
