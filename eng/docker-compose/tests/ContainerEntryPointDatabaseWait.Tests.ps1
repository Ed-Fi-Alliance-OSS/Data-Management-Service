# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Covers the PostgreSQL readiness wait in the container entry scripts src/config/run.sh and
# src/dms/run.sh. pg_isready, nc and dotnet are stubbed on PATH. The pg_isready stub exits 0 so
# that a script which wrongly enters the wait loop finishes at once and fails an assertion
# instead of hanging.

Describe "Container entry point database wait" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:marker = "Marker-Pw-7f3a91c2"
        $script:caseCounter = 0

        function Invoke-EntryPoint {
            param(
                [string]$ScriptRelativePath,
                [hashtable]$Environment
            )

            $script:caseCounter++
            $workspace = Join-Path $TestDrive "case-$($script:caseCounter)"
            $stubDirectory = Join-Path $workspace "bin"
            New-Item -ItemType Directory -Path $stubDirectory -Force | Out-Null

            $logPaths = @{
                pg_isready = Join-Path $workspace "pg-isready-invocations.log"
                nc         = Join-Path $workspace "nc-invocations.log"
                dotnet     = Join-Path $workspace "dotnet-invocations.log"
            }

            foreach ($stubName in $logPaths.Keys) {
                $stubPath = Join-Path $stubDirectory $stubName
                @"
#!/bin/sh
echo "`$*" >> "$($logPaths[$stubName])"
exit 0
"@ | Set-Content -LiteralPath $stubPath -Encoding utf8
                & chmod +x $stubPath
            }

            $envNames = @("PATH", "AppSettings__Datastore") + @($Environment.Keys)
            $envNames = @($envNames | Select-Object -Unique)
            $envSnapshot = @{}
            foreach ($name in $envNames) {
                $envSnapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
            }

            try {
                # Start from a clean slate so ambient values cannot leak into a case.
                foreach ($name in $envNames) {
                    if ($name -ne "PATH") {
                        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                    }
                }
                $env:PATH = "$stubDirectory$([System.IO.Path]::PathSeparator)$($env:PATH)"
                foreach ($name in $Environment.Keys) {
                    if ($null -ne $Environment[$name]) {
                        [System.Environment]::SetEnvironmentVariable($name, $Environment[$name])
                    }
                }

                $runOutput = & bash (Join-Path $script:sourceRepoRoot $ScriptRelativePath) 2>&1
                $exitCode = $LASTEXITCODE
            }
            finally {
                # Restore absence with Remove-Item: SetEnvironmentVariable coerces a $null snapshot
                # to "" from PowerShell, which newer pwsh/.NET on Unix stores as a present-but-blank
                # variable instead of removing it - poisoning ambient-precedence checks downstream.
                foreach ($name in $envNames) {
                    if ($null -eq $envSnapshot[$name]) {
                        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                    }
                    else {
                        [System.Environment]::SetEnvironmentVariable($name, $envSnapshot[$name])
                    }
                }
            }

            $lines = @($runOutput | ForEach-Object { "$_" })
            [pscustomobject]@{
                ExitCode = $exitCode
                Lines    = $lines
                Text     = ($lines -join [Environment]::NewLine)
                PgIsReadyLog = $logPaths.pg_isready
                NcLog        = $logPaths.nc
                DotnetLog    = $logPaths.dotnet
            }
        }
    }

    Context "<Name>" -ForEach @(
        @{
            Name         = "src/config/run.sh"
            Script       = "src/config/run.sh"
            Variable     = "DatabaseSettings__DatabaseConnection"
            Dll          = "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"
            ExtraEnv     = @{}
        }
        @{
            Name         = "src/dms/run.sh"
            Script       = "src/dms/run.sh"
            Variable     = "DATABASE_CONNECTION_STRING_ADMIN"
            Dll          = "EdFi.DataManagementService.Frontend.AspNetCore.dll"
            ExtraEnv     = @{ AppSettings__ApiSchema__UseCustomPath = "false" }
        }
    ) {
        BeforeAll {
            function Invoke-Case {
                param([string]$Value)

                $environment = @{ $Variable = $Value }
                foreach ($key in $ExtraEnv.Keys) {
                    $environment[$key] = $ExtraEnv[$key]
                }
                Invoke-EntryPoint -ScriptRelativePath $Script -Environment $environment
            }

            function Assert-WaitSkipped {
                param($Result)

                $Result.ExitCode | Should -Be 0 -Because $Result.Text
                Test-Path -LiteralPath $Result.PgIsReadyLog |
                    Should -BeFalse -Because "pg_isready must not be invoked when no host can be parsed"
                @($Result.Lines | Where-Object { $_ -match [regex]::Escape($Variable) -and $_ -match "(?i)skipp" }).Count |
                    Should -Be 1 -Because "output must say once that the wait was skipped and name $Variable"
                Get-Content -LiteralPath $Result.DotnetLog | Should -Contain $Dll
            }
        }

        It "skips the PostgreSQL wait when the connection variable is unset" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value $null
            Assert-WaitSkipped $result
        }

        It "skips the PostgreSQL wait when the connection variable is empty" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value ""
            Assert-WaitSkipped $result
        }

        It "skips the PostgreSQL wait for capitalized keywords and does not print the password" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "Host=db;Port=5432;Username=postgres;Password=$($script:marker)"
            Assert-WaitSkipped $result
            $result.Text | Should -Not -Match ([regex]::Escape($script:marker))
        }

        It "waits with the parsed host, port and username for the shipped lowercase value" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=dms-postgresql;port=5432;username=postgres;password=$($script:marker);database=edfi_config;"

            $result.ExitCode | Should -Be 0 -Because $result.Text
            $pgIsReadyInvocations = @(Get-Content -LiteralPath $result.PgIsReadyLog)
            $pgIsReadyInvocations.Count | Should -Be 1
            $pgIsReadyInvocations[0] | Should -BeExactly "-h dms-postgresql -p 5432 -U postgres"
            Get-Content -LiteralPath $result.DotnetLog | Should -Contain $Dll
            $result.Text | Should -Not -Match ([regex]::Escape($script:marker))
        }
    }

    Context "src/config/run.sh with the SQL Server datastore" {
        It "skips the TCP wait when the connection variable is unset" -Skip:(-not (Get-Command -Name bash -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-EntryPoint -ScriptRelativePath "src/config/run.sh" -Environment @{
                AppSettings__Datastore                = "mssql"
                DatabaseSettings__DatabaseConnection = $null
            }

            $result.ExitCode | Should -Be 0 -Because $result.Text
            Test-Path -LiteralPath $result.NcLog | Should -BeFalse -Because "nc must not be invoked when no host can be parsed"
            $result.Text |
                Should -Match "Could not parse a SQL Server host from DatabaseSettings__DatabaseConnection; skipping TCP readiness wait\."
            Get-Content -LiteralPath $result.DotnetLog |
                Should -Contain "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"
        }
    }
}
