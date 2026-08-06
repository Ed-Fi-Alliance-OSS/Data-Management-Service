# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: these tests exercise two self-contained helpers by extracting just the function definition
# from its script via the PowerShell AST and invoking it, without executing the script's top-level body
# (build-dms.ps1's Invoke-Main or the setup script's Docker orchestration).
#
# - Invoke-WithE2ETestProcessContext (build-dms.ps1) must restore every environment variable it mutates
#   to its exact prior state, preserving the unset-versus-valued distinction (PowerShell retains empty
#   and whitespace-valued environment variables).
# - Get-DirectSetupTeardownCommand (standard setup-local-dms.ps1) must print a teardown command carrying
#   the selected engine and the resolved environment-file path, safely single-quoted.
#
# DMS-1300 adds the same treatment for Invoke-WithEnvironmentFileSchemaSettings (both E2E setup
# wrappers), which must make the selected environment file the sole authority for the schema package
# surface of the Docker phases, and must round-trip the caller's exact prior environment.

param()

Describe "Invoke-WithE2ETestProcessContext restores prior environment state exactly (DMS-1284)" {
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $buildScript -FunctionName "Invoke-WithE2ETestProcessContext")))

        $script:mutatedVariables = @(
            "AppSettings__DataStoreDatabaseName"
            "AppSettings__DatabaseEngine"
            "AppSettings__DataStoreAdminConnectionString"
            "AppSettings__DataStoreConnectionString"
            "NODE_OPTIONS"
        )

        $script:testSettings = [pscustomobject]@{
            DataStoreDatabaseName          = "edfi_datamanagementservice_e2e"
            DatabaseEngine                 = "mssql"
            DataStoreAdminConnectionString = "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            DataStoreConnectionString      = "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
        }
    }

    AfterEach {
        foreach ($name in $script:mutatedVariables) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    It "restores AppSettings__DatabaseEngine to its prior <Label> state after the action throws" -ForEach @(
        @{ Label = "absent"; Setup = { Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue }; ExpectExists = $false; ExpectValue = $null }
        @{ Label = "empty"; Setup = { $env:AppSettings__DatabaseEngine = "" }; ExpectExists = $true; ExpectValue = "" }
        @{ Label = "whitespace"; Setup = { $env:AppSettings__DatabaseEngine = "   " }; ExpectExists = $true; ExpectValue = "   " }
        @{ Label = "nonempty"; Setup = { $env:AppSettings__DatabaseEngine = "prior-engine" }; ExpectExists = $true; ExpectValue = "prior-engine" }
    ) {
        & $Setup

        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:AppSettings__DatabaseEngine) | Should -Be $ExpectExists
        if ($ExpectExists) {
            $env:AppSettings__DatabaseEngine | Should -Be $ExpectValue
        }
    }

    It "sets the engine and both connection strings for the action, then restores them" {
        Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreAdminConnectionString -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreConnectionString -ErrorAction SilentlyContinue

        $observed = $null
        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action {
                $script:observed = [pscustomobject]@{
                    Engine       = $env:AppSettings__DatabaseEngine
                    Admin        = $env:AppSettings__DataStoreAdminConnectionString
                    Registration = $env:AppSettings__DataStoreConnectionString
                }
                throw "boom"
            } } | Should -Throw

        $script:observed.Engine | Should -Be "mssql"
        $script:observed.Admin | Should -Be $script:testSettings.DataStoreAdminConnectionString
        $script:observed.Registration | Should -Be $script:testSettings.DataStoreConnectionString

        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreAdminConnectionString) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreConnectionString) | Should -BeFalse
    }

    It "restores every mutated variable from a mix of absent, empty, whitespace, and valued prior states" {
        Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        $env:AppSettings__DatabaseEngine = ""
        $env:AppSettings__DataStoreAdminConnectionString = "   "
        $env:AppSettings__DataStoreConnectionString = "prior-registration"
        $env:NODE_OPTIONS = "--max-old-space-size=4096"

        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:AppSettings__DataStoreDatabaseName) | Should -BeFalse
        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeTrue
        $env:AppSettings__DatabaseEngine | Should -Be ""
        $env:AppSettings__DataStoreAdminConnectionString | Should -Be "   "
        $env:AppSettings__DataStoreConnectionString | Should -Be "prior-registration"
        $env:NODE_OPTIONS | Should -Be "--max-old-space-size=4096"
    }
}

Describe "Get-DirectSetupTeardownCommand carries the engine and resolved environment file (DMS-1284)" {
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $setupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $setupScript -FunctionName "Get-DirectSetupTeardownCommand")))
    }

    It "includes the selected engine and a single-quoted environment-file path" {
        $command = Get-DirectSetupTeardownCommand -DatabaseEngine mssql -EnvironmentFile "/repo/eng/docker-compose/.env.e2e"

        $command | Should -Be "./teardown-local-dms.ps1 -DatabaseEngine mssql -EnvironmentFile '/repo/eng/docker-compose/.env.e2e'"
    }

    It "safely single-quotes a path containing spaces and embedded single quotes" {
        $command = Get-DirectSetupTeardownCommand -DatabaseEngine postgresql -EnvironmentFile "C:\my env\o'brien.env"

        $command | Should -Be "./teardown-local-dms.ps1 -DatabaseEngine postgresql -EnvironmentFile 'C:\my env\o''brien.env'"
    }
}

Describe "setup-local-dms.ps1 wires the resolved environment file into teardown guidance (DMS-1284)" {
    BeforeAll {
        $setupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        $script:setupSource = Get-Content -LiteralPath $setupScript -Raw
    }

    # The teardown-guidance call is a non-executed display line, so a source assertion is the
    # appropriate check that production wires the single-resolution output (data-standard then engine
    # overlay) into teardown rather than the pre-overlay base file.
    It "passes the resolved environment file, not the base, to Get-DirectSetupTeardownCommand" {
        $script:setupSource |
            Should -Match 'Get-DirectSetupTeardownCommand -DatabaseEngine \$DatabaseEngine -EnvironmentFile \$resolvedEnvironmentFile'
        $script:setupSource |
            Should -Not -Match 'Get-DirectSetupTeardownCommand[^\r\n]*-EnvironmentFile \$baseEnvironmentFile'
    }
}

Describe "Invoke-WithEnvironmentFileSchemaSettings makes the environment file authoritative for the <Wrapper> setup phases (DMS-1300)" -ForEach @(
    @{ Wrapper = "direct DMS E2E"; RelativePath = "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1" }
    @{ Wrapper = "Instance Management E2E"; RelativePath = "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1" }
) {
    # Both wrappers carry their own copy of this guard, so both copies are executed here rather than
    # only the direct one. A copy that drifted - an assignment-based clear, a restore that collapses
    # present-but-empty to absent, a finally that does not run on exit - fails in its own iteration.
    #
    # Docker Compose gives process environment variables precedence over --env-file entries, and
    # local-dms.yml resolves USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES with
    # ${VAR:-default} fallbacks. Because ':-' substitutes the default for an empty value as well as an
    # unset one, an ambient blank value silently wins over the selected environment file: DMS starts on
    # the image-baked schemas while provisioning already stamped the file's full package surface, and
    # every data-plane request fails with an EffectiveSchemaHash mismatch. These tests execute the
    # guard rather than pattern-matching it, so the removal and the exact round-trip are real results.

    # Discovery-phase table: every guarded variable crossed with every prior-state shape that has to
    # round-trip. Built here rather than in BeforeAll because -ForEach is bound during discovery.
    $restoreCases = foreach ($variableName in @("USE_API_SCHEMA_PATH", "API_SCHEMA_PATH", "SCHEMA_PACKAGES")) {
        @{ VariableName = $variableName; Label = "absent"; PriorValue = $null }
        @{ VariableName = $variableName; Label = "empty"; PriorValue = "" }
        @{ VariableName = $variableName; Label = "whitespace"; PriorValue = "   " }
        @{ VariableName = $variableName; Label = "false"; PriorValue = "false" }
        @{ VariableName = $variableName; Label = "valued"; PriorValue = "prior-$variableName" }
    }

    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $script:wrapperScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $RelativePath))
        $script:guardFunctionText = Get-ScriptFunctionText -ScriptPath $script:wrapperScript -FunctionName "Invoke-WithEnvironmentFileSchemaSettings"
        . ([scriptblock]::Create($script:guardFunctionText))

        $script:schemaVariables = @("USE_API_SCHEMA_PATH", "API_SCHEMA_PATH", "SCHEMA_PACKAGES")

        # The running pwsh, resolved from the process rather than from PATH, so the child-process test
        # below needs neither a PATH lookup nor a skip guard that could hide it in CI.
        $script:pwshPath = (Get-Process -Id $PID).Path

        function Get-SchemaVariableState {
            $state = @{}
            foreach ($name in @("USE_API_SCHEMA_PATH", "API_SCHEMA_PATH", "SCHEMA_PACKAGES")) {
                $state[$name] = [pscustomobject]@{
                    Exists = [bool](Test-Path -LiteralPath "Env:$name")
                    Value  = [System.Environment]::GetEnvironmentVariable($name)
                }
            }

            return $state
        }
    }

    BeforeEach {
        foreach ($name in $script:schemaVariables) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($name in $script:schemaVariables) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    It "removes every schema variable for the guarded phases when the ambient state is <Label>" -ForEach @(
        @{ Label = "absent"; Ambient = @{} }
        @{ Label = "the compose fallback shape (false and blank)"; Ambient = @{ USE_API_SCHEMA_PATH = "false"; API_SCHEMA_PATH = ""; SCHEMA_PACKAGES = "" } }
        @{ Label = "all empty"; Ambient = @{ USE_API_SCHEMA_PATH = ""; API_SCHEMA_PATH = ""; SCHEMA_PACKAGES = "" } }
        @{ Label = "all whitespace"; Ambient = @{ USE_API_SCHEMA_PATH = "   "; API_SCHEMA_PATH = "   "; SCHEMA_PACKAGES = "   " } }
        @{ Label = "valued but wrong"; Ambient = @{ USE_API_SCHEMA_PATH = "true"; API_SCHEMA_PATH = "/ambient/ApiSchema"; SCHEMA_PACKAGES = '[{"name":"AmbientPackage"}]' } }
    ) {
        foreach ($entry in $Ambient.GetEnumerator()) {
            [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
        }

        $script:observedInsideAction = $null
        Invoke-WithEnvironmentFileSchemaSettings -Action {
            $script:observedInsideAction = Get-SchemaVariableState
        }

        # Removed, not blanked. A present-but-blank value satisfies ${VAR:-default}, so leaving any of
        # these present would reproduce the defect even though the guard appeared to run.
        foreach ($name in $script:schemaVariables) {
            $script:observedInsideAction[$name].Exists |
                Should -BeFalse -Because "$name must be absent for the guarded phases so Docker Compose resolves it from --env-file"
        }
    }

    It "round-trips <VariableName> from its prior <Label> state on the success path" -ForEach $restoreCases {
        if ($null -ne $PriorValue) {
            [System.Environment]::SetEnvironmentVariable($VariableName, $PriorValue)
        }

        # The arranged state is captured, not assumed. Whether a given runtime can hold a
        # present-but-empty environment variable is exactly the platform-dependent behavior this guard
        # must not depend on, so the property under test is that the guard restores whatever it found.
        $arranged = Get-SchemaVariableState

        Invoke-WithEnvironmentFileSchemaSettings -Action { }

        $restored = Get-SchemaVariableState
        foreach ($name in $script:schemaVariables) {
            $restored[$name].Exists | Should -Be $arranged[$name].Exists -Because "$name presence must round-trip"
            $restored[$name].Value | Should -Be $arranged[$name].Value -Because "$name value must round-trip verbatim"
        }
    }

    It "round-trips <VariableName> from its prior <Label> state when a guarded phase throws" -ForEach $restoreCases {
        if ($null -ne $PriorValue) {
            [System.Environment]::SetEnvironmentVariable($VariableName, $PriorValue)
        }

        $arranged = Get-SchemaVariableState

        { Invoke-WithEnvironmentFileSchemaSettings -Action { throw "phase failed" } } |
            Should -Throw -ExpectedMessage "phase failed"

        $restored = Get-SchemaVariableState
        foreach ($name in $script:schemaVariables) {
            $restored[$name].Exists | Should -Be $arranged[$name].Exists -Because "$name presence must round-trip after a failure"
            $restored[$name].Value | Should -Be $arranged[$name].Value -Because "$name value must round-trip verbatim after a failure"
        }
    }

    It "restores a mixed absent, empty, whitespace, and valued starting state per variable" {
        Remove-Item -LiteralPath "Env:USE_API_SCHEMA_PATH" -ErrorAction SilentlyContinue
        [System.Environment]::SetEnvironmentVariable("API_SCHEMA_PATH", "   ")
        [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", "prior-packages")

        $arranged = Get-SchemaVariableState

        Invoke-WithEnvironmentFileSchemaSettings -Action { }

        $restored = Get-SchemaVariableState
        $restored["USE_API_SCHEMA_PATH"].Exists | Should -BeFalse
        $restored["API_SCHEMA_PATH"].Value | Should -Be "   "
        $restored["SCHEMA_PACKAGES"].Value | Should -Be "prior-packages"
        foreach ($name in $script:schemaVariables) {
            $restored[$name].Exists | Should -Be $arranged[$name].Exists
            $restored[$name].Value | Should -Be $arranged[$name].Value
        }
    }

    It "keeps a present-but-empty variable present rather than collapsing it to absent" {
        # The distinction the previous assignment-based clear/restore could not express. Self-gated:
        # if this runtime cannot represent a present-but-empty environment variable at all, there is
        # no distinction to preserve and the case is recorded as skipped rather than failed.
        [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", "")
        if (-not (Test-Path -LiteralPath "Env:SCHEMA_PACKAGES")) {
            Set-ItResult -Skipped -Because "this runtime cannot hold a present-but-empty environment variable"
            return
        }

        Invoke-WithEnvironmentFileSchemaSettings -Action { }

        (Test-Path -LiteralPath "Env:SCHEMA_PACKAGES") | Should -BeTrue
        [System.Environment]::GetEnvironmentVariable("SCHEMA_PACKAGES") | Should -Be ""
    }

    It "restores the prior state when a guarded phase calls exit" {
        # The wrapper's phase failure paths use 'exit $LASTEXITCODE', so the guard has to survive an
        # exit as well as a throw. This runs in a child pwsh because an in-process exit would terminate
        # the Pester run: the child's own outer finally runs after the guard's finally, so it observes
        # the restored state, and the recorded exit code proves the phase's status still propagates.
        $observationPath = Join-Path $TestDrive "schema-env-exit-observations.txt"
        $childScriptPath = Join-Path $TestDrive "schema-env-exit-child.ps1"
        # The path is embedded in single-quoted literals in the generated child script, so an
        # apostrophe in it (possible in a temp path derived from a user name) would close the quote
        # early and break the child. Doubling it keeps the literal intact.
        $escapedObservationPath = $observationPath -replace "'", "''"
        $childScript = @"
$($script:guardFunctionText)

`$env:USE_API_SCHEMA_PATH = 'prior-use'
Remove-Item -LiteralPath 'Env:API_SCHEMA_PATH' -ErrorAction SilentlyContinue
`$env:SCHEMA_PACKAGES = 'prior-packages'

try {
    Invoke-WithEnvironmentFileSchemaSettings -Action {
        'inside:' + [string](Test-Path -LiteralPath 'Env:USE_API_SCHEMA_PATH') +
            ',' + [string](Test-Path -LiteralPath 'Env:API_SCHEMA_PATH') +
            ',' + [string](Test-Path -LiteralPath 'Env:SCHEMA_PACKAGES') |
            Add-Content -LiteralPath '$escapedObservationPath'
        exit 42
    }
}
finally {
    'after:' + [string](Test-Path -LiteralPath 'Env:USE_API_SCHEMA_PATH') +
        '=' + [string]`$env:USE_API_SCHEMA_PATH +
        ',' + [string](Test-Path -LiteralPath 'Env:API_SCHEMA_PATH') +
        ',' + [string](Test-Path -LiteralPath 'Env:SCHEMA_PACKAGES') +
        '=' + [string]`$env:SCHEMA_PACKAGES |
        Add-Content -LiteralPath '$escapedObservationPath'
}
"@
        Set-Content -LiteralPath $childScriptPath -Value $childScript -Encoding utf8

        & $script:pwshPath -NoProfile -File $childScriptPath
        $childExitCode = $LASTEXITCODE

        $childExitCode | Should -Be 42 -Because "the guard must not swallow the phase's exit code"

        $observations = @(Get-Content -LiteralPath $observationPath)
        $observations[0] | Should -Be "inside:False,False,False"
        $observations[1] | Should -Be "after:True=prior-use,False,True=prior-packages"
    }
}

Describe "Both E2E setup wrappers run every Docker phase inside the schema-settings guard (DMS-1300)" {
    BeforeAll {
        function Get-GuardedPhaseInvocation {
            <#
            .SYNOPSIS
            Returns one record per phase-script invocation in a setup wrapper, with whether the
            invocation is lexically inside the wrapper's single Invoke-WithEnvironmentFileSchemaSettings
            -Action block. Uses the AST rather than a text pattern, so the assertion is about the
            structure production actually has, and is not defeated by reformatting or reindentation.
            #>
            param(
                [Parameter(Mandatory)] [string] $ScriptPath
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

            if ($parseErrors.Count -gt 0) {
                throw "'$ScriptPath' has $($parseErrors.Count) parse error(s)."
            }

            $guardCalls = @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq "Invoke-WithEnvironmentFileSchemaSettings"
                },
                $true
            ))

            if ($guardCalls.Count -ne 1) {
                throw "Expected exactly one Invoke-WithEnvironmentFileSchemaSettings invocation in '$ScriptPath'; found $($guardCalls.Count)."
            }

            $actionBlocks = @($guardCalls[0].FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
                },
                $true
            ))

            if ($actionBlocks.Count -ne 1) {
                throw "Expected exactly one -Action script block on the guard invocation in '$ScriptPath'; found $($actionBlocks.Count)."
            }

            $actionExtent = $actionBlocks[0].Extent

            return @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                },
                $true
            ) | ForEach-Object {
                # Bareword script invocations report their own name; a script dispatched through a
                # variable (the Instance Management provision call) reports none, so fall back to the
                # variable name so that form is covered too.
                $name = $_.GetCommandName()
                if ($null -eq $name -and $_.CommandElements[0] -is [System.Management.Automation.Language.VariableExpressionAst]) {
                    $name = '$' + $_.CommandElements[0].VariablePath.UserPath
                }

                if ($name -and ($name -like "*.ps1" -or $name -like '$*Script')) {
                    [pscustomobject]@{
                        Name        = $name
                        Line        = $_.Extent.StartLineNumber
                        InsideGuard = ($_.Extent.StartOffset -ge $actionExtent.StartOffset -and $_.Extent.EndOffset -le $actionExtent.EndOffset)
                    }
                }
            })
        }

        $script:wrapperScripts = [ordered]@{
            "DataManagementService E2E" = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
            "InstanceManagement E2E"    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        }
    }

    It "runs the direct DMS E2E phase sequence inside the guard, in order" {
        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $script:wrapperScripts["DataManagementService E2E"])

        @($invocations | ForEach-Object { $_.Name }) | Should -Be @(
            "./start-local-dms.ps1",
            "./configure-local-data-store.ps1",
            "./provision-e2e-database.ps1",
            "./start-local-dms.ps1"
        )
        @($invocations | Where-Object { -not $_.InsideGuard }) | Should -BeNullOrEmpty
    }

    It "leaves no phase invocation outside the guard in the <Name> wrapper" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $script:wrapperScripts[$Name])

        $invocations.Count | Should -BeGreaterThan 0 -Because "the wrapper must invoke phase scripts"
        @($invocations | Where-Object { -not $_.InsideGuard } | ForEach-Object { "$($_.Name) (line $($_.Line))" }) |
            Should -BeNullOrEmpty -Because "a Compose-invoking phase outside the guard would resolve the schema variables from the ambient process again"
    }
}

Describe "Get-DmsSchemaEnvironmentVerdict fails setup when the DMS container disagrees with the provisioned package surface (DMS-1300)" {
    # The provisioner reads SCHEMA_PACKAGES from the environment file only, so the E2E database is
    # always stamped for the file's package surface; DMS receives its settings through Compose, which
    # resolves them ambient-first. When the two disagree the stack comes up healthy and then fails
    # every data-plane request with an EffectiveSchemaHash mismatch, so the verdict is what turns that
    # into a setup-time failure. Pure function, so no Docker is involved.

    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $script:directSetupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))

        # The verdict delegates classification, parsing, and identity normalization, so all four come
        # across together.
        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackage",
                "Get-DmsSchemaPackageIdentity",
                "Get-DmsSchemaEnvironmentVerdict"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:directSetupScript -FunctionName $functionName)))
        }

        # The path both sides of a passing comparison use, so a test that means "container agrees with
        # the environment file" cannot drift from the fixture's default.
        $script:fixtureApiSchemaPath = "/app/ApiSchema"

        function New-SchemaPackageFixture {
            # One generator for both sides of a comparison, carrying all three identity fields, so a
            # fixture that means "the container agrees with the environment file" agrees on the whole
            # package surface rather than only on how many packages there are.
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure in-memory test fixture factory despite the New- verb; it creates no system state, so -WhatIf/-Confirm semantics add no value.')]
            param(
                [int] $Count = 4,
                [string] $NamePrefix = "EdFi.ApiSchema.Package",
                [string] $Version = "1.0.0",
                [string] $FeedUrl = "https://pkgs.example.org/v3/index.json"
            )

            if ($Count -lt 1) {
                return , @()
            }

            return @(1..$Count | ForEach-Object {
                    [pscustomobject]@{ name = "$NamePrefix$_"; version = $Version; feedUrl = $FeedUrl }
                })
        }

        function Get-ContainerEnvironmentFixture {
            param(
                [string] $UseApiSchemaPath = "true",
                [string] $ApiSchemaPath = "/app/ApiSchema",
                [int] $PackageCount = 4,
                [object[]] $Package,
                [string] $RawSchemaPackages,
                [switch] $OmitUseApiSchemaPath,
                [switch] $OmitApiSchemaPath,
                [switch] $OmitSchemaPackages
            )

            $containerEnvironment = @{ "AppSettings__Datastore" = "postgresql" }

            if (-not $OmitUseApiSchemaPath) {
                $containerEnvironment["AppSettings__UseApiSchemaPath"] = $UseApiSchemaPath
            }

            if (-not $OmitApiSchemaPath) {
                $containerEnvironment["AppSettings__ApiSchemaPath"] = $ApiSchemaPath
            }

            if (-not $OmitSchemaPackages) {
                $containerEnvironment["SCHEMA_PACKAGES"] =
                    if ($PSBoundParameters.ContainsKey("RawSchemaPackages")) {
                        $RawSchemaPackages
                    }
                    else {
                        $containerPackages =
                            if ($PSBoundParameters.ContainsKey("Package")) {
                                @($Package)
                            }
                            else {
                                New-SchemaPackageFixture -Count $PackageCount
                            }

                        ConvertTo-Json -InputObject @($containerPackages) -Compress -Depth 5
                    }
            }

            return $containerEnvironment
        }

        # The environment file's declared surface for every case that is not about the surface itself:
        # four packages from the same generator the container fixture uses.
        $script:fixturePackageIdentity = Get-DmsSchemaPackageIdentity -Package (New-SchemaPackageFixture -Count 4)
    }

    It "passes when the container carries exactly the environment file's package surface" {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -PackageCount 4) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeFalse
        $verdict.Reason | Should -BeNullOrEmpty
    }

    It "fails on the reported compose-fallback shape, naming AppSettings__UseApiSchemaPath" {
        # The exact container state DMS-1300 reported: false plus two blanks, which is what
        # local-dms.yml's ${VAR:-default} produces when the process carries blank schema variables.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -UseApiSchemaPath "false" -ApiSchemaPath "" -RawSchemaPackages "") `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "AppSettings__UseApiSchemaPath is false"
        $verdict.Reason | Should -Match "4 ApiSchema package"
        $verdict.Remediation | Should -Match "USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES"
    }

    It "fails when AppSettings__UseApiSchemaPath is <Label>, reporting a fixed token" -ForEach @(
        @{ Label = "absent"; Container = @{ OmitUseApiSchemaPath = $true }; ExpectedToken = "<absent>" }
        @{ Label = "blank"; Container = @{ UseApiSchemaPath = "" }; ExpectedToken = "<blank>" }
        @{ Label = "whitespace"; Container = @{ UseApiSchemaPath = "   " }; ExpectedToken = "<blank>" }
        @{ Label = "an unrecognized value"; Container = @{ UseApiSchemaPath = "yes" }; ExpectedToken = "<set>" }
        # run.sh:28 gates the package download on a byte-exact [ "$AppSettings__UseApiSchemaPath" = true ],
        # so an uppercase or title-case value downloads nothing while the database is already stamped for
        # the file's packages. Accepting it here passed a container that could only 503.
        @{ Label = "uppercase TRUE"; Container = @{ UseApiSchemaPath = "TRUE" }; ExpectedToken = "<set>" }
        @{ Label = "title-case True"; Container = @{ UseApiSchemaPath = "True" }; ExpectedToken = "<set>" }
    ) {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "AppSettings__UseApiSchemaPath is $([regex]::Escape($ExpectedToken))"
    }

    It "fails when AppSettings__ApiSchemaPath is <Label>, so declared packages have nowhere to land" -ForEach @(
        @{ Label = "absent"; Container = @{ OmitApiSchemaPath = $true }; ExpectedToken = "<absent>" }
        @{ Label = "blank"; Container = @{ ApiSchemaPath = "" }; ExpectedToken = "<blank>" }
        @{ Label = "whitespace"; Container = @{ ApiSchemaPath = "   " }; ExpectedToken = "<blank>" }
    ) {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "AppSettings__ApiSchemaPath is $([regex]::Escape($ExpectedToken))"
    }

    It "fails when the container's package count differs from the provisioned surface" {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -PackageCount 2) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "received 2 ApiSchema package"
        $verdict.Reason | Should -Match "provisioned for the environment file's 4"
    }

    It "fails when the count matches but every package's <Field> differs" -ForEach @(
        @{ Field = "name"; Override = @{ NamePrefix = "Contoso.ApiSchema.Package" } }
        @{ Field = "version"; Override = @{ Version = "9.9.9" } }
        @{ Field = "feedUrl"; Override = @{ FeedUrl = "https://other-feed.example.net/v3/index.json" } }
    ) {
        # Exactly what a count-only comparison cannot see. Four packages resolved from a different
        # name, version, or feed are a different schema surface than the E2E database was provisioned
        # for, so DMS computes a different runtime hash and every data-plane request fails with an
        # EffectiveSchemaHash mismatch - the failure this gate exists to turn into a setup-time error.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package (New-SchemaPackageFixture -Count 4 @Override)) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "differ from the environment file's declared packages by name, version, or feed URL"
        # Not misreported as a count problem: the counts agree.
        $verdict.Reason | Should -Not -Match "but the E2E database was provisioned for the environment file's"
    }

    It "fails when the count matches and only one of the packages differs" {
        # The comparison is over the whole set, not a sampled or first-entry check.
        $divergentPackages = @(New-SchemaPackageFixture -Count 4)
        $divergentPackages[2] = [pscustomobject]@{
            name    = $divergentPackages[2].name
            version = "7.7.7"
            feedUrl = $divergentPackages[2].feedUrl
        }

        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package $divergentPackages) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "differ from the environment file's declared packages by name, version, or feed URL"
    }

    It "never echoes the differing package values into the surface-mismatch failure text" {
        # The mismatch message is derived vocabulary, so container-supplied package text cannot forge
        # log lines or leak a feed URL into the console.
        $sentinelPackages = @(New-SchemaPackageFixture -Count 4 -FeedUrl "https://SENTINEL-FEED-DO-NOT-ECHO.example.net/index.json")

        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package $sentinelPackages) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "SENTINEL-FEED-DO-NOT-ECHO"
    }

    It "accepts the same package surface declared in a different order" {
        # Declaration order is not part of the surface: run.sh downloads the same packages either way,
        # so both sides are sorted ordinally before they are compared. Without the sort this passing
        # case would be reported as a mismatch.
        $reorderedPackages = @(New-SchemaPackageFixture -Count 4)
        [array]::Reverse($reorderedPackages)

        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package $reorderedPackages) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeFalse
        $verdict.Reason | Should -BeNullOrEmpty
    }

    # ConvertFrom-Json is lenient about some structurally invalid JSON: it accepts a missing closing
    # bracket and a trailing comma, parsing both as arrays. Those shapes are therefore classified by
    # entry count, not as malformed, and still fail the verdict whenever the count disagrees with the
    # environment file. Only inputs the parser actually rejects belong in this table.
    It "fails without throwing when the container's SCHEMA_PACKAGES is <Label>" -ForEach @(
        @{ Label = "absent"; Container = @{ OmitSchemaPackages = $true } }
        @{ Label = "blank"; Container = @{ RawSchemaPackages = "" } }
        @{ Label = "whitespace"; Container = @{ RawSchemaPackages = "   " } }
        @{ Label = "not JSON at all"; Container = @{ RawSchemaPackages = "not-json" } }
        @{ Label = "an array with a malformed element"; Container = @{ RawSchemaPackages = '[{"name":}]' } }
        @{ Label = "JSON null"; Container = @{ RawSchemaPackages = "null" } }
        @{ Label = "a JSON object rather than an array"; Container = @{ RawSchemaPackages = '{"name":"Package1"}' } }
    ) {
        $verdict = $null
        { $script:verdict = Get-DmsSchemaEnvironmentVerdict `
                -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
                -ExpectedPackageIdentity $script:fixturePackageIdentity `
                -EnvironmentFileUsesApiSchemaPath $true `
                -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath } | Should -Not -Throw

        $script:verdict.ShouldFail | Should -BeTrue
        $script:verdict.Reason | Should -Match "SCHEMA_PACKAGES is absent, blank, or not a JSON array"
    }

    It "fails when the container's ApiSchemaPath is present but not the path the environment file selected" {
        # The token check only proves the path is non-blank. A container pointed at a different path is
        # materializing packages somewhere other than where the environment file said, which no other
        # check in the verdict can see.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -ApiSchemaPath "/somewhere/else") `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "AppSettings__ApiSchemaPath differs from the environment file's API_SCHEMA_PATH"
        # Neither path is echoed, so a container-supplied value cannot reach the message.
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "/somewhere/else"
    }

    It "treats an empty container SCHEMA_PACKAGES array as zero packages, not as a malformed value" {
        # '[]' is a valid JSON array, so it belongs on the count-mismatch path: reporting it as
        # malformed would misdescribe a container that simply received no packages.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -RawSchemaPackages "[]") `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "received 0 ApiSchema package"
        $verdict.Reason | Should -Match "provisioned for the environment file's 4"
        $verdict.Reason | Should -Not -Match "not a JSON array"
    }

    It "accepts a one-package JSON array without mistaking a JSON object for a one-item list" {
        # ConvertFrom-Json unwraps a single-element array to one PSCustomObject, so parsing without
        # -NoEnumerate rejected a valid one-package container as "not a JSON array". The shape check
        # cannot be relaxed by wrapping the parse result in @(...) instead, because that would make a
        # JSON object look like a one-item package list, so both halves are pinned together here.
        $onePackageIdentity = Get-DmsSchemaPackageIdentity -Package (New-SchemaPackageFixture -Count 1)

        $onePackageArray = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -PackageCount 1) `
            -ExpectedPackageIdentity $onePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $onePackageArray.ShouldFail |
            Should -BeFalse -Because "a one-package container matches a one-package environment file"

        $jsonObject = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -RawSchemaPackages '{"name":"Package1"}') `
            -ExpectedPackageIdentity $onePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $jsonObject.ShouldFail | Should -BeTrue -Because "a JSON object is not a package list"
        $jsonObject.Reason | Should -Match "SCHEMA_PACKAGES is absent, blank, or not a JSON array"
    }

    It "reports a package-bearing environment file that does not enable the ApiSchema path as the inconsistency" {
        # Not a skip. The provisioner stamps the file's packages regardless of USE_API_SCHEMA_PATH, so
        # such a file guarantees the mismatch, and the remediation belongs on the file, not the shell.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -UseApiSchemaPath "false" -ApiSchemaPath "" -RawSchemaPackages "") `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $false `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "the environment file declares 4 ApiSchema package\(s\) but does not set USE_API_SCHEMA_PATH=true"
        $verdict.Remediation | Should -Match "Set USE_API_SCHEMA_PATH=true in the environment file"
    }

    It "refuses an empty declared package surface, because the file-only reader cannot produce one" {
        # There is no "no packages declared" branch by design: an absent, malformed, or empty
        # SCHEMA_PACKAGES already fails the provision phase, so it can never reach the gate and be
        # classified as acceptable. The contract is encoded as parameter validation.
        { Get-DmsSchemaEnvironmentVerdict `
                -ContainerEnvironment (Get-ContainerEnvironmentFixture) `
                -ExpectedPackageIdentity @() `
                -EnvironmentFileUsesApiSchemaPath $true `
                -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath } | Should -Throw
    }

    It "never echoes the container's raw SCHEMA_PACKAGES value into the failure text" {
        # The message vocabulary is fixed and derived, so container-supplied text cannot forge log
        # lines or bloat the failure with a package blob.
        $rawSchemaPackages = "[{`"name`":`"SENTINEL-DO-NOT-ECHO`"}]`nforged-log-line"
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -RawSchemaPackages $rawSchemaPackages) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "SENTINEL-DO-NOT-ECHO"
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "forged-log-line"
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "`n"
    }
}

Describe "Assert-DmsContainerSchemaEnvironment throws on a mismatch and returns on agreement (DMS-1300)" {
    # Executes the assertion itself, against stubbed readers, so the wiring between the file-only
    # expectations, the verdict, and the throw is a real result rather than a source assertion. An
    # inverted 'if ($verdict.ShouldFail)' branch fails both cases below.
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $script:directSetupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))

        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackage",
                "Get-DmsSchemaPackageIdentity",
                "Get-DmsSchemaEnvironmentVerdict",
                "Assert-DmsContainerSchemaEnvironment"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:directSetupScript -FunctionName $functionName)))
        }

        # The real Compose value semantics, not a stub: the quoting and inline-comment cases below are
        # only meaningful if the assertion normalizes the environment file's raw text the way Docker
        # Compose does. Production reaches this function through the same module import.
        Import-Module (Join-Path $PSScriptRoot "../database-safety.psm1") -Force

        # Stubs for the three readers the assertion depends on, so no Docker and no environment file are
        # involved. Each returns whatever the current test arranged.
        # Each stub declares the parameters production binds by name, so a renamed argument at the call
        # site fails here rather than silently passing. The values themselves are not needed, hence the
        # discards.
        function Get-DmsContainerEnvironment {
            param([Parameter(Mandatory)] [string] $ContainerName)

            $null = $ContainerName
            return $script:stubContainerEnvironment
        }

        function Get-SchemaPackagesFromEnvironmentFile {
            param([Parameter(Mandatory)] [string] $EnvironmentFilePath)

            $null = $EnvironmentFilePath
            return $script:stubDeclaredPackages
        }

        function Get-EnvValue {
            param(
                [hashtable] $EnvValues,
                [Parameter(Mandatory)] [string] $Name,
                [string] $DefaultValue = ""
            )

            $null = $EnvValues

            if ($script:stubEnvironmentFileValues.ContainsKey($Name)) {
                return $script:stubEnvironmentFileValues[$Name]
            }

            return $DefaultValue
        }
    }

    BeforeEach {
        # A four-package environment file and a container that agrees with it. The declared packages are
        # PSCustomObjects carrying all three identity fields, which is what ConvertFrom-Json hands back
        # from a real SCHEMA_PACKAGES declaration.
        $script:stubDeclaredPackages = @(1..4 | ForEach-Object {
                [pscustomobject]@{
                    name    = "EdFi.ApiSchema.Package$_"
                    version = "1.0.0"
                    feedUrl = "https://pkgs.example.org/v3/index.json"
                }
            })
        $script:stubEnvironmentFileValues = @{
            USE_API_SCHEMA_PATH = "true"
            API_SCHEMA_PATH     = "/app/ApiSchema"
        }
        $script:stubContainerEnvironment = @{
            "AppSettings__UseApiSchemaPath" = "true"
            "AppSettings__ApiSchemaPath"    = "/app/ApiSchema"
            "SCHEMA_PACKAGES"               = ConvertTo-Json -InputObject $script:stubDeclaredPackages -Compress -Depth 5
        }
    }

    It "returns without throwing when the container agrees with the environment file" {
        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "throws with the setup-mismatch prefix when the container <Label>" -ForEach @(
        @{ Label = "has UseApiSchemaPath false"; Mutate = { $script:stubContainerEnvironment["AppSettings__UseApiSchemaPath"] = "false" } }
        @{ Label = "has a blank ApiSchemaPath"; Mutate = { $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "" } }
        @{ Label = "points at a different ApiSchemaPath"; Mutate = { $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/somewhere/else" } }
        @{ Label = "received a different package count"; Mutate = { $script:stubContainerEnvironment["SCHEMA_PACKAGES"] = '[{"name":"Package1"}]' } }
        @{ Label = "received the same number of packages at a different version"; Mutate = {
                $script:stubContainerEnvironment["SCHEMA_PACKAGES"] = ConvertTo-Json -Compress -Depth 5 -InputObject @(
                    $script:stubDeclaredPackages | ForEach-Object {
                        [pscustomobject]@{ name = $_.name; version = "9.9.9"; feedUrl = $_.feedUrl }
                    })
            }
        }
    ) {
        & $Mutate

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "throws when the environment file declares packages without enabling the ApiSchema path" {
        $script:stubEnvironmentFileValues["USE_API_SCHEMA_PATH"] = "false"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "compares against the environment file's API_SCHEMA_PATH, not a hardcoded default" {
        # Both sides move together: a container matching a non-default environment-file path must pass.
        $script:stubEnvironmentFileValues["API_SCHEMA_PATH"] = "/custom/ApiSchema"
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/custom/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "accepts a Compose-legal <Label> API_SCHEMA_PATH declaration" -ForEach @(
        @{ Label = "double-quoted"; RawValue = '"/custom/ApiSchema"' }
        @{ Label = "single-quoted"; RawValue = "'/custom/ApiSchema'" }
        @{ Label = "inline-commented"; RawValue = "/custom/ApiSchema # where the E2E packages are mounted" }
        @{ Label = "quoted and inline-commented"; RawValue = '"/custom/ApiSchema" # where the E2E packages are mounted' }
    ) {
        # Docker Compose strips surrounding quotes and a whitespace-preceded inline comment before it
        # passes the value into the container, so a correctly started container legitimately carries the
        # bare path. Comparing the environment file's raw text failed such a stack at setup time even
        # though nothing was wrong with it - a false failure a custom -EnvironmentFile can easily hit.
        $script:stubEnvironmentFileValues["API_SCHEMA_PATH"] = $RawValue
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/custom/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "still fails a genuinely different path when the declaration is quoted" {
        # Normalization must not turn the path comparison into a no-op.
        $script:stubEnvironmentFileValues["API_SCHEMA_PATH"] = '"/custom/ApiSchema"'
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/somewhere/else"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "accepts a Compose-legal <Label> USE_API_SCHEMA_PATH declaration" -ForEach @(
        @{ Label = "double-quoted"; RawValue = '"true"' }
        @{ Label = "single-quoted"; RawValue = "'true'" }
        @{ Label = "inline-commented"; RawValue = "true # file-based ApiSchema packages" }
    ) {
        # Same false failure on the other file-read expectation: raw quoted text is not equal to "true",
        # so the gate reported a correctly configured environment file as internally inconsistent.
        $script:stubEnvironmentFileValues["USE_API_SCHEMA_PATH"] = $RawValue

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "throws when the environment file spells USE_API_SCHEMA_PATH as <Label>" -ForEach @(
        @{ Label = "uppercase TRUE"; RawValue = "TRUE" }
        @{ Label = "title-case True"; RawValue = "True" }
        @{ Label = "quoted uppercase TRUE"; RawValue = '"TRUE"' }
    ) {
        # Compose passes the value through verbatim and run.sh:28 compares it byte-exact, so a file
        # declaring TRUE starts a container that downloads no packages while phase 3 already stamped the
        # database for the file's full surface. Normalization strips the quotes but must not fold case,
        # which the quoted variant pins.
        $script:stubEnvironmentFileValues["USE_API_SCHEMA_PATH"] = $RawValue

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "throws when the container's AppSettings__UseApiSchemaPath is <Label>" -ForEach @(
        @{ Label = "uppercase TRUE"; Value = "TRUE" }
        @{ Label = "title-case True"; Value = "True" }
    ) {
        # The container side of the same rule: an ambient TRUE reaching Compose produces a container
        # that run.sh treats as "not set", so the gate must not accept it as agreement.
        $script:stubContainerEnvironment["AppSettings__UseApiSchemaPath"] = $Value

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath "/repo/eng/docker-compose/.env.e2e" `
                -EnvironmentValues @{} `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }
}

Describe "Get-DmsContainerEnvironment reads the container environment and fails closed (DMS-1300)" {
    # Executes the reader against a stubbed 'docker', so its parsing rules and its fail-closed behavior
    # are real results rather than a source pattern, and no Docker daemon is required.
    BeforeAll {
        function Get-ScriptFunctionText {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            ) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return $functionAst.Extent.Text
        }

        $script:directSetupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:directSetupScript -FunctionName "Get-DmsContainerEnvironment")))

        # A PowerShell function shadows the native command of the same name, so the reader's
        # 'docker inspect' resolves here. Production decides success from $LASTEXITCODE, which only a
        # native command normally writes, so the stub sets it explicitly.
        function docker {
            $script:stubDockerArguments = @($args)
            $global:LASTEXITCODE = $script:stubDockerExitCode
            return $script:stubDockerOutput
        }
    }

    BeforeEach {
        $script:stubDockerExitCode = 0
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @("AppSettings__UseApiSchemaPath=true")
        $script:stubDockerArguments = @()
    }

    It "inspects the requested container for its environment only" {
        $null = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $script:stubDockerArguments | Should -Be @("inspect", "ed-fi-api", "--format", "{{json .Config.Env}}")
    }

    It "reads each entry into a key and value" {
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @(
            "AppSettings__UseApiSchemaPath=true",
            "AppSettings__ApiSchemaPath=/app/ApiSchema"
        )

        $containerEnvironment = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $containerEnvironment["AppSettings__UseApiSchemaPath"] | Should -Be "true"
        $containerEnvironment["AppSettings__ApiSchemaPath"] | Should -Be "/app/ApiSchema"
    }

    It "keeps every '=' after the first inside the value" {
        # Container values routinely contain '=' - connection strings and the SCHEMA_PACKAGES JSON both
        # do - so splitting on every separator would truncate exactly the values the gate compares.
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @(
            'AppSettings__DataStoreConnectionString=Host=dms-postgresql;Database=edfi_e2e;Username=postgres',
            'SCHEMA_PACKAGES=[{"name":"EdFi.ApiSchema.Package1","version":"1.0.0"}]'
        )

        $containerEnvironment = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $containerEnvironment["AppSettings__DataStoreConnectionString"] |
            Should -Be "Host=dms-postgresql;Database=edfi_e2e;Username=postgres"
        $containerEnvironment["SCHEMA_PACKAGES"] |
            Should -Be '[{"name":"EdFi.ApiSchema.Package1","version":"1.0.0"}]'
    }

    It "keeps an entry whose value is empty, so a blank container value stays distinguishable from an absent one" {
        # Get-DmsSchemaEnvironmentToken reports <blank> and <absent> as different findings, which only
        # works if the reader preserves the difference.
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @("AppSettings__ApiSchemaPath=")

        $containerEnvironment = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $containerEnvironment.ContainsKey("AppSettings__ApiSchemaPath") | Should -BeTrue
        $containerEnvironment["AppSettings__ApiSchemaPath"] | Should -Be ""
    }

    It "skips an entry that has no '=' rather than failing or inventing a key" {
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @(
            "MALFORMED_ENTRY",
            "AppSettings__ApiSchemaPath=/app/ApiSchema"
        )

        $containerEnvironment = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $containerEnvironment.ContainsKey("MALFORMED_ENTRY") | Should -BeFalse
        $containerEnvironment["AppSettings__ApiSchemaPath"] | Should -Be "/app/ApiSchema"
    }

    It "throws when docker inspect fails, so an inability to verify is never read as a pass" {
        $script:stubDockerExitCode = 1
        $script:stubDockerOutput = ""

        { Get-DmsContainerEnvironment -ContainerName "ed-fi-api" } |
            Should -Throw -ExpectedMessage "Unable to inspect Docker container 'ed-fi-api'*"
    }
}

Describe "The direct DMS E2E setup wrapper verifies the started container against the environment file only (DMS-1300)" {
    BeforeAll {
        $script:directSetupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        $script:directSetupSource = Get-Content -LiteralPath $script:directSetupScript -Raw

        function Test-CommandInsideSchemaGuard {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $CommandName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

            $guardCall = @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq "Invoke-WithEnvironmentFileSchemaSettings"
                },
                $true
            )) | Select-Object -First 1

            $actionExtent = (@($guardCall.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
                },
                $true
            )) | Select-Object -First 1).Extent

            $calls = @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq $CommandName
                },
                $true
            ))

            if ($calls.Count -ne 1) {
                throw "Expected exactly one '$CommandName' invocation in '$ScriptPath'; found $($calls.Count)."
            }

            return ($calls[0].Extent.StartOffset -ge $actionExtent.StartOffset -and $calls[0].Extent.EndOffset -le $actionExtent.EndOffset)
        }

        function Get-FunctionCommandName {
            <#
            .SYNOPSIS
            Returns the distinct command names a named function invokes, from the AST. Comment text and
            string literals are excluded by construction, so an assertion about what the function CALLS
            cannot be satisfied or defeated by what it merely mentions.
            #>
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $FunctionName
            )

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

            $functionAst = @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
                },
                $true
            )) | Select-Object -First 1

            if ($null -eq $functionAst) {
                throw "Function '$FunctionName' was not found in '$ScriptPath'."
            }

            return @($functionAst.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                },
                $true
            ) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Select-Object -Unique)
        }
    }

    It "runs the container verification inside the schema-settings guard" {
        # Inside the guard, so the file-only expectation cannot be contaminated by an ambient override
        # even if a future edit reaches for a Compose-precedence reader.
        Test-CommandInsideSchemaGuard -ScriptPath $script:directSetupScript -CommandName "Assert-DmsContainerSchemaEnvironment" |
            Should -BeTrue
    }

    It "reads both expectations from the environment file, never with Compose precedence" {
        # Asserted over the commands the function actually invokes, not its text: the function's own
        # comment names Get-ComposeResolvedEnvValue in order to prohibit it, and a text search cannot
        # tell a prohibition from a call.
        #
        # Get-ComposeResolvedEnvValue resolves ambient-first. Using it for either expectation would let
        # the very override this gate exists to catch decide what "correct" means, so the gate would
        # agree with a wrongly-started container and pass.
        $invoked = @(Get-FunctionCommandName -ScriptPath $script:directSetupScript -FunctionName "Assert-DmsContainerSchemaEnvironment")

        $invoked | Should -Contain "Get-SchemaPackagesFromEnvironmentFile"
        $invoked | Should -Contain "Get-EnvValue"
        $invoked | Should -Not -Contain "Get-ComposeResolvedEnvValue"
        $script:directSetupSource | Should -Match 'Get-EnvValue -EnvValues \$EnvironmentValues -Name "USE_API_SCHEMA_PATH"'
    }

    It "imports the same file-only package reader the provision phase uses" {
        $script:directSetupSource | Should -Match "Import-Module \.\./schema-package-utility\.psm1 -Force"
    }

    It "throws rather than warning when the verdict fails" {
        $script:directSetupSource | Should -Match 'throw "DMS E2E setup mismatch: '
        $script:directSetupSource | Should -Not -Match 'Write-Warning[^\r\n]*setup mismatch'
    }
}
