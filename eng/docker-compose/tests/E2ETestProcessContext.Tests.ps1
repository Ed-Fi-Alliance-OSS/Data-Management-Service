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

Describe "Invoke-WithEnvironmentFileSchemaSettings makes the environment file authoritative for the direct DMS E2E setup phases (DMS-1300)" {
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

        $script:directSetupScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
        $script:guardFunctionText = Get-ScriptFunctionText -ScriptPath $script:directSetupScript -FunctionName "Invoke-WithEnvironmentFileSchemaSettings"
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
            Add-Content -LiteralPath '$observationPath'
        exit 42
    }
}
finally {
    'after:' + [string](Test-Path -LiteralPath 'Env:USE_API_SCHEMA_PATH') +
        '=' + [string]`$env:USE_API_SCHEMA_PATH +
        ',' + [string](Test-Path -LiteralPath 'Env:API_SCHEMA_PATH') +
        ',' + [string](Test-Path -LiteralPath 'Env:SCHEMA_PACKAGES') +
        '=' + [string]`$env:SCHEMA_PACKAGES |
        Add-Content -LiteralPath '$observationPath'
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

        # The verdict delegates classification and counting, so all three come across together.
        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackageCount",
                "Get-DmsSchemaEnvironmentVerdict"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:directSetupScript -FunctionName $functionName)))
        }

        function Get-ContainerEnvironmentFixture {
            param(
                [string] $UseApiSchemaPath = "true",
                [string] $ApiSchemaPath = "/app/ApiSchema",
                [int] $PackageCount = 4,
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
                        ConvertTo-Json -InputObject @(1..$PackageCount | ForEach-Object { @{ name = "Package$_" } }) -Compress -Depth 5
                    }
            }

            return $containerEnvironment
        }
    }

    It "passes when the container carries exactly the environment file's package surface" {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -PackageCount 4) `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

        $verdict.ShouldFail | Should -BeFalse
        $verdict.Reason | Should -BeNullOrEmpty
    }

    It "fails on the reported compose-fallback shape, naming AppSettings__UseApiSchemaPath" {
        # The exact container state DMS-1300 reported: false plus two blanks, which is what
        # local-dms.yml's ${VAR:-default} produces when the process carries blank schema variables.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -UseApiSchemaPath "false" -ApiSchemaPath "" -RawSchemaPackages "") `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

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
    ) {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

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
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "AppSettings__ApiSchemaPath is $([regex]::Escape($ExpectedToken))"
    }

    It "fails when the container's package count differs from the provisioned surface" {
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -PackageCount 2) `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "received 2 ApiSchema package"
        $verdict.Reason | Should -Match "provisioned for the environment file's 4"
    }

    It "fails without throwing when the container's SCHEMA_PACKAGES is <Label>" -ForEach @(
        @{ Label = "absent"; Container = @{ OmitSchemaPackages = $true } }
        @{ Label = "blank"; Container = @{ RawSchemaPackages = "" } }
        @{ Label = "whitespace"; Container = @{ RawSchemaPackages = "   " } }
        @{ Label = "not JSON at all"; Container = @{ RawSchemaPackages = "not-json" } }
        @{ Label = "a truncated array"; Container = @{ RawSchemaPackages = '[{"name":"Package1"}' } }
        @{ Label = "a JSON object rather than an array"; Container = @{ RawSchemaPackages = '{"name":"Package1"}' } }
    ) {
        $verdict = $null
        { $script:verdict = Get-DmsSchemaEnvironmentVerdict `
                -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
                -ExpectedPackageCount 4 `
                -EnvironmentFileUsesApiSchemaPath $true } | Should -Not -Throw

        $script:verdict.ShouldFail | Should -BeTrue
        $script:verdict.Reason | Should -Match "SCHEMA_PACKAGES is absent, blank, or not a JSON array"
    }

    It "reports a package-bearing environment file that does not enable the ApiSchema path as the inconsistency" {
        # Not a skip. The provisioner stamps the file's packages regardless of USE_API_SCHEMA_PATH, so
        # such a file guarantees the mismatch, and the remediation belongs on the file, not the shell.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -UseApiSchemaPath "false" -ApiSchemaPath "" -RawSchemaPackages "") `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $false

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "the environment file declares 4 ApiSchema package\(s\) but does not set USE_API_SCHEMA_PATH=true"
        $verdict.Remediation | Should -Match "Set USE_API_SCHEMA_PATH=true in the environment file"
    }

    It "refuses a package count below one, because the file-only reader cannot produce one" {
        # There is no "no packages declared" branch by design: an absent, malformed, or empty
        # SCHEMA_PACKAGES already fails the provision phase, so it can never reach the gate and be
        # classified as acceptable. The contract is encoded as parameter validation.
        { Get-DmsSchemaEnvironmentVerdict `
                -ContainerEnvironment (Get-ContainerEnvironmentFixture) `
                -ExpectedPackageCount 0 `
                -EnvironmentFileUsesApiSchemaPath $true } | Should -Throw
    }

    It "never echoes the container's raw SCHEMA_PACKAGES value into the failure text" {
        # The message vocabulary is fixed and derived, so container-supplied text cannot forge log
        # lines or bloat the failure with a package blob.
        $rawSchemaPackages = "[{`"name`":`"SENTINEL-DO-NOT-ECHO`"}]`nforged-log-line"
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -RawSchemaPackages $rawSchemaPackages) `
            -ExpectedPackageCount 4 `
            -EnvironmentFileUsesApiSchemaPath $true

        $verdict.ShouldFail | Should -BeTrue
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "SENTINEL-DO-NOT-ECHO"
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "forged-log-line"
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "`n"
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

    It "fails closed when the container cannot be inspected" {
        $inspectFunction = [regex]::Match(
            $script:directSetupSource,
            '(?ms)^function Get-DmsContainerEnvironment \{.*?^\}'
        ).Value

        $inspectFunction | Should -Match 'if \(\$LASTEXITCODE -ne 0\) \{'
        $inspectFunction | Should -Match "Unable to inspect Docker container"
    }

    It "throws rather than warning when the verdict fails" {
        $script:directSetupSource | Should -Match 'throw "DMS E2E setup mismatch: '
        $script:directSetupSource | Should -Not -Match 'Write-Warning[^\r\n]*setup mismatch'
    }
}
