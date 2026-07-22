# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: the DMS E2E and Instance Management E2E suites tear down through one shared,
# engine-aware primitive. Teardown must delegate to the project-scoped
# `start-local-dms.ps1 -d -v -DatabaseEngine <postgresql|mssql>` down (with parameters bound BY
# NAME, not positional array splatting) and must never reach for machine-wide cleanup
# (dangling-volume prune, container-name regex removal, unprefixed volume removal, or deletion of
# the shared external `dms` network). These tests invoke the module against a fake primitive and a
# mocked Docker and assert on the actually bound parameters and behavior, not on script text.

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

        It "forwards the postgresql engine with -d -v -RemoveBootstrap as named switches" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.StartParameters.d | Should -BeTrue
            $plan.StartParameters.v | Should -BeTrue
            $plan.StartParameters.RemoveBootstrap | Should -BeTrue
            $plan.StartParameters.DatabaseEngine | Should -Be "postgresql"
        }

        It "forwards the mssql engine" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.DatabaseEngine | Should -Be "mssql"
            $plan.StartParameters.DatabaseEngine | Should -Be "mssql"
        }

        It "points at the start-local-dms.ps1 primitive in the compose root" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            $plan.StartScript | Should -Match "start-local-dms\.ps1$"
            [System.IO.Path]::GetDirectoryName($plan.StartScript) | Should -Be ([System.IO.Path]::GetFullPath($script:composeRoot))
        }

        It "resolves the environment file to an absolute path under the compose root" {
            $plan = Get-E2ETeardownPlan -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot

            [System.IO.Path]::IsPathRooted($plan.EnvironmentFilePath) | Should -BeTrue
            $plan.EnvironmentFilePath | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot ".env.e2e")))
            $plan.StartParameters.EnvironmentFile | Should -Be $plan.EnvironmentFilePath
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

    Context "Invoke-E2EEngineAwareTeardown binds named parameters to the primitive" {
        BeforeAll {
            # A fake start-local-dms.ps1 with the same parameter names as the real primitive.
            # It records the values it actually binds so the test proves named binding, not
            # positional array splatting. ValidateSet on -DatabaseEngine would fail if a switch
            # such as -d were bound positionally into it.
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
[pscustomobject]@{
    d               = [bool]$d
    v               = [bool]$v
    DatabaseEngine  = $DatabaseEngine
    EnvironmentFile = $EnvironmentFile
    RemoveBootstrap = [bool]$RemoveBootstrap
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot "bound-parameters.json")
exit 0
'@
            Set-Content -LiteralPath (Join-Path $script:composeRoot "start-local-dms.ps1") -Value $fakePrimitive
            $script:boundParametersPath = Join-Path $script:composeRoot "bound-parameters.json"
        }

        AfterEach {
            Remove-Item -LiteralPath $script:boundParametersPath -Force -ErrorAction SilentlyContinue
        }

        It "binds -d and -v as switches and forwards the mssql engine, environment file, and -RemoveBootstrap" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot -SkipLocalImageRemoval | Out-Null

            $bound = Get-Content -LiteralPath $script:boundParametersPath -Raw | ConvertFrom-Json
            $bound.d | Should -BeTrue
            $bound.v | Should -BeTrue
            $bound.RemoveBootstrap | Should -BeTrue
            $bound.DatabaseEngine | Should -Be "mssql"
            $bound.EnvironmentFile | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $script:composeRoot ".env.e2e")))
        }

        It "binds the postgresql engine by name" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot -SkipLocalImageRemoval | Out-Null

            $bound = Get-Content -LiteralPath $script:boundParametersPath -Raw | ConvertFrom-Json
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
            # Mock the primitive so no real stack is torn down; capture the forwarded parameters.
            Mock -ModuleName e2e-teardown Invoke-StartLocalDmsTeardown { }

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

        It "delegates teardown to the project-scoped primitive with the selected engine and environment" {
            Invoke-E2EEngineAwareTeardown -DatabaseEngine mssql -EnvironmentFile ".env.e2e" -ComposeRoot $script:composeRoot | Out-Null

            Should -Invoke -ModuleName e2e-teardown Invoke-StartLocalDmsTeardown -Times 1 -Exactly -ParameterFilter {
                ($StartScript -match "start-local-dms\.ps1$") -and
                ($StartParameters.d -eq $true) -and
                ($StartParameters.v -eq $true) -and
                ($StartParameters.RemoveBootstrap -eq $true) -and
                ($StartParameters.DatabaseEngine -eq "mssql") -and
                ($StartParameters.EnvironmentFile -match "\.env\.e2e$")
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
