# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Covers the PostgreSQL readiness wait in the container entry scripts src/config/run.sh and
# src/dms/run.sh. pg_isready, nc and dotnet are stubbed on PATH. The pg_isready stub exits 0 so
# that a script which wrongly enters the wait loop finishes at once and fails an assertion
# instead of hanging. pg_isready records each argument in brackets, so an argument that is
# missing, empty or split in two is visible. Every case runs under bash and under sh: the
# Dockerfile images run the scripts with bash, the Nuget.Dockerfile images with busybox ash, and
# sh (dash on the CI runner) stands in for ash's shell syntax. It does not stand in for the
# images' busybox awk, which the connection string parser runs on, so the parser is limited to
# POSIX awk.

Describe "Container entry point database wait" {
    BeforeAll {
        $script:sourceRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:marker = "Marker-Pw-7f3a91c2"
        $script:caseCounter = 0

        function Invoke-EntryPoint {
            param(
                [string]$Shell,
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
                $record = if ($stubName -eq "pg_isready") {
                    "printf '[%s]' `"`$@`" >> `"$($logPaths[$stubName])`"; echo >> `"$($logPaths[$stubName])`""
                }
                else {
                    "echo `"`$*`" >> `"$($logPaths[$stubName])`""
                }
                @"
#!/bin/sh
$record
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
                # Start from a clean slate so ambient values cannot leak into a case. A $null value
                # in $Environment leaves the variable unset; an empty string sets it to empty.
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

                $runOutput = & $Shell (Join-Path $script:sourceRepoRoot $ScriptRelativePath) 2>&1
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
                ExitCode     = $exitCode
                Lines        = $lines
                Text         = ($lines -join [Environment]::NewLine)
                PgIsReadyLog = $logPaths.pg_isready
                NcLog        = $logPaths.nc
                DotnetLog    = $logPaths.dotnet
            }
        }
    }

    Context "<Script> under <Shell>" -ForEach @(
        foreach ($shell in @("bash", "sh")) {
            @{
                Script   = "src/config/run.sh"
                Shell    = $shell
                Variable = "DatabaseSettings__DatabaseConnection"
                Dll      = "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"
                ExtraEnv = @{}
            }
            @{
                Script   = "src/dms/run.sh"
                Shell    = $shell
                Variable = "DATABASE_CONNECTION_STRING_ADMIN"
                Dll      = "EdFi.DataManagementService.Frontend.AspNetCore.dll"
                ExtraEnv = @{ AppSettings__ApiSchema__UseCustomPath = "false" }
            }
        }
    ) {
        BeforeAll {
            $skipLine = "No host= setting found in $Variable; skipping the PostgreSQL readiness wait."

            function Invoke-Case {
                param([AllowNull()][object]$Value)

                $environment = @{ $Variable = $Value }
                foreach ($key in $ExtraEnv.Keys) {
                    $environment[$key] = $ExtraEnv[$key]
                }
                Invoke-EntryPoint -Shell $Shell -ScriptRelativePath $Script -Environment $environment
            }

            function Assert-WaitSkipped {
                param($Result)

                $Result.ExitCode | Should -Be 0 -Because $Result.Text
                Test-Path -LiteralPath $Result.PgIsReadyLog |
                    Should -BeFalse -Because "pg_isready must not be invoked when no host can be parsed"
                @($Result.Lines | Where-Object { $_ -ceq $skipLine }).Count |
                    Should -Be 1 -Because "output must say exactly once that the wait was skipped"
                Get-Content -LiteralPath $Result.DotnetLog | Should -Contain $Dll
                $Result.Text | Should -Not -Match ([regex]::Escape($script:marker))
            }

            function Assert-WaitedWith {
                param($Result, [string]$ExpectedArguments)

                $Result.ExitCode | Should -Be 0 -Because $Result.Text
                Test-Path -LiteralPath $Result.PgIsReadyLog |
                    Should -BeTrue -Because "pg_isready must be invoked when a host is parsed: $($Result.Text)"
                $pgIsReadyInvocations = @(Get-Content -LiteralPath $Result.PgIsReadyLog)
                $pgIsReadyInvocations.Count | Should -Be 1
                $pgIsReadyInvocations[0] | Should -BeExactly $ExpectedArguments
                Get-Content -LiteralPath $Result.DotnetLog | Should -Contain $Dll
                $Result.Text | Should -Not -Match ([regex]::Escape($script:marker))
                Get-Content -LiteralPath $Result.PgIsReadyLog -Raw |
                    Should -Not -Match ([regex]::Escape($script:marker))
            }
        }

        It "skips the PostgreSQL wait when the connection variable is unset" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            Assert-WaitSkipped (Invoke-Case -Value $null)
        }

        It "skips the PostgreSQL wait when the connection variable is empty" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            Assert-WaitSkipped (Invoke-Case -Value "")
        }

        It "skips the PostgreSQL wait for capitalized keywords and does not print the password" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            Assert-WaitSkipped (Invoke-Case -Value "Host=db;Port=5432;Username=postgres;Password=$($script:marker)")
        }

        It "does not take a host from inside another setting's value" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            Assert-WaitSkipped (Invoke-Case -Value "Host=db;Password=ghost=$($script:marker)")
        }

        It "waits with the parsed host, port and username for the shipped lowercase value" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=dms-postgresql;port=5432;username=postgres;password=$($script:marker);database=edfi_config;"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "waits with only the host setting's value when another value contains host=" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=dms-postgresql;port=5432;username=postgres;password=ghost=$($script:marker);"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "passes an empty port to pg_isready when the lowercase value omits it" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=dms-postgresql;username=postgres;password=$($script:marker);"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][][-U][postgres]"
        }

        It "passes an empty username to pg_isready when the lowercase value omits it" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=dms-postgresql;port=5432;password=$($script:marker);"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][]"
        }

        # The cases below follow the .NET connection string rules the services themselves parse
        # with: a quoted value may contain semicolons, a doubled quote inside it is a literal
        # quote, and the last occurrence of a key wins.
        It "reads the host setting that follows a quoted value containing ;host=" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "password=`"a;host=$($script:marker)`";host=dms-postgresql;port=5432;username=postgres"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "skips the PostgreSQL wait when the only host= is inside a quoted value" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            Assert-WaitSkipped (Invoke-Case -Value "Host=db;Password=`"safe;host=$($script:marker);tail`"")
        }

        It "treats single-quoted values and doubled quotes as part of the value" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "password='x;host=$($script:marker)';host=dms-postgresql;port=5432;username=postgres;database=`"a`"`"b;host=$($script:marker)`""
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "removes the quotes around a quoted host and port" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=`"dms-postgresql`";port='5432';username=postgres;password=$($script:marker)"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "unwraps a value surrounded by one pair of quotes, as docker run --env-file passes it" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "`"host=dms-postgresql;port=5432;username=postgres;password=$($script:marker)`""
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }

        It "uses the last occurrence of a repeated key" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-Case -Value "host=first-host;host=dms-postgresql;port=5432;username=postgres;password=$($script:marker)"
            Assert-WaitedWith $result "[-h][dms-postgresql][-p][5432][-U][postgres]"
        }
    }

    Context "src/config/run.sh with the SQL Server datastore under <Shell>" -ForEach @(
        @{ Shell = "bash" }
        @{ Shell = "sh" }
    ) {
        It "skips the TCP wait when the connection variable is unset" -Skip:(-not (Get-Command -Name $Shell -ErrorAction SilentlyContinue) -or -not (Get-Command -Name chmod -ErrorAction SilentlyContinue)) {
            $result = Invoke-EntryPoint -Shell $Shell -ScriptRelativePath "src/config/run.sh" -Environment @{
                AppSettings__Datastore               = "mssql"
                DatabaseSettings__DatabaseConnection = $null
            }

            $result.ExitCode | Should -Be 0 -Because $result.Text
            Test-Path -LiteralPath $result.NcLog | Should -BeFalse -Because "nc must not be invoked when no host can be parsed"
            @($result.Lines | Where-Object {
                    $_ -ceq "Could not parse a SQL Server host from DatabaseSettings__DatabaseConnection; skipping TCP readiness wait."
                }).Count | Should -Be 1
            Get-Content -LiteralPath $result.DotnetLog |
                Should -Contain "EdFi.DmsConfigurationService.Frontend.AspNetCore.dll"
        }
    }
}
