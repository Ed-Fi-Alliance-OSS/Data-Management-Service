# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1271 restore-candidate workspace guardrails: the DMS_BOOTSTRAP_ROOT_OVERRIDE seam in
# bootstrap-manifest.psm1 (import-time validation, no import poisoning) and the fail-fast
# refusal in every phase command that consumes the ACTIVE workspace. A leaked override must
# never let a live-service phase read a half-validated restore candidate.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'Pester mock bodies execute in the mocked module''s session state, where test-scope locals are invisible; global variables are the documented crossing mechanism and are removed in AfterEach/AfterAll.')]
param()

BeforeAll {
    $script:dockerComposeDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:manifestModulePath = Join-Path $script:dockerComposeDir "bootstrap-manifest.psm1"
    $script:activeBootstrapRoot = Join-Path $script:dockerComposeDir ".bootstrap"
    $script:restoreWorkspaceRoot = Join-Path $script:dockerComposeDir ".bootstrap-restore"
    Import-Module (Join-Path $script:dockerComposeDir "bootstrap-restore.psm1") -Force
    # Without -Force: bootstrap-restore's nested import already bound an instance, and a forced
    # re-import here would strip that binding (the documented nested-Force gotcha in reverse).
    Import-Module (Join-Path $script:dockerComposeDir "../DatabaseTemplates/Template-RestoreCore.psm1")
    Import-Module (Join-Path $script:dockerComposeDir "../DatabaseTemplates/Template-RestoreTrust.psm1")

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

    It "heals the session's module instance after the candidate build (no import poisoning)" {
        # The REAL prepare scripts import bootstrap-manifest -Force -Global while the override
        # is set, baking the candidate root into the session instance; a later no-Force nested
        # bind (e.g. the schema workspace validation) would then resolve the moved candidate
        # path. Proven by the live restore smoke; the stub mimics exactly that import.
        # This stub re-imports the REAL module, whose import-time containment validation only
        # accepts candidates under the real .bootstrap-restore - so this test alone uses a
        # (cleaned-up) workspace there instead of TestDrive.
        $realWorkspaceRoot = Join-Path $script:restoreWorkspaceRoot "poison-regress-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $realWorkspaceRoot -Force | Out-Null
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath
        $manifestModulePath = Join-Path $script:dockerComposeDir "bootstrap-manifest.psm1"
        @"
param([string]`$EnvironmentFile)
Import-Module '$($manifestModulePath.Replace("'", "''"))' -Force -Global
Add-Content -LiteralPath '$($script:prepareLogPath.Replace("'", "''"))' -Value "schema staged-root=[`$(Get-BootstrapRoot)]"
New-Item -ItemType Directory -Path `$env:DMS_BOOTSTRAP_ROOT_OVERRIDE -Force | Out-Null
Set-Content -LiteralPath (Join-Path `$env:DMS_BOOTSTRAP_ROOT_OVERRIDE 'bootstrap-manifest.json') -Value '{"version":1}'
"@ | Set-Content -LiteralPath $stub.SchemaScriptPath -Encoding utf8

        try {
            $result = New-RestoreCandidateWorkspace `
                -EnvironmentFile "x.env" `
                -WorkspaceRoot $realWorkspaceRoot `
                -PrepareSchemaScriptPath $stub.SchemaScriptPath `
                -PrepareClaimsScriptPath $stub.ClaimsScriptPath

            # The prepare phase itself saw the CANDIDATE root through the re-imported module...
            @(Get-Content -LiteralPath $script:prepareLogPath) | Where-Object { $_ -eq "schema staged-root=[$($result.CandidateDirectory)]" } |
                Should -Not -BeNullOrEmpty
            # ...but after the candidate build, the session resolves the ACTIVE root again.
            Get-BootstrapRoot | Should -Be $script:activeBootstrapRoot
        }
        finally {
            if (Test-Path -LiteralPath $realWorkspaceRoot) {
                Remove-Item -LiteralPath $realWorkspaceRoot -Recurse -Force
            }
        }
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

Describe "Assert-DmsComposeProjectStopped (stop proof)" {
    BeforeAll {
        # Pester's Mock needs a resolvable command; environments without docker (e.g. the pwsh
        # validation container) get an inert global function the mocks then replace.
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    It "passes when the project has no running containers, filtering docker ps by the compose project label" {
        Mock docker { $global:LASTEXITCODE = 0 } -ModuleName bootstrap-restore

        { Assert-DmsComposeProjectStopped -ProjectName "dms-local" } | Should -Not -Throw

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            ($args -join " ") -eq "ps --filter label=com.docker.compose.project=dms-local --format {{.Names}}"
        }
    }

    It "fails naming every running container of the project" {
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-postgres-1"; "dms-local-dms-1" } -ModuleName bootstrap-restore

        { Assert-DmsComposeProjectStopped -ProjectName "dms-local" } |
            Should -Throw "*still has running containers: dms-local-postgres-1, dms-local-dms-1*"
    }

    It "fails closed when docker itself errors: indeterminate is never treated as stopped" {
        Mock docker { $global:LASTEXITCODE = 1; "Cannot connect to the Docker daemon" } -ModuleName bootstrap-restore

        { Assert-DmsComposeProjectStopped -ProjectName "dms-published" } |
            Should -Throw "*Stop proof is indeterminate*exited with code 1*Cannot connect to the Docker daemon*"
    }

    It "fails closed when docker is missing entirely: the PRODUCTION resolution path, no mocks" {
        # A CommandNotFound failure neither sets LASTEXITCODE nor flows through 2>&1, so without
        # explicit resolution an unresolvable docker would read as "stopped". Prove the real
        # code path in a child pwsh whose PATH is emptied AFTER startup, with the default
        # Continue error preference - the exact environment where the silent fall-through lived.
        $modulePath = Join-Path $script:dockerComposeDir "bootstrap-restore.psm1"
        $probePath = Join-Path $TestDrive "docker-missing-probe.ps1"
        @(
            "`$ErrorActionPreference = 'Continue'"
            "Import-Module '$($modulePath.Replace("'", "''"))'"
            "`$env:PATH = ''"
            "try {"
            "    Assert-DmsComposeProjectStopped -ProjectName dms-local"
            "    Write-Output 'VERDICT: no-throw'"
            "}"
            "catch {"
            "    Write-Output ('VERDICT: threw ' + `$_.Exception.Message)"
            "}"
        ) -join "`n" | Set-Content -LiteralPath $probePath -Encoding utf8

        $verdict = @(& pwsh -NoProfile -NonInteractive -File $probePath) | Where-Object { $_ -like "VERDICT:*" }
        $verdict | Should -BeLike "VERDICT: threw*Stop proof is indeterminate*'docker' command is not available*"
    }
}

Describe "Stop-RestoreDatabaseOnlySlice" {
    AfterEach {
        Remove-Item Env:\POSTGRES_USE_TMPFS -ErrorAction SilentlyContinue
    }

    Context "database-only compose set" {
        It "mirrors the start scripts' -DbOnly set: the engine file only, tmpfs solely under the PostgreSQL opt-in" {
            Remove-Item Env:\POSTGRES_USE_TMPFS -ErrorAction SilentlyContinue
            $postgresqlSet = @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine postgresql)
            $postgresqlSet.Count | Should -Be 2
            $postgresqlSet[0] | Should -Be "-f"
            $postgresqlSet[1] | Should -BeLike "*postgresql.yml"

            $mssqlSet = @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine mssql)
            $mssqlSet.Count | Should -Be 2
            $mssqlSet[1] | Should -BeLike "*mssql.yml"

            # Same condition the start scripts use: the process-environment opt-in, and only on
            # PostgreSQL - SQL Server never gets the tmpfs override.
            $env:POSTGRES_USE_TMPFS = "TRUE"
            $tmpfsSet = @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine postgresql)
            $tmpfsSet.Count | Should -Be 4
            $tmpfsSet[3] | Should -BeLike "*postgresql-tmpfs.yml"
            @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine mssql).Count | Should -Be 2
        }

        It "never includes application, CMS, or bootstrap compose files" {
            $env:POSTGRES_USE_TMPFS = "true"
            foreach ($engine in @("postgresql", "mssql")) {
                $composeSet = @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine $engine) -join " "
                foreach ($forbidden in @("local-dms.yml", "published-dms.yml", "local-config.yml", "published-config.yml", "bootstrap-dms.yml", "keycloak.yml", "kafka.yml", "swagger-ui.yml")) {
                    $composeSet | Should -Not -BeLike "*$forbidden*"
                }
            }
        }
    }

    Context "stop invocation" {
        BeforeEach {
            $global:StopSliceArguments = $null
            Mock Invoke-RestoreDockerCommand {
                $global:StopSliceArguments = @($ArgumentList)
            } -ModuleName bootstrap-restore
        }

        AfterEach {
            Remove-Variable -Name StopSliceArguments -Scope Global -ErrorAction SilentlyContinue
        }

        It "stops only the db service with the compose files, the preflight env file, and this run's project (<Project>/<Engine>)" -ForEach @(
            @{ Project = "dms-local"; Engine = "postgresql"; ExpectedFile = "postgresql.yml" }
            @{ Project = "dms-published"; Engine = "mssql"; ExpectedFile = "mssql.yml" }
        ) {
            Stop-RestoreDatabaseOnlySlice `
                -ProjectName $Project `
                -DatabaseEngine $Engine `
                -EnvironmentFile "C:\work\.env.restore-preflight"

            $arguments = @($global:StopSliceArguments)
            $joined = $arguments -join " "
            $arguments[0] | Should -Be "compose"
            $joined | Should -BeLike "*-f *$ExpectedFile*"
            $joined | Should -BeLike "*--env-file C:\work\.env.restore-preflight*"
            $joined | Should -BeLike "*-p $Project*"

            # "stop db" and nothing else: the last two tokens are the whole action.
            $arguments[-2] | Should -Be "stop"
            $arguments[-1] | Should -Be "db"

            # Never a teardown: no down, no -v, no volume/orphan removal anywhere in the argv.
            foreach ($forbidden in @("down", "-v", "--volumes", "rm", "--remove-orphans")) {
                $arguments | Should -Not -Contain $forbidden
            }
        }

        It "fails closed through the shared docker runner" {
            Mock Invoke-RestoreDockerCommand { throw "docker unavailable" } -ModuleName bootstrap-restore

            { Stop-RestoreDatabaseOnlySlice -ProjectName "dms-local" -DatabaseEngine postgresql -EnvironmentFile "x.env" } |
                Should -Throw "*docker unavailable*"
        }
    }
}

Describe "Publish-RestoreCandidateWorkspace (whole-tree commit)" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }

        function script:New-WorkspaceTree {
            # Builds a bootstrap-workspace-shaped tree. Includes a dotfile so byte-identity
            # comparison is proven to see hidden files (Linux hides them without -Force).
            param(
                [Parameter(Mandatory)]
                [string]$Path,

                [hashtable]$File = @{
                    "bootstrap-manifest.json"      = '{"version":1}'
                    "ApiSchema/core.json"          = '{"core":true}'
                    ".hidden-marker"               = "dot"
                }
            )

            foreach ($relativePath in $File.Keys) {
                $fullPath = Join-Path $Path $relativePath
                New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
                Set-Content -LiteralPath $fullPath -Value $File[$relativePath] -Encoding utf8 -NoNewline
            }
            return $Path
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:publishRoot = Join-Path $TestDrive "publish-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:publishRoot -Force | Out-Null
        $script:activeRootUnderTest = Join-Path $script:publishRoot ".bootstrap"
        $script:candidateUnderTest = Join-Path $script:publishRoot "candidate-x"
        # Default: both compose projects are stopped.
        Mock docker { $global:LASTEXITCODE = 0 } -ModuleName bootstrap-restore
    }

    It "re-proves the stop precondition for BOTH known compose projects before touching anything" {
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest | Out-Null

        foreach ($projectName in @("dms-local", "dms-published")) {
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
                ($args -join " ") -like "*label=com.docker.compose.project=$projectName*"
            }
        }
    }

    It "refuses to commit while a project is running, leaving active and candidate untouched" {
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-dms-1" } -ModuleName bootstrap-restore
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*still has running containers*"

        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be "old"
        $script:candidateUnderTest | Should -Exist
    }

    It "discards a byte-identical candidate and reuses the active tree as-is" {
        New-WorkspaceTree -Path $script:activeRootUnderTest | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        $result = Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest

        $result.Replaced | Should -BeFalse
        Test-Path -LiteralPath $script:candidateUnderTest | Should -BeFalse
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest ".hidden-marker") -Raw | Should -Be "dot"
    }

    It "replaces the ENTIRE active tree when any byte differs: no stale file survives" {
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{
            "bootstrap-manifest.json" = '{"version":1,"old":true}'
            "stale-only-in-active.txt" = "stale"
        } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        $result = Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest

        $result.Replaced | Should -BeTrue
        Test-Path -LiteralPath $script:candidateUnderTest | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:activeRootUnderTest "stale-only-in-active.txt") | Should -BeFalse
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be '{"version":1}'
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest ".hidden-marker") -Raw | Should -Be "dot"
    }

    It "moves the candidate in when no active workspace exists" {
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        $result = Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest

        $result.Replaced | Should -BeTrue
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "ApiSchema/core.json") -Raw | Should -Be '{"core":true}'
    }

    It "an injected failure between remove and move leaves no active workspace and an intact candidate" {
        Mock Move-Item { throw "injected move failure" } -ModuleName bootstrap-restore
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*injected move failure*"

        # The accepted failure shape: never a partial/mixed tree - the active workspace is gone
        # entirely and the candidate remains intact for diagnosis; the next run re-stages.
        Test-Path -LiteralPath $script:activeRootUnderTest | Should -BeFalse
        Get-Content -LiteralPath (Join-Path $script:candidateUnderTest ".hidden-marker") -Raw | Should -Be "dot"
    }

    It "refuses a candidate without a bootstrap manifest before any stop proof or workspace read" {
        New-Item -ItemType Directory -Path $script:candidateUnderTest -Force | Out-Null
        New-WorkspaceTree -Path $script:activeRootUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*no bootstrap-manifest.json*"

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
        $script:activeRootUnderTest | Should -Exist
    }
}

Describe "New-RestorePreflightEnvironment" {
    BeforeEach {
        $script:preflightRoot = Join-Path $TestDrive "preflight-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:preflightRoot -Force | Out-Null
        $script:baseEnvironmentFile = Join-Path $script:preflightRoot "effective.env"
        @(
            "POSTGRES_DB_NAME=edfi_datamanagementservice"
            "POSTGRES_PASSWORD=secret-pass"
            "POSTGRES_PORT=5544"
            "DMS_DATASTORE=postgresql"
        ) -join "`n" | Set-Content -LiteralPath $script:baseEnvironmentFile -Encoding utf8
        $script:derivedDirectory = Join-Path $script:preflightRoot "derived"
    }

    It "PostgreSQL: derives an env whose only change is a generated, reserved-safe, non-target POSTGRES_DB_NAME" {
        $preflight = New-RestorePreflightEnvironment `
            -EnvironmentFile $script:baseEnvironmentFile `
            -DatabaseEngine postgresql `
            -TargetDatabaseName "edfi_datamanagementservice" `
            -DerivedDirectory $script:derivedDirectory

        $preflight.IsDerived | Should -BeTrue
        $preflight.PreflightDatabaseName | Should -Match "^edfi_dms_restore_preflight_[0-9a-f]{12}$"
        $preflight.PreflightDatabaseName | Should -Not -Be "edfi_datamanagementservice"
        Test-ReservedDatabaseName -DatabaseEngine postgresql -DatabaseName $preflight.PreflightDatabaseName |
            Should -BeFalse

        # Only POSTGRES_DB_NAME changed; every other line of the effective env is carried
        # verbatim so the -DbOnly run behaves identically apart from the initialized database.
        $derivedLines = @(Get-Content -LiteralPath $preflight.EnvironmentFile) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $derivedLines | Should -Contain "POSTGRES_DB_NAME=$($preflight.PreflightDatabaseName)"
        $baselineOtherLines = @(Get-Content -LiteralPath $script:baseEnvironmentFile) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -notlike "POSTGRES_DB_NAME=*" }
        $derivedOtherLines = @($derivedLines | Where-Object { $_ -notlike "POSTGRES_DB_NAME=*" })
        $derivedOtherLines | Should -Be $baselineOtherLines
    }

    It "SQL Server: passes the effective env through unchanged and writes no derived file" {
        $preflight = New-RestorePreflightEnvironment `
            -EnvironmentFile $script:baseEnvironmentFile `
            -DatabaseEngine mssql `
            -TargetDatabaseName "edfi_datamanagementservice" `
            -DerivedDirectory $script:derivedDirectory

        $preflight.IsDerived | Should -BeFalse
        $preflight.EnvironmentFile | Should -Be $script:baseEnvironmentFile
        $preflight.PreflightDatabaseName | Should -Be ""
        Test-Path -LiteralPath $script:derivedDirectory | Should -BeFalse
    }
}

Describe "Invoke-RestoreCatalogQuery" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    It "runs psql through docker exec with the admin transport for PostgreSQL" {
        Mock docker { $global:LASTEXITCODE = 0; "row1" } -ModuleName bootstrap-restore

        $rows = Invoke-RestoreCatalogQuery `
            -DatabaseEngine postgresql `
            -ContainerName "dms-postgresql" `
            -DatabaseName "postgres" `
            -Query "SELECT 1;" `
            -FailureMessage "query failed."

        @($rows) | Should -Be @("row1")
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            ($args -join " ") -eq "exec dms-postgresql psql -U postgres -d postgres -tA -c SELECT 1;"
        }
    }

    It "runs sqlcmd through docker exec with the sa transport for SQL Server" {
        Mock docker { $global:LASTEXITCODE = 0 } -ModuleName bootstrap-restore

        Invoke-RestoreCatalogQuery `
            -DatabaseEngine mssql `
            -ContainerName "dms-mssql" `
            -DatabaseName "master" `
            -MssqlPassword "pw!" `
            -Query "SELECT 1;" `
            -FailureMessage "query failed." | Out-Null

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $joined = $args -join " "
            $joined -like "exec -e SQLCMDPASSWORD=pw! dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -h -1 -W -Q SELECT 1;"
        }
    }

    It "throws the caller's failure message on a nonzero exit" {
        Mock docker { $global:LASTEXITCODE = 1; "connection refused" } -ModuleName bootstrap-restore

        { Invoke-RestoreCatalogQuery `
                -DatabaseEngine postgresql `
                -ContainerName "dms-postgresql" `
                -DatabaseName "postgres" `
                -Query "SELECT 1;" `
                -FailureMessage "Live check failed." } |
            Should -Throw "Live check failed.*connection refused*"
    }
}

Describe "Assert-RestoreTargetSafety" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        # Safe defaults: only the db service is running, every catalog answer is benign, and
        # the live version satisfies the manifest below. Tests override per scenario.
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-db-1|db" } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*server_version*") { return "16.8" }
            if ($Query -like "*ProductVersion*") { return "17.0.900.7" }
            return @()
        } -ModuleName bootstrap-restore
        $script:pgManifest = [pscustomobject]@{ engineVersion = "16.8" }
        $script:mssqlManifest = [pscustomobject]@{ engineVersion = "17.0.900.7" }
    }

    It "passes for a safe PostgreSQL target, skipping the publication query when the target is absent" {
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Not -Throw

        # reserved-catalog + engine-version + replication-slots + target-existence; the
        # in-target publication query must not run against an absent database.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 4 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*pg_publication*" }
    }

    It "passes for a safe SQL Server target" {
        { Assert-RestoreTargetSafety -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:mssqlManifest } |
            Should -Not -Throw
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 3 -Exactly
    }

    It "refuses a denylisted target name before any docker or catalog activity" {
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "postgres" -Manifest $script:pgManifest } |
            Should -Throw "*reserved postgresql system database name*"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "refuses the separate-topology Configuration Service database before any docker or catalog activity" {
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_configurationservice" -Manifest $script:pgManifest -SeparateConfigDatabase -EffectiveConfigDatabaseName "edfi_configurationservice" } |
            Should -Throw "*Configuration Service database*"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "refuses a target the live catalog marks as template/system, whatever its name (<Engine>)" -ForEach @(
        @{ Engine = "postgresql"; QueryPattern = "*datistemplate*" }
        @{ Engine = "mssql"; QueryPattern = "*database_id <= 4*" }
    ) {
        Mock Invoke-RestoreCatalogQuery { "sneaky_system_db" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like $QueryPattern }
        $manifest = if ($Engine -eq "mssql") { $script:mssqlManifest } else { $script:pgManifest }

        { Assert-RestoreTargetSafety -DatabaseEngine $Engine -TargetDatabaseName "sneaky_system_db" -Manifest $manifest } |
            Should -Throw "*live catalog marks restore target*reserved/system*"
    }

    It "refuses when a non-database container of either compose project is running" {
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-db-1|db"; "dms-local-dms-1|dms" } -ModuleName bootstrap-restore

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*running non-database containers*dms-local-dms-1|dms*"
    }

    It "refuses a running container without a readable service label (fail-closed)" {
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-mystery-1|" } -ModuleName bootstrap-restore

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*running non-database containers*dms-local-mystery-1*"
    }

    It "treats a docker failure during the running-service check as indeterminate" {
        Mock docker { $global:LASTEXITCODE = 1; "daemon unreachable" } -ModuleName bootstrap-restore

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*Target safety is indeterminate*daemon unreachable*"
    }

    It "refuses when the live server major is older than the manifest's engine major" {
        $newerManifest = [pscustomobject]@{ engineVersion = "17.2" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $newerManifest } |
            Should -Throw "*live server major version 16 is older than the restore manifest's engine major version 17*"
    }

    It "fails closed on an unparsable or missing live engine version" {
        Mock Invoke-RestoreCatalogQuery { "beta-release" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version*" }
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*Cannot parse a major version*live server*"

        Mock Invoke-RestoreCatalogQuery { @() } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version*" }
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*returned no version*"
    }

    It "refuses a PostgreSQL target bound by a replication slot" {
        Mock Invoke-RestoreCatalogQuery { "dms_cdc_slot" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*pg_replication_slots*" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*bound by replication slot(s): dms_cdc_slot*"
    }

    It "refuses an existing PostgreSQL target that contains a publication" {
        Mock Invoke-RestoreCatalogQuery { "1" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "SELECT 1 FROM pg_database*" }
        Mock Invoke-RestoreCatalogQuery { "dms_publication" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*pg_publication*" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*contains publication(s): dms_publication*"

        # The publication query connects INSIDE the target database, never the admin database.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -like "*pg_publication*" -and $DatabaseName -eq "edfi_datamanagementservice"
        }
    }

    It "refuses a CDC-enabled SQL Server target" {
        Mock Invoke-RestoreCatalogQuery { "edfi_datamanagementservice" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*is_cdc_enabled*" }

        { Assert-RestoreTargetSafety -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:mssqlManifest } |
            Should -Throw "*has CDC enabled*governed new-generation recovery workflow*"
    }
}

Describe "Invoke-RestoreScratchValidation" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }

        function script:New-ScratchInventory {
            # A DMS-only inventory that passes the gate: dms, one resource project (edfi) with
            # its tracked_changes companion, and the engine's always-present support schema.
            # Built fresh per call so tests can mutate FullInventory and ArtifactInventory
            # independently.
            param(
                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql"
            )

            $supportSchemaName = if ($DatabaseEngine -eq "mssql") { "dbo" } else { "public" }
            return @{
                schemas    = @(
                    @{ schemaName = "dms"; objects = @(@{ name = "Document"; type = "table" }, @{ name = "EffectiveSchema"; type = "table" }) },
                    @{ schemaName = "edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = "tracked_changes_edfi"; objects = @(@{ name = "School"; type = "table" }) },
                    @{ schemaName = $supportSchemaName; objects = @() }
                )
                principals = @()
            }
        }

        function script:New-ScratchCatalogFact {
            # The consumer facts record Get-RestoreDatabaseCatalogFact would return from a
            # correctly restored scratch database, matching New-ScratchStage's manifest.
            param(
                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql"
            )

            return [pscustomobject]@{
                ApiSchemaFormatVersion     = "1.0.0"
                EffectiveSchemaHash        = ("ab" * 32)
                ResourceKeyCount           = 42
                ResourceKeySeedHashB64     = [System.Convert]::ToBase64String([byte[]](1..32))
                EngineVersion              = $(if ($DatabaseEngine -eq "mssql") { "17.0.900.7" } else { "16.8" })
                DatabaseCompatibilityLevel = $(if ($DatabaseEngine -eq "mssql") { 170 } else { $null })
                DocumentJsonColumnType     = $(if ($DatabaseEngine -eq "mssql") { "nvarchar" } else { "jsonb" })
                FullInventory              = (New-ScratchInventory -DatabaseEngine $DatabaseEngine)
                ArtifactInventory          = (New-ScratchInventory -DatabaseEngine $DatabaseEngine)
            }
        }

        function script:New-ScratchStage {
            # A staged-package stand-in: a real artifact file whose hash the manifest records,
            # plus the manifest fields scratch validation consumes. The inventorySha256 is
            # computed from the default facts fixture so the comparator passes by construction.
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql",

                [hashtable]$ManifestOverride = @{}
            )

            $artifactExtension = if ($DatabaseEngine -eq "mssql") { "bak" } else { "sql" }
            $artifactPath = Join-Path $Directory "artifact.$artifactExtension"
            Set-Content -LiteralPath $artifactPath -Value "scratch artifact bytes" -Encoding utf8 -NoNewline

            $referenceFact = New-ScratchCatalogFact -DatabaseEngine $DatabaseEngine
            $manifest = @{
                databaseEngine             = $DatabaseEngine
                effectiveSchemaHash        = $referenceFact.EffectiveSchemaHash
                apiSchemaFormatVersion     = $referenceFact.ApiSchemaFormatVersion
                resourceKeyCount           = $referenceFact.ResourceKeyCount
                resourceKeySeedHashB64     = $referenceFact.ResourceKeySeedHashB64
                engineVersion              = $referenceFact.EngineVersion
                documentJsonColumnType     = $referenceFact.DocumentJsonColumnType
                databaseCompatibilityLevel = $referenceFact.DatabaseCompatibilityLevel
                inventorySha256            = (Get-CanonicalInventoryHash -Inventory $referenceFact.ArtifactInventory)
                artifactFileName           = "artifact.$artifactExtension"
                artifactSha256             = (Get-FileSha256Hex -Path $artifactPath)
                projects                   = @("edfi")
            }
            foreach ($overrideKey in $ManifestOverride.Keys) {
                $manifest[$overrideKey] = $ManifestOverride[$overrideKey]
            }

            return [pscustomobject]@{
                StageDirectory = $Directory
                ArtifactPath   = $artifactPath
                Manifest       = [pscustomobject]$manifest
            }
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:scratchRoot = Join-Path $TestDrive "scratch-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:scratchRoot -Force | Out-Null
        $script:candidateFact = [pscustomobject]@{
            EffectiveSchemaHash    = ("ab" * 32)
            ApiSchemaFormatVersion = "1.0.0"
        }

        # Defaults: every docker command succeeds (FILELISTONLY answers with a two-file list),
        # the package's SourceIdentity is one valid row, and the populated count is nonzero.
        Mock docker {
            $global:LASTEXITCODE = 0
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*SourceIdentity*") { return "11111111-1111-1111-1111-111111111111" }
            if ($Query -like "*COUNT(`*)*") { return "5" }
            return @()
        } -ModuleName bootstrap-restore
        $global:ScratchFactToReturn = New-ScratchCatalogFact
        Mock Get-RestoreDatabaseCatalogFact { $global:ScratchFactToReturn } -ModuleName bootstrap-restore
    }

    AfterEach {
        Remove-Variable -Name ScratchFactToReturn -Scope Global -ErrorAction SilentlyContinue
    }

    It "PostgreSQL: replays into a generated scratch, validates, captures the package SourceIdentity, and drops the scratch" {
        $stage = New-ScratchStage -Directory $script:scratchRoot

        $result = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql

        $result.PackageSourceIdentity | Should -Be "11111111-1111-1111-1111-111111111111"
        @($result.ProjectSchemaNames) | Should -Be @("edfi")

        # Replay sequence: role init, terminate, drop-if-exists, create - then cp and psql -f.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*edfi_dms_enqueue_owner*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like 'CREATE DATABASE "edfi_dms_restore_scratch_*' }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "cp * dms-postgresql:/tmp/restore-scratch-*.sql" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U postgres -d edfi_dms_restore_scratch_* -v ON_ERROR_STOP=1 -f /tmp/restore-scratch-*.sql" }

        # The scratch is dropped in finally (the initial defensive drop plus the cleanup drop)
        # and the transient in-container file is removed.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql rm -f /tmp/restore-scratch-*.sql" }
    }

    It "SQL Server: cp, FILELISTONLY, then RESTORE with MOVE clauses derived from the scratch name" {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql

        Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql | Out-Null

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "cp * dms-mssql:/var/opt/mssql/data/restore-scratch-*.bak" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE FILELISTONLY FROM DISK*" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $joined = $args -join " "
            $joined -like "*RESTORE DATABASE [[]edfi_dms_restore_scratch_*] FROM DISK = N'/var/opt/mssql/data/restore-scratch-*.bak' WITH MOVE N'edfi_dms' TO N'/var/opt/mssql/data/edfi_dms_restore_scratch_*.mdf', MOVE N'edfi_dms_log' TO N'/var/opt/mssql/data/edfi_dms_restore_scratch_*_log.ldf', REPLACE;*"
        }

        # Minimal kind: the populated predicate never runs; the scratch is dropped in finally.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*COUNT(`*)*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
    }

    It "a re-hash mismatch aborts before ANY docker activity" {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        Add-Content -LiteralPath $stage.ArtifactPath -Value "tampered"

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*changed since staging*"

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "on <Name>: fails naming the defect, drops the scratch, and performs no further validation" -ForEach @(
        @{ Name = "an effective-schema hash mismatch"; FactMutation = { param($fact) $fact.EffectiveSchemaHash = ("cd" * 32) }; Expected = "*does not match the restore manifest*effectiveSchemaHash*" }
        @{ Name = "inventory drift"; FactMutation = { param($fact) $fact.ArtifactInventory.schemas[1].objects += @{ name = "Smuggled"; type = "table" } }; Expected = "*inventorySha256*" }
        @{ Name = "a dmscs schema in the scratch"; FactMutation = { param($fact) $fact.FullInventory.schemas += @{ schemaName = "dmscs"; objects = @(@{ name = "Application"; type = "table" }) } }; Expected = "*dmscs*" }
        @{ Name = "a lookalike companion schema"; FactMutation = { param($fact) $fact.FullInventory.schemas += @{ schemaName = "tracked_changesx"; objects = @(@{ name = "X"; type = "table" }) } }; Expected = "*tracked_changesx*" }
        @{ Name = "a wrong ApiSchema format version"; FactMutation = { param($fact) $fact.ApiSchemaFormatVersion = "9.9.9" }; Expected = "*apiSchemaFormatVersion*" }
        @{ Name = "a mismatched DocumentJson storage type"; FactMutation = { param($fact) $fact.DocumentJsonColumnType = "json" }; Expected = "*documentJsonColumnType*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        & $FactMutation $global:ScratchFactToReturn

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql } |
            Should -Throw $Expected

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
    }

    It "on <Name> (SQL Server): fails naming the defect and drops the scratch" -ForEach @(
        @{ Name = "a mismatched compatibility level"; FactMutation = { param($fact) $fact.DatabaseCompatibilityLevel = 160 }; Expected = "*databaseCompatibilityLevel*" }
        @{ Name = "a mismatched DocumentJson storage type"; FactMutation = { param($fact) $fact.DocumentJsonColumnType = "varchar" }; Expected = "*documentJsonColumnType*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        & $FactMutation $global:ScratchFactToReturn

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql } |
            Should -Throw $Expected

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
    }

    It "fails when the manifest and the candidate workspace disagree" {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        $script:candidateFact.EffectiveSchemaHash = ("ef" * 32)

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*does not match the candidate workspace's*"
    }

    It "fails when the manifest's project set differs from the scratch partition" {
        $stage = New-ScratchStage -Directory $script:scratchRoot -ManifestOverride @{ projects = @("edfi", "tpdm") }

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*declares projects*edfi, tpdm*"
    }

    It "fails a Populated package whose scratch contains no non-descriptor, non-school-year documents" {
        Mock Invoke-RestoreCatalogQuery { "0" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*COUNT(`*)*" }
        $stage = New-ScratchStage -Directory $script:scratchRoot

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql } |
            Should -Throw "*templateKind 'Populated'*no non-descriptor, non-school-year documents*"
    }

    It "fails the SourceIdentity capture when the scratch has <Name>" -ForEach @(
        @{ Name = "no identity row"; Rows = @(); ExpectedCount = 0 }
        @{ Name = "two identity rows"; Rows = @("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"); ExpectedCount = 2 }
    ) {
        $global:ScratchIdentityRows = $Rows
        Mock Invoke-RestoreCatalogQuery { $global:ScratchIdentityRows } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*SourceIdentity*" }
        $stage = New-ScratchStage -Directory $script:scratchRoot

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*exactly one dms.DataStoreIdentity row, found $ExpectedCount*"

        Remove-Variable -Name ScratchIdentityRows -Scope Global -ErrorAction SilentlyContinue
    }
}

Describe "Invoke-RestoreTargetReplacement" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:targetRoot = Join-Path $TestDrive "target-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:targetRoot -Force | Out-Null
        $script:packageSourceIdentity = "11111111-1111-1111-1111-111111111111"

        # Call-sequence capture across the three mock surfaces: the safety gate must precede
        # the first destructive statement.
        $global:TargetCallSequence = [System.Collections.Generic.List[string]]::new()
        Mock Assert-RestoreTargetSafety { $global:TargetCallSequence.Add("safety") } -ModuleName bootstrap-restore
        Mock docker {
            $global:LASTEXITCODE = 0
            $global:TargetCallSequence.Add("docker " + ($args -join " "))
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            $global:TargetCallSequence.Add("query " + $Query)
            if ($Query -like "*CASE WHEN DB_ID(N'edfi_datamanagementservice')*") { return "1" }
            if ($Query -like "*UPDATE*") { return @() }
            if ($Query -like "*SourceIdentity*") { return "33333333-3333-3333-3333-333333333333" }
            return @()
        } -ModuleName bootstrap-restore
    }

    AfterEach {
        Remove-Variable -Name TargetCallSequence -Scope Global -ErrorAction SilentlyContinue
    }

    It "PostgreSQL: safety gate first, then drop/create/replay of the target, reseed, and verified identity" {
        $stage = New-ScratchStage -Directory $script:targetRoot

        $result = Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql

        $result.RestoredSourceIdentity | Should -Be "33333333-3333-3333-3333-333333333333"

        # The non-destructive gate is the very first act.
        $global:TargetCallSequence[0] | Should -Be "safety"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*pg_terminate_backend*edfi_datamanagementservice*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -eq 'DROP DATABASE IF EXISTS "edfi_datamanagementservice";' }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -eq 'CREATE DATABASE "edfi_datamanagementservice";' }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "cp * dms-postgresql:/tmp/restore-target-*.sql" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -v ON_ERROR_STOP=1 -f /tmp/restore-target-*.sql" }
        # Reseed runs against the TARGET database, then the transient file is removed.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*UPDATE*DataStoreIdentity*" -and $DatabaseName -eq "edfi_datamanagementservice" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql rm -f /tmp/restore-target-*.sql" }
    }

    It "SQL Server: cp, exists-check, single-user drop, FILELISTONLY, RESTORE MOVE with target-derived names" {
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine mssql

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine mssql | Out-Null

        $global:TargetCallSequence[0] | Should -Be "safety"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [[]edfi_datamanagementservice*" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $joined = $args -join " "
            $joined -like "*RESTORE DATABASE [[]edfi_datamanagementservice] FROM DISK = N'/var/opt/mssql/data/restore-target-*.bak' WITH MOVE N'edfi_dms' TO N'/var/opt/mssql/data/edfi_datamanagementservice.mdf', MOVE N'edfi_dms_log' TO N'/var/opt/mssql/data/edfi_datamanagementservice_log.ldf', REPLACE;*"
        }
    }

    It "SQL Server: skips the drop when the target does not exist" {
        Mock Invoke-RestoreCatalogQuery {
            $global:TargetCallSequence.Add("query " + $Query)
            if ($Query -like "*CASE WHEN DB_ID(N'edfi_datamanagementservice')*") { return "0" }
            if ($Query -like "*UPDATE*") { return @() }
            if ($Query -like "*SourceIdentity*") { return "33333333-3333-3333-3333-333333333333" }
            return @()
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine mssql

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine mssql | Out-Null

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SINGLE_USER*" }
    }

    It "a re-hash mismatch after the safety gate aborts before any destructive statement" {
        $stage = New-ScratchStage -Directory $script:targetRoot
        Add-Content -LiteralPath $stage.ArtifactPath -Value "tampered"

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql } |
            Should -Throw "*changed after scratch validation*"

        $global:TargetCallSequence | Should -Be @("safety")
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "PostgreSQL: a mixed-case target is quoted so it cannot case-fold onto a different database" {
        $stage = New-ScratchStage -Directory $script:targetRoot

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "EdFi_DMS" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql | Out-Null

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -ceq 'DROP DATABASE IF EXISTS "EdFi_DMS";' }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -ceq 'CREATE DATABASE "EdFi_DMS";' }
        # The psql connection parameter is exact-case by nature; the replay targets the same
        # database the quoted CREATE produced.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -clike "exec dms-postgresql psql -U postgres -d EdFi_DMS -v ON_ERROR_STOP=1 -f /tmp/restore-target-*.sql" }
    }

    It "PostgreSQL: a copy failure leaves the target completely untouched - no drop, no create" {
        Mock docker {
            $global:TargetCallSequence.Add("docker " + ($args -join " "))
            if ($args[0] -eq "cp") {
                $global:LASTEXITCODE = 1
                "no space left on device"
            }
            else {
                $global:LASTEXITCODE = 0
            }
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql } |
            Should -Throw "*Failed to copy the staged artifact*no space left on device*"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "DROP DATABASE*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "CREATE DATABASE*" }
    }

    It "hard-fails on <Name> (<Engine>) after reseed, before any service can select the target" -ForEach @(
        @{ Name = "zero identity rows"; Engine = "postgresql"; Rows = @(); Expected = "*Expected exactly one dms.DataStoreIdentity row after restore, found 0*" }
        @{ Name = "zero identity rows"; Engine = "mssql"; Rows = @(); Expected = "*Expected exactly one dms.DataStoreIdentity row after restore, found 0*" }
        @{ Name = "two identity rows"; Engine = "postgresql"; Rows = @("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"); Expected = "*found 2*" }
        @{ Name = "two identity rows"; Engine = "mssql"; Rows = @("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"); Expected = "*found 2*" }
        @{ Name = "a non-UUID value"; Engine = "postgresql"; Rows = @("not-a-uuid"); Expected = "*is not a valid UUID*" }
        @{ Name = "a non-UUID value"; Engine = "mssql"; Rows = @("not-a-uuid"); Expected = "*is not a valid UUID*" }
        @{ Name = "the empty UUID"; Engine = "postgresql"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = "*is the empty UUID*" }
        @{ Name = "the empty UUID"; Engine = "mssql"; Rows = @("00000000-0000-0000-0000-000000000000"); Expected = "*is the empty UUID*" }
        @{ Name = "a value equal to the package's"; Engine = "postgresql"; Rows = @("11111111-1111-1111-1111-111111111111"); Expected = "*still matches the package value*" }
        @{ Name = "a value equal to the package's"; Engine = "mssql"; Rows = @("11111111-1111-1111-1111-111111111111"); Expected = "*still matches the package value*" }
    ) {
        $global:TargetIdentityRows = $Rows
        Mock Invoke-RestoreCatalogQuery {
            $global:TargetCallSequence.Add("query " + $Query)
            if ($Query -like "*CASE WHEN DB_ID(N'edfi_datamanagementservice')*") { return "1" }
            if ($Query -like "*UPDATE*") { return @() }
            if ($Query -like "*SourceIdentity*") { return $global:TargetIdentityRows }
            return @()
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine $Engine

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine $Engine } |
            Should -Throw "*failed the SourceIdentity verification*$($Expected.Trim('*'))*"

        Remove-Variable -Name TargetIdentityRows -Scope Global -ErrorAction SilentlyContinue
    }
}

Describe "Remove-RestorePreflightDatabase and Remove-RestoreScratchDatabase" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item function:global:docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        Mock Invoke-RestoreCatalogQuery { @() } -ModuleName bootstrap-restore
    }

    It "drops a generated PostgreSQL preflight database through the admin connection" {
        Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_dms_restore_preflight_0123456789ab"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -eq 'DROP DATABASE IF EXISTS "edfi_dms_restore_preflight_0123456789ab";' -and $DatabaseName -eq "postgres"
        }
    }

    It "is a no-op for SQL Server and for a blank name" {
        Remove-RestorePreflightDatabase -DatabaseEngine mssql -PreflightDatabaseName "edfi_dms_restore_preflight_0123456789ab"
        Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName ""

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "refuses to drop anything that is not a generator-shaped name" {
        { Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_datamanagementservice" } |
            Should -Throw "*Refusing to drop 'edfi_datamanagementservice'*"
        { Remove-RestoreScratchDatabase -DatabaseEngine postgresql -ScratchDatabaseName "edfi_datamanagementservice" -ContainerName "dms-postgresql" } |
            Should -Throw "*Refusing to drop 'edfi_datamanagementservice'*"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "refuses a suffix that is not exactly the generator's 12 hex characters (<Name>)" -ForEach @(
        @{ Name = "too short"; Suffix = "0123456789a" }
        @{ Name = "too long"; Suffix = "0123456789abc" }
    ) {
        { Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_dms_restore_preflight_$Suffix" } |
            Should -Throw "*Refusing to drop*"
        { Remove-RestoreScratchDatabase -DatabaseEngine postgresql -ScratchDatabaseName "edfi_dms_restore_scratch_$Suffix" -ContainerName "dms-postgresql" } |
            Should -Throw "*Refusing to drop*"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "preflight cleanup warns loudly instead of throwing when the server is unreachable" {
        Mock Invoke-RestoreCatalogQuery { throw "server gone" } -ModuleName bootstrap-restore

        $warnings = @()
        Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_dms_restore_preflight_0123456789ab" -WarningVariable warnings 3>$null

        @($warnings) | Should -Not -BeNullOrEmpty
        ([string]$warnings[0]) | Should -BeLike "*Preflight database cleanup did not complete*server gone*"
    }
}
