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

    function script:Import-ManifestModule {
        Import-Module $script:manifestModulePath -Force -Global
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
