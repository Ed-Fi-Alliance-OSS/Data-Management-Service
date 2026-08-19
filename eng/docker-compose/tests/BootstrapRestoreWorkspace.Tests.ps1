# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1271 restore-candidate workspace guardrails: the DMS_BOOTSTRAP_ROOT_OVERRIDE seam in
# bootstrap-manifest.psm1 (import-time validation, no import poisoning) and the fail-fast
# refusal in every phase command that consumes the ACTIVE workspace. A leaked override must
# never let a live-service phase read a half-validated restore candidate.

BeforeAll {
    $script:dockerComposeDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:manifestModulePath = Join-Path $script:dockerComposeDir "bootstrap-manifest.psm1"
    $script:activeBootstrapRoot = Join-Path $script:dockerComposeDir ".bootstrap"
    $script:restoreWorkspaceRoot = Join-Path $script:dockerComposeDir ".bootstrap-restore"
    Import-Module (Join-Path $script:dockerComposeDir "bootstrap-restore.psm1") -Force

    function script:Import-ManifestModule {
        Import-Module $script:manifestModulePath -Force -Global
    }

    function script:New-CandidatePrepareStub {
        # Recording stubs standing in for the prepare phase scripts: each records its override
        # view and arguments; the schema stub's behavior is selectable so failure paths can be
        # exercised. WriteManifest mimics a real prepare run by writing the candidate manifest
        # INTO the override directory - proving the stub consumed the override, not a fixed path.
        param(
            [Parameter(Mandatory)]
            [string]$Directory,

            [Parameter(Mandatory)]
            [string]$LogPath,

            [ValidateSet("WriteManifest", "NoManifest", "Throw", "ExitCode")]
            [string]$SchemaBehavior = "WriteManifest"
        )

        $schemaBody = switch ($SchemaBehavior) {
            "WriteManifest" {
                "New-Item -ItemType Directory -Path `$env:DMS_BOOTSTRAP_ROOT_OVERRIDE -Force | Out-Null`n" +
                "Set-Content -LiteralPath (Join-Path `$env:DMS_BOOTSTRAP_ROOT_OVERRIDE 'bootstrap-manifest.json') -Value '{`"version`":1}'"
            }
            "NoManifest" { "" }
            "Throw"      { "throw 'schema stub failure'" }
            "ExitCode"   { "exit 5" }
        }

        $schemaScriptPath = Join-Path $Directory "stub-prepare-dms-schema.ps1"
        @"
param([string]`$EnvironmentFile)
Add-Content -LiteralPath '$LogPath' -Value "schema env=[`$EnvironmentFile] override=[`$env:DMS_BOOTSTRAP_ROOT_OVERRIDE]"
$schemaBody
"@ | Set-Content -LiteralPath $schemaScriptPath -Encoding utf8

        $claimsScriptPath = Join-Path $Directory "stub-prepare-dms-claims.ps1"
        @"
Add-Content -LiteralPath '$LogPath' -Value "claims override=[`$env:DMS_BOOTSTRAP_ROOT_OVERRIDE]"
"@ | Set-Content -LiteralPath $claimsScriptPath -Encoding utf8

        return [pscustomobject]@{
            SchemaScriptPath = $schemaScriptPath
            ClaimsScriptPath = $claimsScriptPath
        }
    }

    function script:New-CandidateFixture {
        # A structurally complete candidate workspace as the prepare phases would leave it:
        # root manifest with the schema section (including the restore cross-check fields) and
        # the ApiSchema workspace manifest declaring a core ('Ed-Fi'/'ed-fi') plus one extension.
        param(
            [Parameter(Mandatory)]
            [string]$Directory,

            [string]$DataStandardVersion = "5.2.0",

            [string]$ApiSchemaFormatVersion = "1.0.0",

            [string]$EffectiveSchemaHash = ("ab" * 32),

            [string[]]$SelectedExtensions = @("sample")
        )

        $candidateDirectory = Join-Path $Directory "candidate-$([Guid]::NewGuid().ToString('n'))"
        $apiSchemaRoot = Join-Path $candidateDirectory "ApiSchema"
        foreach ($projectDirectory in @("Ed-Fi", "Sample")) {
            $schemaDirectory = Join-Path (Join-Path $apiSchemaRoot "schemas") $projectDirectory
            New-Item -ItemType Directory -Path $schemaDirectory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $schemaDirectory "ApiSchema.json") -Value "{}" -Encoding utf8
        }

        [ordered]@{
            version  = 1
            projects = @(
                [ordered]@{ projectName = "Ed-Fi"; projectEndpointName = "ed-fi"; isExtensionProject = $false; schemaPath = "schemas/Ed-Fi/ApiSchema.json" },
                [ordered]@{ projectName = "Sample"; projectEndpointName = "sample"; isExtensionProject = $true; schemaPath = "schemas/Sample/ApiSchema.json" }
            )
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $apiSchemaRoot "bootstrap-api-schema-manifest.json") -Encoding utf8

        [ordered]@{
            version = 1
            schema  = [ordered]@{
                selectionMode          = "Standard"
                selectedExtensions     = @($SelectedExtensions)
                effectiveSchemaHash    = $EffectiveSchemaHash
                workspaceFingerprint   = ("0" * 64)
                apiSchemaManifestPath  = "ApiSchema/bootstrap-api-schema-manifest.json"
                dataStandardVersion    = $DataStandardVersion
                apiSchemaFormatVersion = $ApiSchemaFormatVersion
            }
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $candidateDirectory "bootstrap-manifest.json") -Encoding utf8

        return $candidateDirectory
    }

    function script:Set-CandidatePathField {
        # Rewrites one candidate-supplied path field in place, modeling a malformed candidate
        # that tries to point the cross-check outside its own tree: 'apiSchemaManifestPath'
        # edits the root manifest's schema section; 'schemaPath' edits the core project entry in
        # the ApiSchema manifest.
        param(
            [Parameter(Mandatory)]
            [string]$CandidateDirectory,

            [Parameter(Mandatory)]
            [ValidateSet("apiSchemaManifestPath", "schemaPath")]
            [string]$Field,

            [Parameter(Mandatory)]
            [string]$Value
        )

        if ($Field -eq "apiSchemaManifestPath") {
            $manifestPath = Join-Path $CandidateDirectory "bootstrap-manifest.json"
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
            $manifest["schema"]["apiSchemaManifestPath"] = $Value
            $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        }
        else {
            $apiSchemaManifestPath = Join-Path $CandidateDirectory "ApiSchema/bootstrap-api-schema-manifest.json"
            $apiSchemaManifest = Get-Content -LiteralPath $apiSchemaManifestPath -Raw | ConvertFrom-Json -AsHashtable
            $apiSchemaManifest["projects"][0]["schemaPath"] = $Value
            $apiSchemaManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $apiSchemaManifestPath -Encoding utf8
        }
    }

    function script:New-StubSchemaTool {
        # A .ps1 api-schema-tools stand-in (Get-RestoreCandidateRelationalMappingVersion runs
        # .ps1 tools via a child pwsh): records its full argv and writes ddl.manifest.json with
        # the configured relational mapping version into the requested --output directory.
        param(
            [Parameter(Mandatory)]
            [string]$Directory,

            [string]$MappingVersion = "v2"
        )

        $logPath = Join-Path $Directory "schema-tool-args.log"
        $toolPath = Join-Path $Directory "api-schema-tools.ps1"
        @"
Add-Content -LiteralPath '$logPath' -Value (`$args -join ' ')
`$outputIndex = [array]::IndexOf(`$args, '--output')
`$outputDirectory = `$args[`$outputIndex + 1]
New-Item -ItemType Directory -Path `$outputDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path `$outputDirectory 'ddl.manifest.json') -Value '{"relational_mapping_version":"$MappingVersion"}'
exit 0
"@ | Set-Content -LiteralPath $toolPath -Encoding utf8

        return [pscustomobject]@{
            ToolPath = $toolPath
            LogPath  = $logPath
        }
    }

    function script:New-CrossCheckManifest {
        # A restore-manifest stand-in carrying exactly the fields the candidate cross-check
        # compares, defaulting to values that MATCH New-CandidateFixture and the v2 stub tool.
        param(
            [hashtable]$Override = @{}
        )

        $manifest = @{
            databaseEngine           = "postgresql"
            documentJsonColumnType   = "jsonb"
            dataStandardVersion      = "5.2.0"
            apiSchemaFormatVersion   = "1.0.0"
            effectiveSchemaHash      = ("ab" * 32)
            relationalMappingVersion = "v2"
            projects                 = @("edfi", "sample")
        }
        foreach ($overrideKey in $Override.Keys) {
            $manifest[$overrideKey] = $Override[$overrideKey]
        }
        return [pscustomobject]$manifest
    }

    function script:New-CandidateOverridePath {
        return Join-Path $script:restoreWorkspaceRoot "candidate-$([Guid]::NewGuid().ToString('n'))"
    }

    function script:New-PrepareFallbackProbe {
        # The prepare-dms-schema.ps1 fallback only runs when bootstrap-manifest.psm1 is NOT
        # loaded, and the script has no dot-source guard, so extract exactly the fallback
        # if-statement via AST and execute it in a clean child pwsh session (where Get-Command
        # finds no module Get-BootstrapRoot). The probe file lives in the given directory, so the
        # fallback's $PSScriptRoot-anchored roots relocate there - the validation SEMANTICS are
        # what the probe pins, not the production location.
        param(
            [Parameter(Mandatory)]
            [string]$Directory
        )

        $prepareScript = Join-Path $script:dockerComposeDir "prepare-dms-schema.ps1"
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($prepareScript, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "prepare-dms-schema.ps1 failed to parse."
        }
        $fallbackBlock = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.Contains("Get-Command Get-BootstrapRoot")
        }, $true)
        if ($null -eq $fallbackBlock) {
            throw "The module-absent fallback block was not found in prepare-dms-schema.ps1."
        }

        $probePath = Join-Path $Directory "fallback-probe.ps1"
        @(
            "`$ErrorActionPreference = 'Stop'"
            "function Format-LogSafeText { param(`$Value) [string]`$Value }"
            $fallbackBlock.Extent.Text
            "Get-BootstrapRoot"
        ) -join "`n" | Set-Content -LiteralPath $probePath -Encoding utf8
        return $probePath
    }

    function script:Restore-CleanModuleState {
        # Later suites in the same session must never inherit a candidate-redirected module
        # instance or a stray override value; Remove-Item is required because assigning $null/""
        # can leave a present-but-blank variable on some hosts.
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        Remove-Item function:global:docker -ErrorAction SilentlyContinue
        Import-ManifestModule
    }
}

Describe "DMS_BOOTSTRAP_ROOT_OVERRIDE workspace redirection" {
    AfterEach {
        Restore-CleanModuleState
    }

    It "resolves the active .bootstrap root when no override is set" {
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $script:activeBootstrapRoot
    }

    It "treats a whitespace-only override as absent" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = " "
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $script:activeBootstrapRoot
    }

    It "redirects the workspace root and path resolution into a validated candidate directory" {
        $candidate = New-CandidateOverridePath
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidate
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $candidate
        Resolve-BootstrapPath -RelativePath "ApiSchema/core.json" |
            Should -Be ([System.IO.Path]::GetFullPath((Join-Path $candidate "ApiSchema/core.json")))
    }

    It "rejects a relative override at import time" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = ".bootstrap-restore/candidate-x"
        { Import-ManifestModule } | Should -Throw "*must be an absolute path strictly inside*"
    }

    It "rejects an override outside .bootstrap-restore at import time, including the active .bootstrap" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = Join-Path $TestDrive "elsewhere"
        { Import-ManifestModule } | Should -Throw "*must point strictly inside*"

        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $script:activeBootstrapRoot
        { Import-ManifestModule } | Should -Throw "*must point strictly inside*"
    }

    It "rejects the restore workspace root itself, with and without a trailing separator" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $script:restoreWorkspaceRoot
        { Import-ManifestModule } | Should -Throw "*must point strictly inside*"

        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $script:restoreWorkspaceRoot + [System.IO.Path]::DirectorySeparatorChar
        { Import-ManifestModule } | Should -Throw "*must point strictly inside*"
    }

    It "does not poison later imports: -Force re-evaluation restores the active root after the override is cleared" {
        $candidate = New-CandidateOverridePath
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidate
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $candidate

        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $script:activeBootstrapRoot
        Resolve-BootstrapPath -RelativePath "bootstrap-manifest.json" |
            Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:activeBootstrapRoot "bootstrap-manifest.json")))
    }

    It "rejects a case-variant .BOOTSTRAP-RESTORE sibling on case-sensitive platforms" -Skip:$IsWindows {
        # On Linux the case-variant is a DIFFERENT directory, outside the restore workspace and
        # outside its .gitignore entry; the containment check must not accept it.
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = Join-Path (Join-Path $script:dockerComposeDir ".BOOTSTRAP-RESTORE") "candidate-x"
        { Import-ManifestModule } | Should -Throw "*must point strictly inside*"
    }

    It "accepts a case-variant spelling on Windows, where it is the same directory" -Skip:(-not $IsWindows) {
        $candidate = Join-Path (Join-Path $script:dockerComposeDir ".BOOTSTRAP-RESTORE") "candidate-x"
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidate
        Import-ManifestModule
        Get-BootstrapRoot | Should -Be $candidate
    }

    It "prepare-dms-schema.ps1's module-absent fallback mirrors the override with the same validation" {
        $probePath = New-PrepareFallbackProbe -Directory $TestDrive
        $candidate = Join-Path (Join-Path $TestDrive ".bootstrap-restore") "candidate-fallback"
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidate
        $resolvedRoot = & pwsh -NoProfile -NonInteractive -File $probePath | Select-Object -Last 1
        $LASTEXITCODE | Should -Be 0
        $resolvedRoot | Should -Be $candidate

        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = ".bootstrap-restore/candidate-fallback"
        $rejectionOutput = & pwsh -NoProfile -NonInteractive -File $probePath 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
        $rejectionOutput | Should -BeLike "*must be an absolute path strictly inside*"

        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = Join-Path $TestDrive "elsewhere"
        $rejectionOutput = & pwsh -NoProfile -NonInteractive -File $probePath 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
        $rejectionOutput | Should -BeLike "*must point strictly inside*"
    }

    It "the fallback rejects a case-variant .BOOTSTRAP-RESTORE sibling on case-sensitive platforms" -Skip:$IsWindows {
        $probePath = New-PrepareFallbackProbe -Directory $TestDrive
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = Join-Path (Join-Path $TestDrive ".BOOTSTRAP-RESTORE") "candidate-x"
        $rejectionOutput = & pwsh -NoProfile -NonInteractive -File $probePath 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
        $rejectionOutput | Should -BeLike "*must point strictly inside*"
    }
}

Describe "Assert-NoBootstrapRootOverride" {
    BeforeEach {
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        Import-ManifestModule
    }

    AfterEach {
        Restore-CleanModuleState
    }

    It "passes when the override is absent or whitespace" {
        { Assert-NoBootstrapRootOverride -PhaseName "test-phase" } | Should -Not -Throw
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = " "
        { Assert-NoBootstrapRootOverride -PhaseName "test-phase" } | Should -Not -Throw
    }

    It "refuses at call time when the override is set, naming the phase - even on a module imported before the leak" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = New-CandidateOverridePath
        { Assert-NoBootstrapRootOverride -PhaseName "test-phase" } |
            Should -Throw "test-phase must not run while DMS_BOOTSTRAP_ROOT_OVERRIDE is set*"
    }
}

Describe "consuming phase commands refuse a leaked restore-candidate override" {
    BeforeEach {
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        # Shadow docker for the whole test so ANY compose activity is observable: the refusal
        # must fire before the first side effect, so this log must never come into existence.
        $script:dockerCallLog = Join-Path $TestDrive "docker-calls-$([Guid]::NewGuid().ToString('n')).txt"
        $dockerCallLog = $script:dockerCallLog
        Set-Item -Path function:global:docker -Value {
            Add-Content -LiteralPath $dockerCallLog -Value ($args -join " ")
        }.GetNewClosure()
    }

    AfterEach {
        Restore-CleanModuleState
    }

    It "Invoke-BootstrapStartupConfiguration refuses before any manifest read or environment activation" {
        Import-ManifestModule
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = New-CandidateOverridePath
        { Invoke-BootstrapStartupConfiguration } |
            Should -Throw "Startup configuration*must not run while DMS_BOOTSTRAP_ROOT_OVERRIDE is set*"
        $script:dockerCallLog | Should -Not -Exist
    }

    It "configure-local-data-store.ps1 refuses before any CMS or compose activity" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = New-CandidateOverridePath
        . (Join-Path $script:dockerComposeDir "configure-local-data-store.ps1")
        { Invoke-ConfigureLocalDataStore } |
            Should -Throw "configure-local-data-store.ps1 must not run while DMS_BOOTSTRAP_ROOT_OVERRIDE is set*"
        $script:dockerCallLog | Should -Not -Exist
    }

    It "provision-dms-schema.ps1 refuses before any database activity" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = New-CandidateOverridePath
        . (Join-Path $script:dockerComposeDir "provision-dms-schema.ps1")
        { Invoke-ProvisionDmsSchema } |
            Should -Throw "provision-dms-schema.ps1 must not run while DMS_BOOTSTRAP_ROOT_OVERRIDE is set*"
        $script:dockerCallLog | Should -Not -Exist
    }

    It "load-dms-seed-data.ps1 refuses before resolving the environment (which can seed .env)" {
        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = New-CandidateOverridePath
        { & (Join-Path $script:dockerComposeDir "load-dms-seed-data.ps1") } |
            Should -Throw "load-dms-seed-data.ps1 must not run while DMS_BOOTSTRAP_ROOT_OVERRIDE is set*"
        $script:dockerCallLog | Should -Not -Exist
    }
}

Describe "New-RestoreCandidateWorkspace" {
    BeforeEach {
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        $script:candidateWorkspaceRoot = Join-Path $TestDrive "ws-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:candidateWorkspaceRoot -Force | Out-Null
        $script:prepareLogPath = Join-Path $script:candidateWorkspaceRoot "prepare-calls.log"
    }

    AfterEach {
        Restore-CleanModuleState
    }

    It "runs both prepare phases under the override, forwards the environment file, and clears the override on success" {
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath

        $result = New-RestoreCandidateWorkspace `
            -EnvironmentFile "C:\some\effective.env" `
            -WorkspaceRoot $script:candidateWorkspaceRoot `
            -PrepareSchemaScriptPath $stub.SchemaScriptPath `
            -PrepareClaimsScriptPath $stub.ClaimsScriptPath

        $result.CandidateDirectory | Should -BeLike (Join-Path $script:candidateWorkspaceRoot "candidate-*")
        $result.CandidateManifestPath | Should -Exist

        $log = @(Get-Content -LiteralPath $script:prepareLogPath)
        $log.Count | Should -Be 2
        # Both phases saw the override pointing at THIS candidate (the schema stub also wrote the
        # manifest through it), the environment file was forwarded, and schema ran before claims.
        $log[0] | Should -Be "schema env=[C:\some\effective.env] override=[$($result.CandidateDirectory)]"
        $log[1] | Should -Be "claims override=[$($result.CandidateDirectory)]"

        # State hygiene: the override never outlives the candidate build.
        Test-Path Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE | Should -BeFalse
    }

    It "state hygiene: clears the override and removes the partial candidate when a prepare phase throws" {
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath -SchemaBehavior Throw

        { New-RestoreCandidateWorkspace `
                -EnvironmentFile "x.env" `
                -WorkspaceRoot $script:candidateWorkspaceRoot `
                -PrepareSchemaScriptPath $stub.SchemaScriptPath `
                -PrepareClaimsScriptPath $stub.ClaimsScriptPath } |
            Should -Throw "*schema stub failure*"

        Test-Path Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:candidateWorkspaceRoot -Directory -Filter "candidate-*") | Should -BeNullOrEmpty
        @(Get-Content -LiteralPath $script:prepareLogPath) | Where-Object { $_ -like "claims*" } |
            Should -BeNullOrEmpty -Because "the claims phase must not run after a schema-phase failure"
    }

    It "treats a nonzero prepare exit code as failure even though the stub script did not throw" {
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath -SchemaBehavior ExitCode

        { New-RestoreCandidateWorkspace `
                -EnvironmentFile "x.env" `
                -WorkspaceRoot $script:candidateWorkspaceRoot `
                -PrepareSchemaScriptPath $stub.SchemaScriptPath `
                -PrepareClaimsScriptPath $stub.ClaimsScriptPath } |
            Should -Throw "*exited with code 5*"

        Test-Path Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:candidateWorkspaceRoot -Directory -Filter "candidate-*") | Should -BeNullOrEmpty
    }

    It "fails, cleans up, and clears the override when the prepare phases produce no candidate manifest" {
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath -SchemaBehavior NoManifest

        { New-RestoreCandidateWorkspace `
                -EnvironmentFile "x.env" `
                -WorkspaceRoot $script:candidateWorkspaceRoot `
                -PrepareSchemaScriptPath $stub.SchemaScriptPath `
                -PrepareClaimsScriptPath $stub.ClaimsScriptPath } |
            Should -Throw "*produced no candidate bootstrap manifest*"

        Test-Path Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE | Should -BeFalse
        @(Get-ChildItem -LiteralPath $script:candidateWorkspaceRoot -Directory -Filter "candidate-*") | Should -BeNullOrEmpty
    }
}

Describe "ConvertTo-RestoreProjectSchemaName" {
    It "normalizes endpoint names to database schema names: lowercase, hyphens removed" {
        ConvertTo-RestoreProjectSchemaName -ProjectEndpointName "ed-fi" | Should -Be "edfi"
        ConvertTo-RestoreProjectSchemaName -ProjectEndpointName "TPDM" | Should -Be "tpdm"
        ConvertTo-RestoreProjectSchemaName -ProjectEndpointName "sample" | Should -Be "sample"
    }

    It "rejects an endpoint that normalizes to an empty schema name" {
        { ConvertTo-RestoreProjectSchemaName -ProjectEndpointName "-" } |
            Should -Throw "*normalizes to an empty schema name*"
    }
}

Describe "package-to-candidate cross-check" {
    BeforeEach {
        $script:crossCheckRoot = Join-Path $TestDrive "xc-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:crossCheckRoot -Force | Out-Null
        $script:stageDirectory = Join-Path $script:crossCheckRoot "stage"
        New-Item -ItemType Directory -Path $script:stageDirectory -Force | Out-Null
    }

    It "passes when the package manifest matches the candidate, emitting ddl into the stage - never the candidate" {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        $candidateFact = Invoke-RestoreCandidateCrossCheck `
            -Manifest (New-CrossCheckManifest) `
            -CandidateDirectory $candidateDirectory `
            -DatabaseEngine postgresql `
            -StageDirectory $script:stageDirectory `
            -SchemaToolPath $tool.ToolPath

        $candidateFact.CoreProjectEndpointName | Should -Be "ed-fi"
        @($candidateFact.SelectedExtensions) | Should -Be @("sample")

        # Success removes nothing; the wrapper consumes both later.
        $candidateDirectory | Should -Exist
        $script:stageDirectory | Should -Exist

        # The tool ran once, over the candidate's schema files core-first, with --ddl-manifest,
        # the selected dialect, and an output directory INSIDE the private stage.
        $toolArgs = @(Get-Content -LiteralPath $tool.LogPath)
        $toolArgs.Count | Should -Be 1
        # Join-Path normalizes the embedded separators per platform, matching how the module
        # builds the paths it passes to the tool.
        $expectedCoreSchemaPath = Join-Path $candidateDirectory "ApiSchema/schemas/Ed-Fi/ApiSchema.json"
        $expectedExtensionSchemaPath = Join-Path $candidateDirectory "ApiSchema/schemas/Sample/ApiSchema.json"
        $toolArgs[0] | Should -BeLike "ddl emit --schema $expectedCoreSchemaPath $expectedExtensionSchemaPath --output $(Join-Path $script:stageDirectory 'ddl-validation-*') --dialect pgsql --ddl-manifest"
        @(Get-ChildItem -LiteralPath $candidateDirectory -Recurse -File -Filter "ddl.manifest.json") |
            Should -BeNullOrEmpty -Because "the candidate tree must stay byte-identical to what the prepare phases produced"
    }

    It "uses the mssql dialect for a SQL Server cross-check" {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        Invoke-RestoreCandidateCrossCheck `
            -Manifest (New-CrossCheckManifest -Override @{ databaseEngine = "mssql"; documentJsonColumnType = "nvarchar" }) `
            -CandidateDirectory $candidateDirectory `
            -DatabaseEngine mssql `
            -StageDirectory $script:stageDirectory `
            -SchemaToolPath $tool.ToolPath | Out-Null

        @(Get-Content -LiteralPath $tool.LogPath)[0] | Should -BeLike "*--dialect mssql --ddl-manifest"
    }

    It "on <Name> mismatch: fails naming the field, removes the candidate AND the staged package, touches nothing else" -ForEach @(
        @{ Name = "dataStandardVersion"; Override = @{ dataStandardVersion = "6.1.0" }; Engine = "postgresql"; Expected = "*Data Standard mismatch*'6.1.0'*'5.2.0'*" }
        @{ Name = "apiSchemaFormatVersion"; Override = @{ apiSchemaFormatVersion = "2.0.0" }; Engine = "postgresql"; Expected = "*ApiSchema format version mismatch*" }
        @{ Name = "effectiveSchemaHash"; Override = @{ effectiveSchemaHash = ("cd" * 32) }; Engine = "postgresql"; Expected = "*Effective schema hash mismatch*" }
        @{ Name = "relationalMappingVersion"; Override = @{ relationalMappingVersion = "v1" }; Engine = "postgresql"; Expected = "*Relational mapping version mismatch*'v1'*'v2'*" }
        @{ Name = "projects"; Override = @{ projects = @("edfi", "tpdm") }; Engine = "postgresql"; Expected = "*Project set mismatch*[[]edfi, tpdm*[[]edfi, sample*" }
        @{ Name = "databaseEngine"; Override = @{ databaseEngine = "mssql"; documentJsonColumnType = "nvarchar" }; Engine = "postgresql"; Expected = "*declares databaseEngine 'mssql'*selected 'postgresql'*" }
        @{ Name = "documentJsonColumnType"; Override = @{ documentJsonColumnType = "json" }; Engine = "postgresql"; Expected = "*DocumentJson physical baseline mismatch*'json'*'jsonb'*" }
    ) {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        { Invoke-RestoreCandidateCrossCheck `
                -Manifest (New-CrossCheckManifest -Override $Override) `
                -CandidateDirectory $candidateDirectory `
                -DatabaseEngine $Engine `
                -StageDirectory $script:stageDirectory `
                -SchemaToolPath $tool.ToolPath } |
            Should -Throw $Expected

        # Mismatch discards both transient inputs; the active workspace and the target database
        # are never referenced by the cross-check at all (it takes only explicit private paths).
        Test-Path -LiteralPath $candidateDirectory | Should -BeFalse
        Test-Path -LiteralPath $script:stageDirectory | Should -BeFalse
    }

    It "rejects a candidate whose <Name> escapes via parent traversal, without invoking the schema tool" -ForEach @(
        @{ Name = "apiSchemaManifestPath"; Field = "apiSchemaManifestPath"; Value = "../outside/bootstrap-api-schema-manifest.json" }
        @{ Name = "projects schemaPath"; Field = "schemaPath"; Value = "../../outside/ApiSchema.json" }
    ) {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        Set-CandidatePathField -CandidateDirectory $candidateDirectory -Field $Field -Value $Value
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        { Invoke-RestoreCandidateCrossCheck `
                -Manifest (New-CrossCheckManifest) `
                -CandidateDirectory $candidateDirectory `
                -DatabaseEngine postgresql `
                -StageDirectory $script:stageDirectory `
                -SchemaToolPath $tool.ToolPath } |
            Should -Throw "*must not contain empty, current, or parent path segments*"

        $tool.LogPath | Should -Not -Exist -Because "no path outside the candidate may ever reach the schema tool"
        Test-Path -LiteralPath $candidateDirectory | Should -BeFalse
        Test-Path -LiteralPath $script:stageDirectory | Should -BeFalse
    }

    It "rejects a candidate whose <Name> is an absolute path, without invoking the schema tool" -ForEach @(
        @{ Name = "apiSchemaManifestPath"; Field = "apiSchemaManifestPath" }
        @{ Name = "projects schemaPath"; Field = "schemaPath" }
    ) {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        # An absolute path pointing OUTSIDE the candidate - the shape a candidate would need to
        # reach the active .bootstrap workspace.
        $absoluteEscapePath = Join-Path $script:crossCheckRoot "outside-target.json"
        Set-Content -LiteralPath $absoluteEscapePath -Value "{}" -Encoding utf8
        Set-CandidatePathField -CandidateDirectory $candidateDirectory -Field $Field -Value $absoluteEscapePath
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        { Invoke-RestoreCandidateCrossCheck `
                -Manifest (New-CrossCheckManifest) `
                -CandidateDirectory $candidateDirectory `
                -DatabaseEngine postgresql `
                -StageDirectory $script:stageDirectory `
                -SchemaToolPath $tool.ToolPath } |
            Should -Throw "*must be relative to the bootstrap workspace*"

        $tool.LogPath | Should -Not -Exist -Because "no path outside the candidate may ever reach the schema tool"
        Test-Path -LiteralPath $candidateDirectory | Should -BeFalse
        Test-Path -LiteralPath $script:stageDirectory | Should -BeFalse
    }

    It "fails closed when the candidate manifest lacks the cross-check fields (pre-4.2 candidate)" {
        $candidateDirectory = New-CandidateFixture -Directory $script:crossCheckRoot
        $manifestPath = Join-Path $candidateDirectory "bootstrap-manifest.json"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        $manifest["schema"].Remove("dataStandardVersion")
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        $tool = New-StubSchemaTool -Directory $script:crossCheckRoot

        { Invoke-RestoreCandidateCrossCheck `
                -Manifest (New-CrossCheckManifest) `
                -CandidateDirectory $candidateDirectory `
                -DatabaseEngine postgresql `
                -StageDirectory $script:stageDirectory `
                -SchemaToolPath $tool.ToolPath } |
            Should -Throw "*schema section is missing 'dataStandardVersion'*"

        Test-Path -LiteralPath $candidateDirectory | Should -BeFalse
        Test-Path -LiteralPath $script:stageDirectory | Should -BeFalse
    }
}
