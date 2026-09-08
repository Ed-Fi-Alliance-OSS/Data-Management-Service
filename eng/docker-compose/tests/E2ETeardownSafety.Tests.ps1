# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: the DMS E2E and Instance Management E2E suites tear down through one shared,
# engine-aware primitive set. Teardown must delegate to the project-scoped
# `-d -v -DatabaseEngine <postgresql|mssql>` down of BOTH start-local-dms.ps1 (dms-local) and
# start-published-dms.ps1 (dms-published, created by `E2ETest -UsePublishedImage`), with parameters
# bound BY NAME rather than positional array splatting, and must never reach for machine-wide
# cleanup (dangling-volume prune, container-name regex removal, unprefixed volume removal, or
# deletion of the shared external `dms` network). These tests invoke the module against fake
# primitives and a mocked Docker, and invoke the real primitives against a recording `docker`
# function, so the assertions are on actually bound parameters and emitted commands, not script text.

param()

Describe "E2E engine-aware teardown (DMS-1284)" {
    BeforeAll {
        $script:teardownModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../e2e-teardown.psm1"))
        Import-Module $script:teardownModule -Force
    }

    AfterAll {
        Remove-Module e2e-teardown -Force -ErrorAction SilentlyContinue
    }

    Context "Get-E2ETeardownPlan builds project-scoped primitive parameters" {
        BeforeAll {
            $script:composeRoot = Join-Path $TestDrive "docker-compose"
            New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.e2e") -Value "E2E_DATABASE_NAME=edfi_e2e"
        }

        It "covers both compose projects: the local primitive then the published primitive" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            @($plan.TeardownSteps | ForEach-Object { [System.IO.Path]::GetFileName($_.StartScript) }) |
                Should -Be @("start-local-dms.ps1", "start-published-dms.ps1")
        }

        It "forwards the postgresql engine with -d -v as named switches to every project" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            foreach ($step in $plan.TeardownSteps) {
                $step.StartParameters.d | Should -BeTrue
                $step.StartParameters.v | Should -BeTrue
                $step.StartParameters.DatabaseEngine | Should -Be "postgresql"
            }
        }

        It "keeps -RemoveBootstrap off in every project so no primitive deletes the shared workspace mid-teardown" {
            # The .bootstrap workspace is shared by both compose projects and bind-mounted into the DMS
            # services. A primitive that removed it after its own down would take it away from a project
            # that is still running, so the wrapper owns the removal instead.
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            foreach ($step in $plan.TeardownSteps) {
                $step.StartParameters.RemoveBootstrap | Should -BeFalse
            }
        }

        It "resolves the shared bootstrap workspace under the compose root" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.BootstrapWorkspacePath |
                Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot ".bootstrap")))
        }

        It "forwards the mssql engine to every project" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.DatabaseEngine | Should -Be "mssql"
            @($plan.TeardownSteps | ForEach-Object { $_.StartParameters.DatabaseEngine }) | Should -Be @("mssql", "mssql")
        }

        It "points at primitives in the compose root" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            foreach ($step in $plan.TeardownSteps) {
                [System.IO.Path]::GetDirectoryName($step.StartScript) | Should -Be ([System.IO.Path]::GetFullPath($script:composeRoot))
            }
        }

        It "gives every project its own parameter copy so adjusting one step cannot change another" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.TeardownSteps[0].StartParameters.DatabaseEngine = "mssql"

            $plan.TeardownSteps[1].StartParameters.DatabaseEngine | Should -Be "postgresql"
        }

        It "resolves the environment file to an absolute path under the compose root" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            [System.IO.Path]::IsPathRooted($plan.EnvironmentFilePath) | Should -BeTrue
            $plan.EnvironmentFilePath | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot ".env.e2e")))
            @($plan.TeardownSteps | ForEach-Object { $_.StartParameters.EnvironmentFile }) |
                Should -Be @($plan.EnvironmentFilePath, $plan.EnvironmentFilePath)
        }

        It "fails fast when the selected environment file is absent (no silent fallback)" {
            { Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.routeContext.e2e" -ComposeRoot $script:composeRoot } |
                Should -Throw -ExpectedMessage "*environment file not found*"
        }

        It "targets exactly the two known locally-built images and no others" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.KnownLocalImageNames | Should -Be @("ed-fi-api-local", "ed-fi-api-config-local")
        }
    }

    Context "Invoke-E2EEngineAwareTeardown binds named parameters to every primitive" {
        BeforeAll {
            # Fake local and published primitives with the same parameter names as the real ones.
            # Each records the values it actually binds, under its own name, so the tests prove named
            # binding for both compose projects rather than positional array splatting. ValidateSet on
            # -DatabaseEngine would fail if a switch such as -d were bound positionally into it.
            $script:composeRoot = Join-Path $TestDrive "docker-compose-bind"
            New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.e2e") -Value "E2E_DATABASE_NAME=edfi_e2e"

            $fakePrimitive = @'
param(
    [switch] $d,
    [switch] $v,
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql",
    [string] $EnvironmentFile,
    [switch] $RemoveBootstrap
)
$boundFileName = [System.IO.Path]::GetFileNameWithoutExtension($PSCommandPath) + ".bound.json"
[pscustomobject]@{
    d               = [bool]$d
    v               = [bool]$v
    DatabaseEngine  = $DatabaseEngine
    EnvironmentFile = $EnvironmentFile
    RemoveBootstrap = [bool]$RemoveBootstrap
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot $boundFileName)
exit 0
'@
            $script:fakePrimitiveNames = @("start-local-dms", "start-published-dms")
            foreach ($primitiveName in $script:fakePrimitiveNames) {
                Set-Content -LiteralPath (Join-Path $script:composeRoot "$primitiveName.ps1") -Value $fakePrimitive
            }
        }

        AfterEach {
            foreach ($primitiveName in $script:fakePrimitiveNames) {
                Remove-Item -LiteralPath (Join-Path $script:composeRoot "$primitiveName.bound.json") -Force -ErrorAction SilentlyContinue
            }
        }

        It "binds -d and -v as switches and forwards the mssql engine and environment file to <_>" -ForEach @("start-local-dms", "start-published-dms") {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot -SkipLocalImageRemoval | Out-Null

            $bound = Get-Content -LiteralPath (Join-Path $script:composeRoot "$_.bound.json") -Raw | ConvertFrom-Json
            $bound.d | Should -BeTrue
            $bound.v | Should -BeTrue
            $bound.RemoveBootstrap | Should -BeFalse
            $bound.DatabaseEngine | Should -Be "mssql"
            $bound.EnvironmentFile | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot ".env.e2e")))
        }

        It "binds the postgresql engine by name to <_>" -ForEach @("start-local-dms", "start-published-dms") {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot -SkipLocalImageRemoval | Out-Null

            $bound = Get-Content -LiteralPath (Join-Path $script:composeRoot "$_.bound.json") -Raw | ConvertFrom-Json
            $bound.DatabaseEngine | Should -Be "postgresql"
            $bound.d | Should -BeTrue
            $bound.v | Should -BeTrue
        }
    }

    Context "Invoke-E2EEngineAwareTeardown never selects unrelated resources" {
        BeforeAll {
            $script:composeRoot = Join-Path $TestDrive "docker-compose-run"
            New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.e2e") -Value "E2E_DATABASE_NAME=edfi_e2e"
        }

        BeforeEach {
            # Mock the primitives so no real stack is torn down; capture the forwarded parameters.
            Mock -ModuleName e2e-teardown Invoke-ComposeProjectTeardown { }

            # Seed Docker discovery with UNRELATED resources so the assertions prove the wrapper
            # never enumerates or removes anything beyond the compose project + the two known images.
            Mock -ModuleName e2e-teardown docker {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq "images" -and ($args -contains "-q")) {
                    $requestedImage = $args[-1]
                    if ($requestedImage -in @("ed-fi-api-local", "ed-fi-api-config-local")) {
                        return "sha256:knownlocalimage"
                    }
                    return $null
                }
                if ($args[0] -eq "ps") { return @("unrelated-app", "someone-elses-dms", "kafka-of-another-project") }
                if ($args[0] -eq "volume" -and ($args -contains "ls")) { return @("unrelated_data", "dms-local_dms-postgresql", "other-dangling") }
                if ($args[0] -eq "network" -and ($args -contains "ls")) { return @("dms", "bridge", "unrelated-net") }
                return $null
            }
        }

        It "delegates teardown of <_> to the project-scoped primitive with the selected engine and environment" -ForEach @("start-local-dms", "start-published-dms") {
            $primitivePattern = "$([regex]::Escape($_))\.ps1$"

            Invoke-E2EEngineAwareTeardown -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Invoke -ModuleName e2e-teardown Invoke-ComposeProjectTeardown -Times 1 -Exactly -ParameterFilter {
                ($StartScript -match $primitivePattern) -and
                ($StartParameters.d -eq $true) -and
                ($StartParameters.v -eq $true) -and
                ($StartParameters.RemoveBootstrap -eq $false) -and
                ($StartParameters.DatabaseEngine -eq "mssql") -and
                ($StartParameters.EnvironmentFile -match "\.env\.e2e$")
            }
        }

        It "tears down exactly the two known compose projects and nothing else" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Invoke -ModuleName e2e-teardown Invoke-ComposeProjectTeardown -Times 2 -Exactly
        }

        It "still tears down the published project when the local project's down fails, then reports the failure" {
            Mock -ModuleName e2e-teardown Invoke-ComposeProjectTeardown {
                if ($StartScript -match "start-local-dms\.ps1$") {
                    throw "Engine-aware teardown failed: start-local-dms.ps1 exited with code 1."
                }
            }

            { Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot } |
                Should -Throw -ExpectedMessage "*start-local-dms.ps1 exited with code 1*"

            Should -Invoke -ModuleName e2e-teardown Invoke-ComposeProjectTeardown -Times 1 -Exactly -ParameterFilter {
                $StartScript -match "start-published-dms\.ps1$"
            }
        }

        It "removes the shared bootstrap workspace after every compose project is down" {
            $bootstrapWorkspace = Join-Path $script:composeRoot ".bootstrap"
            New-Item -ItemType Directory -Path (Join-Path $bootstrapWorkspace "ApiSchema") -Force | Out-Null

            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Test-Path -LiteralPath $bootstrapWorkspace | Should -BeFalse
        }

        It "removes the shared bootstrap workspace once rather than once per compose project" {
            Mock -ModuleName e2e-teardown Remove-E2EBootstrapWorkspace { }

            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Invoke -ModuleName e2e-teardown Remove-E2EBootstrapWorkspace -Times 1 -Exactly
        }

        It "preserves the bootstrap workspace when a compose project's down fails" {
            # The surviving project's DMS services still bind-mount the workspace, and the developer
            # needs it in place to retry, so a failed down must leave it alone.
            Mock -ModuleName e2e-teardown Invoke-ComposeProjectTeardown {
                if ($StartScript -match "start-published-dms\.ps1$") {
                    throw "Engine-aware teardown failed: start-published-dms.ps1 exited with code 1."
                }
            }
            $bootstrapWorkspace = Join-Path $script:composeRoot ".bootstrap"
            New-Item -ItemType Directory -Path (Join-Path $bootstrapWorkspace "ApiSchema") -Force | Out-Null

            try {
                { Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot } |
                    Should -Throw -ExpectedMessage "*start-published-dms.ps1 exited with code 1*"

                Test-Path -LiteralPath $bootstrapWorkspace | Should -BeTrue
            }
            finally {
                Remove-Item -LiteralPath $bootstrapWorkspace -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "never runs a machine-wide dangling-volume prune" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { ($args -join " ") -match "dangling=true" }
        }

        It "never force-removes containers by name pattern" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "rm" -and ($args -contains "-f") }
            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "ps" }
        }

        It "never removes the shared external dms network" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "network" }
        }

        It "never removes volumes directly (compose-project down is the sole volume authority)" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "volume" -and ($args -contains "rm") }
        }

        It "removes only the two known locally-built images" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Invoke -ModuleName e2e-teardown docker -Times 1 -Exactly -ParameterFilter { $args[0] -eq "rmi" -and ($args -contains "ed-fi-api-local") }
            Should -Invoke -ModuleName e2e-teardown docker -Times 1 -Exactly -ParameterFilter { $args[0] -eq "rmi" -and ($args -contains "ed-fi-api-config-local") }
        }

        It "never removes a published or unrelated image" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "rmi" -and -not (($args -join " ") -match "^rmi ed-fi-api-(local|config-local) ") }
        }

        It "skips image removal when -SkipLocalImageRemoval is set" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot -SkipLocalImageRemoval | Out-Null

            Should -Not -Invoke -ModuleName e2e-teardown docker -ParameterFilter { $args[0] -eq "rmi" }
        }

        It "throws naming the exact image when its removal fails" {
            Mock -ModuleName e2e-teardown docker {
                if ($args[0] -eq "images" -and ($args -contains "-q")) { return "sha256:knownlocalimage" }
                if ($args[0] -eq "rmi") { $global:LASTEXITCODE = 1; return "Error: image is referenced in multiple repositories" }
                $global:LASTEXITCODE = 0
                return $null
            }

            { Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot } |
                Should -Throw -ExpectedMessage "*ed-fi-api-local*"
        }
    }
}

Describe "Teardown down set keeps every volume-bearing compose file" {
    BeforeAll {
        $script:realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:standardE2EEnvironmentFile = Join-Path $script:realComposeRoot ".env.e2e"

        # `docker compose down -v` removes the named volumes of the services in the composed set only,
        # so a compose file omitted from the down set takes its volumes with it. Teardown resolves the
        # identity provider from the environment file, which need not name the provider the running
        # stack was started with, so the down set must not gate keycloak.yml on that value.
        #
        # The real primitive is invoked in a child pwsh with `docker` replaced by a recording
        # function, so the assertion is on the command the script actually emits rather than on its
        # source text, and no Docker resource is touched. The function has no param block, so flags
        # such as -p reach $args instead of binding to PowerShell common parameters. A child process
        # keeps the primitive's process-environment writes out of this Pester session.
        $script:captureScript = Join-Path $TestDrive "capture-teardown-compose-set.ps1"
        Set-Content -LiteralPath $script:captureScript -Value @'
param(
    [Parameter(Mandatory)] [string] $StartScript,
    [Parameter(Mandatory)] [string] $EnvironmentFile,
    [Parameter(Mandatory)] [string] $LogPath
)

$ErrorActionPreference = "Stop"

function docker {
    Add-Content -LiteralPath $env:DMS_TEARDOWN_CAPTURE_LOG -Value (($args | ForEach-Object { $_ }) -join " ")
    $global:LASTEXITCODE = 0
}

$env:DMS_TEARDOWN_CAPTURE_LOG = $LogPath
& $StartScript -d -v -EnvironmentFile $EnvironmentFile
'@
    }

    It "keeps the standard E2E environment file on self-contained identity (precondition)" {
        Get-Content -LiteralPath $script:standardE2EEnvironmentFile |
            Should -Contain "DMS_CONFIG_IDENTITY_PROVIDER=self-contained"
    }

    It "includes keycloak.yml in the <Project> down set even when the environment file selects self-contained identity" -ForEach @(
        @{ Primitive = "start-local-dms.ps1"; Project = "dms-local" }
        @{ Primitive = "start-published-dms.ps1"; Project = "dms-published" }
    ) {
        $logPath = Join-Path $TestDrive "$Project-down.log"
        Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue

        & pwsh -NoProfile -File $script:captureScript `
            -StartScript (Join-Path $script:realComposeRoot $Primitive) `
            -EnvironmentFile $script:standardE2EEnvironmentFile `
            -LogPath $logPath | Out-Null

        $LASTEXITCODE | Should -Be 0 -Because "the teardown path must complete against the recorded Docker"
        $downCommands = @(Get-Content -LiteralPath $logPath | Where-Object { $_ -match " -p $Project down" })
        $downCommands.Count | Should -Be 1 -Because "teardown issues exactly one project-scoped down"
        $downCommands[0] | Should -Match "down --remove-orphans -v$" -Because "teardown removes the project's volumes"
        $downCommands[0] | Should -Match "-f keycloak\.yml" `
            -Because "keycloak.yml carries the dms-keycloak named volume, so omitting it leaks ${Project}_dms-keycloak past down -v"
    }
}
