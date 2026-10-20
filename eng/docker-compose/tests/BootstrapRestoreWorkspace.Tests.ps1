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
        # Function: removal paths use the visible name without a scope qualifier.
        Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
        Import-ManifestModule
    }

    function global:Invoke-TestContainerFileDocker {
        # A stand-in container filesystem for the docker mocks (global, because mock bodies run
        # in the module's session state): "cp" stores the bytes it reads from the host path,
        # and "exec <container> sha256sum <path>" answers with their hash the way sha256sum
        # prints it.
        param([object[]]$DockerArgs)

        if ($DockerArgs[0] -eq "cp") {
            $global:TestContainerFiles[([string]$DockerArgs[2] -split ":", 2)[1]] = [System.IO.File]::ReadAllBytes($DockerArgs[1])
        }
        elseif ($DockerArgs[0] -eq "exec" -and $DockerArgs[2] -eq "sha256sum") {
            $containerBytes = $global:TestContainerFiles[[string]$DockerArgs[3]]
            "$([System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([byte[]]$containerBytes)).ToLowerInvariant())  $($DockerArgs[3])"
        }
    }

    function global:Invoke-TestContainerInputDocker {
        # The stand-in for Invoke-RestoreDockerInputCommand ("docker cp - <container>:<dir>"):
        # runs the production input writer into memory, reads the result back with .NET's own
        # TarReader (header checksum, one regular file), and stores the file in the stand-in
        # container filesystem. With $global:ReplaceStagedArtifactBeforeCopy set, every path in
        # $global:HostStagingPathsToReplace is overwritten with other bytes right BEFORE the
        # writer runs; with $global:CorruptContainerCopy set, the stored copy differs from what
        # was streamed (a damaged transfer).
        param(
            [string[]]$ArgumentList,
            [scriptblock]$InputWriter
        )

        if ($global:ReplaceStagedArtifactBeforeCopy) {
            foreach ($hostPath in @($global:HostStagingPathsToReplace)) {
                (Get-Item -LiteralPath $hostPath).IsReadOnly = $false
                [System.IO.File]::WriteAllText($hostPath, "replaced host staging bytes")
            }
        }

        $inputStream = [System.IO.MemoryStream]::new()
        $writerResult = & $InputWriter $inputStream
        $global:TestContainerInputs.Add($inputStream.ToArray())
        $inputStream.Position = 0
        $tarReader = [System.Formats.Tar.TarReader]::new($inputStream)
        $tarEntry = $tarReader.GetNextEntry($true)
        if ($null -eq $tarEntry -or $tarEntry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile -or $null -ne $tarReader.GetNextEntry()) {
            throw "docker cp - expects exactly one regular file in its tar input."
        }
        $fileBytes = [System.IO.MemoryStream]::new()
        $tarEntry.DataStream.CopyTo($fileBytes)
        $storedBytes = $fileBytes.ToArray()
        if ($global:CorruptContainerCopy) {
            $storedBytes = [System.Text.Encoding]::UTF8.GetBytes("damaged in transfer")
        }
        $containerDirectory = ([string]$ArgumentList[2] -split ":", 2)[1]
        $global:TestContainerFiles["$containerDirectory/$($tarEntry.Name)"] = $storedBytes

        return $writerResult
    }

    function global:New-TestRetainedPackage {
        # A real in-memory .nupkg-shaped zip holding the artifact entry, as staging retains it.
        param(
            [Parameter(Mandatory)]
            [string]$EntryName,

            [Parameter(Mandatory)]
            [byte[]]$ArtifactBytes
        )

        $packageStream = [System.IO.MemoryStream]::new()
        $packageArchive = [System.IO.Compression.ZipArchive]::new($packageStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        $entryStream = $packageArchive.CreateEntry($EntryName).Open()
        $entryStream.Write($ArtifactBytes, 0, $ArtifactBytes.Length)
        $entryStream.Dispose()
        $packageArchive.Dispose()
        return , $packageStream.ToArray()
    }
}

AfterAll {
    Remove-Item -LiteralPath Function:\Invoke-TestContainerFileDocker -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\Invoke-TestContainerInputDocker -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\New-TestRetainedPackage -ErrorAction SilentlyContinue
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

    It "copies the published schema tool into the candidate before the prepare phases, so their real tool discovery resolves the candidate's copy" {
        # The published tool's documented location is <bootstrap root>/tools/api-schema-tools.
        $activeRoot = Join-Path $script:candidateWorkspaceRoot "active-bootstrap"
        $publishedTool = Join-Path $activeRoot "tools/api-schema-tools"
        New-Item -ItemType Directory -Path $publishedTool -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $publishedTool "api-schema-tools") -Value "published apphost" -NoNewline
        Set-Content -LiteralPath (Join-Path $publishedTool "EdFi.DataManagementService.SchemaTools.dll") -Value "published assembly" -NoNewline

        # The REAL module import-time containment only accepts candidates under the real
        # .bootstrap-restore, so this test uses a (cleaned-up) workspace there.
        $realWorkspaceRoot = Join-Path $script:restoreWorkspaceRoot "tool-preserve-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $realWorkspaceRoot -Force | Out-Null
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath
        $manifestModulePath = Join-Path $script:dockerComposeDir "bootstrap-manifest.psm1"
        $schemaToolModulePath = Join-Path $script:dockerComposeDir "bootstrap-schema-tool.psm1"
        @"
param([string]`$EnvironmentFile)
Import-Module '$($manifestModulePath.Replace("'", "''"))' -Force -Global
Import-Module '$($schemaToolModulePath.Replace("'", "''"))' -Force
Add-Content -LiteralPath '$($script:prepareLogPath.Replace("'", "''"))' -Value "schema tool=[`$(Resolve-DmsSchemaTool)]"
Set-Content -LiteralPath (Join-Path `$env:DMS_BOOTSTRAP_ROOT_OVERRIDE 'bootstrap-manifest.json') -Value '{"version":1}'
"@ | Set-Content -LiteralPath $stub.SchemaScriptPath -Encoding utf8

        try {
            $result = New-RestoreCandidateWorkspace `
                -EnvironmentFile "x.env" `
                -WorkspaceRoot $realWorkspaceRoot `
                -PrepareSchemaScriptPath $stub.SchemaScriptPath `
                -PrepareClaimsScriptPath $stub.ClaimsScriptPath `
                -ActiveBootstrapRoot $activeRoot

            $candidateTool = Join-Path $result.CandidateDirectory "tools/api-schema-tools"
            @(Get-Content -LiteralPath $script:prepareLogPath)[0] | Should -Be "schema tool=[$(Join-Path $candidateTool 'api-schema-tools')]"
            # The candidate carries the whole published tool, so the whole-tree commit keeps it.
            Get-Content -LiteralPath (Join-Path $candidateTool "EdFi.DataManagementService.SchemaTools.dll") -Raw | Should -Be "published assembly"
            # The active tool is read, never moved or changed.
            Get-Content -LiteralPath (Join-Path $publishedTool "api-schema-tools") -Raw | Should -Be "published apphost"
        }
        finally {
            if (Test-Path -LiteralPath $realWorkspaceRoot) {
                Remove-Item -LiteralPath $realWorkspaceRoot -Recurse -Force
            }
        }
    }

    It "builds a candidate without tools when no published schema tool exists" {
        $stub = New-CandidatePrepareStub -Directory $script:candidateWorkspaceRoot -LogPath $script:prepareLogPath
        $activeRoot = Join-Path $script:candidateWorkspaceRoot "active-without-tools"
        New-Item -ItemType Directory -Path $activeRoot -Force | Out-Null

        $result = New-RestoreCandidateWorkspace `
            -EnvironmentFile "x.env" `
            -WorkspaceRoot $script:candidateWorkspaceRoot `
            -PrepareSchemaScriptPath $stub.SchemaScriptPath `
            -PrepareClaimsScriptPath $stub.ClaimsScriptPath `
            -ActiveBootstrapRoot $activeRoot

        Join-Path $result.CandidateDirectory "tools" | Should -Not -Exist
        $result.CandidateManifestPath | Should -Exist
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
            -Manifest (New-CrossCheckManifest -Override @{ databaseEngine = "mssql"; documentJsonColumnType = "nvarchar(max)" }) `
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
        @{ Name = "databaseEngine"; Override = @{ databaseEngine = "mssql"; documentJsonColumnType = "nvarchar(max)" }; Engine = "postgresql"; Expected = "*declares databaseEngine 'mssql'*selected 'postgresql'*" }
        @{ Name = "documentJsonColumnType"; Override = @{ documentJsonColumnType = "json" }; Engine = "postgresql"; Expected = "*DocumentJson physical baseline mismatch*'json'*'jsonb'*" }
        @{ Name = "a narrowed SQL Server documentJsonColumnType"; Override = @{ databaseEngine = "mssql"; documentJsonColumnType = "nvarchar(4000)" }; Engine = "mssql"; Expected = "*DocumentJson physical baseline mismatch*'nvarchar(4000)'*'nvarchar(max)'*" }
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
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
        # The previous tree was set aside and removed once the candidate was in place.
        @(Get-ChildItem -LiteralPath (Join-Path $script:publishRoot ".bootstrap-restore") -Directory -Filter "previous-*") | Should -BeNullOrEmpty
    }

    It "keeps the published schema tool the candidate carries when it replaces the active tree" {
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{
            "bootstrap-manifest.json"             = '{"version":1,"old":true}'
            "tools/api-schema-tools/api-schema-tools" = "published apphost"
        } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest -File @{
            "bootstrap-manifest.json"             = '{"version":1}'
            "tools/api-schema-tools/api-schema-tools" = "published apphost"
        } | Out-Null

        $result = Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest

        $result.Replaced | Should -BeTrue
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "tools/api-schema-tools/api-schema-tools") -Raw | Should -Be "published apphost"
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be '{"version":1}'
    }

    It "moves the candidate in when no active workspace exists" {
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        $result = Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest

        $result.Replaced | Should -BeTrue
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "ApiSchema/core.json") -Raw | Should -Be '{"core":true}'
    }

    It "a failure publishing the candidate renames the previous tree back and keeps no backup" {
        Mock Move-Item { throw "injected move failure" } -ModuleName bootstrap-restore -ParameterFilter { $LiteralPath -eq $script:candidateUnderTest }
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old"; ".hidden-marker" = "old-dot" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "Publishing the restore candidate failed: injected move failure The previous bootstrap workspace was restored and the restore target was not touched."

        # The previous tree is back, whole, under the active name; no backup is left behind.
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be "old"
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest ".hidden-marker") -Raw | Should -Be "old-dot"
        @(Get-ChildItem -LiteralPath (Join-Path $script:publishRoot ".bootstrap-restore") -Directory -Filter "previous-*") | Should -BeNullOrEmpty
        # The candidate is the caller's to clean up (the wrapper's finally removes it).
        $script:candidateUnderTest | Should -Exist
    }

    It "a failure restoring the previous tree names where it was kept intact" {
        Mock Move-Item { throw "injected move failure" } -ModuleName bootstrap-restore -ParameterFilter { $LiteralPath -eq $script:candidateUnderTest }
        Mock Move-Item { throw "injected restore failure" } -ModuleName bootstrap-restore -ParameterFilter { $LiteralPath -like "*previous-*" }
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*injected move failure*could not be restored: injected restore failure It is kept intact at '*previous-*'*"

        $kept = @(Get-ChildItem -LiteralPath (Join-Path $script:publishRoot ".bootstrap-restore") -Directory -Filter "previous-*")
        $kept | Should -HaveCount 1
        Get-Content -LiteralPath (Join-Path $kept[0].FullName "bootstrap-manifest.json") -Raw | Should -Be "old"
    }

    It "a failure setting the previous tree aside changes nothing" {
        Mock Move-Item { throw "injected aside failure" } -ModuleName bootstrap-restore -ParameterFilter { $LiteralPath -eq $script:activeRootUnderTest }
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*injected aside failure*"

        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be "old"
        Get-Content -LiteralPath (Join-Path $script:candidateUnderTest ".hidden-marker") -Raw | Should -Be "dot"
    }

    It "a non-terminating provider error <Case> still stops publication and keeps the previous tree" -ForEach @(
        @{ Case = "setting the previous tree aside"; Step = "aside" }
        @{ Case = "renaming the candidate into place"; Step = "publish" }
    ) {
        # Like a real provider error, the mock's Write-Error is non-terminating unless the call
        # itself asked for -ErrorAction Stop; the module does not inherit a caller's Stop preference.
        $step = $Step
        Mock Move-Item {
            $boundAction = if ($PesterBoundParameters.ContainsKey("ErrorAction")) { $PesterBoundParameters["ErrorAction"] } else { "Continue" }
            Write-Error -Message "injected non-terminating move failure" -ErrorAction $boundAction
        } -ModuleName bootstrap-restore -ParameterFilter {
            ($step -eq "aside" -and $LiteralPath -eq $script:activeRootUnderTest) -or ($step -eq "publish" -and $LiteralPath -eq $script:candidateUnderTest)
        }
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{ "bootstrap-manifest.json" = "old"; ".hidden-marker" = "old-dot" } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*injected non-terminating move failure*"

        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be "old"
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest ".hidden-marker") -Raw | Should -Be "old-dot"
        Join-Path $script:activeRootUnderTest "candidate-x" | Should -Not -Exist -Because "the candidate must never be moved inside the active tree"
        $script:candidateUnderTest | Should -Exist
        @(Get-ChildItem -LiteralPath (Join-Path $script:publishRoot ".bootstrap-restore") -Directory -Filter "previous-*" -ErrorAction SilentlyContinue) |
            Should -BeNullOrEmpty
    }

    It "refuses to replace an active tree holding another project's retained CDC configuration, even with every project stopped" {
        $peerConfiguration = "cdc-runtime/bootstrap-0123456789abcdef0123456789abcdef.settings.json"
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{
            "bootstrap-manifest.json" = "old"
            $peerConfiguration        = "published-cdc-settings"
        } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw "*holds retained CDC configuration or state for dms-local or dms-published*"

        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest $peerConfiguration) -Raw | Should -Be "published-cdc-settings"
        Get-Content -LiteralPath (Join-Path $script:activeRootUnderTest "bootstrap-manifest.json") -Raw | Should -Be "old"
        $script:candidateUnderTest | Should -Exist
    }

    It "refuses to replace an active tree that nests a retained dms-published deployment's source-state journals" {
        $journal = Join-Path $script:activeRootUnderTest "cdc-state/published/journal.json"
        New-WorkspaceTree -Path $script:activeRootUnderTest -File @{
            "bootstrap-manifest.json"            = "old"
            "cdc-state/published/journal.json"   = "journal"
        } | Out-Null
        New-WorkspaceTree -Path $script:candidateUnderTest | Out-Null
        $deploymentDirectory = Join-Path $script:publishRoot ".cdc-deployments"
        New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
        $deploymentPath = Join-Path $deploymentDirectory "dms-published.json"
        @{ Version = 1; Project = "dms-published"; Entries = @(@{ StatePath = (Split-Path -Parent $journal) }) } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $deploymentPath -Encoding utf8
        if ($IsLinux) {
            [System.IO.File]::SetUnixFileMode($deploymentDirectory, [System.IO.UnixFileMode]448)
            [System.IO.File]::SetUnixFileMode($deploymentPath, [System.IO.UnixFileMode]384)
        }
        # The CDC guard qualifies the inventory only on Linux (owner-only files); elsewhere it
        # cannot read it and refuses as unreadable. Either way nothing may be removed.
        $expectedRefusal = if ($IsLinux) { "*blocked by a surviving protected source-state root*" } else { "*protection inventory is unreadable*" }

        { Publish-RestoreCandidateWorkspace -CandidateDirectory $script:candidateUnderTest -ActiveBootstrapRoot $script:activeRootUnderTest } |
            Should -Throw $expectedRefusal

        Get-Content -LiteralPath $journal -Raw | Should -Be "journal"
        $script:candidateUnderTest | Should -Exist
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

    It "PostgreSQL: refuses a target spelled like POSTGRES_USER (<User>) before writing any derived env" -ForEach @(
        @{ User = "edfi_datamanagementservice" }
        @{ User = "EDFI_DataManagementService" }
    ) {
        { New-RestorePreflightEnvironment `
                -EnvironmentFile $script:baseEnvironmentFile `
                -DatabaseEngine postgresql `
                -TargetDatabaseName "edfi_datamanagementservice" `
                -PostgresUser $User `
                -DerivedDirectory $script:derivedDirectory } |
            Should -Throw "*same name as the PostgreSQL administrator (POSTGRES_USER)*"

        Test-Path -LiteralPath $script:derivedDirectory | Should -BeFalse
    }

    It "PostgreSQL: refuses a POSTGRES_USER over 63 bytes, which PostgreSQL would truncate (<Case>), before writing any derived env" -ForEach @(
        @{ Case = "64 ASCII bytes"; User = ("a" * 64) }
        @{ Case = "64 UTF-8 bytes in 32 characters"; User = ([string][char]0xE9 * 32) }
    ) {
        { New-RestorePreflightEnvironment `
                -EnvironmentFile $script:baseEnvironmentFile `
                -DatabaseEngine postgresql `
                -TargetDatabaseName ("a" * 63) `
                -PostgresUser $User `
                -DerivedDirectory $script:derivedDirectory } |
            Should -Throw "*administrator name (POSTGRES_USER) is longer than 63 bytes*"

        Test-Path -LiteralPath $script:derivedDirectory | Should -BeFalse
    }

    It "PostgreSQL: a custom POSTGRES_USER that differs from the target still derives the preflight env" {
        $preflight = New-RestorePreflightEnvironment `
            -EnvironmentFile $script:baseEnvironmentFile `
            -DatabaseEngine postgresql `
            -TargetDatabaseName "edfi_datamanagementservice" `
            -PostgresUser "dmsadmin" `
            -DerivedDirectory $script:derivedDirectory

        $preflight.IsDerived | Should -BeTrue
        @(Get-Content -LiteralPath $preflight.EnvironmentFile) | Should -Contain "POSTGRES_DB_NAME=$($preflight.PreflightDatabaseName)"
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

Describe "Wait-RestorePostgresqlTcpReady" {
    BeforeEach {
        $global:TcpReadyProbes = [System.Collections.Generic.List[string]]::new()
        $global:TcpReadyAnswersAfter = 0
        # A fresh volume's initialization server answers only the socket: every probe without -h
        # succeeds, and a TCP probe succeeds only once TcpReadyAnswersAfter earlier TCP probes failed
        # (0 = never).
        Mock Test-NativeCommandWithTimeout {
            $global:TcpReadyProbes.Add("$FilePath $($ArgumentList -join ' ') timeout=$TimeoutSeconds")
            if ($ArgumentList -notcontains "-h") { return $true }
            $tcpProbeCount = @($global:TcpReadyProbes | Where-Object { $_ -like "* -h *" }).Count
            return ($global:TcpReadyAnswersAfter -gt 0 -and $tcpProbeCount -gt $global:TcpReadyAnswersAfter)
        } -ModuleName bootstrap-restore
        Mock Start-Sleep { } -ModuleName bootstrap-restore
    }

    AfterEach {
        Remove-Variable -Name TcpReadyProbes, TcpReadyAnswersAfter -Scope Global -ErrorAction SilentlyContinue
    }

    It "returns only after a bounded TCP probe on 127.0.0.1 succeeds, never on a socket-only answer" {
        $global:TcpReadyAnswersAfter = 2

        Wait-RestorePostgresqlTcpReady -PostgresUser "dmsadmin"

        @($global:TcpReadyProbes) | Should -Be @(
            "docker exec dms-postgresql pg_isready -h 127.0.0.1 -p 5432 -U dmsadmin timeout=10"
            "docker exec dms-postgresql pg_isready -h 127.0.0.1 -p 5432 -U dmsadmin timeout=10"
            "docker exec dms-postgresql pg_isready -h 127.0.0.1 -p 5432 -U dmsadmin timeout=10"
        )
        Should -Invoke Start-Sleep -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Seconds -eq 3 }
    }

    It "throws after the probe bound while only the socket answers, without sleeping after the last probe" {
        { Wait-RestorePostgresqlTcpReady -MaxAttempts 3 } |
            Should -Throw "PostgreSQL (dms-postgresql) did not accept TCP connections on 127.0.0.1:5432 after 3 readiness probes*Nothing was restored."

        @($global:TcpReadyProbes) | Should -HaveCount 3
        @($global:TcpReadyProbes | Where-Object { $_ -notlike "docker exec dms-postgresql pg_isready -h 127.0.0.1 -p 5432 -U postgres timeout=10" }) | Should -BeNullOrEmpty
        Should -Invoke Start-Sleep -ModuleName bootstrap-restore -Times 2 -Exactly
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
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

    It "runs psql as the caller's PostgreSQL administrator when POSTGRES_USER overrides it" {
        Mock docker { $global:LASTEXITCODE = 0 } -ModuleName bootstrap-restore

        Invoke-RestoreCatalogQuery `
            -DatabaseEngine postgresql `
            -ContainerName "dms-postgresql" `
            -DatabaseName "postgres" `
            -PostgresUser "dmsadmin" `
            -Query "SELECT 1;" `
            -FailureMessage "query failed." | Out-Null

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            ($args -join " ") -eq "exec dms-postgresql psql -U dmsadmin -d postgres -tA -c SELECT 1;"
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

    Context "standard output and standard error" {
        BeforeEach {
            # The stub runs a real child process, so rows and diagnostics leave on the two OS
            # streams exactly as docker exec's would, and the exit code is the child's own.
            $global:CatalogStreamCalls = [System.Collections.Generic.List[string]]::new()
            Mock docker {
                $global:CatalogStreamCalls.Add($args -join " ")
                & ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -NoProfile -NonInteractive -Command $global:CatalogStreamChildCommand
            } -ModuleName bootstrap-restore
        }

        AfterEach {
            Remove-Variable -Name CatalogStreamCalls, CatalogStreamChildCommand -Scope Global -ErrorAction SilentlyContinue
        }

        It "<Engine>: a successful query returns only its standard-output rows, never its standard-error diagnostic" -ForEach @(
            @{ Engine = "postgresql"; Database = "postgres"; Transport = "exec dms-postgresql psql -U postgres -d postgres -tA -c SELECT 1;"; Diagnostic = "WARNING:  there is no transaction in progress" }
            @{ Engine = "mssql"; Database = "master"; Transport = "exec -e SQLCMDPASSWORD=pw! dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -h -1 -W -Q SELECT 1;"; Diagnostic = "Sqlcmd: Warning: The last operation was terminated because the user pressed CTRL+C." }
        ) {
            $global:CatalogStreamChildCommand = "[Console]::Out.WriteLine('row1'); [Console]::Error.WriteLine('$Diagnostic'); [Console]::Out.WriteLine('row2'); exit 0"
            $container = if ($Engine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }

            $rows = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $Engine `
                -ContainerName $container `
                -DatabaseName $Database `
                -MssqlPassword "pw!" `
                -Query "SELECT 1;" `
                -FailureMessage "query failed."

            @($rows) | Should -Be @("row1", "row2")
            @($global:CatalogStreamCalls) | Should -Be @($Transport)
        }

        It "<Engine>: a failed query throws with its standard-error diagnostic and its partial output" -ForEach @(
            @{ Engine = "postgresql"; Database = "postgres"; Diagnostic = "ERROR:  relation dms.missing does not exist" }
            @{ Engine = "mssql"; Database = "master"; Diagnostic = "Msg 208, Level 16, State 1: Invalid object name dms.missing." }
        ) {
            $global:CatalogStreamChildCommand = "[Console]::Out.WriteLine('partial-row'); [Console]::Error.WriteLine('$Diagnostic'); exit 1"
            $container = if ($Engine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }

            $message = $null
            try {
                Invoke-RestoreCatalogQuery `
                    -DatabaseEngine $Engine `
                    -ContainerName $container `
                    -DatabaseName $Database `
                    -MssqlPassword "pw!" `
                    -Query "SELECT 1;" `
                    -FailureMessage "Live check failed." | Out-Null
            }
            catch {
                $message = $_.Exception.Message
            }

            # The two pipes are drained independently, so only their presence is asserted, not
            # their relative order.
            $message | Should -BeLike "Live check failed. (*)"
            $message | Should -BeLike "*$Diagnostic*"
            $message | Should -BeLike "*partial-row*"
            @($global:CatalogStreamCalls) | Should -HaveCount 1
            $global:CatalogStreamCalls[0] | Should -BeLike "exec *$container*-d $Database *"
        }
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        # Safe defaults: only the db service is running, every catalog answer is benign, the
        # live version satisfies the manifest below, and the PostgreSQL platform is supported
        # (18.6, UTF-8 template1). Tests override per scenario.
        Mock docker { $global:LASTEXITCODE = 0; "dms-local-db-1|db" } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*server_version_num*") { return "180006|UTF8" }
            if ($Query -like "*server_version*") { return "18.6" }
            if ($Query -like "*ProductVersion*") { return "17.0.900.7" }
            return @()
        } -ModuleName bootstrap-restore
        $script:pgManifest = [pscustomobject]@{ engineVersion = "18.6" }
        $script:mssqlManifest = [pscustomobject]@{ engineVersion = "17.0.900.7" }
    }

    It "PostgreSQL: every live check, including the in-target publication query, connects as the caller's administrator" {
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*server_version_num*") { return "180006|UTF8" }
            if ($Query -like "*server_version*") { return "18.6" }
            if ($Query -like "SELECT 1 FROM pg_database*") { return "1" }
            return @()
        } -ModuleName bootstrap-restore

        Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest -PostgresUser "dmsadmin"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -like "*pg_publication*" -and $DatabaseName -ceq "edfi_datamanagementservice" -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
    }

    It "passes for a safe PostgreSQL target, skipping the publication query when the target is absent" {
        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Not -Throw

        # reserved-catalog + live-sessions + engine-version + platform + replication-slots + target-existence;
        # the in-target publication query must not run against an absent database.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 6 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*pg_publication*" }
    }

    It "passes for a safe SQL Server target" {
        { Assert-RestoreTargetSafety -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:mssqlManifest } |
            Should -Not -Throw
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 4 -Exactly
    }

    It "<Engine>: checks all target sessions from the admin database, including idle clients" -ForEach @(
        @{ Engine = "postgresql"; AdminDatabase = "postgres"; SessionQuery = "SELECT pid FROM pg_stat_activity WHERE datname = 'edfi_datamanagementservice' AND pid <> pg_backend_pid();" }
        @{ Engine = "mssql"; AdminDatabase = "master"; SessionQuery = "SET NOCOUNT ON; SELECT session_id FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'edfi_datamanagementservice') AND session_id <> @@SPID;" }
    ) {
        $manifest = if ($Engine -eq "mssql") { $script:mssqlManifest } else { $script:pgManifest }

        Assert-RestoreTargetSafety -DatabaseEngine $Engine -TargetDatabaseName "edfi_datamanagementservice" -Manifest $manifest

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq $SessionQuery -and $DatabaseName -ceq $AdminDatabase
        }
    }

    It "<Engine>: refuses a connected client even when only database containers are running" -ForEach @(
        @{ Engine = "postgresql"; SessionPattern = "*FROM pg_stat_activity*" }
        @{ Engine = "mssql"; SessionPattern = "*FROM sys.dm_exec_sessions*" }
    ) {
        Mock Invoke-RestoreCatalogQuery { "57" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like $SessionPattern }
        $manifest = if ($Engine -eq "mssql") { $script:mssqlManifest } else { $script:pgManifest }

        { Assert-RestoreTargetSafety -DatabaseEngine $Engine -TargetDatabaseName "edfi_datamanagementservice" -Manifest $manifest } |
            Should -Throw "*Restore target 'edfi_datamanagementservice' has connected sessions*Stop all clients*"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter {
            $Query -like "*server_version*" -or $Query -like "*ProductVersion*"
        }
    }

    It "<Engine>: fails closed when the session check fails" -ForEach @(
        @{ Engine = "postgresql"; SessionPattern = "*FROM pg_stat_activity*" }
        @{ Engine = "mssql"; SessionPattern = "*FROM sys.dm_exec_sessions*" }
    ) {
        Mock Invoke-RestoreCatalogQuery { throw $FailureMessage } -ModuleName bootstrap-restore -ParameterFilter { $Query -like $SessionPattern }
        $manifest = if ($Engine -eq "mssql") { $script:mssqlManifest } else { $script:pgManifest }

        { Assert-RestoreTargetSafety -DatabaseEngine $Engine -TargetDatabaseName "edfi_datamanagementservice" -Manifest $manifest } |
            Should -Throw "Live-session check failed for restore target 'edfi_datamanagementservice'."
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

    It "SQL Server: refuses the separate topology's fixed edfi_configurationservice before any docker or catalog activity, even when the effective CMS name differs" {
        { Assert-RestoreTargetSafety -DatabaseEngine mssql -TargetDatabaseName "EDFI_ConfigurationService" -Manifest $script:mssqlManifest -SeparateConfigDatabase -EffectiveConfigDatabaseName "ambient_cms" } |
            Should -Throw "*resolves to 'edfi_configurationservice'*Restore never replaces a separate CMS database."
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
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
        $newerManifest = [pscustomobject]@{ engineVersion = "19.2" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $newerManifest } |
            Should -Throw "*live server major version 18 is older than the restore manifest's engine major version 19*"
    }

    It "PostgreSQL: reads the platform floor once, from the admin database, as the caller's administrator" {
        Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest -PostgresUser "dmsadmin"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq "SELECT current_setting('server_version_num') || '|' || COALESCE((SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = 'template1'), '');" -and
            $DatabaseName -ceq "postgres" -and $PostgresUser -ceq "dmsadmin"
        }
    }

    It "PostgreSQL: accepts a supported platform (<Answer>), including an older-major package on it" -ForEach @(
        @{ Answer = "180000|UTF8" }
        @{ Answer = "180006|UTF8" }
        @{ Answer = "190002|UTF8" }
    ) {
        Mock Invoke-RestoreCatalogQuery { $Answer } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version_num*" }
        $olderPackage = [pscustomobject]@{ engineVersion = "16.8" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $olderPackage } |
            Should -Not -Throw
    }

    It "PostgreSQL: refuses a server older than PostgreSQL 18 (<Answer>), even for a package from an older engine" -ForEach @(
        @{ Answer = "170005|UTF8"; Version = "17.5"; Detected = "server_version_num 170005" }
        @{ Answer = "160008|UTF8"; Version = "16.8"; Detected = "server_version_num 160008" }
        @{ Answer = "179999|UTF8"; Version = "17.99"; Detected = "server_version_num 179999" }
    ) {
        # The engine-major check alone admits these: the live major is not older than the package's.
        Mock Invoke-RestoreCatalogQuery { $Answer } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version_num*" }
        Mock Invoke-RestoreCatalogQuery { $Version } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version')*" }
        $olderPackage = [pscustomobject]@{ engineVersion = "16.8" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $olderPackage } |
            Should -Throw "DMS requires PostgreSQL 18 or later with a UTF-8 database encoding*Detected: $Detected."
        # Refused before the remaining live checks.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*pg_replication_slots*" }
    }

    It "PostgreSQL: refuses a non-UTF-8 database-creation encoding (template1 <Encoding>)" -ForEach @(
        @{ Encoding = "SQL_ASCII" }
        @{ Encoding = "LATIN1" }
        @{ Encoding = "WIN1252" }
        @{ Encoding = "utf8" }
    ) {
        Mock Invoke-RestoreCatalogQuery { "180006|$Encoding" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version_num*" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "DMS requires PostgreSQL 18 or later with a UTF-8 database encoding*template1*Detected: template1 encoding $Encoding."
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*pg_replication_slots*" }
    }

    It "PostgreSQL: fails closed when template1's encoding cannot be read" {
        Mock Invoke-RestoreCatalogQuery { "180006|" } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version_num*" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*Detected: template1 encoding unavailable."
    }

    It "PostgreSQL: fails closed on an unreadable or missing platform answer (<Case>)" -ForEach @(
        @{ Case = "no row"; Rows = @() }
        @{ Case = "blank row"; Rows = @("  ") }
        @{ Case = "no separator"; Rows = @("180006") }
        @{ Case = "non-numeric version"; Rows = @("eighteen|UTF8") }
    ) {
        Mock Invoke-RestoreCatalogQuery { $Rows } -ModuleName bootstrap-restore -ParameterFilter { $Query -like "*server_version_num*" }

        { Assert-RestoreTargetSafety -DatabaseEngine postgresql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:pgManifest } |
            Should -Throw "*PostgreSQL platform check returned an unreadable answer*refusing on an indeterminate answer*"
    }

    It "SQL Server: does not run the PostgreSQL platform check" {
        Assert-RestoreTargetSafety -DatabaseEngine mssql -TargetDatabaseName "edfi_datamanagementservice" -Manifest $script:mssqlManifest

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*server_version_num*" }
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
                DocumentJsonColumnType     = $(if ($DatabaseEngine -eq "mssql") { "nvarchar(max)" } else { "jsonb" })
                FullInventory              = (New-ScratchInventory -DatabaseEngine $DatabaseEngine)
                ArtifactInventory          = (New-ScratchInventory -DatabaseEngine $DatabaseEngine)
                DatabaseLevelObjects       = [string[]]@()
            }
        }

        function script:New-ScratchStage {
            # A staged-package stand-in: the retained authenticated package bytes (a real zip
            # holding the artifact entry) the transfers stream from, a host-side record of the
            # artifact at ArtifactPath like staging leaves, plus the manifest fields scratch
            # validation consumes. The inventorySha256 is computed from the default facts
            # fixture so the comparator passes by construction.
            param(
                [Parameter(Mandatory)]
                [string]$Directory,

                [ValidateSet("postgresql", "mssql")]
                [string]$DatabaseEngine = "postgresql",

                [hashtable]$ManifestOverride = @{}
            )

            $artifactExtension = if ($DatabaseEngine -eq "mssql") { "bak" } else { "sql" }
            $artifactPath = Join-Path $Directory "artifact.$artifactExtension"
            $artifactBytes = [System.Text.Encoding]::UTF8.GetBytes("scratch artifact bytes")
            [System.IO.File]::WriteAllBytes($artifactPath, $artifactBytes)
            $artifactEntryName = "content/artifact.$artifactExtension"
            $packageBytes = New-TestRetainedPackage -EntryName $artifactEntryName -ArtifactBytes $artifactBytes

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
                StageDirectory    = $Directory
                PackageBytes      = $packageBytes
                PackageSha256     = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($packageBytes)).ToLowerInvariant()
                ArtifactEntryName = $artifactEntryName
                ArtifactPath      = $artifactPath
                Manifest          = [pscustomobject]$manifest
            }
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:scratchRoot = Join-Path $TestDrive "scratch-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:scratchRoot -Force | Out-Null
        $script:candidateFact = [pscustomobject]@{
            EffectiveSchemaHash    = ("ab" * 32)
            ApiSchemaFormatVersion = "1.0.0"
            SchemaFilePaths        = [string[]]@("$script:scratchRoot/ApiSchema/schemas/Ed-Fi/ApiSchema.json")
        }

        # The authoritative inventory is derived from the CANDIDATE schema by replaying its DDL
        # into a throwaway database; the derivation itself is covered by its own tests. Here it
        # defaults to agreeing with whatever scratch inventory the test returns, so existing
        # cases still fail for their own reasons. A test sets the override to model a package
        # whose artifact (and manifest) carry objects the candidate schema does not define.
        $global:AuthoritativeInventoryOverride = $null
        Mock Get-RestoreAuthoritativeObjectInventory {
            if ($null -ne $global:AuthoritativeInventoryOverride) {
                return $global:AuthoritativeInventoryOverride
            }
            return $global:ScratchFactToReturn.FullInventory
        } -ModuleName bootstrap-restore

        # Defaults: every docker command succeeds (FILELISTONLY answers with a two-file list,
        # cp and sha256sum act on a stand-in container filesystem), the package's
        # SourceIdentity is one valid row, and the populated count is nonzero.
        $global:TestContainerFiles = @{}
        $global:TestContainerInputs = [System.Collections.Generic.List[byte[]]]::new()
        $global:ReplaceStagedArtifactBeforeCopy = $false
        $global:HostStagingPathsToReplace = @()
        $global:CorruptContainerCopy = $false
        Mock docker {
            $global:LASTEXITCODE = 0
            Invoke-TestContainerFileDocker -DockerArgs $args
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreDockerInputCommand {
            Invoke-TestContainerInputDocker -ArgumentList $ArgumentList -InputWriter $InputWriter
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
        Remove-Variable -Name AuthoritativeInventoryOverride -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name TestContainerFiles -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name TestContainerInputs -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name ReplaceStagedArtifactBeforeCopy -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name HostStagingPathsToReplace -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name CorruptContainerCopy -Scope Global -ErrorAction SilentlyContinue
    }

    It "PostgreSQL: replays into a generated scratch, validates, captures the package SourceIdentity, and drops the scratch" {
        $stage = New-ScratchStage -Directory $script:scratchRoot

        $result = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql

        $result.PackageSourceIdentity | Should -Be "11111111-1111-1111-1111-111111111111"
        @($result.ProjectSchemaNames) | Should -Be @("edfi")

        # Replay sequence: role init, terminate, drop-if-exists, create - then cp and psql -f.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*edfi_dms_enqueue_owner*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like 'CREATE DATABASE "edfi_dms_restore_scratch_*' }
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($ArgumentList -join " ") -ceq "cp - dms-postgresql:/tmp" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $args[0] -eq "cp" }
        @($global:TestContainerFiles.Keys) | Should -HaveCount 1
        @($global:TestContainerFiles.Keys)[0] | Should -BeLike "/tmp/restore-scratch-*.sql"
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U postgres -d edfi_dms_restore_scratch_* -v ON_ERROR_STOP=1 --single-transaction -f $(@($global:TestContainerFiles.Keys)[0])" }

        # The scratch is dropped in finally (the initial defensive drop plus the cleanup drop)
        # and the transient in-container file is removed.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql rm -f /tmp/restore-scratch-*.sql" }
    }

    It "derives the authoritative inventory with the schema tool it was given" {
        $stage = New-ScratchStage -Directory $script:scratchRoot

        Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql -SchemaToolPath "/opt/external/api-schema-tools" | Out-Null

        Should -Invoke Get-RestoreAuthoritativeObjectInventory -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $SchemaToolPath -ceq "/opt/external/api-schema-tools" }
    }

    It "PostgreSQL: accepts a replay holding only the privileges the authoritative DDL grants" {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        $global:ScratchPrivilegeRows = @(
            'function|EXECUTE|edfi_dms_enqueue_owner|edfi_dms_enqueue_owner|dms."TF_Document_EnqueueProjectionInsert"()',
            'function|EXECUTE|postgres|-|dms.uuidv5(namespace_uuid uuid, name_text text)',
            'relation|INSERT|postgres|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"',
            'relation|SELECT|postgres|edfi_dms_enqueue_owner|dms."DocumentCacheState"',
            'relation|SELECT|postgres|postgres|dms."Document"',
            'schema|USAGE|pg_database_owner|-|public',
            'schema|USAGE|postgres|edfi_dms_enqueue_owner|dms'
        )
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchPrivilegeRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the privileges of scratch database *" }

            $result = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql -PostgresUser "dmsadmin"

            $result.PackageSourceIdentity | Should -Be "11111111-1111-1111-1111-111111111111"
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
                $Query -ceq (Get-PostgresqlPrivilegeQuerySql) -and $DatabaseName -like "edfi_dms_restore_scratch_*" -and $PostgresUser -ceq "dmsadmin"
            }
        }
        finally {
            Remove-Variable -Name ScratchPrivilegeRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "PostgreSQL: refuses a replay carrying <Name> before the authoritative comparison, and drops the scratch" -ForEach @(
        @{ Name = "a grant to an unrelated cluster role"; Row = 'relation|SELECT|postgres|reporting_reader|dms."Document"'; Violation = 'relation dms."Document" SELECT to reporting_reader (owner postgres)' }
        @{ Name = "an enqueue function executable by PUBLIC"; Row = 'function|EXECUTE|edfi_dms_enqueue_owner|-|dms."TF_Document_EnqueueProjectionUpdate"()'; Violation = 'function dms."TF_Document_EnqueueProjectionUpdate"() EXECUTE to PUBLIC (owner edfi_dms_enqueue_owner)' }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        $global:ScratchPrivilegeRows = @('schema|USAGE|postgres|edfi_dms_enqueue_owner|dms', $Row)
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchPrivilegeRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the privileges of scratch database *" }

            { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
                Should -Throw "Restore scratch database 'edfi_dms_restore_scratch_*' carries privileges the authoritative DMS DDL does not grant: $Violation. The package is refused before the target is touched."

            Should -Invoke Get-RestoreAuthoritativeObjectInventory -ModuleName bootstrap-restore -Times 0 -Exactly
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
        }
        finally {
            Remove-Variable -Name ScratchPrivilegeRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "PostgreSQL: accepts the restoring administrator's ownership plus the enqueue functions and the built-in public schema owner, read as that administrator" {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        $global:ScratchOwnershipRows = @(
            'function|edfi_dms_enqueue_owner|dms."TF_Document_EnqueueProjectionInsert"()',
            'schema|pg_database_owner|public'
        )
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchOwnershipRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the object ownership of scratch database *" }

            $result = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql -PostgresUser "dmsadmin"

            $result.PackageSourceIdentity | Should -Be "11111111-1111-1111-1111-111111111111"
            # Every non-system schema, connected as the custom administrator, so "not owned by
            # current_user" means "not owned by the administrator that restored it".
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
                $Query -ceq (Get-PostgresqlTemplateOwnershipQuerySql) -and $DatabaseName -like "edfi_dms_restore_scratch_*" -and $PostgresUser -ceq "dmsadmin"
            }
        }
        finally {
            Remove-Variable -Name ScratchOwnershipRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "PostgreSQL: refuses <Name> before any target work, and drops the scratch" -ForEach @(
        # The privilege rows alone pass: the unexpected owner holds only its own privileges.
        @{ Name = "an object owned by an unexpected existing role"; OwnershipRow = 'relation|reporting_reader|dms."Document"'; PrivilegeRow = 'relation|SELECT|reporting_reader|reporting_reader|dms."Document"'; Violation = 'relation dms."Document" (owner reporting_reader)' }
        # An empty ACL expands to no privilege rows at all, so only the ownership read sees it.
        @{ Name = "an unexpectedly owned object with no privilege rows"; OwnershipRow = 'relation|reporting_reader|dms."Document"'; PrivilegeRow = $null; Violation = 'relation dms."Document" (owner reporting_reader)' }
        @{ Name = "a table owned by the enqueue owner"; OwnershipRow = 'relation|edfi_dms_enqueue_owner|dms."DocumentProjectionWork"'; PrivilegeRow = $null; Violation = 'relation dms."DocumentProjectionWork" (owner edfi_dms_enqueue_owner)' }
        @{ Name = "a DMS schema owned by the public-schema owner"; OwnershipRow = 'schema|pg_database_owner|dms'; PrivilegeRow = $null; Violation = 'schema dms (owner pg_database_owner)' }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        $global:ScratchOwnershipRows = @('schema|pg_database_owner|public', $OwnershipRow)
        $global:ScratchPrivilegeRows = @('schema|USAGE|postgres|edfi_dms_enqueue_owner|dms', $PrivilegeRow) | Where-Object { $null -ne $_ }
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchOwnershipRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the object ownership of scratch database *" }
            Mock Invoke-RestoreCatalogQuery { $global:ScratchPrivilegeRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the privileges of scratch database *" }

            { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
                Should -Throw "Restore scratch database 'edfi_dms_restore_scratch_*' carries ownership the authoritative DMS DDL does not produce: $Violation. Only the administrator, edfi_dms_enqueue_owner (functions only), and pg_database_owner (the public schema only) may own objects. The package is refused before the target is touched."

            Should -Invoke Get-RestoreAuthoritativeObjectInventory -ModuleName bootstrap-restore -Times 0 -Exactly
            # Cleanup: the scratch is dropped again in finally and the in-container replay file removed.
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql rm -f /tmp/restore-scratch-*.sql" }
            # Nothing outside the administrative and scratch databases was touched: the only
            # replay went into the scratch, and no catalog statement named another database.
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql * -f *" }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U postgres -d edfi_dms_restore_scratch_* -v ON_ERROR_STOP=1 --single-transaction -f *" }
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $DatabaseName -cne "postgres" -and $DatabaseName -notlike "edfi_dms_restore_scratch_*" }
        }
        finally {
            Remove-Variable -Name ScratchOwnershipRows -Scope Global -ErrorAction SilentlyContinue
            Remove-Variable -Name ScratchPrivilegeRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "SQL Server: cp, FILELISTONLY, then RESTORE with MOVE clauses derived from the scratch name" {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql

        Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql | Out-Null

        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($ArgumentList -join " ") -ceq "cp - dms-mssql:/var/opt/mssql/data" }
        @($global:TestContainerFiles.Keys) | Should -HaveCount 1
        @($global:TestContainerFiles.Keys)[0] | Should -BeLike "/var/opt/mssql/data/restore-scratch-*.bak"
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE FILELISTONLY FROM DISK*" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $joined = $args -join " "
            $joined -like "*RESTORE DATABASE [[]edfi_dms_restore_scratch_*] FROM DISK = N'/var/opt/mssql/data/restore-scratch-*.bak' WITH MOVE N'edfi_dms' TO N'/var/opt/mssql/data/edfi_dms_restore_scratch_*.mdf', MOVE N'edfi_dms_log' TO N'/var/opt/mssql/data/edfi_dms_restore_scratch_*_log.ldf', REPLACE;*"
        }

        # Minimal kind: the populated predicate never runs; the scratch is dropped in finally.
        # SQL Server reads the scratch's permissions and role memberships, not PostgreSQL privileges.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*COUNT(`*)*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $FailureMessage -like "Failed to read the privileges*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq (Get-MssqlSecurityStateQuerySql) -and $DatabaseName -like "edfi_dms_restore_scratch_*"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
    }

    It "SQL Server: accepts a restore holding only a new database's permissions and role memberships" {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        $global:ScratchSecurityRows = @(
            "member|16384|1|db_owner|dbo",
            "permission|0|CONNECT|GRANT|1|0|0|dbo|DATABASE",
            "permission|0|VIEW ANY COLUMN ENCRYPTION KEY DEFINITION|GRANT|0|0|0|public|DATABASE",
            "permission|0|VIEW ANY COLUMN MASTER KEY DEFINITION|GRANT|0|0|0|public|DATABASE",
            "permission|1|SELECT|GRANT|0|-385|0|public|OBJECT::sys.objects"
        )
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchSecurityRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the permissions and role memberships of scratch database *" }

            $result = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql -MssqlPassword "Scratch_Pw1!"

            $result.PackageSourceIdentity | Should -Be "11111111-1111-1111-1111-111111111111"
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
                $Query -ceq (Get-MssqlSecurityStateQuerySql) -and $DatabaseName -like "edfi_dms_restore_scratch_*" -and $MssqlPassword -ceq "Scratch_Pw1!"
            }
        }
        finally {
            Remove-Variable -Name ScratchSecurityRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "SQL Server: refuses a restore carrying <Name> before the authoritative comparison, drops the scratch, and touches no other database" -ForEach @(
        @{ Name = "CONNECT granted to guest"; Rows = @("permission|0|CONNECT|GRANT|2|0|0|guest|DATABASE"); Violation = "GRANT CONNECT ON DATABASE TO guest" }
        @{ Name = "SELECT on a DMS schema granted to public"; Rows = @("permission|3|SELECT|GRANT|0|5|0|public|SCHEMA::edfi"); Violation = "GRANT SELECT ON SCHEMA::edfi TO public" }
        @{ Name = "both grants together"; Rows = @("permission|0|CONNECT|GRANT|2|0|0|guest|DATABASE", "permission|3|SELECT|GRANT|0|5|0|public|SCHEMA::edfi"); Violation = "GRANT CONNECT ON DATABASE TO guest, GRANT SELECT ON SCHEMA::edfi TO public" }
        @{ Name = "guest as a member of db_datareader"; Rows = @("member|16390|2|db_datareader|guest"); Violation = "role db_datareader member guest" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        $global:ScratchSecurityRows = @("member|16384|1|db_owner|dbo", "permission|0|CONNECT|GRANT|1|0|0|dbo|DATABASE") + $Rows
        try {
            Mock Invoke-RestoreCatalogQuery { $global:ScratchSecurityRows } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to read the permissions and role memberships of scratch database *" }

            { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql } |
                Should -Throw "Restore scratch database 'edfi_dms_restore_scratch_*' carries permissions or role memberships the authoritative DMS DDL does not produce: $Violation. Only the built-in defaults of a new database are allowed. The package is refused before the target is touched."

            Should -Invoke Get-RestoreAuthoritativeObjectInventory -ModuleName bootstrap-restore -Times 0 -Exactly
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
            # Cleanup: the scratch is dropped and the in-container backup copy removed.
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-mssql rm -f /var/opt/mssql/data/restore-scratch-*.bak" }
            # Only the scratch was restored, and no statement named a database other than master
            # or the scratch.
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE DATABASE*" }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE DATABASE [[]edfi_dms_restore_scratch_*" }
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $DatabaseName -cne "master" -and $DatabaseName -notlike "edfi_dms_restore_scratch_*" }
        }
        finally {
            Remove-Variable -Name ScratchSecurityRows -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "SQL Server: refuses a backup holding a <Name> file before any RESTORE, and still cleans up" -ForEach @(
        @{ Name = "FILESTREAM (S)"; Row = "edfi_fs|/var/opt/mssql/data/edfi_fs|S|extra"; Type = "S"; LogicalName = "edfi_fs" }
        @{ Name = "full-text catalog (F)"; Row = "edfi_ft|/var/opt/mssql/data/edfi_ft|F|extra"; Type = "F"; LogicalName = "edfi_ft" }
    ) {
        $global:ScratchFileListRow = $Row
        Mock docker {
            $global:LASTEXITCODE = 0
            Invoke-TestContainerFileDocker -DockerArgs $args
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                $global:ScratchFileListRow
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        try {
            { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql } |
                Should -Throw "Backup 'artifact.bak' contains file '$LogicalName' of unsupported type '$Type'.*"

            Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE DATABASE*" }
            Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 0 -Exactly
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-mssql rm -f /var/opt/mssql/data/restore-scratch-*.bak" }
        }
        finally {
            Remove-Variable -Name ScratchFileListRow -Scope Global -ErrorAction SilentlyContinue
        }
    }

    It "<Engine>: replacing the host staging file right before the copy cannot change the replayed bytes" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; Consumer = "*psql*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; Consumer = "*RESTORE DATABASE*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine $Engine
        $global:ReplaceStagedArtifactBeforeCopy = $true
        $global:HostStagingPathsToReplace = @($stage.ArtifactPath)

        Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine | Out-Null

        # The host file really was replaced before the transfer ran...
        [System.IO.File]::ReadAllText($stage.ArtifactPath) | Should -BeExactly "replaced host staging bytes"
        # ...yet the container received the authenticated bytes, the in-container hash passed,
        # and the consumer ran against that copy.
        @($global:TestContainerFiles.Values) | Should -HaveCount 1
        [System.Text.Encoding]::UTF8.GetString(@($global:TestContainerFiles.Values)[0]) | Should -BeExactly "scratch artifact bytes"
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container sha256sum *" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like $Consumer }
    }

    It "<Engine>: a copy that lands damaged in the container is refused before any replay" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; Consumer = "*psql*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; Consumer = "*RESTORE *" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine $Engine
        $global:CorruptContainerCopy = $true

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine } |
            Should -Throw "*artifact copied into container '$Container' has SHA-256*not the authenticated manifest's artifactSha256 '$($stage.Manifest.artifactSha256)'*did not deliver the authenticated bytes*Refusing the scratch*"

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container sha256sum *" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like $Consumer }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 0 -Exactly
        # The transient in-container copy is still removed.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container rm -f *" }
    }

    It "<Engine>: retained bytes whose artifact disagrees with the manifest are refused after the transfer, before the in-container hash and any replay" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; Consumer = "*psql*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; Consumer = "*RESTORE *" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine -ManifestOverride @{ artifactSha256 = ("ee" * 32) }
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine $Engine

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine } |
            Should -Throw "*Failed to copy the staged artifact into container '$Container'. The bytes streamed from the retained package hash to*not the manifest's artifactSha256 '$("ee" * 32)'*"

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container sha256sum *" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like $Consumer }
        # The transient in-container copy is still removed.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container rm -f *" }
    }

    It "a stage whose retained bytes are <Name> aborts before ANY docker activity" -ForEach @(
        @{ Name = "not the authenticated package"; Mutate = { param($stage) $stage.PackageSha256 = ("ff" * 32) }; Expected = "*retained package bytes hash to*not the authenticated*aborting before any database activity.*" }
        @{ Name = "absent"; Mutate = { param($stage) $stage.PSObject.Properties.Remove("PackageBytes") }; Expected = "*does not carry its retained authenticated package bytes; aborting before any database activity.*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        & $Mutate $stage

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw $Expected

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "on <Name>: fails naming the defect, drops the scratch, and performs no further validation" -ForEach @(
        @{ Name = "an effective-schema hash mismatch"; FactMutation = { param($fact) $fact.EffectiveSchemaHash = ("cd" * 32) }; Expected = "*does not match the restore manifest*effectiveSchemaHash*" }
        @{ Name = "inventory drift"; FactMutation = { param($fact) $fact.ArtifactInventory.schemas[1].objects += @{ name = "Smuggled"; type = "table" } }; Expected = "*inventorySha256*" }
        @{ Name = "a dmscs schema in the scratch"; FactMutation = { param($fact) $fact.FullInventory.schemas += @{ schemaName = "dmscs"; objects = @(@{ name = "Application"; type = "table" }) } }; Expected = "*dmscs*" }
        @{ Name = "a lookalike companion schema"; FactMutation = { param($fact) $fact.FullInventory.schemas += @{ schemaName = "tracked_changesx"; objects = @(@{ name = "X"; type = "table" }) } }; Expected = "*tracked_changesx*" }
        @{ Name = "a wrong ApiSchema format version"; FactMutation = { param($fact) $fact.ApiSchemaFormatVersion = "9.9.9" }; Expected = "*apiSchemaFormatVersion*" }
        @{ Name = "a mismatched DocumentJson storage type"; FactMutation = { param($fact) $fact.DocumentJsonColumnType = "json" }; Expected = "*documentJsonColumnType*" }
        @{ Name = "an event trigger in the scratch"; FactMutation = { param($fact) $fact.DatabaseLevelObjects = [string[]]@("eventtrigger|intruder_event_trigger") }; Expected = "*Restore scratch database*database-level objects the DMS schema does not create: eventtrigger|intruder_event_trigger*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        & $FactMutation $global:ScratchFactToReturn

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql } |
            Should -Throw $Expected

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
    }

    It "on <Name> (SQL Server): fails naming the defect, drops the scratch, and removes the in-container backup copy" -ForEach @(
        @{ Name = "a mismatched compatibility level"; FactMutation = { param($fact) $fact.DatabaseCompatibilityLevel = 160 }; Expected = "*databaseCompatibilityLevel*" }
        @{ Name = "a mismatched DocumentJson storage type"; FactMutation = { param($fact) $fact.DocumentJsonColumnType = "varchar" }; Expected = "*documentJsonColumnType*" }
        @{ Name = "a copied database master key"; FactMutation = { param($fact) $fact.DatabaseLevelObjects = [string[]]@("symmetrickey|##MS_DatabaseMasterKey##") }; Expected = "*Restore scratch database*database-level objects the DMS schema does not create: symmetrickey|##MS_DatabaseMasterKey##*" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        & $FactMutation $global:ScratchFactToReturn

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql } |
            Should -Throw $Expected

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
        # The .bak copied into the SQL Server data directory is removed on the failure path too.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-mssql rm -f /var/opt/mssql/data/restore-scratch-*.bak" }
    }

    It "SQL Server: every scratch step, including the failure-path cleanup, uses the caller's password" {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine mssql
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine mssql
        $global:ScratchFactToReturn.DatabaseCompatibilityLevel = 160

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine mssql -MssqlPassword "Env_File_Only1!" } |
            Should -Throw "*databaseCompatibilityLevel*"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -like "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*" -and $MssqlPassword -ceq "Env_File_Only1!"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $MssqlPassword -cne "Env_File_Only1!" }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $MssqlPassword -cne "Env_File_Only1!" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { ($args -join " ") -like "*SQLCMDPASSWORD=Env_File_Only1!*" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter {
            ($args -join " ") -like "*SQLCMDPASSWORD=*" -and ($args -join " ") -notlike "*SQLCMDPASSWORD=Env_File_Only1!*"
        }
    }

    It "PostgreSQL: every scratch step uses the caller's administrator, including the cleanup (<Case>)" -ForEach @(
        @{ Case = "validation passes"; Fail = $false }
        @{ Case = "validation fails after the replay"; Fail = $true }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot
        if ($Fail) {
            $global:AuthoritativeInventoryOverride = @{ schemas = @(@{ schemaName = "dms"; objects = @() }); principals = @() }
        }

        $run = { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Populated -DatabaseEngine postgresql -PostgresUser "dmsadmin" }
        if ($Fail) {
            $run | Should -Throw "*does not match the authoritative inventory*"
        }
        else {
            & $run | Out-Null
        }

        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U dmsadmin -d edfi_dms_restore_scratch_* -v ON_ERROR_STOP=1 --single-transaction -f /tmp/restore-scratch-*.sql" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "*psql -U postgres*" }
        # The defensive drop and the cleanup drop in finally.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter {
            $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*' -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $PostgresUser -ceq "dmsadmin" }
        Should -Invoke Get-RestoreAuthoritativeObjectInventory -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $PostgresUser -ceq "dmsadmin" }
    }

    It "<Engine>: a successful validation whose <Step> fails returns no proof, so nothing can commit" -ForEach @(
        @{ Engine = "postgresql"; Step = "scratch drop"; Container = "dms-postgresql" }
        @{ Engine = "mssql"; Step = "scratch drop"; Container = "dms-mssql" }
        @{ Engine = "postgresql"; Step = "in-container artifact removal"; Container = "dms-postgresql" }
        @{ Engine = "mssql"; Step = "in-container artifact removal"; Container = "dms-mssql" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine $Engine
        if ($Step -eq "scratch drop") {
            Mock Invoke-RestoreCatalogQuery { throw "drop refused" } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to drop scratch database *" }
        }
        else {
            Mock docker { $global:LASTEXITCODE = 1; "rm refused" } -ModuleName bootstrap-restore -ParameterFilter { ($args -join " ") -like "exec * rm -f *" }
        }

        $script:scratchProof = $null
        { $script:scratchProof = Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine } |
            Should -Throw "*Scratch validation passed, but its cleanup did not complete:*refused*Remove scratch database 'edfi_dms_restore_scratch_*' and '*restore-scratch-*' in container '$Container'*"

        $script:scratchProof | Should -BeNullOrEmpty -Because "a proof must never reach the caller while scratch state remains"
        # Every validation step ran and passed, through the SourceIdentity capture.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
    }

    It "<Engine>: a validation failure stays the error when the scratch cleanup also fails, and the cleanup failure is still reported" -ForEach @(
        @{ Engine = "postgresql" }
        @{ Engine = "mssql" }
    ) {
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine
        $global:ScratchFactToReturn = New-ScratchCatalogFact -DatabaseEngine $Engine
        $global:ScratchFactToReturn.EffectiveSchemaHash = ("cd" * 32)
        Mock Invoke-RestoreCatalogQuery { throw "drop refused" } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to drop scratch database *" }

        $caught = $null
        $cleanupWarnings = @()
        try {
            Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine -WarningVariable cleanupWarnings 3>$null | Out-Null
        }
        catch {
            $caught = $_
        }

        $caught | Should -Not -BeNullOrEmpty
        $caught.Exception.Message | Should -BeLike "*does not match the restore manifest*effectiveSchemaHash*"
        $caught.Exception.Message | Should -Not -BeLike "*drop refused*"
        @($cleanupWarnings | Where-Object { ([string]$_) -like "*Scratch cleanup did not complete*drop refused*" }) | Should -HaveCount 1
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

    It "refuses a self-consistent contaminated package on <Engine>: manifest and scratch agree, the candidate schema does not (<SchemaName>.ExtraTable)" -ForEach @(
        @{ Engine = "postgresql"; SchemaName = "dms"; AdminDatabase = "postgres"; ScratchDropPattern = 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*'; ScratchDropCount = 2; ArtifactRemovalPattern = "exec dms-postgresql rm -f /tmp/restore-scratch-*.sql" }
        @{ Engine = "postgresql"; SchemaName = "edfi"; AdminDatabase = "postgres"; ScratchDropPattern = 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_*'; ScratchDropCount = 2; ArtifactRemovalPattern = "exec dms-postgresql rm -f /tmp/restore-scratch-*.sql" }
        @{ Engine = "mssql"; SchemaName = "dms"; AdminDatabase = "master"; ScratchDropPattern = "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*"; ScratchDropCount = 1; ArtifactRemovalPattern = "exec dms-mssql rm -f /var/opt/mssql/data/restore-scratch-*.bak" }
        @{ Engine = "mssql"; SchemaName = "edfi"; AdminDatabase = "master"; ScratchDropPattern = "IF DB_ID(N'edfi_dms_restore_scratch_*DROP DATABASE*"; ScratchDropCount = 1; ArtifactRemovalPattern = "exec dms-mssql rm -f /var/opt/mssql/data/restore-scratch-*.bak" }
    ) {
        # The attack this closes: a signed package whose ARTIFACT carries an extra object and
        # whose MANIFEST inventory declares that same object. Every manifest-versus-database
        # comparison is then satisfied, because the package is being checked against its own
        # claims.
        $contaminatedFact = New-ScratchCatalogFact -DatabaseEngine $Engine
        foreach ($inventory in @($contaminatedFact.FullInventory, $contaminatedFact.ArtifactInventory)) {
            $targetSchema = @($inventory.schemas | Where-Object { $_.schemaName -eq $SchemaName })[0]
            $targetSchema.objects += @{ name = "ExtraTable"; type = "table" }
        }
        $global:ScratchFactToReturn = $contaminatedFact

        # The manifest records the contaminated inventory, so its hash matches the database.
        $stage = New-ScratchStage -Directory $script:scratchRoot -DatabaseEngine $Engine -ManifestOverride @{
            inventorySha256 = (Get-CanonicalInventoryHash -Inventory $contaminatedFact.ArtifactInventory)
        }

        # Control: prove the self-referential comparison is NOT enough - it passes outright.
        { Assert-RestoreManifestMatchesDatabase -Manifest $stage.Manifest -Facts $contaminatedFact -DatabaseEngine $Engine } |
            Should -Not -Throw

        # The authoritative inventory comes from the candidate schema's own DDL, which defines
        # no ExtraTable anywhere.
        $global:AuthoritativeInventoryOverride = New-ScratchInventory -DatabaseEngine $Engine

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine $Engine } |
            Should -Throw "*does not match the authoritative inventory derived from the candidate schema*$SchemaName.ExtraTable (table)*"

        # The gate fires before the identity capture, and the scratch and its in-container
        # artifact copy are still removed.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SourceIdentity*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times $ScratchDropCount -Exactly -ParameterFilter { $Query -like $ScratchDropPattern }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like $ArtifactRemovalPattern }

        # No call reached any database other than the engine's admin database and the
        # generated scratch: the refusal comes before anything could touch a target.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter {
            $DatabaseName -cne $AdminDatabase -and $DatabaseName -cnotmatch '^edfi_dms_restore_scratch_[0-9a-f]{12}\z'
        }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $DatabaseEngine -ceq $Engine -and $DatabaseName -cmatch '^edfi_dms_restore_scratch_[0-9a-f]{12}\z'
        }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 1 -Exactly
    }

    It "refuses an artifact missing an object the candidate schema defines" {
        # The mirror case: the authoritative inventory is the reference, so a truncated artifact
        # is rejected too, naming what it lacks.
        $global:AuthoritativeInventoryOverride = New-ScratchInventory
        $targetSchema = @($global:AuthoritativeInventoryOverride.schemas | Where-Object { $_.schemaName -eq "edfi" })[0]
        $targetSchema.objects += @{ name = "RequiredTable"; type = "table" }
        $stage = New-ScratchStage -Directory $script:scratchRoot

        { Invoke-RestoreScratchValidation -Stage $stage -CandidateFact $script:candidateFact -RestoreTemplate Minimal -DatabaseEngine postgresql } |
            Should -Throw "*objects the candidate schema defines but the artifact lacks: edfi.RequiredTable (table)*"
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

Describe "Get-RestoreAuthoritativeObjectInventory" {
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }

        function script:New-StubDdlEmitTool {
            # An api-schema-tools stand-in for "ddl emit": records its argv and writes the
            # dialect SQL file the caller then replays.
            param(
                [Parameter(Mandatory)]
                [string]$Directory
            )

            $logPath = Join-Path $Directory "ddl-emit-args.log"
            $toolPath = Join-Path $Directory "api-schema-tools.ps1"
            @"
Add-Content -LiteralPath '$logPath' -Value (`$args -join ' ')
`$outputIndex = [array]::IndexOf(`$args, '--output')
`$outputDirectory = `$args[`$outputIndex + 1]
`$dialectIndex = [array]::IndexOf(`$args, '--dialect')
`$dialect = `$args[`$dialectIndex + 1]
New-Item -ItemType Directory -Path `$outputDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path `$outputDirectory "`$dialect.sql") -Value 'CREATE SCHEMA dms;'
exit 0
"@ | Set-Content -LiteralPath $toolPath -Encoding utf8

            return [pscustomobject]@{ ToolPath = $toolPath; LogPath = $logPath }
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:derivationRoot = Join-Path $TestDrive "derive-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:derivationRoot -Force | Out-Null
        $script:derivationTool = New-StubDdlEmitTool -Directory $script:derivationRoot

        $global:DerivationQueryLog = [System.Collections.Generic.List[string]]::new()
        $global:DerivationDockerLog = [System.Collections.Generic.List[string]]::new()
        Mock Invoke-RestoreCatalogQuery { $global:DerivationQueryLog.Add([string]$Query); @() } -ModuleName bootstrap-restore
        Mock Invoke-RestoreDockerCommand { $global:DerivationDockerLog.Add(($ArgumentList -join " ")); @() } -ModuleName bootstrap-restore
        Mock Get-RestoreDatabaseCatalogFact {
            [pscustomobject]@{ FullInventory = @{ schemas = @(@{ schemaName = "dms"; objects = @() }); principals = @() } }
        } -ModuleName bootstrap-restore
    }

    AfterEach {
        Remove-Variable -Name DerivationQueryLog -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name DerivationDockerLog -Scope Global -ErrorAction SilentlyContinue
    }

    It "PostgreSQL: emits the candidate DDL, replays it into a generated expected database, and drops that database" {
        $inventory = Get-RestoreAuthoritativeObjectInventory `
            -SchemaFilePath ([string[]]@("C:\candidate\core.json", "C:\candidate\ext.json")) `
            -DatabaseEngine postgresql `
            -ContainerName "dms-postgresql" `
            -OutputRoot $script:derivationRoot `
            -SchemaToolPath $script:derivationTool.ToolPath

        @($inventory.schemas).Count | Should -Be 1

        # The emit is driven by the CANDIDATE schema files, core first, into the private stage.
        $emitArgs = @(Get-Content -LiteralPath $script:derivationTool.LogPath)
        $emitArgs.Count | Should -Be 1
        $emitArgs[0] | Should -BeLike "ddl emit --schema C:\candidate\core.json C:\candidate\ext.json --output *expected-ddl-* --dialect pgsql*"

        # The expected database is created, seeded with pgcrypto, replayed, then dropped.
        $queries = @($global:DerivationQueryLog)
        @($queries | Where-Object { $_ -like 'CREATE DATABASE "edfi_dms_restore_expected_*' }) | Should -Not -BeNullOrEmpty
        @($queries | Where-Object { $_ -like "*pgcrypto*" }) | Should -Not -BeNullOrEmpty
        @($queries | Where-Object { $_ -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_expected_*' }) | Should -Not -BeNullOrEmpty

        $dockerCalls = @($global:DerivationDockerLog)
        @($dockerCalls | Where-Object { $_ -like "cp *pgsql.sql dms-postgresql:/tmp/restore-expected-*.sql" }) | Should -Not -BeNullOrEmpty
        @($dockerCalls | Where-Object { $_ -like "exec dms-postgresql psql -U postgres -d edfi_dms_restore_expected_* -v ON_ERROR_STOP=1 -f /tmp/restore-expected-*.sql" }) | Should -Not -BeNullOrEmpty
        @($dockerCalls | Where-Object { $_ -like "exec dms-postgresql rm -f /tmp/restore-expected-*.sql" }) | Should -Not -BeNullOrEmpty
    }

    It "PostgreSQL: creates, replays, reads, and drops the expected database as the caller's administrator" {
        Get-RestoreAuthoritativeObjectInventory `
            -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
            -DatabaseEngine postgresql `
            -ContainerName "dms-postgresql" `
            -OutputRoot $script:derivationRoot `
            -SchemaToolPath $script:derivationTool.ToolPath `
            -PostgresUser "dmsadmin" | Out-Null

        @($global:DerivationDockerLog | Where-Object { $_ -like "exec dms-postgresql psql -U dmsadmin -d edfi_dms_restore_expected_* -v ON_ERROR_STOP=1 -f /tmp/restore-expected-*.sql" }) |
            Should -Not -BeNullOrEmpty
        @($global:DerivationDockerLog | Where-Object { $_ -like "*psql -U postgres*" }) | Should -BeNullOrEmpty
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -like 'CREATE DATABASE "edfi_dms_restore_expected_*' -and $PostgresUser -ceq "dmsadmin"
        }
        # Remove-RestoreExpectedDatabase is not mocked, so its drop arrives here too.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter {
            $Query -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_expected_*' -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $PostgresUser -ceq "dmsadmin" }
    }

    It "SQL Server: creates, replays with sqlcmd -i, and single-user-drops the expected database" {
        Get-RestoreAuthoritativeObjectInventory `
            -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
            -DatabaseEngine mssql `
            -ContainerName "dms-mssql" `
            -OutputRoot $script:derivationRoot `
            -SchemaToolPath $script:derivationTool.ToolPath | Out-Null

        @(Get-Content -LiteralPath $script:derivationTool.LogPath)[0] | Should -BeLike "*--dialect mssql*"
        # The DDL goes to the mssql-owned data directory, never sticky /tmp, where the mssql
        # user cannot remove the root-owned file docker cp leaves behind.
        $dockerCalls = @($global:DerivationDockerLog)
        @($dockerCalls | ForEach-Object { $_ -replace '^cp .+ (?=dms-mssql:)', 'cp ' } | Where-Object { $_ -like "*/tmp/*" }) | Should -BeNullOrEmpty
        @($dockerCalls | Where-Object { $_ -match '^cp \S*mssql\.sql dms-mssql:/var/opt/mssql/data/restore-expected-[0-9a-f]{32}\.sql$' }) | Should -HaveCount 1
        @($dockerCalls | Where-Object { $_ -match 'sqlcmd -S localhost -U sa -d edfi_dms_restore_expected_[0-9a-f]{12} -C -b -i /var/opt/mssql/data/restore-expected-[0-9a-f]{32}\.sql$' }) | Should -HaveCount 1
        @($dockerCalls | Where-Object { $_ -match '^exec dms-mssql rm -f /var/opt/mssql/data/restore-expected-[0-9a-f]{32}\.sql$' }) | Should -HaveCount 1
        @($global:DerivationQueryLog | Where-Object { $_ -like "IF DB_ID(N'edfi_dms_restore_expected_*SINGLE_USER*DROP DATABASE*" }) |
            Should -Not -BeNullOrEmpty
    }

    It "drops the expected database even when the in-container DDL removal fails (<engine>)" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; DropPattern = 'DROP DATABASE IF EXISTS "edfi_dms_restore_expected_*'; DropCount = 2 }
        @{ Engine = "mssql"; Container = "dms-mssql"; DropPattern = "IF DB_ID(N'edfi_dms_restore_expected_*SINGLE_USER*DROP DATABASE*"; DropCount = 1 }
    ) {
        Mock Invoke-RestoreDockerCommand {
            $global:DerivationDockerLog.Add(($ArgumentList -join " "))
            if ($ArgumentList[0] -eq "exec" -and $ArgumentList -contains "rm") {
                throw "$FailureMessage (rm: Operation not permitted)"
            }
            @()
        } -ModuleName bootstrap-restore

        $inventory = Get-RestoreAuthoritativeObjectInventory `
            -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
            -DatabaseEngine $Engine `
            -ContainerName $Container `
            -OutputRoot $script:derivationRoot `
            -SchemaToolPath $script:derivationTool.ToolPath `
            -WarningVariable cleanupWarnings `
            -WarningAction SilentlyContinue

        @($inventory.schemas).Count | Should -Be 1 -Because "a cleanup warning never turns a successful derivation into a failure"
        @($global:DerivationDockerLog | Where-Object { $_ -like "exec $Container rm -f *restore-expected-*.sql" }) | Should -HaveCount 1
        @($global:DerivationQueryLog | Where-Object { $_ -like $DropPattern }) |
            Should -HaveCount $DropCount -Because "a failed file removal must not skip the expected-database drop (PostgreSQL also drops once before CREATE)"
        @($cleanupWarnings | Where-Object { "$_" -like "Expected-DDL cleanup did not complete:*Operation not permitted*" }) | Should -HaveCount 1
        @($cleanupWarnings | Where-Object { "$_" -like "Expected-database cleanup*" }) | Should -BeNullOrEmpty
    }

    It "drops the expected database even when the inventory read fails" {
        Mock Get-RestoreDatabaseCatalogFact { throw "catalog unreachable" } -ModuleName bootstrap-restore

        { Get-RestoreAuthoritativeObjectInventory `
                -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
                -DatabaseEngine postgresql `
                -ContainerName "dms-postgresql" `
                -OutputRoot $script:derivationRoot `
                -SchemaToolPath $script:derivationTool.ToolPath } |
            Should -Throw "*catalog unreachable*"

        @($global:DerivationQueryLog | Where-Object { $_ -like 'DROP DATABASE IF EXISTS "edfi_dms_restore_expected_*' }) |
            Should -Not -BeNullOrEmpty -Because "the expected database is throwaway state that must never outlive the derivation"
    }

    It "<Engine>: a successful derivation whose expected-database drop fails returns no inventory" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql" }
        @{ Engine = "mssql"; Container = "dms-mssql" }
    ) {
        Mock Invoke-RestoreCatalogQuery { throw "drop refused" } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to drop expected database *" }

        $script:derivedInventory = $null
        { $script:derivedInventory = Get-RestoreAuthoritativeObjectInventory `
                -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
                -DatabaseEngine $Engine `
                -ContainerName $Container `
                -OutputRoot $script:derivationRoot `
                -SchemaToolPath $script:derivationTool.ToolPath } |
            Should -Throw "*Authoritative inventory derivation passed, but its expected-database cleanup did not complete:*drop refused*Remove expected database 'edfi_dms_restore_expected_*' in container '$Container'*"

        $script:derivedInventory | Should -BeNullOrEmpty -Because "an inventory must never reach the scratch verdict while the expected database may remain"
        # The derivation itself passed, and the independent DDL-file cleanup still ran.
        Should -Invoke Get-RestoreDatabaseCatalogFact -ModuleName bootstrap-restore -Times 1 -Exactly
        @($global:DerivationDockerLog | Where-Object { $_ -like "exec $Container rm -f *restore-expected-*.sql" }) | Should -HaveCount 1
    }

    It "<Engine>: a derivation failure stays the error when the expected-database drop also fails, and the drop failure is still reported" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql" }
        @{ Engine = "mssql"; Container = "dms-mssql" }
    ) {
        Mock Get-RestoreDatabaseCatalogFact { throw "catalog unreachable" } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery { throw "drop refused" } -ModuleName bootstrap-restore -ParameterFilter { $FailureMessage -like "Failed to drop expected database *" }

        $derivationError = $null
        try {
            Get-RestoreAuthoritativeObjectInventory `
                -SchemaFilePath ([string[]]@("C:\candidate\core.json")) `
                -DatabaseEngine $Engine `
                -ContainerName $Container `
                -OutputRoot $script:derivationRoot `
                -SchemaToolPath $script:derivationTool.ToolPath `
                -WarningVariable cleanupWarnings `
                -WarningAction SilentlyContinue | Out-Null
        }
        catch {
            $derivationError = $_
        }

        "$derivationError" | Should -BeLike "*catalog unreachable*"
        "$derivationError" | Should -Not -BeLike "*expected-database cleanup*"
        @($cleanupWarnings | Where-Object { "$_" -like "Expected-database cleanup did not complete:*drop refused*Remove expected database 'edfi_dms_restore_expected_*' in container '$Container'*" }) |
            Should -HaveCount 1
        @($global:DerivationDockerLog | Where-Object { $_ -like "exec $Container rm -f *restore-expected-*.sql" }) | Should -HaveCount 1
    }

    It "refuses to drop anything that is not a generated expected-database name" {
        { Remove-RestoreExpectedDatabase -DatabaseEngine postgresql -ExpectedDatabaseName "edfi_datamanagementservice" -ContainerName "dms-postgresql" } |
            Should -Throw "*Refusing to drop 'edfi_datamanagementservice'*"
        { Remove-RestoreExpectedDatabase -DatabaseEngine postgresql -ExpectedDatabaseName "edfi_dms_restore_expected_0123456789abc" -ContainerName "dms-postgresql" } |
            Should -Throw "*Refusing to drop*"
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
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
        $global:TestContainerFiles = @{}
        $global:TestContainerInputs = [System.Collections.Generic.List[byte[]]]::new()
        $global:ReplaceStagedArtifactBeforeCopy = $false
        $global:HostStagingPathsToReplace = @()
        $global:CorruptContainerCopy = $false
        Mock docker {
            $global:LASTEXITCODE = 0
            $global:TargetCallSequence.Add("docker " + ($args -join " "))
            Invoke-TestContainerFileDocker -DockerArgs $args
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreDockerInputCommand {
            $global:TargetCallSequence.Add("docker " + ($ArgumentList -join " "))
            Invoke-TestContainerInputDocker -ArgumentList $ArgumentList -InputWriter $InputWriter
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
        Remove-Variable -Name TestContainerFiles -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name TestContainerInputs -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name ReplaceStagedArtifactBeforeCopy -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name HostStagingPathsToReplace -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name CorruptContainerCopy -Scope Global -ErrorAction SilentlyContinue
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
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($ArgumentList -join " ") -ceq "cp - dms-postgresql:/tmp" }
        @($global:TestContainerFiles.Keys) | Should -HaveCount 1
        @($global:TestContainerFiles.Keys)[0] | Should -BeLike "/tmp/restore-target-*.sql"
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -v ON_ERROR_STOP=1 --single-transaction -f $(@($global:TestContainerFiles.Keys)[0])" }
        # Reseed runs against the TARGET database, then the transient file is removed.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -like "*UPDATE*DataStoreIdentity*" -and $DatabaseName -eq "edfi_datamanagementservice" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql rm -f /tmp/restore-target-*.sql" }
    }

    It "PostgreSQL: the safety gate, drop/create, replay, and reseed all run as the caller's administrator" {
        $stage = New-ScratchStage -Directory $script:targetRoot

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql -PostgresUser "dmsadmin" | Out-Null

        Should -Invoke Assert-RestoreTargetSafety -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $PostgresUser -ceq "dmsadmin" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-postgresql psql -U dmsadmin -d edfi_datamanagementservice -v ON_ERROR_STOP=1 --single-transaction -f /tmp/restore-target-*.sql" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "*psql -U postgres*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -eq 'DROP DATABASE IF EXISTS "edfi_datamanagementservice";' -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -like "*UPDATE*DataStoreIdentity*" -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
    }

    It "SQL Server: cp, FILELISTONLY and MOVE clauses, safety recheck, exists-check, single-user drop, RESTORE MOVE with target-derived names" {
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine mssql

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine mssql | Out-Null

        $global:TargetCallSequence[0] | Should -Be "safety"
        # The file list is read and parsed before the last safety gate, which stays adjacent to
        # the first destructive statement.
        $sequence = @($global:TargetCallSequence)
        $fileListIndex = [array]::FindIndex([string[]]$sequence, [Predicate[string]] { param($c) $c -like "*RESTORE FILELISTONLY*" })
        $recheckIndex = [array]::FindLastIndex([string[]]$sequence, [Predicate[string]] { param($c) $c -ceq "safety" })
        $dropIndex = [array]::FindIndex([string[]]$sequence, [Predicate[string]] { param($c) $c -like "*SINGLE_USER*DROP DATABASE*" })
        $restoreIndex = [array]::FindIndex([string[]]$sequence, [Predicate[string]] { param($c) $c -like "*RESTORE DATABASE*" })
        $fileListIndex | Should -BeGreaterThan 0
        $recheckIndex | Should -BeGreaterThan $fileListIndex
        $dropIndex | Should -Be ($recheckIndex + 2) -Because "only the exists-check sits between the last safety gate and the drop"
        $restoreIndex | Should -Be ($dropIndex + 1) -Because "the target RESTORE follows the completed drop"
        # The IF governs only the ALTER (no BEGIN), so a target left RESTORING is still dropped
        # instead of failing on the ALTER.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq "BEGIN TRY IF DATABASEPROPERTYEX(N'edfi_datamanagementservice', 'Status') = N'ONLINE' ALTER DATABASE [edfi_datamanagementservice] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [edfi_datamanagementservice]; END TRY BEGIN CATCH IF DB_ID(N'edfi_datamanagementservice') IS NOT NULL AND DATABASEPROPERTYEX(N'edfi_datamanagementservice', 'Status') = N'ONLINE' ALTER DATABASE [edfi_datamanagementservice] SET MULTI_USER WITH ROLLBACK IMMEDIATE; THROW; END CATCH;"
        }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $joined = $args -join " "
            $joined -like "*RESTORE DATABASE [[]edfi_datamanagementservice] FROM DISK = N'/var/opt/mssql/data/restore-target-*.bak' WITH MOVE N'edfi_dms' TO N'/var/opt/mssql/data/edfi_datamanagementservice.mdf', MOVE N'edfi_dms_log' TO N'/var/opt/mssql/data/edfi_datamanagementservice_log.ldf', REPLACE;*"
        }
    }

    It "SQL Server: a failed drop of the existing target stops the restore before the target RESTORE" {
        Mock Invoke-RestoreCatalogQuery {
            $global:TargetCallSequence.Add("query " + $Query)
            if ($Query -like "*CASE WHEN DB_ID(N'edfi_datamanagementservice')*") { return "1" }
            if ($Query -like "*DROP DATABASE*") { throw $FailureMessage }
            return @()
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine mssql

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine mssql } |
            Should -Throw "*Failed to drop existing target database 'edfi_datamanagementservice'.*"

        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE DATABASE*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*DataStoreIdentity*" }
    }

    It "SQL Server: <Name> stops the restore before the existing target is dropped" -ForEach @(
        @{ Name = "an unreadable file list"; Answer = $null; Expected = "*Failed to read the file list from the staged artifact*" }
        @{ Name = "a file list without a log file"; Answer = @("edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"); Expected = "*Could not determine the data and log logical file names*" }
        @{ Name = "a FILESTREAM (S) file in the backup"; Answer = @("edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra", "edfi_fs|/var/opt/mssql/data/edfi_fs|S|extra", "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"); Expected = "*contains file 'edfi_fs' of unsupported type 'S'*" }
        @{ Name = "a full-text catalog (F) file in the backup"; Answer = @("edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra", "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra", "edfi_ft|/var/opt/mssql/data/edfi_ft|F|extra"); Expected = "*contains file 'edfi_ft' of unsupported type 'F'*" }
    ) {
        $global:FileListAnswer = $Answer
        Mock docker {
            $global:TargetCallSequence.Add("docker " + ($args -join " "))
            Invoke-TestContainerFileDocker -DockerArgs $args
            $global:LASTEXITCODE = 0
            if (($args -join " ") -like "*FILELISTONLY*") {
                if ($null -eq $global:FileListAnswer) { $global:LASTEXITCODE = 1; return "Msg 3201: Cannot open backup device." }
                $global:FileListAnswer
            }
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine mssql
        try {
            { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine mssql } |
                Should -Throw $Expected

            # The existing target was never touched: no drop, no restore, and the final safety
            # gate (which guards the destructive statements) was never reached.
            Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*SINGLE_USER*" -or $Query -like "*DROP DATABASE*" }
            Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like "*RESTORE DATABASE*" }
            Should -Invoke Assert-RestoreTargetSafety -ModuleName bootstrap-restore -Times 1 -Exactly
            Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec dms-mssql rm -f /var/opt/mssql/data/restore-target-*.bak" }
        }
        finally {
            Remove-Variable -Name FileListAnswer -Scope Global -ErrorAction SilentlyContinue
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

    It "retained bytes that are not the authenticated package abort after the safety gate, before any destructive statement" {
        $stage = New-ScratchStage -Directory $script:targetRoot
        $stage.PackageSha256 = ("ff" * 32)

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql } |
            Should -Throw "*retained package bytes hash to*not the authenticated*aborting before any destructive statement.*"

        $global:TargetCallSequence | Should -Be @("safety")
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 0 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly
    }

    It "PostgreSQL: a mixed-case target is quoted so it cannot case-fold onto a different database" {
        $stage = New-ScratchStage -Directory $script:targetRoot

        Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "EdFi_DMS" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql | Out-Null

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -ceq 'DROP DATABASE IF EXISTS "EdFi_DMS";' }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $Query -ceq 'CREATE DATABASE "EdFi_DMS";' }
        # The psql connection parameter is exact-case by nature; the replay targets the same
        # database the quoted CREATE produced.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -clike "exec dms-postgresql psql -U postgres -d EdFi_DMS -v ON_ERROR_STOP=1 --single-transaction -f /tmp/restore-target-*.sql" }
    }

    It "PostgreSQL: a copy failure leaves the target completely untouched - no drop, no create" {
        # Invoke-RestoreDockerInputCommand's own nonzero-exit contract: the caller's message
        # plus the process output.
        Mock Invoke-RestoreDockerInputCommand {
            $global:TargetCallSequence.Add("docker " + ($ArgumentList -join " "))
            throw "$FailureMessage (no space left on device)"
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql } |
            Should -Throw "*Failed to copy the staged artifact*no space left on device*"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "DROP DATABASE*" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "CREATE DATABASE*" }
    }

    It "PostgreSQL: a failed single-transaction replay stops before the reseed and reports the target as not restored" {
        Mock docker {
            $global:TargetCallSequence.Add("docker " + ($args -join " "))
            Invoke-TestContainerFileDocker -DockerArgs $args
            $global:LASTEXITCODE = 0
            if (($args -join " ") -like "*psql*--single-transaction -f /tmp/restore-target-*") {
                $global:LASTEXITCODE = 3
                "psql:/tmp/restore-target.sql:4120: ERROR:  out of shared memory"
            }
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:targetRoot

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine postgresql } |
            Should -Throw "Replay of the artifact into target database 'edfi_datamanagementservice' failed, so target restoration did not complete. The target was already dropped and recreated; rerun the restore before using 'edfi_datamanagementservice'. (*out of shared memory*)"

        # The replay ran as one transaction after the drop/create, and nothing ran on the target
        # after it: no reseed, no identity read; only the transient artifact was removed.
        $sequence = @($global:TargetCallSequence)
        $replayIndex = [array]::FindIndex([string[]]$sequence, [Predicate[string]] { param($c) $c -like "docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -v ON_ERROR_STOP=1 --single-transaction -f /tmp/restore-target-*.sql" })
        $replayIndex | Should -BeGreaterThan ([array]::IndexOf([string[]]$sequence, 'query CREATE DATABASE "edfi_datamanagementservice";'))
        @($sequence[($replayIndex + 1)..($sequence.Count - 1)]) | Should -HaveCount 1
        $sequence[-1] | Should -BeLike "docker exec dms-postgresql rm -f /tmp/restore-target-*.sql"
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*DataStoreIdentity*" }
    }

    It "<Engine>: replacing the host staging file after scratch validation cannot change the bytes the target restores" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; Consumer = "*psql*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; Consumer = "*RESTORE DATABASE*" }
    ) {
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine $Engine
        $global:ReplaceStagedArtifactBeforeCopy = $true
        $global:HostStagingPathsToReplace = @($stage.ArtifactPath)

        $result = Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine $Engine

        [System.IO.File]::ReadAllText($stage.ArtifactPath) | Should -BeExactly "replaced host staging bytes"
        @($global:TestContainerFiles.Values) | Should -HaveCount 1
        [System.Text.Encoding]::UTF8.GetString(@($global:TestContainerFiles.Values)[0]) | Should -BeExactly "scratch artifact bytes"
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like $Consumer }
        $result.RestoredSourceIdentity | Should -Be "33333333-3333-3333-3333-333333333333"
    }

    It "<Engine>: a copy that lands damaged in the container never reaches the target" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; Consumer = "*psql*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; Consumer = "*RESTORE *" }
    ) {
        $stage = New-ScratchStage -Directory $script:targetRoot -DatabaseEngine $Engine
        $global:CorruptContainerCopy = $true

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity $script:packageSourceIdentity -DatabaseEngine $Engine } |
            Should -Throw "*artifact copied into container '$Container' has SHA-256*not the authenticated manifest's artifactSha256 '$($stage.Manifest.artifactSha256)'*did not deliver the authenticated bytes*Refusing any change to target database 'edfi_datamanagementservice'*"

        # Nothing that could change the target ran: no drop, no create, no replay, no restore.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $Query -like "*DROP DATABASE*" -or $Query -like "*CREATE DATABASE*" -or $Query -like "*UPDATE*" }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { ($args -join " ") -like $Consumer }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container rm -f *" }
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

Describe "Get-RestoreDatabaseCatalogFact" {
    It "reads the database-level objects with the catalog facts, so the scratch gate can refuse them" {
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*pg_event_trigger*") { return @("eventtrigger|intruder_event_trigger") }
            if ($Query -like "*EffectiveSchema*") { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
            if ($Query -like "*server_version*") { return "16.8" }
            if ($Query -like "*DocumentJson*") { return "DocumentCache|jsonb" }
            if ($Query -like "*relkind*") { return @("dms|Document|table") }
            if ($Query -like "*pg_namespace*") { return @("dms", "public") }
            return @()
        } -ModuleName bootstrap-restore

        $facts = Get-RestoreDatabaseCatalogFact -DatabaseEngine postgresql -ContainerName dms-postgresql -DatabaseName "edfi_dms_restore_scratch_0123456789ab"

        $facts.DatabaseLevelObjects | Should -Be @("eventtrigger|intruder_event_trigger")
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq (Get-InventoryDatabaseObjectQuerySql -DatabaseEngine postgresql) -and $DatabaseName -ceq "edfi_dms_restore_scratch_0123456789ab"
        }
    }

    It "reads every PostgreSQL fact as the caller's administrator" {
        Mock Invoke-RestoreCatalogQuery {
            if ($Query -like "*EffectiveSchema*") { return "1.0.0|$('ab' * 32)|42|$('cd' * 32)" }
            if ($Query -like "*server_version*") { return "16.8" }
            if ($Query -like "*DocumentJson*") { return "DocumentCache|jsonb" }
            if ($Query -like "*relkind*") { return @("dms|Document|table") }
            if ($Query -like "*pg_namespace*") { return @("dms", "public") }
            return @()
        } -ModuleName bootstrap-restore

        Get-RestoreDatabaseCatalogFact -DatabaseEngine postgresql -ContainerName dms-postgresql -DatabaseName "edfi_dms_restore_scratch_0123456789ab" -PostgresUser "dmsadmin" | Out-Null

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -ceq (Get-InventoryObjectQuerySql -DatabaseEngine postgresql) -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
    }
}

Describe "Invoke-RestoreTargetReplacement target-safety recheck after the transfer" {
    # The real Assert-RestoreTargetSafety runs here, reading the same docker ps answers the restore
    # would. A DMS container or external client that connects while the artifact is copied in
    # must stop the restore before any statement touches the target.
    BeforeAll {
        $script:createdDockerFallback = $false
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Set-Item -Path function:global:docker -Value { throw "the docker fallback must always be mocked" }
            $script:createdDockerFallback = $true
        }
    }

    AfterAll {
        if ($script:createdDockerFallback) {
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        $script:recheckRoot = Join-Path $TestDrive "recheck-$([Guid]::NewGuid().ToString('n'))"
        New-Item -ItemType Directory -Path $script:recheckRoot -Force | Out-Null
        $global:TestContainerFiles = @{}
        $global:TestContainerInputs = [System.Collections.Generic.List[byte[]]]::new()
        $global:ReplaceStagedArtifactBeforeCopy = $false
        $global:HostStagingPathsToReplace = @()
        $global:CorruptContainerCopy = $false
        $global:DmsStartedDuringTransfer = $false
        $global:RecheckQueries = [System.Collections.Generic.List[string]]::new()
        $global:PlatformChangedDuringTransfer = $false
        Mock docker {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq "ps") {
                "dms-local-db-1|db"
                if ($global:DmsStartedDuringTransfer -and ($args -join " ") -like "*project=dms-local *") {
                    "dms-local-dms-1|dms"
                }
                return
            }
            Invoke-TestContainerFileDocker -DockerArgs $args
            # SQL Server reads the backup's file list before the post-transfer recheck.
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreDockerInputCommand {
            $writerResult = Invoke-TestContainerInputDocker -ArgumentList $ArgumentList -InputWriter $InputWriter
            $global:DmsStartedDuringTransfer = $true
            return $writerResult
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            $global:RecheckQueries.Add($Query)
            if ($Query -like "*server_version_num*") {
                if ($global:PlatformChangedDuringTransfer) { return "180006|SQL_ASCII" }
                return "180006|UTF8"
            }
            if ($Query -like "*server_version*") { return "18.6" }
            if ($Query -like "*ProductVersion*") { return "17.0.900.7" }
            return @()
        } -ModuleName bootstrap-restore
    }

    AfterEach {
        Remove-Variable -Name TestContainerFiles -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name TestContainerInputs -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name ReplaceStagedArtifactBeforeCopy -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name HostStagingPathsToReplace -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name CorruptContainerCopy -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name DmsStartedDuringTransfer -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name RecheckQueries -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name PlatformChangedDuringTransfer -Scope Global -ErrorAction SilentlyContinue
    }

    It "<Engine>: a service that starts during the transfer stops the restore before any statement touches the target" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql" }
        @{ Engine = "mssql"; Container = "dms-mssql" }
    ) {
        $stage = New-ScratchStage -Directory $script:recheckRoot -DatabaseEngine $Engine

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity "11111111-1111-1111-1111-111111111111" -DatabaseEngine $Engine } |
            Should -Throw "*Compose project 'dms-local' has running non-database containers*dms-local-dms-1|dms*"

        # The transfer completed and its in-container hash passed, so only the gate that runs
        # after it can have refused.
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $args[0] -eq "exec" -and $args[2] -eq "sha256sum" }
        # Every catalog query was a read-only safety-gate query; nothing reached the target.
        @($global:RecheckQueries | Where-Object {
                $_ -notmatch 'datistemplate|database_id <= 4|pg_stat_activity|sys.dm_exec_sessions|server_version|ProductVersion|pg_replication_slots|is_cdc_enabled|^SELECT 1 FROM pg_database '
            }) | Should -BeNullOrEmpty
        # SQL Server's read-only RESTORE FILELISTONLY runs before the recheck; nothing else did.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $args[0] -eq "exec" -and $args[2] -notin @("sha256sum", "rm") -and ($args -join " ") -notlike "*RESTORE FILELISTONLY FROM DISK*" }
        # The transient in-container copy is still removed.
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container rm -f *" }
    }

    It "<Engine>: an external client connecting during transfer prevents disconnect, drop and replay" -ForEach @(
        @{ Engine = "postgresql"; Container = "dms-postgresql"; SessionPattern = "*FROM pg_stat_activity*" }
        @{ Engine = "mssql"; Container = "dms-mssql"; SessionPattern = "*FROM sys.dm_exec_sessions*" }
    ) {
        # No compose service starts. The transfer flag now represents a host/foreign-project
        # client's connection, visible only to the live-session query on the second gate.
        Mock docker {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq "ps") { return "dms-local-db-1|db" }
            Invoke-TestContainerFileDocker -DockerArgs $args
            if (($args -join " ") -like "*FILELISTONLY*") {
                "edfi_dms|/var/opt/mssql/data/edfi.mdf|D|extra"
                "edfi_dms_log|/var/opt/mssql/data/edfi_log.ldf|L|extra"
            }
        } -ModuleName bootstrap-restore
        Mock Invoke-RestoreCatalogQuery {
            $global:RecheckQueries.Add($Query)
            if ($global:DmsStartedDuringTransfer) { return "57" }
            return @()
        } -ModuleName bootstrap-restore -ParameterFilter { $Query -like $SessionPattern }
        $stage = New-ScratchStage -Directory $script:recheckRoot -DatabaseEngine $Engine

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity "11111111-1111-1111-1111-111111111111" -DatabaseEngine $Engine } |
            Should -Throw "*Restore target 'edfi_datamanagementservice' has connected sessions*"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 2 -Exactly -ParameterFilter { $Query -like $SessionPattern }
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { $args[0] -eq "exec" -and $args[2] -eq "sha256sum" }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter {
            $Query -match 'pg_terminate_backend|SINGLE_USER|DROP DATABASE|CREATE DATABASE|DataStoreIdentity'
        }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter {
            ($args -join " ") -like "*RESTORE DATABASE*" -or ($args -join " ") -like "*--single-transaction*"
        }
        Should -Invoke docker -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter { ($args -join " ") -like "exec $Container rm -f *" }
    }

    It "postgresql: an unsupported platform is refused before the transfer, and nothing reaches the target" {
        Mock Invoke-RestoreCatalogQuery {
            $global:RecheckQueries.Add($Query)
            if ($Query -like "*server_version_num*") { return "170005|UTF8" }
            if ($Query -like "*server_version*") { return "17.5" }
            return @()
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:recheckRoot -DatabaseEngine postgresql

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity "11111111-1111-1111-1111-111111111111" -DatabaseEngine postgresql } |
            Should -Throw "DMS requires PostgreSQL 18 or later*Detected: server_version_num 170005."

        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 0 -Exactly
        @($global:RecheckQueries | Where-Object { $_ -match 'DROP DATABASE|CREATE DATABASE|pg_terminate_backend' }) | Should -BeNullOrEmpty
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $args[0] -eq "exec" }
    }

    It "postgresql: the post-transfer recheck repeats the platform check before any statement touches the target" {
        Mock Invoke-RestoreDockerInputCommand {
            $writerResult = Invoke-TestContainerInputDocker -ArgumentList $ArgumentList -InputWriter $InputWriter
            $global:PlatformChangedDuringTransfer = $true
            return $writerResult
        } -ModuleName bootstrap-restore
        $stage = New-ScratchStage -Directory $script:recheckRoot -DatabaseEngine postgresql

        { Invoke-RestoreTargetReplacement -Stage $stage -TargetDatabaseName "edfi_datamanagementservice" -PackageSourceIdentity "11111111-1111-1111-1111-111111111111" -DatabaseEngine postgresql } |
            Should -Throw "DMS requires PostgreSQL 18 or later*Detected: template1 encoding SQL_ASCII."

        # The gate ran on both sides of the transfer.
        @($global:RecheckQueries | Where-Object { $_ -like "*server_version_num*" }).Count | Should -Be 2
        Should -Invoke Invoke-RestoreDockerInputCommand -ModuleName bootstrap-restore -Times 1 -Exactly
        @($global:RecheckQueries | Where-Object { $_ -match 'DROP DATABASE|CREATE DATABASE|pg_terminate_backend|restore_global' }) | Should -BeNullOrEmpty
        Should -Invoke docker -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $args[0] -eq "exec" -and $args[2] -notin @("sha256sum", "rm") }
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
            Remove-Item -LiteralPath Function:\docker -ErrorAction SilentlyContinue
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

    It "drops the preflight and scratch databases as the caller's PostgreSQL administrator" {
        Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_dms_restore_preflight_0123456789ab" -PostgresUser "dmsadmin" -Required
        Remove-RestoreScratchDatabase -DatabaseEngine postgresql -ScratchDatabaseName "edfi_dms_restore_scratch_0123456789ab" -ContainerName "dms-postgresql" -PostgresUser "dmsadmin"

        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -eq 'DROP DATABASE IF EXISTS "edfi_dms_restore_preflight_0123456789ab";' -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $Query -eq 'DROP DATABASE IF EXISTS "edfi_dms_restore_scratch_0123456789ab";' -and $PostgresUser -ceq "dmsadmin"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 4 -Exactly
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 0 -Exactly -ParameterFilter { $PostgresUser -cne "dmsadmin" }
    }

    It "SQL Server: switches the scratch to SINGLE_USER only when it is ONLINE, and drops it in either state" {
        Remove-RestoreScratchDatabase -DatabaseEngine mssql -ScratchDatabaseName "edfi_dms_restore_scratch_0123456789ab" -ContainerName "dms-mssql"

        # Inside the existence block the IF governs only the ALTER (no BEGIN), so a scratch left
        # RESTORING by a failed RESTORE is still dropped instead of failing on the ALTER.
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly -ParameterFilter {
            $DatabaseName -ceq "master" -and
            $Query -ceq "IF DB_ID(N'edfi_dms_restore_scratch_0123456789ab') IS NOT NULL BEGIN IF DATABASEPROPERTYEX(N'edfi_dms_restore_scratch_0123456789ab', 'Status') = N'ONLINE' ALTER DATABASE [edfi_dms_restore_scratch_0123456789ab] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [edfi_dms_restore_scratch_0123456789ab]; END"
        }
        Should -Invoke Invoke-RestoreCatalogQuery -ModuleName bootstrap-restore -Times 1 -Exactly
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

    It "required preflight cleanup throws instead of warning, so the caller stops before the next stage" {
        Mock Invoke-RestoreCatalogQuery { throw "server gone" } -ModuleName bootstrap-restore

        { Remove-RestorePreflightDatabase -DatabaseEngine postgresql -PreflightDatabaseName "edfi_dms_restore_preflight_0123456789ab" -Required } |
            Should -Throw "*Preflight database cleanup did not complete*server gone*Drop 'edfi_dms_restore_preflight_0123456789ab' in container 'dms-postgresql' manually*"
    }
}
