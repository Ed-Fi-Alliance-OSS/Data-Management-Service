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
# DMS-1300 adds the same treatment for Invoke-WithDmsEnvironmentFileSchemaAuthority (both E2E setup
# wrappers), which must make the selected environment file the sole authority for the schema package
# surface of the Docker phases, and must round-trip the caller's exact prior environment.

param()

# Defined once here, in the file's root BeforeAll, so every Describe below extracts functions the same
# way. Seven identical copies used to live in seven per-Describe BeforeAll blocks, which is exactly the
# kind of duplication that drifts. A plain top-level function definition would not work: Pester runs the
# file body during discovery only, so the run phase would not see it.
BeforeAll {
    function Get-ScriptFunctionText {
        <#
        .SYNOPSIS
        Returns the source text of one function definition from a script or module, so a test can
        dot-source just that function without executing the file's top-level body.
        #>
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $FunctionName
        )

        $parseErrors = $null
        $tokens = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

        if ($parseErrors.Count -gt 0) {
            throw "'$ScriptPath' has $($parseErrors.Count) parse error(s)."
        }

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

    function Get-ScriptCommandInvocation {
        <#
        .SYNOPSIS
        Returns every invocation of one command name in a script or module, as CommandAst nodes, so an
        assertion about WHERE a command is called can read the extents production actually has.
        .DESCRIPTION
        Over the AST rather than the text, which matters in this file specifically: both setup wrappers
        deliberately NAME commands they must not call, and a text search cannot tell a prohibition from
        a call.
        #>
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $CommandName
        )

        $parseErrors = $null
        $tokens = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

        if ($parseErrors.Count -gt 0) {
            throw "'$ScriptPath' has $($parseErrors.Count) parse error(s), so '$CommandName' invocations cannot be located."
        }

        return @($ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq $CommandName
            },
            $true
        ))
    }

    function Get-SchemaGuardActionExtent {
        <#
        .SYNOPSIS
        Returns the extent of the -Action script block of EVERY
        Invoke-WithDmsEnvironmentFileSchemaAuthority invocation in a setup wrapper, in source order, so
        a containment test can ask whether something is inside ANY guard.
        .DESCRIPTION
        One or more, deliberately not exactly one. Each phase invocation is guarded separately: the
        guard restores the caller's prior environment when its action returns, so a single call around
        a whole phase sequence removes the three schema names only once, before the first phase, and a
        phase that re-creates one of them leaves it set for every later phase in that sequence.

        Shared by the phase detector and the verifier-placement check below, so "which blocks count as
        the guard" is decided in one place rather than in two that drift apart.
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
                $node.GetCommandName() -eq "Invoke-WithDmsEnvironmentFileSchemaAuthority"
            },
            $true
        ))

        if ($guardCalls.Count -lt 1) {
            throw "Expected at least one Invoke-WithDmsEnvironmentFileSchemaAuthority invocation in '$ScriptPath'; found none."
        }

        return @(
            foreach ($guardCall in $guardCalls) {
                # Bound to the ARGUMENT of -Action, not to "the one script block anywhere under the
                # guard call". Counting descendants made any legitimate nested script block - a
                # ForEach-Object over the route-context databases, a Where-Object filter - break the
                # wiring test with a detector error rather than a finding. Both spellings are accepted:
                # '-Action { }', where the block is the following element, and '-Action:{ }', where the
                # parser attaches it to the parameter itself.
                $guardElements = @($guardCall.CommandElements)
                $actionArgument = $null
                for ($elementIndex = 0; $elementIndex -lt $guardElements.Count; $elementIndex++) {
                    $element = $guardElements[$elementIndex]
                    if ($element -isnot [System.Management.Automation.Language.CommandParameterAst] -or
                        $element.ParameterName -ne "Action") {
                        continue
                    }

                    $actionArgument = if ($null -ne $element.Argument) {
                        $element.Argument
                    }
                    elseif ($elementIndex + 1 -lt $guardElements.Count) {
                        $guardElements[$elementIndex + 1]
                    }

                    break
                }

                if ($actionArgument -isnot [System.Management.Automation.Language.ScriptBlockExpressionAst]) {
                    throw "The guard invocation at line $($guardCall.Extent.StartLineNumber) in '$ScriptPath' does not pass a script block to -Action."
                }

                $actionArgument.Extent
            }
        )
    }
}

Describe "Invoke-WithE2ETestProcessContext restores prior environment state exactly (DMS-1284)" {
    BeforeAll {
        $buildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $buildScript -FunctionName "Invoke-WithE2ETestProcessContext")))

        $script:mutatedVariables = @(
            "AppSettings__DataStoreDatabaseName"
            "AppSettings__DatabaseEngine"
            "AppSettings__DataStoreAdminConnectionString"
            "AppSettings__DataStoreConnectionString"
            "AppSettings__DataStoreSnapshotConnectionString"
            "DMS_E2E_ENVIRONMENT_FILE"
            "NODE_OPTIONS"
        )

        $script:testSettings = [pscustomobject]@{
            EnvironmentFile                   = "/repo/eng/docker-compose/.derived/.env.e2e.document-cache.e2e.mssql"
            DataStoreDatabaseName             = "edfi_datamanagementservice_e2e"
            DatabaseEngine                    = "mssql"
            DataStoreAdminConnectionString    = "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            DataStoreConnectionString         = "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=secret;TrustServerCertificate=true;"
            DataStoreSnapshotConnectionString = "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e_snapshot;User Id=sa;Password=secret;TrustServerCertificate=true;"
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

    It "sets the engine and connection strings for the action, then restores them" {
        Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreAdminConnectionString -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreConnectionString -ErrorAction SilentlyContinue
        Remove-Item Env:AppSettings__DataStoreSnapshotConnectionString -ErrorAction SilentlyContinue
        Remove-Item Env:DMS_E2E_ENVIRONMENT_FILE -ErrorAction SilentlyContinue

        $observed = $null
        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action {
                $script:observed = [pscustomobject]@{
                    Engine          = $env:AppSettings__DatabaseEngine
                    Admin           = $env:AppSettings__DataStoreAdminConnectionString
                    Registration    = $env:AppSettings__DataStoreConnectionString
                    Snapshot        = $env:AppSettings__DataStoreSnapshotConnectionString
                    EnvironmentFile = $env:DMS_E2E_ENVIRONMENT_FILE
                }
                throw "boom"
            } } | Should -Throw

        $script:observed.Engine | Should -Be "mssql"
        $script:observed.Admin | Should -Be $script:testSettings.DataStoreAdminConnectionString
        $script:observed.Registration | Should -Be $script:testSettings.DataStoreConnectionString
        $script:observed.Snapshot | Should -Be $script:testSettings.DataStoreSnapshotConnectionString
        $script:observed.EnvironmentFile | Should -Be $script:testSettings.EnvironmentFile

        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreAdminConnectionString) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreConnectionString) | Should -BeFalse
        (Test-Path Env:AppSettings__DataStoreSnapshotConnectionString) | Should -BeFalse
        (Test-Path Env:DMS_E2E_ENVIRONMENT_FILE) | Should -BeFalse
    }

    It "restores every mutated variable from a mix of absent, empty, whitespace, and valued prior states" {
        Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        $env:AppSettings__DatabaseEngine = ""
        $env:AppSettings__DataStoreAdminConnectionString = "   "
        $env:AppSettings__DataStoreConnectionString = "prior-registration"
        $env:AppSettings__DataStoreSnapshotConnectionString = "prior-snapshot"
        $env:DMS_E2E_ENVIRONMENT_FILE = "prior-environment-file"
        $env:NODE_OPTIONS = "--max-old-space-size=4096"

        { Invoke-WithE2ETestProcessContext -E2ETestSettings $script:testSettings -Action { throw "boom" } } |
            Should -Throw

        (Test-Path Env:AppSettings__DataStoreDatabaseName) | Should -BeFalse
        (Test-Path Env:AppSettings__DatabaseEngine) | Should -BeTrue
        $env:AppSettings__DatabaseEngine | Should -Be ""
        $env:AppSettings__DataStoreAdminConnectionString | Should -Be "   "
        $env:AppSettings__DataStoreConnectionString | Should -Be "prior-registration"
        $env:AppSettings__DataStoreSnapshotConnectionString | Should -Be "prior-snapshot"
        $env:DMS_E2E_ENVIRONMENT_FILE | Should -Be "prior-environment-file"
        $env:NODE_OPTIONS | Should -Be "--max-old-space-size=4096"
    }
}

Describe "Get-DirectSetupTeardownCommand carries the engine and resolved environment file (DMS-1284)" {
    BeforeAll {
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

Describe "Invoke-WithDmsEnvironmentFileSchemaAuthority makes the environment file authoritative for the E2E setup phases (DMS-1300)" {
    # One guard, in the module both E2E setup wrappers import, so it is executed once here rather than
    # once per wrapper copy. That the wrappers reach THIS definition - importing the module, defining no
    # guard of their own, and running every Docker phase inside it - is asserted by the wiring blocks
    # below; this block is about what the guard does when it runs: an assignment-based clear, a restore
    # that collapses present-but-empty to absent, or a finally that does not run on exit each fail here.
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
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))
        $script:guardFunctionText = Get-ScriptFunctionText -ScriptPath $script:schemaEnvironmentModule -FunctionName "Invoke-WithDmsEnvironmentFileSchemaAuthority"
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
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
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

        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action { }

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

        { Invoke-WithDmsEnvironmentFileSchemaAuthority -Action { throw "phase failed" } } |
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

        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action { }

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

        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action { }

        (Test-Path -LiteralPath "Env:SCHEMA_PACKAGES") | Should -BeTrue
        [System.Environment]::GetEnvironmentVariable("SCHEMA_PACKAGES") | Should -Be ""
    }

    It "guards a call that omits -Enabled and passes through one made with -Enabled:`$false" {
        # The gate build-dms.ps1 needs - several of its call sites gate the removal on a caller switch
        # or on the E2E settings object, and their phases must run WITHOUT it - is a PARAMETER of this
        # guard rather than a wrapper around it. build-dms.ps1 used to carry that wrapper, and its name
        # was one more name in the scope chain of the setup wrappers it invokes in-process.
        #
        # Both directions matter and both are executed, not pattern-matched. A default that came out
        # false would leave every unparameterized caller - which is both E2E setup wrappers - running
        # its Compose phases with the ambient variables present, and an inverted test would do the same
        # to the gated build-script call sites; either reads correctly in source.
        foreach ($name in $script:schemaVariables) {
            [System.Environment]::SetEnvironmentVariable($name, "ambient-$name")
        }

        $script:observedWhenDefault = $null
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            $script:observedWhenDefault = Get-SchemaVariableState
        }

        $script:observedWhenDisabled = $null
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$false -Action {
            $script:observedWhenDisabled = Get-SchemaVariableState
        }

        # A disabled call still RUNS the action: it is a pass-through, not a skip.
        $script:observedWhenDisabled | Should -Not -BeNullOrEmpty -Because "-Enabled:`$false must still invoke the action"

        foreach ($name in $script:schemaVariables) {
            $script:observedWhenDefault[$name].Exists |
                Should -BeFalse -Because "a call that omits -Enabled must guard, which removes $name"
            $script:observedWhenDisabled[$name].Value |
                Should -Be "ambient-$name" -Because "-Enabled:`$false must be a pass-through, so it must not remove $name"
            [System.Environment]::GetEnvironmentVariable($name) |
                Should -Be "ambient-$name" -Because "the caller's $name must be restored either way"
        }
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
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
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
            invocation is lexically inside ANY Invoke-WithDmsEnvironmentFileSchemaAuthority -Action
            block, and which one. Uses the AST rather than a text pattern, so the assertion is about
            the structure production actually has, and is not defeated by reformatting or
            reindentation.
            .DESCRIPTION
            One or more guards, not exactly one: each phase is guarded on its own, so the question a
            phase record answers is "is this phase inside SOME guard", and GuardIndex identifies which
            - a phase sharing a guard block with an earlier phase runs after that phase has already had
            the chance to re-create one of the three names in this process.
            #>
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,

                # For a script whose phases all live inside functions, which is build-dms.ps1's shape.
                # Off by default, because for the wrappers a command inside a function belongs to a
                # helper rather than to the phase sequence.
                [switch] $IncludeFunctionBody
            )

            $actionExtent = @(Get-SchemaGuardActionExtent -ScriptPath $ScriptPath)

            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)

            if ($parseErrors.Count -gt 0) {
                throw "'$ScriptPath' has $($parseErrors.Count) parse error(s)."
            }

            return @($ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                },
                $true
            ) | Where-Object {
                # Only the wrapper's own body dispatches phases. Commands inside a function definition
                # belong to a helper rather than to the phase sequence - including the guard's own
                # '& $Action' (in the shared module for the wrappers, inline in the fixture below),
                # which is variable-dispatched and by construction sits outside the -Action block it
                # invokes, so including it would report the guard as a phase escaping itself.
                #
                # -IncludeFunctionBody lifts that exclusion for a script that dispatches its phases from
                # inside functions, where this filter would otherwise remove every phase AND every guard
                # call and return an empty set. A caller using it has to scope the result by name, since a
                # script's other functions invoke commands that are not phases at all.
                if ($IncludeFunctionBody) {
                    return $true
                }

                $ancestor = $_.Parent
                $insideFunction = $false
                while ($null -ne $ancestor) {
                    if ($ancestor -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
                        $insideFunction = $true
                        break
                    }
                    $ancestor = $ancestor.Parent
                }

                -not $insideFunction
            } | ForEach-Object {
                # Bareword script invocations report their own name. A phase dispatched through a
                # VARIABLE reports none, and every such call in a wrapper body is a phase invocation,
                # so all of them are collected rather than only the ones whose variable happens to be
                # named '...Script'. Narrowing on the name silently dropped forms like '& $phase' from
                # the result set, and the sibling "nothing outside the guard" assertion then passed
                # vacuously for exactly the call it was meant to cover.
                $name = $_.GetCommandName()
                $isVariableDispatched = $_.CommandElements[0] -is [System.Management.Automation.Language.VariableExpressionAst]
                if ($null -eq $name -and $isVariableDispatched) {
                    $name = '$' + $_.CommandElements[0].VariablePath.UserPath
                }

                # A bareword 'docker' can orchestrate Compose directly - 'docker compose --env-file ...
                # up ...' - and such a call is a phase for this detector's purposes: it resolves the
                # three schema names ambient-first exactly as a phase script's own compose call does, so
                # one added outside the guard is the same escape. Matching only '*.ps1' barewords made
                # every such call invisible, and the sibling "nothing outside the guard" assertion would
                # have passed while the orchestration ran unguarded.
                #
                # Classified by what the command DOES, not by being named 'docker': both wrappers run
                # 'docker version' as a pre-flight daemon check that reads none of the three names and
                # legitimately sits before the guard, so it must stay excluded. The backtick in the
                # character class keeps a line-continued 'docker `<newline> compose' matching.
                $isComposeOrchestration = $false
                if ($name -eq "docker") {
                    $commandText = $_.Extent.Text
                    $isComposeOrchestration =
                        $commandText -match 'docker[\s`]+compose' -or $commandText -match '--env-file'
                }

                if ($name -and ($isVariableDispatched -or $name -like "*.ps1" -or $isComposeOrchestration)) {
                    # The index of the FIRST guard block containing this invocation, or $null when no
                    # guard does. The guards are siblings in these wrappers, so at most one contains a
                    # given phase.
                    $phaseExtent = $_.Extent
                    $guardIndex = $null
                    for ($extentIndex = 0; $extentIndex -lt $actionExtent.Count; $extentIndex++) {
                        if ($phaseExtent.StartOffset -ge $actionExtent[$extentIndex].StartOffset -and
                            $phaseExtent.EndOffset -le $actionExtent[$extentIndex].EndOffset) {
                            $guardIndex = $extentIndex
                            break
                        }
                    }

                    [pscustomobject]@{
                        Name        = $name
                        Line        = $phaseExtent.StartLineNumber
                        # Carried so an assertion can order the phases against another command's
                        # position, which line numbers alone cannot do for two calls on one line.
                        EndOffset   = $phaseExtent.EndOffset
                        InsideGuard = ($null -ne $guardIndex)
                        # Which guard block, so an assertion can tell "every phase is guarded" from
                        # "every phase is guarded SEPARATELY".
                        GuardIndex  = $guardIndex
                    }
                }
            })
        }

        $script:wrapperScripts = [ordered]@{
            "DataManagementService E2E" = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
            "InstanceManagement E2E"    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        }

        # The real guard, for the replay below: the leak regression has to run the same
        # removal-and-restore production runs, not a stand-in for it.
        . ([scriptblock]::Create((Get-ScriptFunctionText `
                        -ScriptPath ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))) `
                        -FunctionName "Invoke-WithDmsEnvironmentFileSchemaAuthority")))
    }

    It "runs the direct DMS E2E phase sequence inside the guard, in order" {
        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $script:wrapperScripts["DataManagementService E2E"])

        @($invocations | ForEach-Object { $_.Name }) | Should -Be @(
            "./start-local-dms.ps1",
            "./configure-local-data-store.ps1",
            "./provision-e2e-database.ps1",
            # The snapshot derivative's database is provisioned in its own guarded phase, right after
            # the primary and still before DMS starts.
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

    It "removes SCHEMA_PACKAGES again for every later phase in the <Name> wrapper, so a phase that re-creates it cannot leak into the next" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        # THE GUARD-SHAPE REGRESSION, replayed rather than pattern-matched. Inside the guard is not the
        # whole requirement; inside a guard of its OWN is. The guard restores the caller's prior
        # environment when its action returns, so one call wrapped around the whole phase sequence
        # removes the three schema names exactly once, before phase 1. Phase scripts run in this same
        # PowerShell process and can re-create one of them - start-local-dms.ps1 sets them in-process on
        # purpose in bootstrap mode - after which every later phase in that one guarded sequence sees
        # the re-created value and Compose resolves it ambient-first again.
        #
        # The grouping comes from production (which guard block each phase invocation actually sits in)
        # and the guard is the real one, so this fails on a wrapper that guards its sequence once and
        # passes on one that guards each phase. The phases themselves are not executed: what is being
        # replayed is the removal boundary around them.
        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $script:wrapperScripts[$Name] | Sort-Object EndOffset)

        $invocations.Count |
            Should -BeGreaterThan 1 -Because "the replay needs a later phase to observe what an earlier one left behind"
        @($invocations | Where-Object { -not $_.InsideGuard }) |
            Should -BeNullOrEmpty -Because "an unguarded phase has no removal boundary to replay"

        # Script-scoped: '& $Action' runs the block in a child scope, so a plain assignment inside it
        # would be discarded when the guard returns.
        $script:replayedPhaseCount = 0
        $script:phaseObservingLeakedPackages = @()

        foreach ($guardIndex in @($invocations | ForEach-Object { $_.GuardIndex } | Select-Object -Unique)) {
            $guardedPhases = @($invocations | Where-Object { $_.GuardIndex -eq $guardIndex })

            Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
                foreach ($phase in $guardedPhases) {
                    $script:replayedPhaseCount++

                    if ($script:replayedPhaseCount -eq 1) {
                        # Phase 1 re-creates SCHEMA_PACKAGES in this process, as a phase script that
                        # activates bootstrap mode does.
                        [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", '[{"name":"ReCreatedByPhaseOne"}]')
                        continue
                    }

                    if (Test-Path -LiteralPath "Env:SCHEMA_PACKAGES") {
                        $script:phaseObservingLeakedPackages += "$($phase.Name) (line $($phase.Line))"
                    }
                }
            }
        }

        # The guard's own restore returns SCHEMA_PACKAGES to whatever this shell had, so the replay
        # leaves no state behind whichever shape the wrapper has.
        $script:phaseObservingLeakedPackages |
            Should -BeNullOrEmpty -Because "each phase must be guarded on its own, so SCHEMA_PACKAGES re-created by an earlier phase is removed again before the next phase runs"
    }

    It "verifies the started container after the LAST guarded phase in the <Name> wrapper" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        # Inside the guard is not the requirement; inside the guard AND after the DMS-only start is.
        # The verification inspects a RUNNING container, so a call moved ahead of
        # './start-local-dms.ps1 -DmsOnly' either inspects whatever the previous run left behind or
        # fails to find a container at all - and neither outcome says anything about the stack the
        # scenarios are about to run against. The sibling assertions elsewhere in this file only pin
        # that the call sits inside the -Action block, which a verifier hoisted to the top of the
        # guarded sequence still satisfies, so the ordering is pinned here.
        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $script:wrapperScripts[$Name])
        $lastGuardedPhase = @($invocations | Where-Object { $_.InsideGuard } | Sort-Object EndOffset)[-1]

        $verifierCall = @(Get-ScriptCommandInvocation `
                -ScriptPath $script:wrapperScripts[$Name] `
                -CommandName "Assert-DmsContainerSchemaEnvironment")

        $verifierCall.Count | Should -Be 1 -Because "the wrapper verifies the started container exactly once"
        $lastGuardedPhase.Name | Should -Be "./start-local-dms.ps1" -Because "the last guarded phase is the -DmsOnly start whose container the verification reads"
        $verifierCall[0].Extent.StartOffset |
            Should -BeGreaterThan $lastGuardedPhase.EndOffset -Because "verifying before the last guarded phase ($($lastGuardedPhase.Name), line $($lastGuardedPhase.Line)) would read a container this run has not started yet"
    }

    It "detects a variable-dispatched phase whose variable is not named '...Script'" {
        # The detector's own regression guard. It previously matched only '*.ps1' barewords and
        # variables named '$*Script', so a future phase dispatched as '& $provisionPath' or '& $phase'
        # was dropped from the result set entirely - and the sibling "nothing outside the guard"
        # assertion then passed vacuously for precisely the call it exists to cover. The fixture puts
        # such a call OUTSIDE the guard, so a detector that misses it reports no escape at all.
        $fixturePath = Join-Path $TestDrive "variable-dispatched-phase.ps1"
        Set-Content -LiteralPath $fixturePath -Encoding utf8 -Value @'
function Invoke-WithDmsEnvironmentFileSchemaAuthority {
    param([scriptblock] $Action)
    & $Action
}

$guardedPhase = "./start-local-dms.ps1"
$escapedPhase = "./provision-e2e-database.ps1"

Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
    & $guardedPhase -InfraOnly
}

& $escapedPhase -DatabaseName "edfi_e2e"
'@

        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $fixturePath)

        # '& $Action' inside the guard function is excluded: it is the guard's own dispatch, not a phase.
        @($invocations | ForEach-Object { $_.Name }) | Should -Be @('$guardedPhase', '$escapedPhase')
        @($invocations | Where-Object { -not $_.InsideGuard } | ForEach-Object { $_.Name }) |
            Should -Be @('$escapedPhase')
    }

    It "detects a bareword 'docker compose --env-file' call outside the guard while ignoring 'docker version'" {
        # The detector's second regression guard. A phase does not have to be a .ps1 script: a compose
        # call written inline resolves USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES
        # ambient-first exactly as a phase script's does, so one added outside the guard is the same
        # escape - and a detector that only recognized '*.ps1' barewords reported no escape at all.
        # 'docker version' is in the fixture too, unguarded, because both wrappers really do run it as a
        # pre-flight daemon check before the guard: it reads none of the three names, so classifying it
        # as an escape would fail the wiring tests on correct wrappers.
        $fixturePath = Join-Path $TestDrive "bareword-compose-phase.ps1"
        Set-Content -LiteralPath $fixturePath -Encoding utf8 -Value @'
function Invoke-WithDmsEnvironmentFileSchemaAuthority {
    param([scriptblock] $Action)
    & $Action
}

docker version 2>&1

Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
    ./start-local-dms.ps1 -InfraOnly
}

docker compose -p dms-local --env-file ./.env.e2e -f local-dms.yml up -d dms
'@

        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $fixturePath)

        @($invocations | ForEach-Object { $_.Name }) | Should -Be @("./start-local-dms.ps1", "docker")
        @($invocations | Where-Object { -not $_.InsideGuard } | ForEach-Object { $_.Name }) |
            Should -Be @("docker") -Because "an unguarded compose call resolves the schema variables from the ambient process again"
    }

    It "reads the -Action argument rather than counting script blocks, so a nested block is not a detector error" {
        # The detector used to require exactly ONE script block anywhere under the guard call, which made
        # a legitimate nested block inside the guarded action - a ForEach-Object over the route-context
        # databases, say - throw out of the detector instead of producing a finding.
        $fixturePath = Join-Path $TestDrive "nested-script-block-phase.ps1"
        Set-Content -LiteralPath $fixturePath -Encoding utf8 -Value @'
function Invoke-WithDmsEnvironmentFileSchemaAuthority {
    param([scriptblock] $Action)
    & $Action
}

Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
    @("edfi_a", "edfi_b") | ForEach-Object {
        ./provision-e2e-database.ps1 -DatabaseName $_
    }
}
'@

        $invocations = @(Get-GuardedPhaseInvocation -ScriptPath $fixturePath)

        @($invocations | ForEach-Object { $_.Name }) | Should -Be @("./provision-e2e-database.ps1")
        @($invocations | Where-Object { -not $_.InsideGuard }) | Should -BeNullOrEmpty
    }

    It "runs every DMS start and bootstrap phase in build-dms.ps1 inside the schema guard" {
        # build-dms.ps1 was structurally EXEMPT from this invariant while being the caller CI actually
        # invokes for both E2ETest and InstanceE2ETest: every one of its guard call sites and guarded
        # phases sits inside a function, and the wrapper-shaped detector excludes function bodies, so
        # pointing it at this script returned an empty set and passed vacuously. -IncludeFunctionBody is
        # what makes the same question answerable here.
        #
        # Scoped to the phases that CREATE the DMS container from an --env-file: the two start scripts,
        # the two bootstrap scripts, and the image-mode-selected '& $startupScriptPath'. Those are the
        # invocations whose Compose resolution the guard governs, and the scoping is what keeps the
        # assertion about them rather than about every command the script's other functions run - the
        # ad-hoc 'docker run' of DockerRun, or the data-store and provision phases, none of which create
        # a DMS container from these three names.
        #
        # '& $instanceSetupScript' is allowlisted for a different reason: it hands off to the Instance
        # Management setup wrapper, which guards each of its own phases - asserted for that wrapper
        # elsewhere in this block - so it is guarded internally rather than at the hand-off.
        $buildScriptPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        $dmsStartPhaseName = @(
            "./start-local-dms.ps1"
            "./start-published-dms.ps1"
            "./bootstrap-local-dms.ps1"
            "./bootstrap-published-dms.ps1"
            '$startupScriptPath'
        )

        $invocations = @(
            Get-GuardedPhaseInvocation -ScriptPath $buildScriptPath -IncludeFunctionBody |
                Where-Object { $dmsStartPhaseName -contains $_.Name }
        )

        $invocations.Count |
            Should -BeGreaterThan 0 -Because "build-dms.ps1 starts the DMS container for both E2E lanes, so finding none of those phases would mean this assertion measures nothing"
        @($invocations | Where-Object { -not $_.InsideGuard } | ForEach-Object { "$($_.Name) (line $($_.Line))" }) |
            Should -BeNullOrEmpty -Because "a DMS start phase outside the guard resolves the schema variables from the ambient process again, and build-dms.ps1 is the caller CI runs"
    }

    It "defines no function in build-dms.ps1 that shadows one of the module's exports" {
        # THE REGRESSION FOR THE IN-PROCESS BINDING DEFECT. build-dms.ps1 InstanceE2ETest invokes the
        # Instance Management wrapper with '& $instanceSetupScript', so the wrapper's scope is a CHILD of
        # build-dms.ps1's script scope. Command lookup walks that scope chain before it reaches the
        # session state this module's exports live in, so any name build-dms.ps1 also defines binds the
        # BUILD SCRIPT's function rather than the export the wrapper meant to call.
        #
        # build-dms.ps1 no longer defines a same-purpose helper at all - it imports this module and
        # calls the guard directly, passing -Enabled where its call sites gate the removal - so there is
        # nothing to collide today. This holds that property: a helper reintroduced here under an
        # exported name silently rebinds the wrappers' calls.
        #
        # Asserted over the whole export surface rather than the one guard: every name this module
        # exports is reachable from a wrapper the build script invokes in-process, so every one of them
        # is exposed to the same shadowing.
        $buildScriptPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        $modulePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))

        $definedFunctionName = @(
            foreach ($path in @($buildScriptPath, $modulePath)) {
                $parseErrors = $null
                $tokens = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)

                if ($parseErrors.Count -gt 0) {
                    throw "'$path' has $($parseErrors.Count) parse error(s)."
                }

                , @($ast.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                    },
                    $true
                ) | ForEach-Object { $_.Name })
            }
        )

        # Case-insensitive, which '-contains' is: PowerShell command resolution is too, so a module
        # export differing from a build-script function only in casing shadows exactly the same way.
        $collidingName = @(
            $definedFunctionName[1] |
                Where-Object { $definedFunctionName[0] -contains $_ }
        )

        $collidingName | Should -BeNullOrEmpty -Because "build-dms.ps1 invokes a setup wrapper in-process, so a name it also defines shadows the module export the wrapper means to call"
    }

    It "reaches the guard by its exported name in the <Name> wrapper" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        # Over the AST, not the text: both wrappers name commands in comments in order to explain what
        # they must not do, and a text search cannot tell a prohibition from a call.
        $parseErrors = $null
        $tokens = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:wrapperScripts[$Name], [ref]$tokens, [ref]$parseErrors)

        $invokedName = @($ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            },
            $true
        ) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })

        $invokedName | Should -Contain "Invoke-WithDmsEnvironmentFileSchemaAuthority"
    }
}

Describe "Get-DmsSchemaEnvironmentVerdict fails setup when the DMS container disagrees with the provisioned package surface (DMS-1300)" {
    # The provisioner reads SCHEMA_PACKAGES from the environment file only, so the E2E database is
    # always stamped for the file's package surface; DMS receives its settings through Compose, which
    # resolves them ambient-first. When the two disagree the stack comes up healthy and then fails
    # every data-plane request with an EffectiveSchemaHash mismatch, so the verdict is what turns that
    # into a setup-time failure. Pure function, so no Docker is involved.

    BeforeAll {
        # The verifier lives in the module both E2E setup wrappers import, not in either wrapper.
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))

        # The verdict delegates classification, parsing, and identity normalization, so all four come
        # across together.
        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackage",
                "Get-DmsSchemaPackageIdentity",
                "Get-DmsSchemaEnvironmentVerdict"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:schemaEnvironmentModule -FunctionName $functionName)))
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
        # The remediation names actions that reach the causes this verdict can actually have. It used to
        # ask for a re-run "from a shell that does not set USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and
        # SCHEMA_PACKAGES", which cannot apply: the gate runs inside the guard that has already removed
        # those three names from the process, so a developer following that advice changed nothing. A
        # stale container and a wrong environment file are reachable, and both of these address them.
        $verdict.Remediation | Should -Match "teardown-local-dms\.ps1"
        $verdict.Remediation | Should -Match "re-run setup"
        $verdict.Remediation | Should -Match "-EnvironmentFile"
        $verdict.Remediation | Should -Not -Match "Re-run setup from a shell"
        # No path to the teardown wrapper: the setup flows run their phases from eng/docker-compose,
        # where no relative path to either suite's copy resolves.
        $verdict.Remediation | Should -Not -Match "\./teardown-local-dms\.ps1"
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
        # Actionable: the FILE's expected identity at the mismatching index is named, so the failure says
        # which package the E2E database was provisioned for. Every entry differs in this case, so the
        # first sorted position is the one reported.
        $verdict.Reason | Should -Match ([regex]::Escape($script:fixturePackageIdentity[0]))
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

    It "names the environment file's expected package at the mismatching sorted position" {
        # The point of reporting the expected identity: with one entry diverging, the message has to
        # identify THAT package rather than the first one, or a developer is sent to diff the wrong
        # entry. Only Package3's version differs, and the identities sort by their JSON text - which
        # begins with the name - so the mismatch lands at sorted position 3.
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
        $verdict.Reason | Should -Match "The first difference is at sorted position 3 of 4"
        $verdict.Reason | Should -Match ([regex]::Escape($script:fixturePackageIdentity[2]))
        $verdict.Reason | Should -Match "EdFi\.ApiSchema\.Package3"
        # The expected identity, not the container's: the container is on 7.7.7 at that position, and the
        # environment file's 1.0.0 is what the E2E database was provisioned for.
        $verdict.Reason | Should -Match '"version":"1\.0\.0"'
        $verdict.Reason | Should -Not -Match "7\.7\.7"
    }

    It "never echoes the container's package values into the surface-mismatch failure text" {
        # The container-supplied half stays out of the message, so it cannot forge log lines or leak a
        # feed URL into the console. Only the environment FILE's expected identity is named, and this
        # fixture keeps the sentinel on the container side to pin that split: the file's own packages
        # carry the default feed.
        $sentinelPackages = @(New-SchemaPackageFixture -Count 4 -FeedUrl "https://SENTINEL-FEED-DO-NOT-ECHO.example.net/index.json")

        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package $sentinelPackages) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "SENTINEL-FEED-DO-NOT-ECHO"
        # Not satisfiable by dropping the expected identity too: the actionable half must still be there.
        $verdict.Reason | Should -Match ([regex]::Escape($script:fixturePackageIdentity[0]))
    }

    It "keeps the surface-mismatch failure text single-line, so a package blob cannot forge log lines" {
        # The expected identity comes from the environment file rather than the container, but it is
        # still interpolated into the message, so it is pinned to the same shape the rest of this
        # vocabulary has: compact JSON on one line, which is what Get-DmsSchemaPackageIdentity emits.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -Package (New-SchemaPackageFixture -Count 4 -Version "9.9.9")) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        "$($verdict.Reason) $($verdict.Remediation)" | Should -Not -Match "`n"
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

    It "reports a package-bearing environment file with no API_SCHEMA_PATH against the file, not the container" {
        # The symmetric file-side inconsistency to the case above. Without its own branch this reaches
        # the container's blank-path check, whose remediation asks for a teardown and a re-run - and
        # re-creating the container cannot fix a value the FILE never declared.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture -ApiSchemaPath "") `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath ""

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Reason | Should -Match "the environment file declares 4 ApiSchema package\(s\) but no API_SCHEMA_PATH"
        $verdict.Remediation | Should -Match "Set API_SCHEMA_PATH in the environment file"
        $verdict.Remediation | Should -Not -Match "teardown-local-dms"
    }

    It "answers every container-side branch with the teardown-and-re-run remediation and no stale shell advice" -ForEach @(
        @{ Label = "UseApiSchemaPath false"; Container = @{ UseApiSchemaPath = "false" } }
        @{ Label = "a blank ApiSchemaPath"; Container = @{ ApiSchemaPath = "" } }
        @{ Label = "a different ApiSchemaPath"; Container = @{ ApiSchemaPath = "/somewhere/else" } }
        @{ Label = "a non-array SCHEMA_PACKAGES"; Container = @{ RawSchemaPackages = "not-json" } }
        @{ Label = "a different package count"; Container = @{ PackageCount = 2 } }
        @{ Label = "the same count at a different version"; Container = @{ Package = @(1..4 | ForEach-Object {
                    [pscustomobject]@{ name = "EdFi.ApiSchema.Package$_"; version = "9.9.9"; feedUrl = "https://pkgs.example.org/v3/index.json" }
                }) }
        }
    ) {
        # Every branch the CONTAINER can fail on, held to the same remediation in one table rather than
        # one branch at a time: the advice that had to be replaced ("Re-run setup from a shell that does
        # not set USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES") reached all six, and it
        # named a cause the gate has already ruled out - it runs inside the guard that removed those
        # three names. A future edit that restores it in any one branch fails here.
        $verdict = Get-DmsSchemaEnvironmentVerdict `
            -ContainerEnvironment (Get-ContainerEnvironmentFixture @Container) `
            -ExpectedPackageIdentity $script:fixturePackageIdentity `
            -EnvironmentFileUsesApiSchemaPath $true `
            -EnvironmentFileApiSchemaPath $script:fixtureApiSchemaPath

        $verdict.ShouldFail | Should -BeTrue
        $verdict.Remediation | Should -Match "teardown-local-dms\.ps1"
        $verdict.Remediation | Should -Match "re-created from the selected environment file"
        $verdict.Remediation | Should -Match "select a different -EnvironmentFile"
        $verdict.Remediation | Should -Not -Match "Re-run setup from a shell"
        $verdict.Remediation | Should -Not -Match "\./teardown-local-dms\.ps1"
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
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))

        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackage",
                "Get-DmsSchemaPackageIdentity",
                "Get-DmsSchemaEnvironmentVerdict",
                "Get-DmsEnvironmentFileDeclaredValue",
                "Assert-DmsContainerSchemaEnvironment"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:schemaEnvironmentModule -FunctionName $functionName)))
        }

        # The real sequential env-file resolver, not a stub: the quoting, inline-comment, reference and
        # declaration-order cases below are only meaningful if the assertion reads the environment file
        # the way Docker Compose does. Production reaches this function through the same module import.
        Import-Module (Join-Path $PSScriptRoot "../database-safety.psm1") -Force

        # Referenced names are resolved ambient-first inside the sequential resolver, exactly as Compose
        # does, so the fixtures below would inherit a value from a dirty dev shell. Removed for the
        # duration of this block and restored afterward, which also mirrors the production precondition:
        # the setup guard has already removed the schema names before the gate runs.
        $script:neutralizedNames = @(
            "USE_API_SCHEMA_PATH", "API_SCHEMA_PATH", "SCHEMA_PACKAGES", "SCHEMA_ROOT", "ENABLE_SCHEMA_PATH"
        )
        $script:neutralizedPriorValues = @{}
        foreach ($name in $script:neutralizedNames) {
            $script:neutralizedPriorValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }

        # Only the two readers that would need Docker or a package declaration are stubbed. The
        # environment file itself is real: production resolves it sequentially from disk, so a stubbed
        # key/value map would reintroduce exactly the collapsed-map model this suite has to reject.
        # Each stub declares the parameters production binds by name, so a renamed argument at the call
        # site fails here rather than silently passing.
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

        $script:environmentFileFixtureCount = 0

        function New-EnvironmentFileFixture {
            <#
            .SYNOPSIS
            Writes $script:environmentFileLines to a real file and returns its path, so declaration
            ORDER is part of the fixture rather than being flattened into a hashtable.
            #>
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes a throwaway fixture under TestDrive; -WhatIf/-Confirm semantics add no value.')]
            param()

            $script:environmentFileFixtureCount++
            $path = Join-Path $TestDrive "gate-fixture-$($script:environmentFileFixtureCount).env"
            [System.IO.File]::WriteAllLines($path, [string[]]$script:environmentFileLines)
            return $path
        }
    }

    AfterAll {
        foreach ($name in $script:neutralizedNames) {
            if ($null -eq $script:neutralizedPriorValues[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $script:neutralizedPriorValues[$name])
            }
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
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "API_SCHEMA_PATH=/app/ApiSchema"
        )
        $script:stubContainerEnvironment = @{
            "AppSettings__UseApiSchemaPath" = "true"
            "AppSettings__ApiSchemaPath"    = "/app/ApiSchema"
            "SCHEMA_PACKAGES"               = ConvertTo-Json -InputObject $script:stubDeclaredPackages -Compress -Depth 5
        }
    }

    It "returns without throwing when the container agrees with the environment file" {
        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
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
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "carries the fixed docker inspect capture command, without echoing any container value" {
        # The reason names WHICH setting disagrees but deliberately never echoes the container's value,
        # and both remediation actions are things the developer has already done by the time they see this
        # - so without a capture command the failure names nothing they can escalate. Appended as a FIXED
        # string carrying only the container name the CALLER passed, which is why the sanitization
        # invariant is unchanged: the sentinel on the container side must still not appear.
        $script:stubContainerEnvironment["SCHEMA_PACKAGES"] = '[{"name":"SENTINEL-DO-NOT-ECHO"}]'

        $failure = { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -PassThru

        $failure.Exception.Message |
            Should -BeLike "*Capture the container's actual settings with: docker inspect ed-fi-api --format '{{json .Config.Env}}'"
        $failure.Exception.Message | Should -Not -BeLike "*SENTINEL-DO-NOT-ECHO*"
    }

    It "throws when the environment file declares packages without enabling the ApiSchema path" {
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=false"
            "API_SCHEMA_PATH=/app/ApiSchema"
        )

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "reports the file-side failure when the environment file declares packages and <Label> USE_API_SCHEMA_PATH" -ForEach @(
        @{ Label = "omits"; Lines = @("API_SCHEMA_PATH=/app/ApiSchema") }
        @{ Label = "blanks"; Lines = @("USE_API_SCHEMA_PATH=", "API_SCHEMA_PATH=/app/ApiSchema") }
    ) {
        # The file-only read's fallback, exercised rather than pattern-matched: an undeclared (or
        # blank) USE_API_SCHEMA_PATH must resolve to false, so the verdict is the FILE-side "declares
        # packages but does not set USE_API_SCHEMA_PATH=true" - the file is internally inconsistent and
        # re-creating the container cannot fix it. The container in this fixture agrees with everything
        # it can, so a fallback that defaulted the missing name to true would let this pass, and one
        # that reported it against the container would give the wrong remediation.
        $script:environmentFileLines = $Lines

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } |
            Should -Throw -ExpectedMessage "DMS E2E setup mismatch: the environment file declares 4 ApiSchema package(s) but does not set USE_API_SCHEMA_PATH=true,*Set USE_API_SCHEMA_PATH=true in the environment file*"
    }

    It "reports the file-side failure when the environment file declares packages and <Label> API_SCHEMA_PATH" -ForEach @(
        @{ Label = "omits"; Lines = @("USE_API_SCHEMA_PATH=true") }
        @{ Label = "blanks"; Lines = @("USE_API_SCHEMA_PATH=true", "API_SCHEMA_PATH=") }
        @{ Label = "whitespaces"; Lines = @("USE_API_SCHEMA_PATH=true", 'API_SCHEMA_PATH="   "') }
    ) {
        # The other file-only fallback. An undeclared, blank, or whitespace API_SCHEMA_PATH resolves to
        # the empty string, and the file-side branch must be reached BEFORE the container's own blank
        # path branch: the container's path is populated here, so a gate that only noticed a blank
        # CONTAINER path would pass this file, and one that answered a missing file value with the
        # container remediation would send a developer to tear the stack down over a value the file
        # never declared.
        $script:environmentFileLines = $Lines

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } |
            Should -Throw -ExpectedMessage "DMS E2E setup mismatch: the environment file declares 4 ApiSchema package(s) but no API_SCHEMA_PATH,*Set API_SCHEMA_PATH in the environment file*"
    }

    It "compares against the environment file's API_SCHEMA_PATH, not a hardcoded default" {
        # Both sides move together: a container matching a non-default environment-file path must pass.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "API_SCHEMA_PATH=/custom/ApiSchema"
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/custom/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
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
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "API_SCHEMA_PATH=$RawValue"
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/custom/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "still fails a genuinely different path when the declaration is quoted" {
        # Normalization must not turn the path comparison into a no-op.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            'API_SCHEMA_PATH="/custom/ApiSchema"'
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/somewhere/else"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It "accepts a Compose-legal <Label> USE_API_SCHEMA_PATH declaration" -ForEach @(
        @{ Label = "double-quoted"; RawValue = '"true"' }
        @{ Label = "single-quoted"; RawValue = "'true'" }
        @{ Label = "inline-commented"; RawValue = "true # file-based ApiSchema packages" }
    ) {
        # Same false failure on the other file-read expectation: raw quoted text is not equal to "true",
        # so the gate reported a correctly configured environment file as internally inconsistent.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=$RawValue"
            "API_SCHEMA_PATH=/app/ApiSchema"
        )

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It 'resolves a ${VAR} reference in API_SCHEMA_PATH the way Docker Compose does' {
        # Compose interpolates ${VAR}/$VAR in an --env-file entry before the container sees it, so the
        # container legitimately carries the RESOLVED path. Comparing the literal reference text kept
        # the expected side as '${SCHEMA_ROOT}/ApiSchema', which can never equal what the container
        # received - a hard setup abort on a stack that came up correctly.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "SCHEMA_ROOT=/app"
            'API_SCHEMA_PATH=${SCHEMA_ROOT}/ApiSchema'
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/app/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It 'resolves a ${VAR} reference in USE_API_SCHEMA_PATH the way Docker Compose does' {
        # Same false failure on the other file-read expectation: the unresolved reference is not
        # "true", so the gate reported an internally consistent environment file as declaring packages
        # without enabling the ApiSchema path.
        $script:environmentFileLines = @(
            "ENABLE_SCHEMA_PATH=true"
            'USE_API_SCHEMA_PATH=${ENABLE_SCHEMA_PATH}'
            "API_SCHEMA_PATH=/app/ApiSchema"
        )

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It 'still fails a genuinely different path when the declaration is a ${VAR} reference' {
        # Reference resolution must not turn the path comparison into a no-op: a file pointing
        # somewhere the container is not is still the mismatch this gate exists to report.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "SCHEMA_ROOT=/somewhere/else"
            'API_SCHEMA_PATH=${SCHEMA_ROOT}/ApiSchema'
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/app/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It 'freezes API_SCHEMA_PATH at its own line, so a later re-declaration of the name it referenced cannot change it' {
        # Docker Compose resolves an --env-file in declaration ORDER: API_SCHEMA_PATH is frozen as
        # /app/ApiSchema when Compose reads that line, and the later SCHEMA_ROOT=/other applies only to
        # lines after it. Reading the expected value from a collapsed key/value map instead kept just
        # the FINAL SCHEMA_ROOT, expected /other/ApiSchema, and failed a correctly started stack.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "SCHEMA_ROOT=/app"
            'API_SCHEMA_PATH=${SCHEMA_ROOT}/ApiSchema'
            "SCHEMA_ROOT=/other"
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/app/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It 'still fails when the container carries what the collapsed key/value model would have expected' {
        # The mirror of the case above, so the fix is not merely "accept both". The container is on
        # /other/ApiSchema - the value the collapsed model computed - which Compose never produced from
        # this file, and that must still be reported as a mismatch.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "SCHEMA_ROOT=/app"
            'API_SCHEMA_PATH=${SCHEMA_ROOT}/ApiSchema'
            "SCHEMA_ROOT=/other"
        )
        $script:stubContainerEnvironment["AppSettings__ApiSchemaPath"] = "/other/ApiSchema"

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }

    It 'freezes USE_API_SCHEMA_PATH at its own line, so a later re-declaration of the name it referenced cannot change it' {
        # The same declaration-order rule on the boolean. USE_API_SCHEMA_PATH is frozen as true; the
        # later ENABLE_SCHEMA_PATH=false does not reach back. The collapsed model resolved the
        # reference against the final false and reported the file as declaring packages without
        # enabling the ApiSchema path.
        $script:environmentFileLines = @(
            "ENABLE_SCHEMA_PATH=true"
            'USE_API_SCHEMA_PATH=${ENABLE_SCHEMA_PATH}'
            "API_SCHEMA_PATH=/app/ApiSchema"
            "ENABLE_SCHEMA_PATH=false"
        )

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It 'uses the last declaration of a repeated key, which is what the compose file itself sees' {
        # Declaration order decides a referencing line's value, but for the key being read it is the
        # LAST declaration that Compose hands to the compose file. Both rules have to hold at once.
        #
        # Asserted on the file reader directly rather than through the whole gate, because a repeated GATE
        # key is now rejected before anything is compared (the case below). The last-wins rule still
        # decides what this reader takes - that rejection exists precisely BECAUSE the package reader
        # takes the first declaration while this one takes the last.
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=true"
            "API_SCHEMA_PATH=/first/ApiSchema"
            "API_SCHEMA_PATH=/last/ApiSchema"
        )
        $sequential = Resolve-DotenvFileSequentially -Path (New-EnvironmentFileFixture)

        Get-DmsEnvironmentFileDeclaredValue `
            -ResolvedEnvironmentFile $sequential `
            -Name "API_SCHEMA_PATH" `
            -DefaultValue "" |
            Should -BeExactly "/last/ApiSchema"
    }

    It "reports the file-side failure when the environment file declares <Label> more than once" -ForEach @(
        @{ Label = "SCHEMA_PACKAGES"; Lines = @(
                "USE_API_SCHEMA_PATH=true"
                "API_SCHEMA_PATH=/app/ApiSchema"
                "SCHEMA_PACKAGES='[{""name"":""EdFi.ApiSchema.Package1""}]'"
                "SCHEMA_PACKAGES='[{""name"":""EdFi.ApiSchema.Package1""},{""name"":""EdFi.ApiSchema.Package2""}]'"
            )
        }
        @{ Label = "API_SCHEMA_PATH"; Lines = @(
                "USE_API_SCHEMA_PATH=true"
                "API_SCHEMA_PATH=/first/ApiSchema"
                "API_SCHEMA_PATH=/app/ApiSchema"
            )
        }
        @{ Label = "USE_API_SCHEMA_PATH"; Lines = @(
                "USE_API_SCHEMA_PATH=false"
                "USE_API_SCHEMA_PATH=true"
                "API_SCHEMA_PATH=/app/ApiSchema"
            )
        }
    ) {
        # A duplicated gate key is legal Compose, but the two sides of this gate then read the same file
        # with two parsers that disagree on which declaration wins: Get-QuotedEnvJson - behind
        # Get-SchemaPackagesFromEnvironmentFile, which is both the expected side and the provisioner's own
        # reader - matches the FIRST SCHEMA_PACKAGES declaration, while Compose delivers the LAST one to
        # the container. That produced a real but misattributed abort: "the container received N
        # package(s)" with a remediation asking for a teardown and re-run that reproduces it every time,
        # over a file the developer can simply fix. A supplied -EnvironmentFile is a documented input, so
        # this has to be reported against the file rather than pinned for tracked files alone.
        #
        # Each fixture's container AGREES with the last declaration, so a check placed after the
        # comparisons would let all three pass. The two scalars are held to the same rule as
        # SCHEMA_PACKAGES because their agreement with Compose otherwise rests on which declaration each
        # reader happens to take.
        $script:environmentFileLines = $Lines

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } |
            Should -Throw -ExpectedMessage "DMS E2E setup mismatch: the environment file *declares $Label more than once.*Remove the duplicate declaration(s) from the environment file."
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
        $script:environmentFileLines = @(
            "USE_API_SCHEMA_PATH=$RawValue"
            "API_SCHEMA_PATH=/app/ApiSchema"
        )

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
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
                -EnvironmentFilePath (New-EnvironmentFileFixture) `
                -ContainerName "ed-fi-api" } | Should -Throw -ExpectedMessage "DMS E2E setup mismatch: *"
    }
}

Describe "Get-DmsContainerEnvironment reads the container environment and fails closed (DMS-1300)" {
    # Executes the reader against a stubbed 'docker', so its parsing rules and its fail-closed behavior
    # are real results rather than a source pattern, and no Docker daemon is required.
    BeforeAll {
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))
        . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:schemaEnvironmentModule -FunctionName "Get-DmsContainerEnvironment")))

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

    It "keeps the literal newlines inside a multi-line SCHEMA_PACKAGES entry" {
        # The shape production actually hands this reader: .env.e2e declares SCHEMA_PACKAGES='[ across
        # several lines, so the container's .Config.Env entry carries literal newlines. The reader splits
        # on the FIRST '=' and keeps the remainder verbatim, which is what makes the value parseable by
        # Get-DmsContainerSchemaPackage - a move to line-splitting, or a trim, would truncate exactly the
        # value the gate compares, and every other case here is single-line.
        $multiLineValue = @'
[
  {"name":"EdFi.ApiSchema.Package1","version":"1.0.0"},
  {"name":"EdFi.ApiSchema.Package2","version":"1.0.0"}
]
'@
        $script:stubDockerOutput = ConvertTo-Json -Compress -InputObject @("SCHEMA_PACKAGES=$multiLineValue")

        $containerEnvironment = Get-DmsContainerEnvironment -ContainerName "ed-fi-api"

        $containerEnvironment["SCHEMA_PACKAGES"] | Should -BeExactly $multiLineValue
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

Describe "Both E2E setup wrappers verify the started container against the environment file only (DMS-1300)" {
    BeforeAll {
        # The verifier is shared, so the wrappers own only the call site and the import; the module
        # owns how the expectations are read. Assertions are aimed at whichever file now holds the
        # behavior, never at the wrapper for code that has moved.
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))
        $script:schemaEnvironmentModuleSource = Get-Content -LiteralPath $script:schemaEnvironmentModule -Raw

        $script:setupWrapperScripts = [ordered]@{
            "DataManagementService E2E" = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1"))
            "InstanceManagement E2E"    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"))
        }

        function Test-CommandInsideSchemaGuard {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $CommandName
            )

            # ANY guard block, not the first one. Each phase is guarded separately, so the verification
            # sits in its own guard after the DMS-only start; a check bound to the first guard call
            # would report the correctly placed verification as unguarded.
            $actionExtent = @(Get-SchemaGuardActionExtent -ScriptPath $ScriptPath)

            $calls = @(Get-ScriptCommandInvocation -ScriptPath $ScriptPath -CommandName $CommandName)

            if ($calls.Count -ne 1) {
                throw "Expected exactly one '$CommandName' invocation in '$ScriptPath'; found $($calls.Count)."
            }

            return @($actionExtent | Where-Object {
                    $calls[0].Extent.StartOffset -ge $_.StartOffset -and $calls[0].Extent.EndOffset -le $_.EndOffset
                }).Count -gt 0
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

    It "runs the container verification inside the schema-settings guard in the <Name> wrapper" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        # Inside the guard, so the file-only expectation cannot be contaminated by an ambient override
        # even if a future edit reaches for a Compose-precedence reader. Both wrappers provision from
        # the environment file and start DMS through ambient-first Compose, so both need the check.
        Test-CommandInsideSchemaGuard -ScriptPath $script:setupWrapperScripts[$Name] -CommandName "Assert-DmsContainerSchemaEnvironment" |
            Should -BeTrue
    }

    It "imports the shared guard and verifier in the <Name> wrapper rather than carrying its own copies" -ForEach @(
        @{ Name = "DataManagementService E2E" }
        @{ Name = "InstanceManagement E2E" }
    ) {
        # A second copy is what this module exists to prevent: two verifiers drift, and only one of
        # them gets the next fix. The same argument applies to the pre-phase schema-settings guard, so
        # neither wrapper may redefine it either.
        $source = Get-Content -LiteralPath $script:setupWrapperScripts[$Name] -Raw

        # Imported, and without -Force: -Force removes a module session-wide before re-importing it,
        # while a plain import reuses an already-loaded instance - which is what build-dms.ps1 loads for
        # its own guarded call sites before invoking a setup wrapper in-process. The same rule the
        # module applies to its own nested imports.
        $source | Should -Match "Import-Module \./dms-schema-environment\.psm1(?! -Force)"
        $source | Should -Not -Match "Import-Module \./dms-schema-environment\.psm1 -Force"
        $source | Should -Not -Match "function Assert-DmsContainerSchemaEnvironment"
        $source | Should -Not -Match "function Get-DmsSchemaEnvironmentVerdict"
        $source | Should -Not -Match "function Get-DmsContainerEnvironment"
        $source | Should -Not -Match "function Invoke-WithDmsEnvironmentFileSchemaAuthority"
    }

    It "reads both expectations from the environment file through the canonical sequential resolver" {
        # Asserted over the commands the function actually invokes, not its text: the function's own
        # comment names Get-ComposeResolvedEnvValue in order to prohibit it, and a text search cannot
        # tell a prohibition from a call.
        #
        # Get-ComposeResolvedEnvValue resolves ambient-first. Using it for either expectation would let
        # the very override this gate exists to catch decide what "correct" means, so the gate would
        # agree with a wrongly-started container and pass.
        #
        # Resolve-DotenvFileSequentially is this repository's one model of how Compose reads an
        # --env-file. Pinning it by name is deliberate: the alternative that keeps being reached for is
        # another normalization step layered on a COLLAPSED key/value map, which cannot express
        # declaration order and is exactly how the frozen-value defect got in. Neither the collapsed
        # reader nor a per-value resolver over it may come back for these two scalars.
        $invoked = @(Get-FunctionCommandName -ScriptPath $script:schemaEnvironmentModule -FunctionName "Assert-DmsContainerSchemaEnvironment")

        $invoked | Should -Contain "Get-SchemaPackagesFromEnvironmentFile"
        $invoked | Should -Contain "Resolve-DotenvFileSequentially"
        $invoked | Should -Contain "Get-DmsEnvironmentFileDeclaredValue"
        $invoked | Should -Not -Contain "Get-ComposeResolvedEnvValue"
        $invoked | Should -Not -Contain "Get-EnvValue"
        $invoked | Should -Not -Contain "Resolve-ComposeEnvRawValue"
        # What each name FALLS BACK TO when the file does not declare it is asserted behaviorally,
        # against real environment-file fixtures, in the Assert-DmsContainerSchemaEnvironment block
        # above - not as a source pattern over the -DefaultValue arguments, which pinned the call's
        # line wrapping and said nothing about the resulting verdict.
    }

    It "reads the frozen declaration rather than the ambient-precedence Effective map" {
        # Resolve-DotenvFileSequentially returns both. Effective applies ambient precedence to the
        # requested key, so taking it would let a shell value Compose never saw define the expected
        # side - the same class of defect as using Get-ComposeResolvedEnvValue.
        $script:schemaEnvironmentModuleSource | Should -Match '\$ResolvedEnvironmentFile\.Declarations'
        $script:schemaEnvironmentModuleSource | Should -Not -Match '\$ResolvedEnvironmentFile\.Effective'
        # Last declaration wins, which is what the compose file itself sees.
        $script:schemaEnvironmentModuleSource | Should -Match 'Select-Object -Last 1'
    }

    It "imports the same file-only package reader the provision phase uses, without -Force" {
        # The module imports its own dependencies rather than relying on whatever the invoking wrapper
        # happened to import, so command resolution does not depend on the call site.
        #
        # Never with -Force on these nested imports. -Force removes the module before re-importing it
        # and removal is session-wide, so it strips an already-imported dependency out of the CALLER's
        # session state and re-imports it into this module's scope alone - which is what left both setup
        # wrappers unable to resolve any database-safety command. The behavioral regression is below;
        # this pins the spelling that causes it.
        $script:schemaEnvironmentModuleSource | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "\.\./schema-package-utility\.psm1"\)(?! -Force)'
        $script:schemaEnvironmentModuleSource | Should -Match 'Import-Module \(Join-Path \$PSScriptRoot "database-safety\.psm1"\)(?! -Force)'
        $script:schemaEnvironmentModuleSource | Should -Not -Match 'Import-Module[^\r\n]*\.psm1"\) -Force'
    }

    It "throws rather than warning when the verdict fails" {
        $script:schemaEnvironmentModuleSource | Should -Match 'throw "DMS E2E setup mismatch: '
        $script:schemaEnvironmentModuleSource | Should -Not -Match 'Write-Warning[^\r\n]*setup mismatch'
    }

    It "leaves every imported command resolvable, and the verifier's own dependencies resolvable, in the <Label> import order" -ForEach @(
        @{
            Label       = "direct wrapper"
            ImportOrder = @(
                "Import-Module ./env-utility.psm1 -Force"
                "Import-Module ./database-safety.psm1 -Force"
                "Import-Module ./dms-schema-environment.psm1"
            )
        }
        @{
            # The build path. build-dms.ps1 imports this module at the top of the script and then invokes
            # a setup wrapper IN-PROCESS, so the wrapper's own three imports run against a session where
            # this module is already loaded - and its two -Force imports remove and re-import the very
            # leaf modules this module nested-imported for itself. The wrapper's final plain import
            # cannot repair that: an already-loaded module is reused, not re-processed, so its import
            # block does not run again. Only exercising this order proves the module's internal
            # dependency resolution survives it.
            Label       = "build-dms.ps1"
            ImportOrder = @(
                "Import-Module ./dms-schema-environment.psm1"
                "Import-Module ./env-utility.psm1 -Force"
                "Import-Module ./database-safety.psm1 -Force"
                "Import-Module ./dms-schema-environment.psm1"
            )
        }
    ) {
        # A real import, not a source assertion. Every other test in this suite reaches this module by
        # extracting function text through the AST, so nothing here ever executed its import block - and
        # a nested 'Import-Module ... -Force' unloaded database-safety out of the importing session,
        # leaving both wrappers to fail at their first database-safety call after the import while every
        # test stayed green.
        #
        # Run in a child pwsh, in a real import order and from the wrappers' own working directory, so
        # the observation is a fresh session's command resolution rather than one this suite has already
        # populated with -Force imports of its own.
        $dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $observationPath = Join-Path $TestDrive "wrapper-import-observations-$Label.txt"
        $childScriptPath = Join-Path $TestDrive "wrapper-import-child-$Label.ps1"
        $environmentFilePath = Join-Path $TestDrive "wrapper-import-fixture-$Label.env"
        # A package-bearing environment file, so the verifier gets past its file-only reader and reaches
        # the container read. Written here rather than pointing at a tracked .env file, so the case does
        # not depend on repository content it is not about.
        Set-Content -LiteralPath $environmentFilePath -Encoding utf8 -Value @'
USE_API_SCHEMA_PATH=true
API_SCHEMA_PATH=/app/ApiSchema
SCHEMA_PACKAGES='[{"name":"EdFi.ApiSchema.Package1","version":"1.0.0","feedUrl":"https://pkgs.example.org/v3/index.json"}]'
'@

        # Every path is embedded in a single-quoted literal in the generated child script, so an
        # apostrophe in any of them would close the quote early. Doubling keeps the literals intact.
        $escapedObservationPath = $observationPath -replace "'", "''"
        $escapedComposeRoot = $dockerComposeRoot -replace "'", "''"
        $escapedEnvironmentFilePath = $environmentFilePath -replace "'", "''"
        $importBlock = $ImportOrder -join [System.Environment]::NewLine
        $childScript = @"
Set-Location -LiteralPath '$escapedComposeRoot'

$importBlock

foreach (`$commandName in @(
        'Assert-E2EDatabaseIsDedicated',
        'Get-ComposeResolvedEnvValue',
        'Resolve-DotenvFileSequentially',
        'Assert-DmsContainerSchemaEnvironment',
        'Invoke-WithDmsEnvironmentFileSchemaAuthority',
        'Get-DmsContainerEnvironment',
        'Get-DmsSchemaEnvironmentVerdict',
        'Get-DmsSchemaPackageIdentity'
    )) {
    `$commandName + '=' + [string][bool](Get-Command `$commandName -ErrorAction SilentlyContinue) |
        Add-Content -LiteralPath '$escapedObservationPath'
}

# GLOBAL, so the module's own 'docker inspect' resolves to it: a module function looks up commands in
# its own session state and then in the global one, never in this script's scope. Fails closed, which
# is the outcome the verifier turns into a throw.
function global:docker {
    `$global:LASTEXITCODE = 1
    return ''
}

# Reaching 'docker inspect' means every module-internal dependency resolved on the way there:
# Get-SchemaPackagesFromEnvironmentFile (schema-package-utility) and Resolve-DotenvFileSequentially
# (database-safety), both nested-imported by this module. A broken one throws CommandNotFoundException
# BEFORE the container read, so the observation distinguishes them.
try {
    Assert-DmsContainerSchemaEnvironment -EnvironmentFilePath '$escapedEnvironmentFilePath' -ContainerName 'ed-fi-api'
    'VerifierOutcome=returned-without-reading-the-container' | Add-Content -LiteralPath '$escapedObservationPath'
}
catch {
    'VerifierOutcome=' + `$_.Exception.Message | Add-Content -LiteralPath '$escapedObservationPath'
}
"@
        Set-Content -LiteralPath $childScriptPath -Value $childScript -Encoding utf8

        & (Get-Process -Id $PID).Path -NoProfile -File $childScriptPath
        $LASTEXITCODE | Should -Be 0 -Because "the $Label import sequence must not fail"

        $observations = @(Get-Content -LiteralPath $observationPath)
        # The three the wrappers call after importing this module: the up-front dedicated-database gate,
        # the Compose-equivalent scalar reader, and the sequential env-file resolver.
        $observations | Should -Contain "Assert-E2EDatabaseIsDedicated=True" -Because "the Instance Management wrapper's up-front dedicated-database gate runs after this import"
        $observations | Should -Contain "Get-ComposeResolvedEnvValue=True" -Because "both wrappers read a scalar through it after this import"
        $observations | Should -Contain "Resolve-DotenvFileSequentially=True" -Because "no import may strip the caller's sequential env-file resolver"
        # The import still has to do its own job, so this is not satisfiable by removing it outright.
        $observations | Should -Contain "Assert-DmsContainerSchemaEnvironment=True" -Because "the module must still export the verifier the wrappers import it for"
        # Every other guard test extracts the function text through the AST, so only a real import can
        # catch the guard being defined in the module but left out of Export-ModuleMember - which would
        # leave both wrappers unable to resolve it at their first phase.
        $observations | Should -Contain "Invoke-WithDmsEnvironmentFileSchemaAuthority=True" -Because "both wrappers now reach the schema-settings guard through this module's exports"
        # build-dms.ps1's runtime hash gate calls this reader by name after importing the module, having
        # dropped its own copy of it, so the export is load-bearing for that script the same way.
        $observations | Should -Contain "Get-DmsContainerEnvironment=True" -Because "build-dms.ps1 reads the container environment through this module's export"
        # The export surface stays at the three commands with an external caller. The internals reach
        # their tests through AST extraction, so exporting them would buy nothing and widen what the
        # in-process shadowing described above can bind to.
        $observations | Should -Contain "Get-DmsSchemaEnvironmentVerdict=False" -Because "the verdict has no caller outside the module"
        $observations | Should -Contain "Get-DmsSchemaPackageIdentity=False" -Because "the identity reducer has no caller outside the module"

        # The behavioral half: resolving the exports says nothing about whether the module can still
        # resolve the commands IT calls. Only a run that gets all the way to the container read proves
        # that, which a source assertion or a Get-Command check cannot.
        $observations | Should -Contain "VerifierOutcome=Unable to inspect Docker container 'ed-fi-api' to verify its schema environment." -Because "the verifier must reach its 'docker inspect' step, which means the module resolved its own nested-imported dependencies in this order"
    }
}

Describe "The container schema gate accepts the repository's own tracked environment files (DMS-1300)" {
    # The rest of this suite stubs every reader, so nothing pins that the env files the E2E flows
    # actually ship with satisfy the gate. The coupling that matters in production is "what the
    # file-only readers extract from the file" versus "what Compose puts in the container", and it is
    # fragile in ways pure-logic tests cannot see: Get-QuotedEnvJson matches only a single-quoted
    # SCHEMA_PACKAGES='[...]' via a non-greedy regex, and a legal Compose value shape (quoting, an
    # inline comment, a ${VAR} reference) can silently break the comparison. No Docker: the container
    # side is a fixture built from the same file, exactly as Compose would pass it through.

    # Discovery-phase matrix of the artifacts the wrappers actually hand the gate, shared by every
    # assertion below so a new artifact is covered by all of them at once rather than by whichever table
    # someone remembered to extend. Both wrappers compose the data-standard overlay first and then the
    # database-engine overlay, so a composed MSSQL file - the artifact an MSSQL run's gate is given, and
    # the one the engine composition rewrites and reorders - belongs here alongside the base files.
    # .env.routeContext.e2e is the Instance Management wrapper's base, and it takes DS overlays the same
    # way .env.e2e does, so its overlaid forms are covered too. Built here rather than in BeforeAll
    # because -ForEach is bound during discovery.
    $trackedEnvironmentFileCases = @(
        @{ Label = ".env.e2e"; BaseName = ".env.e2e"; DataStandardVersion = ""; DatabaseEngine = "postgresql" }
        @{ Label = ".env.routeContext.e2e"; BaseName = ".env.routeContext.e2e"; DataStandardVersion = ""; DatabaseEngine = "postgresql" }
        @{ Label = ".env.e2e composed with .env.ds52"; BaseName = ".env.e2e"; DataStandardVersion = "5.2"; DatabaseEngine = "postgresql" }
        @{ Label = ".env.e2e composed with .env.ds61"; BaseName = ".env.e2e"; DataStandardVersion = "6.1"; DatabaseEngine = "postgresql" }
        @{ Label = ".env.routeContext.e2e composed with .env.ds52"; BaseName = ".env.routeContext.e2e"; DataStandardVersion = "5.2"; DatabaseEngine = "postgresql" }
        @{ Label = ".env.routeContext.e2e composed with .env.ds61"; BaseName = ".env.routeContext.e2e"; DataStandardVersion = "6.1"; DatabaseEngine = "postgresql" }
        @{ Label = ".env.e2e composed for mssql"; BaseName = ".env.e2e"; DataStandardVersion = ""; DatabaseEngine = "mssql" }
        @{ Label = ".env.e2e composed with .env.ds61 for mssql"; BaseName = ".env.e2e"; DataStandardVersion = "6.1"; DatabaseEngine = "mssql" }
        @{ Label = ".env.routeContext.e2e composed for mssql"; BaseName = ".env.routeContext.e2e"; DataStandardVersion = ""; DatabaseEngine = "mssql" }
    )

    BeforeAll {
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))

        foreach ($functionName in @(
                "Get-DmsSchemaEnvironmentToken",
                "Get-DmsContainerSchemaPackage",
                "Get-DmsSchemaPackageIdentity",
                "Get-DmsSchemaEnvironmentVerdict",
                "Get-DmsEnvironmentFileDeclaredValue",
                "Assert-DmsContainerSchemaEnvironment"
            )) {
            . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:schemaEnvironmentModule -FunctionName $functionName)))
        }

        # The REAL readers: Resolve-DotenvFileSequentially (database-safety) and
        # Get-SchemaPackagesFromEnvironmentFile (schema-package-utility) are the two production reaches
        # through the module's own imports; Resolve-DataStandardEnvironmentFile (env-utility) is the
        # production overlay composer used below. Nothing about the file side is stubbed here.
        Import-Module (Join-Path $PSScriptRoot "../env-utility.psm1") -Force
        Import-Module (Join-Path $PSScriptRoot "../database-safety.psm1") -Force
        Import-Module (Join-Path $PSScriptRoot "../../schema-package-utility.psm1") -Force

        # Production reaches Assert-DmsContainerSchemaEnvironment from inside the setup guard, after
        # USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES have been REMOVED from the process.
        # The container fixtures below are built from the sequential resolver's Effective map, which
        # applies ambient precedence, so an invoking shell still carrying USE_API_SCHEMA_PATH=false - the
        # exact dirty-shell shape the guard exists to tolerate - would define the fixture side from a
        # value Compose never sees and fail this block while wrapper behavior is correct. Removed for the
        # duration of the block and restored afterward, so the fixtures are built under the same
        # precondition production's gate runs under.
        $script:guardedSchemaNames = @("USE_API_SCHEMA_PATH", "API_SCHEMA_PATH", "SCHEMA_PACKAGES")
        $script:guardedSchemaPriorValues = @{}
        foreach ($name in $script:guardedSchemaNames) {
            $script:guardedSchemaPriorValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }

        # The one reader that would need Docker. Everything else runs for real.
        function Get-DmsContainerEnvironment {
            param([Parameter(Mandatory)] [string] $ContainerName)

            $null = $ContainerName
            return $script:stubContainerEnvironment
        }

        function Get-TrackedSchemaPackagesRawValue {
            # The file's VERBATIM single-quoted SCHEMA_PACKAGES text, which is what Compose passes into
            # the container (single-quoted, so Compose strips the quotes and interpolates nothing).
            # Deliberately not a re-serialization of what Get-SchemaPackagesFromEnvironmentFile parsed:
            # the container side has to be the file's own bytes, so the gate's parse of the container
            # value and the production reader's parse of the file are two independent paths that this
            # test requires to agree.
            param([Parameter(Mandatory)] [string] $EnvironmentFilePath)

            $content = Get-Content -LiteralPath $EnvironmentFilePath -Raw
            $match = [regex]::Match($content, "(?ms)^[ \t]*SCHEMA_PACKAGES='(?<value>\[.*?\])'")

            if (-not $match.Success) {
                throw "'$EnvironmentFilePath' carries no single-quoted SCHEMA_PACKAGES array."
            }

            return $match.Groups["value"].Value
        }

        # .env.ds52 / .env.ds61 are OVERLAYS: they carry SCHEMA_PACKAGES but not USE_API_SCHEMA_PATH or
        # API_SCHEMA_PATH, so they are only ever consumed composed onto a base file. .env.mssql is the
        # engine overlay for the same reason. All three are composed here with the same production
        # helpers the setup wrappers use, into an isolated root so nothing is written into the
        # repository - the derived output lands under <overlayRoot>/.derived, not under eng/docker-compose.
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:overlayRoot = Join-Path $TestDrive "overlay-root"
        New-Item -ItemType Directory -Path $script:overlayRoot -Force | Out-Null
        foreach ($overlayName in @(".env.ds52", ".env.ds61", ".env.mssql")) {
            Copy-Item -LiteralPath (Join-Path $script:dockerComposeRoot $overlayName) -Destination (Join-Path $script:overlayRoot $overlayName)
        }

        function Resolve-TrackedEnvironmentFile {
            # Data standard FIRST, then database engine - the order both wrappers use, so an MSSQL case
            # here is the same artifact production hands the gate. The engine step is a no-op for
            # postgresql, which is why it is called unconditionally rather than branched around: the
            # helper the wrappers call is the helper this exercises.
            param(
                [Parameter(Mandatory)] [string] $BaseName,
                [string] $DataStandardVersion,
                [string] $DatabaseEngine = "postgresql"
            )

            $resolvedPath = Join-Path $script:dockerComposeRoot $BaseName

            if (-not [string]::IsNullOrWhiteSpace($DataStandardVersion)) {
                $resolvedPath = Resolve-DataStandardEnvironmentFile `
                    -DataStandardVersion $DataStandardVersion `
                    -BaseEnvironmentFile $resolvedPath `
                    -DockerComposeRoot $script:overlayRoot
            }

            return Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine $DatabaseEngine `
                -BaseEnvironmentFile $resolvedPath `
                -DockerComposeRoot $script:overlayRoot
        }
    }

    AfterAll {
        foreach ($name in $script:guardedSchemaNames) {
            if ($null -eq $script:guardedSchemaPriorValues[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $script:guardedSchemaPriorValues[$name])
            }
        }
    }

    It "the tracked <Label> satisfies the container schema gate" -ForEach $trackedEnvironmentFileCases {
        $environmentFilePath = Resolve-TrackedEnvironmentFile -BaseName $BaseName -DataStandardVersion $DataStandardVersion -DatabaseEngine $DatabaseEngine

        # The container fixture is what Compose renders from this same file. Built through the
        # SEQUENTIAL model, the same one production reads: a fixture built from a collapsed key/value
        # map would mirror the very defect this suite has to catch. The Effective map is the right
        # choice on THIS side - it is what the compose file receives, ambient precedence included - and
        # it is a different code path from production's frozen-declaration read, so the two are not
        # asserting each other into agreement. SCHEMA_PACKAGES is passed through verbatim.
        $sequential = Resolve-DotenvFileSequentially -Path $environmentFilePath
        $script:stubContainerEnvironment = @{
            "AppSettings__UseApiSchemaPath" = [string]$sequential.Effective["USE_API_SCHEMA_PATH"]
            "AppSettings__ApiSchemaPath"    = [string]$sequential.Effective["API_SCHEMA_PATH"]
            "SCHEMA_PACKAGES"               = Get-TrackedSchemaPackagesRawValue -EnvironmentFilePath $environmentFilePath
        }

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath $environmentFilePath `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "builds its container fixture under production's precondition, so a dirty invoking shell cannot fail this block" {
        # The regression for the exact shell state DMS-1300 exists to tolerate: a developer shell still
        # carrying USE_API_SCHEMA_PATH=false. The fixtures here read the ambient-first Effective map, so
        # without the block's removal of the three schema names that ambient value - not the file's -
        # defines what the container is claimed to have, and every case above fails for a reason wrapper
        # behavior has nothing to do with.
        foreach ($name in $script:guardedSchemaNames) {
            (Test-Path -LiteralPath "Env:$name") |
                Should -BeFalse -Because "$name must be absent while these fixtures are built, which is the precondition the setup guard leaves for production's gate"
        }

        $environmentFilePath = Resolve-TrackedEnvironmentFile -BaseName ".env.e2e" -DataStandardVersion ""

        # The dirty shell is arranged here rather than assumed, so the mechanism is a real result on a
        # clean CI runner as well as on a polluted developer shell.
        [System.Environment]::SetEnvironmentVariable("USE_API_SCHEMA_PATH", "false")
        try {
            [string](Resolve-DotenvFileSequentially -Path $environmentFilePath).Effective["USE_API_SCHEMA_PATH"] |
                Should -BeExactly "false" -Because "the Effective map applies ambient precedence, which is why the fixtures may only be built with these names removed"
        }
        finally {
            foreach ($name in $script:guardedSchemaNames) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
        }

        # Restored to the production precondition, the identical construction takes the FILE's value and
        # the gate agrees.
        $sequential = Resolve-DotenvFileSequentially -Path $environmentFilePath
        [string]$sequential.Effective["USE_API_SCHEMA_PATH"] | Should -BeExactly "true"
        $script:stubContainerEnvironment = @{
            "AppSettings__UseApiSchemaPath" = [string]$sequential.Effective["USE_API_SCHEMA_PATH"]
            "AppSettings__ApiSchemaPath"    = [string]$sequential.Effective["API_SCHEMA_PATH"]
            "SCHEMA_PACKAGES"               = Get-TrackedSchemaPackagesRawValue -EnvironmentFilePath $environmentFilePath
        }

        { Assert-DmsContainerSchemaEnvironment `
                -EnvironmentFilePath $environmentFilePath `
                -ContainerName "ed-fi-api" } | Should -Not -Throw
    }

    It "the tracked <Label> enables the ApiSchema path and declares at least one package" -ForEach $trackedEnvironmentFileCases {
        # Without this, the gate assertion above could pass on a file that declares no packages at all
        # or leaves the ApiSchema path off, because the fixture would agree with it either way.
        $environmentFilePath = Resolve-TrackedEnvironmentFile -BaseName $BaseName -DataStandardVersion $DataStandardVersion -DatabaseEngine $DatabaseEngine
        $sequential = Resolve-DotenvFileSequentially -Path $environmentFilePath

        Get-DmsEnvironmentFileDeclaredValue `
            -ResolvedEnvironmentFile $sequential `
            -Name "USE_API_SCHEMA_PATH" `
            -DefaultValue "false" |
            Should -BeExactly "true"
        @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $environmentFilePath).Count |
            Should -BeGreaterThan 0
    }

    It "the tracked <Label> declares no repeated key whose value another declaration resolves against" -ForEach $trackedEnvironmentFileCases {
        # The tracked-file guard for the defect the unit fixtures cover synthetically. A key declared
        # twice is legal Compose, but if a LATER declaration re-defines a name an EARLIER line already
        # resolved against, the two models disagree - and the collapsed one silently wins arguments it
        # should lose. This reports that shape landing in a tracked file rather than waiting for a
        # setup abort. Overlay composition can legitimately introduce duplicates, so the assertion is
        # scoped to duplicates that something actually references.
        $environmentFilePath = Resolve-TrackedEnvironmentFile -BaseName $BaseName -DataStandardVersion $DataStandardVersion -DatabaseEngine $DatabaseEngine
        $sequential = Resolve-DotenvFileSequentially -Path $environmentFilePath

        $referencedNames = @($sequential.Declarations | ForEach-Object { $_.References }) | Select-Object -Unique
        $referencedDuplicates = @($sequential.DuplicateKeys | Where-Object { $referencedNames -contains $_ })

        $referencedDuplicates | Should -BeNullOrEmpty -Because "a re-declared name that another line resolves against makes the file's meaning depend on declaration order"
    }

    It "the tracked <Label> declares each gate key at most once, because the provisioner reads the first declaration and Compose delivers the last" -ForEach $trackedEnvironmentFileCases {
        # A repeated declaration of one of the three keys the gate compares is legal Compose but not
        # safe here. For SCHEMA_PACKAGES the two sides read different declarations: Get-QuotedEnvJson -
        # the file-only reader behind both the provisioner and the gate's expected side - matches the
        # FIRST declaration, while Compose passes the LAST into the container (the last-wins rule pinned
        # for scalars above). A tracked or composed file carrying two different SCHEMA_PACKAGES values
        # therefore aborts every production setup deterministically, while this block's fixtures agree
        # with themselves and stay green, because they build the container side from the same first
        # match. For USE_API_SCHEMA_PATH and API_SCHEMA_PATH the gate already reads the last declaration
        # and so agrees with Compose today; they are held to the same rule because a duplicate leaves
        # that agreement resting on which declaration each reader happens to take. Scoped to the gate
        # keys, not to duplicates in general: overlay composition can legitimately re-declare other
        # names, which the sibling assertion above covers.
        $environmentFilePath = Resolve-TrackedEnvironmentFile -BaseName $BaseName -DataStandardVersion $DataStandardVersion -DatabaseEngine $DatabaseEngine
        $sequential = Resolve-DotenvFileSequentially -Path $environmentFilePath

        $duplicatedGateKeys = @(
            $sequential.DuplicateKeys |
                Where-Object { $_ -in @("SCHEMA_PACKAGES", "USE_API_SCHEMA_PATH", "API_SCHEMA_PATH") }
        )

        $duplicatedGateKeys | Should -BeNullOrEmpty -Because "the file-only reader takes the first SCHEMA_PACKAGES declaration while Compose passes the last into the container, so a repeated gate key leaves the gate's two sides depending on which declaration each reader took"
    }
}

Describe "The guard removes exactly the names local-dms.yml resolves for the DMS schema surface (DMS-1300)" {
    # The guard's variable list and the compose file's ${VAR:-default} references are two halves of one
    # contract, and nothing in this suite held them together. The guard removes names so Compose has to
    # fall back to the --env-file; a name local-dms.yml resolves for one of the three DMS schema
    # settings but the guard does not remove is resolved ambient-first again, which is the whole defect.
    # A rename on either side, or a fourth schema setting added to the compose file, would leave the
    # guard silently short while every behavioral test above still passed - they arrange the names the
    # guard already knows.
    #
    # Read with a narrow reader rather than a YAML dependency: only the dms service's environment block
    # is parsed, and only the three keys below are looked up in it. Both sides are also asserted
    # non-empty, so neither a compose-file restructure that the reader cannot follow nor a guard whose
    # list stops being a literal can turn this into an empty-equals-empty pass.

    BeforeAll {
        $script:composeFilePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../local-dms.yml"))
        $script:schemaEnvironmentModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../dms-schema-environment.psm1"))

        # The compose keys the DMS container's schema surface is delivered through: the two
        # AppSettings__* names run.sh and DMS read, and SCHEMA_PACKAGES, which run.sh downloads from.
        $script:schemaComposeKey = @(
            "AppSettings__UseApiSchemaPath",
            "AppSettings__ApiSchemaPath",
            "SCHEMA_PACKAGES"
        )

        function Get-ComposeServiceEnvironmentEntry {
            <#
            .SYNOPSIS
            Returns one compose service's 'environment:' mapping as an ordered key/value map of the
            VERBATIM value text, so a caller can read the ${VAR:-default} references a key resolves.
            .DESCRIPTION
            Scoped to this file's one question rather than general: the repository's compose files are
            two-space-indented block mappings with an inline 'environment:' map, so the block is located
            by indentation depth and left the moment the indentation returns to the service's own level.
            Any other shape - a list-form environment block, a different indent width, a renamed service
            - produces no entries, which the caller turns into a failure rather than a vacuous pass.
            #>
            param(
                [Parameter(Mandatory)] [string] $ComposeFilePath,
                [Parameter(Mandatory)] [string] $ServiceName
            )

            $entries = [ordered]@{}
            $inServices = $false
            $inService = $false
            $inEnvironment = $false

            foreach ($line in (Get-Content -LiteralPath $ComposeFilePath)) {
                # Blank lines and whole-line comments carry no structure, and a comment can be indented
                # to any depth, so neither may move the state machine.
                if ($line -match '^\s*(#.*)?$') {
                    continue
                }

                if ($line -match '^\S') {
                    $inServices = $line -match '^services:\s*$'
                    $inService = $false
                    $inEnvironment = $false
                    continue
                }

                if ($inServices -and $line -match '^  (?<name>[^\s:]+):\s*$') {
                    # Ordinal: a compose service name is case-sensitive.
                    $inService = [string]::Equals($Matches["name"], $ServiceName, [System.StringComparison]::Ordinal)
                    $inEnvironment = $false
                    continue
                }

                if ($inService -and $line -match '^    (?<key>[^\s:]+):') {
                    # Any other key at the service's own depth ends the environment block.
                    $inEnvironment = $Matches["key"] -ceq "environment"
                    continue
                }

                if ($inEnvironment -and $line -match '^      (?<key>[^\s:]+):[ \t]*(?<value>.*)$') {
                    $entries[$Matches["key"]] = $Matches["value"]
                }
            }

            return $entries
        }

        function Get-ComposeInterpolatedName {
            <#
            .SYNOPSIS
            Returns every environment-variable name a compose value interpolates, in order.
            .DESCRIPTION
            Both '${VAR}' and '${VAR:-default}' - the name runs to the first character that cannot be
            part of one, which is where a ':-' default or the closing brace begins.
            #>
            param(
                [Parameter(Mandatory)] [AllowEmptyString()] [string] $Value
            )

            return @(
                [regex]::Matches($Value, '\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)') |
                    ForEach-Object { $_.Groups["name"].Value }
            )
        }

        function Get-SchemaGuardVariableName {
            <#
            .SYNOPSIS
            Returns the names the guard removes, read as the string literals of its
            $schemaEnvironmentVariableNames assignment.
            .DESCRIPTION
            Over the AST rather than by executing the guard, so the list is read even though it is a
            local variable of the function - and so this test states the guard's own list rather than
            re-deriving it from the observable behavior the other blocks already cover.
            #>
            param(
                [Parameter(Mandatory)] [string] $ModulePath
            )

            $guardAst = [scriptblock]::Create(
                (Get-ScriptFunctionText -ScriptPath $ModulePath -FunctionName "Invoke-WithDmsEnvironmentFileSchemaAuthority")
            ).Ast

            $assignment = @($guardAst.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $node.Left.VariablePath.UserPath -eq "schemaEnvironmentVariableNames"
                    },
                    $true
                )) | Select-Object -First 1

            if ($null -eq $assignment) {
                throw "The guard in '$ModulePath' no longer assigns `$schemaEnvironmentVariableNames, so the names it removes cannot be read."
            }

            return @(
                $assignment.Right.FindAll(
                    {
                        param($node)
                        $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
                    },
                    $true
                ) | ForEach-Object { $_.Value }
            )
        }
    }

    It "names the same set local-dms.yml resolves for the dms service's schema settings" {
        $dmsEnvironment = Get-ComposeServiceEnvironmentEntry -ComposeFilePath $script:composeFilePath -ServiceName "dms"

        $dmsEnvironment.Count |
            Should -BeGreaterThan 0 -Because "the reader must have found the dms service's environment block in local-dms.yml"

        $composeReferencedName = @(
            foreach ($key in $script:schemaComposeKey) {
                $dmsEnvironment.Contains($key) |
                    Should -BeTrue -Because "local-dms.yml's dms service must still deliver $key, which is what the gate reads off the container"

                $referenced = @(Get-ComposeInterpolatedName -Value ([string]$dmsEnvironment[$key]))
                $referenced.Count |
                    Should -BeGreaterThan 0 -Because "$key must resolve from an environment variable, which is what makes the guard's removal decide its value"

                $referenced
            }
        ) | Select-Object -Unique

        $guardVariableName = @(Get-SchemaGuardVariableName -ModulePath $script:schemaEnvironmentModule)

        # Set equality, sorted Ordinal so the comparison cannot vary with the host's culture, and
        # -BeExactly because a dotenv name is case-sensitive on the Linux runtime path these resolve on.
        @($guardVariableName | Sort-Object -CaseSensitive) |
            Should -BeExactly @($composeReferencedName | Sort-Object -CaseSensitive) -Because "the guard must remove exactly the names local-dms.yml resolves for the DMS schema surface: one it misses is resolved ambient-first again, and one it removes that compose does not read is a name it clears for no reason"
    }
}
