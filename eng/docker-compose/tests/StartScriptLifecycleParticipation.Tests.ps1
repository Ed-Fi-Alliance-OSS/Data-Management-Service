# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Finding 6: lifecycle participation. Full startup (default / -InfraOnly / -DmsOnly) materializes the CMS
# topology; the database-only diagnostic slice and teardown do NOT participate in CMS topology and must not
# require its materialization (which throws on a minimal environment that omits POSTGRES_DB_NAME) or resolve
# the schema tool. These paths must reach their Compose action honoring the Compose database default.
#
# The teardown path is proven behaviorally against a minimal environment with docker stubbed (a function
# named 'docker' shadows the native command inside the invoked script). The -DbOnly gate is proven
# structurally through the AST, because -DbOnly's real readiness probe uses System.Diagnostics.Process and
# cannot be shadowed by a function stub.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    # A minimal PostgreSQL environment based on the tracked example but WITHOUT POSTGRES_DB_NAME (and the
    # DMS_CONFIG_DATABASE_NAME that references it). postgresql.yml supplies the datastore default, so full
    # startup would still resolve, but the host-side topology resolver has no name to materialize and throws
    # - which is exactly why -DbOnly and teardown must not call it.
    $script:minimalEnvDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-f6-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:minimalEnvDir -Force | Out-Null
    $script:minimalEnv = Join-Path $script:minimalEnvDir "minimal.env"
    (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example")) |
        Where-Object { $_ -notmatch "^POSTGRES_DB_NAME=" -and $_ -notmatch "^DMS_CONFIG_DATABASE_NAME=" } |
        Set-Content -LiteralPath $script:minimalEnv

    # Invoke a start script with the native 'docker' command shadowed by a stub, capturing the docker
    # invocations and returning any terminating error. LASTEXITCODE is forced to 0 so the scripts' own
    # exit-code checks pass without a real Docker daemon.
    function Invoke-StartScriptWithDockerStub {
        param([string]$ScriptName, [hashtable]$ScriptParams)

        # Capture docker invocations through a temp file whose path travels in an environment variable:
        # the stub 'docker' function is invoked from deep inside the start script's scope, where a $script:
        # variable would not resolve back to this test module, and a global variable trips PSScriptAnalyzer.
        $captureFile = Join-Path $script:minimalEnvDir ("docker-calls-" + [guid]::NewGuid().ToString("N") + ".log")
        $env:DMS_F6_DOCKER_CAPTURE = $captureFile
        function docker {
            Add-Content -LiteralPath $env:DMS_F6_DOCKER_CAPTURE -Value ($args -join " ")
            $global:LASTEXITCODE = 0
        }

        $caught = $null
        try {
            & (Join-Path $script:composeRoot $ScriptName) @ScriptParams *> $null
        }
        catch {
            $caught = $_
        }
        $calls = if (Test-Path -LiteralPath $captureFile) { @(Get-Content -LiteralPath $captureFile) } else { @() }
        $env:DMS_F6_DOCKER_CAPTURE = $null
        return [pscustomobject]@{
            Error       = $caught
            DockerCalls = $calls
        }
    }
}

AfterAll {
    if (Test-Path -LiteralPath $script:minimalEnvDir) {
        Remove-Item -Recurse -Force $script:minimalEnvDir -ErrorAction SilentlyContinue
    }
}

Describe "The topology resolver tolerates a minimal environment (it no longer interpolates or validates the datastore in PowerShell)" {
    It "materializes the datastore-key reference for a minimal PostgreSQL environment that omits POSTGRES_DB_NAME (no throw)" {
        # The resolver used to throw here; it now defers datastore resolution and blank-datastore validation
        # to Docker Compose and the runtime contract, materializing only the ${POSTGRES_DB_NAME} seam. The
        # -DbOnly/teardown gating (asserted below) still skips topology materialization and validator
        # resolution because those are full-startup concerns, independent of whether the resolver throws.
        $resolved = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:minimalEnv -DockerComposeRoot $script:minimalEnvDir -DatabaseEngine postgresql
        (ReadValuesFromEnvFile $resolved)['DMS_CONFIG_DATABASE_NAME'] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
    }
}

Describe "Teardown does not participate in CMS topology (finding 6, behavioral)" {
    It "<Script> teardown reaches 'compose down' on a minimal environment without materializing topology" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        $result = Invoke-StartScriptWithDockerStub -ScriptName $Script -ScriptParams @{ d = $true; EnvironmentFile = $script:minimalEnv; DatabaseEngine = 'postgresql' }

        $result.Error | Should -BeNullOrEmpty -Because "teardown must not require CMS-topology materialization on a minimal environment"
        ($result.DockerCalls -join " `n") | Should -Match 'compose .*down' -Because "$Script teardown must reach 'docker compose down'"
    }
}

Describe "Topology materialization is gated to full startup (finding 6, AST participation)" {
    It "<Script> calls Resolve-ConfigDatabaseTopologyEnvironmentFile only inside an if guarded by both -d and -DbOnly" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        $path = Join-Path $script:composeRoot $Script
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)

        $topologyCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Resolve-ConfigDatabaseTopologyEnvironmentFile'
            }, $true)
        @($topologyCalls).Count | Should -Be 1 -Because "$Script materializes topology in exactly one place"

        # Walk up to the enclosing if-statement and confirm its condition gates on both lifecycle flags.
        $node = $topologyCalls[0]
        $enclosingIf = $null
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                $enclosingIf = $node
                break
            }
            $node = $node.Parent
        }
        $enclosingIf | Should -Not -BeNullOrEmpty -Because "the topology call must be inside an if guard"
        $conditionText = $enclosingIf.Clauses[0].Item1.Extent.Text
        $conditionText | Should -Match '\$d' -Because "teardown must not materialize CMS topology"
        $conditionText | Should -Match '\$DbOnly' -Because "the database-only diagnostic slice must not materialize CMS topology"
    }

    It "<Script> resolves the connection validator ('<Resolver>') only on the non-DbOnly startup path (never for -DbOnly or teardown)" -ForEach @(
        # The local lane resolves a host executable; the published lane resolves a host-exe-or-container
        # validator (finding 4). Either way the resolution must be gated away from -DbOnly and teardown.
        @{ Script = 'start-local-dms.ps1';     Resolver = 'Resolve-DmsSchemaTool' }
        @{ Script = 'start-published-dms.ps1'; Resolver = 'Resolve-DmsConnectionValidator' }
    ) {
        $path = Join-Path $script:composeRoot $Script
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)

        $resolveCalls = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq $Resolver
            }, $true)
        @($resolveCalls).Count | Should -Be 1 -Because "$Script resolves the connection validator in one place"

        $node = $resolveCalls[0]
        $guardConditions = [System.Collections.Generic.List[string]]::new()
        while ($null -ne $node) {
            if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                $guardConditions.Add($node.Clauses[0].Item1.Extent.Text)
            }
            $node = $node.Parent
        }
        ($guardConditions -join " `n") | Should -Match '\$DbOnly' -Because "$Script must not resolve the connection validator for -DbOnly or teardown"
    }
}

Describe "Published preflight rejects an invalid separate-topology replacement before any destructive action (behavioral)" {
    BeforeAll {
        $script:preflightDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-preflight-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:preflightDir -Force | Out-Null

        # A fake 'connection validate' tool: it consumes (never echoes) the connection string on stdin and
        # reports the CMS database the contract expects, so the read-only contract resolution succeeds and
        # execution reaches the datastore-target collision check. This stands in for the real api-schema-tools
        # verb here; the provider-oracle tests exercise the verb itself.
        $script:preflightValidator = Join-Path $script:preflightDir "fake-connection-validator.ps1"
        @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
$null = @($input)
Write-Output '{"valid":true,"database":"edfi_configurationservice"}'
exit 0
'@ | Set-Content -LiteralPath $script:preflightValidator -Encoding utf8

        # An env file whose DMS_CONFIG_DATABASE_NAME already equals the separate-topology effective value, so
        # topology materialization is an idempotent no-op that writes no .derived file into the repo. The
        # topology actually under test comes from the stubbed 'docker compose config' JSON, not this file.
        $script:preflightEnv = Join-Path $script:preflightDir "preflight.env"
        (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example")) |
            Where-Object { $_ -notmatch '^DMS_CONFIG_DATABASE_NAME=' } |
            Set-Content -LiteralPath $script:preflightEnv
        Add-Content -LiteralPath $script:preflightEnv -Value 'DMS_CONFIG_DATABASE_NAME=edfi_configurationservice'

        # Invoke start-published-dms.ps1 with the native 'docker' shadowed by a stub. The stub function must
        # be defined in this helper's own scope (not in the It block) so it is inherited by the '& script'
        # child scope - Pester does not propagate a function defined directly inside an It to an invoked
        # script. Only read-only 'docker compose config' is honored (returning a canned resolution); every
        # docker invocation is captured so the caller can prove no destructive/mutating action ran.
        function script:Invoke-PublishedPreflightWithDockerStub {
            param([hashtable]$ScriptParams)

            $captureFile = Join-Path $script:preflightDir ("docker-" + [guid]::NewGuid().ToString("N") + ".log")
            $outputFile = Join-Path $script:preflightDir ("output-" + [guid]::NewGuid().ToString("N") + ".log")

            # Snapshot every piece of global state this helper mutates so it can be restored verbatim in the
            # finally block, keeping the test independent of suite order and clean in a developer session
            # that already has these set (e.g. a real DMS_SCHEMA_TOOL_PATH). A $null snapshot means "was not
            # set" and is restored by removing the variable, not by leaving an empty string behind.
            $dockerFn = if (Test-Path -LiteralPath function:global:docker) { (Get-Item -LiteralPath function:global:docker).ScriptBlock } else { $null }
            $priorCapture = $env:DMS_PREFLIGHT_DOCKER_CAPTURE
            $priorToolPath = $env:DMS_SCHEMA_TOOL_PATH
            $priorComposeJson = $env:DMS_PREFLIGHT_COMPOSE_JSON
            $priorLastExitCode = $global:LASTEXITCODE

            $env:DMS_PREFLIGHT_DOCKER_CAPTURE = $captureFile
            $env:DMS_SCHEMA_TOOL_PATH = $script:preflightValidator
            # The canned resolution: the topology datastore anchor (POSTGRES_DB_NAME=edfi_datamanagementservice)
            # differs from the CMS database (edfi_configurationservice), so the separate-topology contract
            # passes and execution reaches the datastore-target collision.
            $env:DMS_PREFLIGHT_COMPOSE_JSON = '{"services":{"config":{"environment":{"AppSettings__Datastore":"postgresql","DatabaseSettings__DatabaseConnection":"host=dms-postgresql;port=5432;username=postgres;password=x;database=edfi_configurationservice;"}},"dms":{"image":"local/edfi-data-management-service","environment":{"AppSettings__Datastore":"postgresql","DATABASE_CONNECTION_STRING_ADMIN":"host=dms-postgresql;port=5432;username=postgres;password=x;database=edfi_admin;"}},"db":{"environment":{"POSTGRES_DB_NAME":"edfi_datamanagementservice"}}}}'

            # Define the stub in the GLOBAL scope: the compose-config call originates inside the
            # env-utility module function Get-ComposeResolvedConfiguration, whose command resolution sees only
            # the module's own session state and the global scope - never this helper's local scope. A
            # local-scope stub would be bypassed and the real docker would run.
            function global:docker {
                Add-Content -LiteralPath $env:DMS_PREFLIGHT_DOCKER_CAPTURE -Value ($args -join " ")
                if ($args -contains "config") {
                    Write-Output $env:DMS_PREFLIGHT_COMPOSE_JSON
                }
                $global:LASTEXITCODE = 0
            }

            $caught = $null
            try {
                & (Join-Path $script:composeRoot "start-published-dms.ps1") @ScriptParams *> $outputFile
            }
            catch {
                $caught = $_
            }
            finally {
                # Restore the docker function to exactly its prior state: reinstate the caller's own stub if
                # one existed, otherwise remove the function this helper created.
                if ($null -ne $dockerFn) { Set-Item -LiteralPath function:global:docker -Value $dockerFn }
                else { Remove-Item -LiteralPath function:global:docker -ErrorAction SilentlyContinue }
                if ($null -eq $priorCapture) { Remove-Item -LiteralPath Env:DMS_PREFLIGHT_DOCKER_CAPTURE -ErrorAction SilentlyContinue } else { $env:DMS_PREFLIGHT_DOCKER_CAPTURE = $priorCapture }
                if ($null -eq $priorToolPath) { Remove-Item -LiteralPath Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue } else { $env:DMS_SCHEMA_TOOL_PATH = $priorToolPath }
                if ($null -eq $priorComposeJson) { Remove-Item -LiteralPath Env:DMS_PREFLIGHT_COMPOSE_JSON -ErrorAction SilentlyContinue } else { $env:DMS_PREFLIGHT_COMPOSE_JSON = $priorComposeJson }
                $global:LASTEXITCODE = $priorLastExitCode
            }

            return [pscustomobject]@{
                Error       = $caught
                DockerCalls = if (Test-Path -LiteralPath $captureFile) { @(Get-Content -LiteralPath $captureFile) } else { @() }
                OutputText  = if (Test-Path -LiteralPath $outputFile) { (Get-Content -LiteralPath $outputFile -Raw) } else { "" }
            }
        }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:preflightDir) {
            Remove-Item -Recurse -Force $script:preflightDir -ErrorAction SilentlyContinue
        }
    }

    It "throws the topology collision - not the successful-preflight message - and runs no compose up/down/build, network create, volume, or service init" {
        $result = Invoke-PublishedPreflightWithDockerStub -ScriptParams @{
            PreflightOnly           = $true
            SeparateConfigDatabase  = $true
            DataStoreDatabaseName   = "edfi_configurationservice"
            EnvironmentFile         = $script:preflightEnv
            DatabaseEngine          = "postgresql"
        }

        $joined = ($result.DockerCalls -join " `n")

        # The invalid separate-topology replacement (edfi_configurationservice IS the dedicated CMS database)
        # must fail with the topology collision, never reaching the successful-preflight message.
        $result.Error | Should -Not -BeNullOrEmpty -Because "an invalid separate-topology replacement must fail preflight"
        $result.Error.Exception.Message | Should -Match "same physical database as the dedicated configuration database"
        $result.OutputText | Should -Not -Match "Preflight validation complete"

        # Read-only compose config resolution is permitted; NO destructive/mutating action may have run.
        $joined | Should -Match "compose .*config" -Because "read-only compose config resolution is expected"
        $joined | Should -Not -Match "compose .*\bup\b" -Because "no container may be started in preflight"
        $joined | Should -Not -Match "compose .*\bdown\b" -Because "no teardown may run in preflight"
        $joined | Should -Not -Match "compose .*\bbuild\b" -Because "no image build may run in preflight"
        $joined | Should -Not -Match "compose .*\brun\b" -Because "no one-off container may run in preflight"
        $joined | Should -Not -Match "network create" -Because "no network may be created in preflight"
        $joined | Should -Not -Match "network ls" -Because "the network probe runs only after the preflight return"
        $joined | Should -Not -Match "volume" -Because "no volume may be created or deleted in preflight"
    }

    It "full preflight REJECTS a connection-string-injection -DataStoreDatabaseName before any destructive action" {
        # A replacement carrying ';Database=...' would inject a duplicate keyword into the registered
        # connection string (last-wins). Full startup registers a datastore, so preflight must reject it as an
        # unsafe identifier - before any container start, network create, or volume operation.
        $result = Invoke-PublishedPreflightWithDockerStub -ScriptParams @{
            PreflightOnly          = $true
            SeparateConfigDatabase = $true
            DataStoreDatabaseName  = "edfi_datamanagementservice_e2e;Database=edfi_configurationservice"
            EnvironmentFile        = $script:preflightEnv
            DatabaseEngine         = "postgresql"
        }

        $joined = ($result.DockerCalls -join " `n")
        $result.Error | Should -Not -BeNullOrEmpty -Because "an injection-unsafe registered datastore name must fail full preflight"
        $result.Error.Exception.Message | Should -Match "not valid in a database identifier"
        $result.OutputText | Should -Not -Match "Preflight validation complete"
        $joined | Should -Not -Match "compose .*\bup\b"
        $joined | Should -Not -Match "network create"
        $joined | Should -Not -Match "volume"
    }

    It "full preflight REJECTS a leading/trailing-whitespace -DataStoreDatabaseName the providers would trim, before any destructive action" {
        # ' edfi_datamanagementservice_e2e' parses (trimmed) as the same DB the providers see, so an untrimmed
        # value must be rejected in preflight rather than silently targeting the trimmed name at runtime.
        $result = Invoke-PublishedPreflightWithDockerStub -ScriptParams @{
            PreflightOnly          = $true
            SeparateConfigDatabase = $true
            DataStoreDatabaseName  = " edfi_datamanagementservice_e2e"
            EnvironmentFile        = $script:preflightEnv
            DatabaseEngine         = "postgresql"
        }

        $joined = ($result.DockerCalls -join " `n")
        $result.Error | Should -Not -BeNullOrEmpty -Because "an untrimmed registered datastore name must fail full preflight"
        $result.Error.Exception.Message | Should -Match "leading or trailing whitespace"
        $result.OutputText | Should -Not -Match "Preflight validation complete"
        $joined | Should -Not -Match "compose .*\bup\b"
        $joined | Should -Not -Match "network create"
        $joined | Should -Not -Match "volume"
    }

    It "<Mode> preflight IGNORES an invalid -DataStoreDatabaseName it never registers (no throw, preflight completes)" -ForEach @(
        @{ Mode = "InfraOnly" }
        @{ Mode = "DmsOnly" }
    ) {
        # -InfraOnly and -DmsOnly start infrastructure or the DMS service and return before the in-process
        # registration block, so they never consume -DataStoreDatabaseName. An invalid value must NOT block
        # them: the registration resolution is gated to full-startup participation. A full -PreflightOnly with
        # the partial-mode flag reaches the same gate and returns cleanly (the resolution is skipped), proving
        # the value was never validated for a mode that does not register it.
        $params = @{
            PreflightOnly          = $true
            SeparateConfigDatabase = $true
            DataStoreDatabaseName  = "edfi_datamanagementservice_e2e;Database=edfi_configurationservice"
            EnvironmentFile        = $script:preflightEnv
            DatabaseEngine         = "postgresql"
        }
        $params[$Mode] = $true

        $result = Invoke-PublishedPreflightWithDockerStub -ScriptParams $params

        $result.Error | Should -BeNullOrEmpty -Because "$Mode does not register a datastore, so an invalid -DataStoreDatabaseName must be ignored"
        $result.OutputText | Should -Match "Preflight validation complete"
        ($result.DockerCalls -join " `n") | Should -Not -Match "network create" -Because "preflight still returns before any destructive action"
    }
}
