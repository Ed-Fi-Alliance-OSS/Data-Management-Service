# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Findings 6/7: lifecycle participation. Full startup (default / -InfraOnly / -DmsOnly) materializes the CMS
# topology (writing a derived env file) and resolves the schema tool; the database-only diagnostic slice and
# teardown do NOT participate in CMS topology and must do neither. The topology resolver now TOLERATES a
# minimal environment that omits POSTGRES_DB_NAME - it materializes the default-bearing seam without throwing
# (proven by the first Describe) - so these paths are proven to skip topology by the ABSENCE of a derived file
# and of any validator resolution, not by an avoided throw. These paths must reach their Compose action
# honoring the Compose database default.
#
# Both paths are proven BEHAVIORALLY, not structurally. Teardown is exercised with a docker FUNCTION stub (a
# 'docker' function shadows the native command inside the invoked script). -DbOnly cannot use a function stub:
# its readiness probe runs through System.Diagnostics.Process, which resolves the native 'docker' executable
# on PATH and never sees a PowerShell function. It is exercised instead with an OS-appropriate EXECUTABLE
# 'docker' shim placed first on PATH, which intercepts BOTH the '& docker' Compose calls and the Process
# readiness probe.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    # A minimal PostgreSQL environment based on the tracked example but WITHOUT POSTGRES_DB_NAME (and the
    # DMS_CONFIG_DATABASE_NAME that references it). postgresql.yml supplies the datastore default, so full
    # startup still resolves; the topology resolver TOLERATES the omitted key (it materializes the
    # default-bearing seam without throwing - see the first Describe). -DbOnly and teardown must not call it
    # anyway: materializing CMS topology (which writes a derived env file) is a full-startup concern that the
    # database-only slice and teardown do not perform.
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

Describe "The database-only diagnostic slice participates behaviorally, not just structurally (finding 7)" {
    BeforeAll {
        # An OS-appropriate EXECUTABLE 'docker' shim placed first on PATH. It must intercept BOTH the
        # PowerShell '& docker' Compose/network calls AND the System.Diagnostics.Process readiness probe
        # (Test-NativeCommandWithTimeout sets FileName='docker', UseShellExecute=$false, so a 'docker' FUNCTION
        # can never shadow it - only a real executable on PATH does). PowerShell 7 Add-Type cannot emit an
        # executable, so on Windows the shim is a tiny console app built with the .NET SDK (already required by
        # this lane's runtime-contract oracle); on other platforms it is a chmod +x shell script. Both append
        # every invocation's arguments to DMS_F7_DOCKER_CAPTURE and exit 0, so the script's exit-code checks
        # and the pg_isready probe (Test-NativeCommandWithTimeout returns ExitCode -eq 0) succeed immediately.
        $script:f7Dir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-f7-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:f7Dir -Force | Out-Null
        $script:f7ShimDir = Join-Path $script:f7Dir "shim"
        New-Item -ItemType Directory -Path $script:f7ShimDir -Force | Out-Null

        if ($IsWindows) {
            $projectDir = Join-Path $script:f7Dir "shim-src"
            New-Item -ItemType Directory -Path $projectDir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $projectDir "docker.csproj") -Encoding utf8 -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>docker</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@
            Set-Content -LiteralPath (Join-Path $projectDir "Program.cs") -Encoding utf8 -Value @'
using System;
using System.IO;
class DockerShim
{
    static int Main(string[] args)
    {
        string capture = Environment.GetEnvironmentVariable("DMS_F7_DOCKER_CAPTURE");
        if (!string.IsNullOrEmpty(capture))
        {
            File.AppendAllText(capture, string.Join(" ", args) + Environment.NewLine);
        }
        return 0;
    }
}
'@
            $build = & dotnet build (Join-Path $projectDir "docker.csproj") -c Release -o $script:f7ShimDir --nologo -v quiet 2>&1
            if (-not (Test-Path -LiteralPath (Join-Path $script:f7ShimDir "docker.exe"))) {
                throw "Failed to build the docker.exe shim for finding 7: $($build | Out-String)"
            }
        }
        else {
            $shimScript = Join-Path $script:f7ShimDir "docker"
            Set-Content -LiteralPath $shimScript -Encoding ascii -Value @'
#!/usr/bin/env bash
if [ -n "$DMS_F7_DOCKER_CAPTURE" ]; then
  printf '%s\n' "$*" >> "$DMS_F7_DOCKER_CAPTURE"
fi
exit 0
'@
            & chmod +x $shimScript
        }

        function script:Invoke-DbOnlyStartWithDockerExeShim {
            param([string]$ScriptName)

            # A UNIQUE base env filename per invocation - omitting POSTGRES_DB_NAME and DMS_CONFIG_DATABASE_NAME
            # - so the topology-derived target this test guards against is unambiguously attributable to THIS
            # run, not a leftover. The topology resolver TOLERATES the omission (it materializes the seam
            # without throwing), so -DbOnly skipping topology is proven by the derived file's absence, not by an
            # avoided throw.
            $envName = "dms-f7-" + [guid]::NewGuid().ToString("N") + ".env"
            $envFile = Join-Path $script:f7Dir $envName
            (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example")) |
                Where-Object { $_ -notmatch '^POSTGRES_DB_NAME=' -and $_ -notmatch '^DMS_CONFIG_DATABASE_NAME=' } |
                Set-Content -LiteralPath $envFile

            # The derived file the start scripts would write IF -DbOnly wrongly materialized topology:
            # Resolve-ConfigDatabaseTopologyEnvironmentFile writes <DockerComposeRoot>/.derived/<baseName>.config-db,
            # and the scripts pass -DockerComposeRoot $PSScriptRoot (the repo compose dir). It must never appear.
            $derivedTarget = Join-Path (Join-Path $script:composeRoot ".derived") ($envName + ".config-db")

            $captureFile = Join-Path $script:f7Dir ("calls-" + [guid]::NewGuid().ToString("N") + ".log")
            $outputFile = Join-Path $script:f7Dir ("out-" + [guid]::NewGuid().ToString("N") + ".log")

            # Snapshot every piece of state this helper mutates and restore it in finally, so the suite is
            # order-independent and leaves the session exactly as it found it: PATH (the shim is prepended), the
            # capture env var, LASTEXITCODE, DMS_SCHEMA_TOOL_PATH, and any 'docker' FUNCTION or ALIAS (either
            # would shadow the PATH executable and defeat the Process-probe interception, so both are removed).
            $priorPath = $env:PATH
            $priorCapture = $env:DMS_F7_DOCKER_CAPTURE
            $priorLastExit = $global:LASTEXITCODE
            $priorSchemaToolPath = $env:DMS_SCHEMA_TOOL_PATH
            $priorDockerFn = if (Test-Path -LiteralPath function:global:docker) { (Get-Item -LiteralPath function:global:docker).ScriptBlock } else { $null }
            # Snapshot the FULL metadata (definition, options, description) of the GLOBAL 'docker' alias only -
            # never a nearer script/local alias - so restoration is exact and cannot migrate a narrower-scoped
            # alias into global state. Any nearer surviving alias/function shadow is caught by the unfiltered
            # command-resolution guard below.
            $priorDockerAlias = Get-Alias -Name docker -Scope Global -ErrorAction SilentlyContinue

            # The AUTHORITATIVE proof that -DbOnly resolves NO connection validator, independent of module
            # imports and image availability, is DMS_SCHEMA_TOOL_PATH: it is pointed at a unique NONEXISTENT
            # path, so the real Resolve-DmsSchemaTool (finding 4: an explicit path must exist or resolution
            # throws hard, never masked by the image fallback) fails on ANY attempted resolution - even if a
            # regression re-imported bootstrap-schema-tool with -Force and overwrote the shadows below. The
            # throwing global shadows remain as additional defense (-DbOnly never imports the module, so they
            # are not overwritten on the happy path). Snapshot/restore any pre-existing shadow definitions.
            $priorResolveSchema = if (Test-Path -LiteralPath function:global:Resolve-DmsSchemaTool) { (Get-Item -LiteralPath function:global:Resolve-DmsSchemaTool).ScriptBlock } else { $null }
            $priorResolveValidator = if (Test-Path -LiteralPath function:global:Resolve-DmsConnectionValidator) { (Get-Item -LiteralPath function:global:Resolve-DmsConnectionValidator).ScriptBlock } else { $null }

            $caught = $null
            $resolvedDocker = $null
            $derivedCreated = $false
            try {
                if ($null -ne $priorDockerFn) { Remove-Item -LiteralPath function:global:docker -Force }
                if ($null -ne $priorDockerAlias) { Remove-Alias -Name docker -Scope Global -Force }
                $env:DMS_F7_DOCKER_CAPTURE = $captureFile
                $env:DMS_SCHEMA_TOOL_PATH = Join-Path $script:f7Dir ("no-such-tool-" + [guid]::NewGuid().ToString("N") + ".exe")
                $env:PATH = $script:f7ShimDir + [System.IO.Path]::PathSeparator + $priorPath
                function global:Resolve-DmsSchemaTool { throw "Resolve-DmsSchemaTool must not run on the -DbOnly path" }
                function global:Resolve-DmsConnectionValidator { throw "Resolve-DmsConnectionValidator must not run on the -DbOnly path" }

                # FAIL CLOSED before invoking the script. 'docker' must resolve to the shim EXECUTABLE so both
                # the '& docker' calls and the Process readiness probe hit it. Resolution is UNFILTERED
                # (Get-Command with NO -CommandType) so it reflects exactly what '& docker' will run: a
                # -CommandType Application filter would skip a lingering function/alias shadow and still return
                # the real docker.exe, passing the guard while PowerShell actually invoked the shadow. The
                # resolved command must therefore BE an Application whose normalized full path matches the shim
                # exactly (case-insensitively only on Windows); a function/alias shadow, a real client, or no
                # docker at all throws here, before the script is ever invoked (so it can never start anything).
                $expectedShim = [System.IO.Path]::GetFullPath((Join-Path $script:f7ShimDir $(if ($IsWindows) { "docker.exe" } else { "docker" })))
                $resolvedCommand = Get-Command docker -ErrorAction Stop
                $resolvedDocker = if ($resolvedCommand.CommandType -eq 'Application') { [System.IO.Path]::GetFullPath($resolvedCommand.Source) } else { [string]$resolvedCommand.CommandType }
                $pathComparison = if ($IsWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
                if ($resolvedCommand.CommandType -ne 'Application' -or -not [string]::Equals($resolvedDocker, $expectedShim, $pathComparison)) {
                    throw "Refusing to invoke $ScriptName -DbOnly: 'docker' resolved to a $($resolvedCommand.CommandType) ('$($resolvedCommand.Source)'), not the shim executable '$expectedShim'. A shadow or real Docker client could run before an assertion fails."
                }

                & (Join-Path $script:composeRoot $ScriptName) -DbOnly -EnvironmentFile $envFile -DatabaseEngine postgresql *> $outputFile
            }
            catch {
                $caught = $_
            }
            finally {
                # A failed regression that materialized topology would have written the derived file into the
                # repo; record whether it exists (for the assertion) and remove it to keep the tree clean.
                $derivedCreated = Test-Path -LiteralPath $derivedTarget
                if ($derivedCreated) { Remove-Item -LiteralPath $derivedTarget -Force -ErrorAction SilentlyContinue }

                if ($null -ne $priorResolveSchema) { Set-Item -LiteralPath function:global:Resolve-DmsSchemaTool -Value $priorResolveSchema } elseif (Test-Path -LiteralPath function:global:Resolve-DmsSchemaTool) { Remove-Item -LiteralPath function:global:Resolve-DmsSchemaTool -Force }
                if ($null -ne $priorResolveValidator) { Set-Item -LiteralPath function:global:Resolve-DmsConnectionValidator -Value $priorResolveValidator } elseif (Test-Path -LiteralPath function:global:Resolve-DmsConnectionValidator) { Remove-Item -LiteralPath function:global:Resolve-DmsConnectionValidator -Force }
                if ($null -ne $priorDockerFn) { Set-Item -LiteralPath function:global:docker -Value $priorDockerFn } elseif (Test-Path -LiteralPath function:global:docker) { Remove-Item -LiteralPath function:global:docker -Force }
                if ($null -ne $priorDockerAlias) { Set-Alias -Name docker -Value $priorDockerAlias.Definition -Option $priorDockerAlias.Options -Description $priorDockerAlias.Description -Scope Global -Force }
                $env:PATH = $priorPath
                if ($null -eq $priorCapture) { Remove-Item -LiteralPath Env:DMS_F7_DOCKER_CAPTURE -ErrorAction SilentlyContinue } else { $env:DMS_F7_DOCKER_CAPTURE = $priorCapture }
                if ($null -eq $priorSchemaToolPath) { Remove-Item -LiteralPath Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue } else { $env:DMS_SCHEMA_TOOL_PATH = $priorSchemaToolPath }
                $global:LASTEXITCODE = $priorLastExit
            }

            return [pscustomobject]@{
                Error          = $caught
                DockerCalls    = if (Test-Path -LiteralPath $captureFile) { @(Get-Content -LiteralPath $captureFile) } else { @() }
                OutputText     = if (Test-Path -LiteralPath $outputFile) { (Get-Content -LiteralPath $outputFile -Raw) } else { "" }
                EnvFile        = $envFile
                DerivedCreated = $derivedCreated
                ResolvedDocker = $resolvedDocker
            }
        }
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:f7Dir) {
            Remove-Item -Recurse -Force $script:f7Dir -ErrorAction SilentlyContinue
        }
    }

    It "<Script> -DbOnly starts only the database, probes readiness, and resolves no validator or topology on a minimal PostgreSQL environment" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        $result = Invoke-DbOnlyStartWithDockerExeShim -ScriptName $Script
        $joined = ($result.DockerCalls -join " `n")
        $upLines = @($result.DockerCalls | Where-Object { $_ -match '\bup\b' })

        # Returns successfully. This also proves NO validator was resolved: DMS_SCHEMA_TOOL_PATH points at a
        # nonexistent path, so any attempted resolution would throw hard (finding 4) and surface as this
        # terminating error - the authoritative proof, independent of module imports and image availability.
        # (The throwing global resolver shadows are only additional defense.)
        $result.Error | Should -BeNullOrEmpty -Because "$Script -DbOnly must complete on a minimal PostgreSQL environment without resolving a validator or materializing CMS topology"

        # 'docker' resolved to the executable shim (function/alias shadows removed, shim first on PATH), so
        # both the '& docker' calls and the Process readiness probe were genuinely intercepted.
        $result.ResolvedDocker | Should -Match ([regex]::Escape($script:f7ShimDir)) -Because "$Script -DbOnly must resolve 'docker' to the executable shim, not a real client or a shadow"

        # Reaches 'docker compose up ... db' and performs the pg_isready readiness probe (the Process-based
        # probe that only an executable shim can intercept).
        @($upLines).Count | Should -BeGreaterThan 0 -Because "$Script -DbOnly must start the database container"
        $joined | Should -Match 'pg_isready' -Because "$Script -DbOnly must probe PostgreSQL readiness"

        # Materializes NO topology-derived env file: the repository .derived target was never written, and
        # every 'up' uses the original minimal env, never a .derived path.
        $result.DerivedCreated | Should -BeFalse -Because "$Script -DbOnly must not write a topology-derived env file into the repo"
        $joined | Should -Not -Match '\.derived' -Because "$Script -DbOnly must not reference a topology-derived env file"

        # Starts ONLY the 'db' service - no CMS/config, DMS, Keycloak, Kafka, or Swagger service - by
        # inspecting the service arguments after 'up' (the file/-p arguments contain 'dms', so the whole line
        # cannot be matched for service names).
        foreach ($line in $upLines) {
            $line | Should -Match ([regex]::Escape($result.EnvFile)) -Because "the -DbOnly 'up' must use the original env file, not a derived one"
            $tokens = @($line -split '\s+' | Where-Object { $_ -ne '' })
            $upIndex = [array]::IndexOf($tokens, 'up')
            $services = @($tokens[($upIndex + 1)..($tokens.Count - 1)] | Where-Object { $_ -notmatch '^-' })
            $services | Should -Be @('db') -Because "$Script -DbOnly must start only the 'db' service, got: $($services -join ',')"
        }

        # No image-validator run, and no Keycloak/OpenIddict identity initialization.
        $joined | Should -Not -Match 'run .*--network none' -Because "$Script -DbOnly resolves no image validator"
        $joined | Should -Not -Match 'setup-keycloak' -Because "$Script -DbOnly initializes no identity provider"
        $result.OutputText | Should -Not -Match 'Configuration Service|Keycloak|OpenIddict' -Because "$Script -DbOnly starts no CMS or identity service"
    }
}

Describe "Published preflight rejects an invalid separate-topology replacement before any destructive action (behavioral)" {
    BeforeAll {
        $script:preflightDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-preflight-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:preflightDir -Force | Out-Null

        # A fake 'connection validate' tool: it consumes (never echoes) the connection string on stdin and
        # reports the CMS database the contract expects, so the contract resolution succeeds and execution
        # reaches the datastore-target collision check. This stands in for the real api-schema-tools verb
        # here; the provider-oracle tests exercise the verb itself.
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
        # script. The stub returns the canned resolution for 'docker compose config' and exits 0 for every
        # other invocation; every invocation is captured so the caller can prove which Docker operations ran.
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

Describe "A successful preflight performs only support operations before any stack-lifecycle mutation (finding 9)" {
    # F9: preflight no longer claims 'No Docker action was taken'. It may resolve Compose configuration,
    # resolve/reuse/build a host validator tool, pull the selected published image, and run an isolated
    # '--network none' validator container - some of which write persistent state (a published tool, a pulled
    # image), so they are preflight SUPPORT operations, not "read-only". What matters is that they all complete
    # before any stack-lifecycle mutation. These tests prove a SUCCESSFUL preflight on both validator paths:
    # the host tool (start-local and start-published with a configured DMS_SCHEMA_TOOL_PATH) and the published
    # image validator. Each permits the documented support operations and rejects every forbidden mutation.
    BeforeAll {
        # This suite mocks Resolve-DmsSchemaTool inside bootstrap-schema-tool; require exactly one module of
        # that name (isolated-repo suites can leak a temp-workspace copy). Drop any loaded copy, then import
        # the canonical modules.
        Get-Module bootstrap-schema-tool, env-utility | Remove-Module -Force -ErrorAction SilentlyContinue
        Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:composeRoot "bootstrap-schema-tool.psm1") -Force

        $script:f9Dir = Join-Path ([System.IO.Path]::GetTempPath()) ("dms-f9-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $script:f9Dir -Force | Out-Null

        # A fake host validator: it consumes (never echoes) the connection string on stdin and reports the CMS
        # database the separate-topology contract expects, so the contract resolution succeeds and the
        # preflight completes. Stands in for the real api-schema-tools verb (exercised end to end by the
        # provider-oracle and distribution suites).
        $script:f9Validator = Join-Path $script:f9Dir "fake-connection-validator.ps1"
        @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
$null = @($input)
Write-Output '{"valid":true,"database":"edfi_configurationservice"}'
exit 0
'@ | Set-Content -LiteralPath $script:f9Validator -Encoding utf8

        # A separate-topology env whose DMS_CONFIG_DATABASE_NAME already equals the separate literal, so
        # topology materialization is an idempotent no-op that writes no .derived file into the repo.
        $script:f9Env = Join-Path $script:f9Dir "preflight.env"
        (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example")) |
            Where-Object { $_ -notmatch '^DMS_CONFIG_DATABASE_NAME=' } |
            Set-Content -LiteralPath $script:f9Env
        Add-Content -LiteralPath $script:f9Env -Value 'DMS_CONFIG_DATABASE_NAME=edfi_configurationservice'

        # Separate-topology compose resolution: the datastore anchor (edfi_datamanagementservice) differs from
        # the CMS database (edfi_configurationservice), so the separate contract passes.
        $script:f9ComposeJson = '{"services":{"config":{"environment":{"AppSettings__Datastore":"postgresql","DatabaseSettings__DatabaseConnection":"host=dms-postgresql;port=5432;username=postgres;password=x;database=edfi_configurationservice;"}},"dms":{"image":"local/edfi-data-management-service","environment":{"AppSettings__Datastore":"postgresql","DATABASE_CONNECTION_STRING_ADMIN":"host=dms-postgresql;port=5432;username=postgres;password=x;database=edfi_admin;"}},"db":{"environment":{"POSTGRES_DB_NAME":"edfi_datamanagementservice"}}}}'

        function script:Invoke-HostToolPreflightWithDockerStub {
            param([string]$ScriptName, [hashtable]$ScriptParams)

            $captureFile = Join-Path $script:f9Dir ("docker-" + [guid]::NewGuid().ToString("N") + ".log")
            $outputFile = Join-Path $script:f9Dir ("output-" + [guid]::NewGuid().ToString("N") + ".log")

            $dockerFn = if (Test-Path -LiteralPath function:global:docker) { (Get-Item -LiteralPath function:global:docker).ScriptBlock } else { $null }
            $priorCapture = $env:DMS_F9_DOCKER_CAPTURE
            $priorToolPath = $env:DMS_SCHEMA_TOOL_PATH
            $priorComposeJson = $env:DMS_F9_COMPOSE_JSON
            $priorLastExitCode = $global:LASTEXITCODE

            $env:DMS_F9_DOCKER_CAPTURE = $captureFile
            $env:DMS_SCHEMA_TOOL_PATH = $script:f9Validator
            $env:DMS_F9_COMPOSE_JSON = $script:f9ComposeJson

            # Global-scope stub (the compose-config call originates inside the env-utility module, whose command
            # resolution sees only the module and the global scope). It returns the canned resolution for
            # 'docker compose config' and exits 0 for every other invocation; all invocations are captured.
            function global:docker {
                Add-Content -LiteralPath $env:DMS_F9_DOCKER_CAPTURE -Value ($args -join " ")
                if ($args -contains "config") {
                    Write-Output $env:DMS_F9_COMPOSE_JSON
                }
                $global:LASTEXITCODE = 0
            }

            $caught = $null
            try {
                & (Join-Path $script:composeRoot $ScriptName) @ScriptParams *> $outputFile
            }
            catch {
                $caught = $_
            }
            finally {
                if ($null -ne $dockerFn) { Set-Item -LiteralPath function:global:docker -Value $dockerFn } else { Remove-Item -LiteralPath function:global:docker -ErrorAction SilentlyContinue }
                if ($null -eq $priorCapture) { Remove-Item -LiteralPath Env:DMS_F9_DOCKER_CAPTURE -ErrorAction SilentlyContinue } else { $env:DMS_F9_DOCKER_CAPTURE = $priorCapture }
                if ($null -eq $priorToolPath) { Remove-Item -LiteralPath Env:DMS_SCHEMA_TOOL_PATH -ErrorAction SilentlyContinue } else { $env:DMS_SCHEMA_TOOL_PATH = $priorToolPath }
                if ($null -eq $priorComposeJson) { Remove-Item -LiteralPath Env:DMS_F9_COMPOSE_JSON -ErrorAction SilentlyContinue } else { $env:DMS_F9_COMPOSE_JSON = $priorComposeJson }
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
        if (Test-Path -LiteralPath $script:f9Dir) {
            Remove-Item -Recurse -Force $script:f9Dir -ErrorAction SilentlyContinue
        }
    }

    It "<Script> completes a host-tool preflight running only compose config, with no stack-lifecycle mutation" -ForEach @(
        @{ Script = 'start-local-dms.ps1' }
        @{ Script = 'start-published-dms.ps1' }
    ) {
        # DMS_SCHEMA_TOOL_PATH points at a host validator, so Resolve-DmsSchemaTool /
        # Resolve-DmsConnectionValidator resolve a HostExe: the connection string is validated on-host (no
        # 'docker run'), the contract resolves for a valid separate topology, and the preflight completes.
        $result = Invoke-HostToolPreflightWithDockerStub -ScriptName $Script -ScriptParams @{
            PreflightOnly          = $true
            SeparateConfigDatabase = $true
            EnvironmentFile        = $script:f9Env
            DatabaseEngine         = "postgresql"
        }
        $joined = ($result.DockerCalls -join " `n")

        $result.Error | Should -BeNullOrEmpty -Because "$Script host-tool preflight of a valid separate topology must succeed"
        $result.OutputText | Should -Match "Preflight validation complete"
        # The emitted message must DOCUMENT the preflight support operations, not merely announce completion.
        $result.OutputText | Should -Match "Compose configuration" -Because "$Script must state that preflight resolves Compose configuration"
        $result.OutputText | Should -Match "resolve, reuse, or build a host validator tool" -Because "$Script must state that the host validator tool may be resolved, reused, or built"

        # Permitted preflight support operations only: compose config (read-only) and host-tool validation. No
        # stack-lifecycle mutation, and no image validator run (the host tool validates on-host).
        $joined | Should -Match "compose .*config" -Because "compose config is a permitted preflight support operation"
        $joined | Should -Not -Match "compose .*\bup\b" -Because "no container may be started in preflight"
        $joined | Should -Not -Match "compose .*\bdown\b" -Because "no teardown may run in preflight"
        $joined | Should -Not -Match "compose .*\bbuild\b" -Because "no image build may run in preflight"
        $joined | Should -Not -Match "\brun\b .*--network none" -Because "the host tool validates on-host; no image validator runs"
        $joined | Should -Not -Match "network create" -Because "no network may be created in preflight"
        $joined | Should -Not -Match "volume" -Because "no volume may be created or deleted in preflight"
    }

    It "the published image-validator path validates the connection offline (docker run --network none) with no stack-lifecycle mutation" {
        # On a clean Docker/PowerShell-only host the validator runs inside the DMS image. That descriptor
        # cannot be forced through a full start-script invocation - the script's own 'Import-Module -Force'
        # drops a Pester module mock, and the SDK build makes a natural host-resolution failure
        # non-deterministic (a prior suite can leave a prebuilt tool). So the published path is exercised at
        # the exact seam the preflight delegates to: with no host tool available Resolve-DmsConnectionValidator
        # yields a DockerImage descriptor, and Resolve-EffectiveConfigRuntimeContract - the same contract call
        # the preflight makes - validates through it. The verb must run offline via 'docker run --network
        # none', and no stack-lifecycle mutation may occur.
        $captureFile = Join-Path $script:f9Dir ("image-" + [guid]::NewGuid().ToString("N") + ".log")
        $priorCapture = $env:DMS_F9_DOCKER_CAPTURE
        $priorLastExitCode = $global:LASTEXITCODE
        $dockerFn = if (Test-Path -LiteralPath function:global:docker) { (Get-Item -LiteralPath function:global:docker).ScriptBlock } else { $null }
        $env:DMS_F9_DOCKER_CAPTURE = $captureFile

        Mock -ModuleName 'bootstrap-schema-tool' Resolve-DmsSchemaTool { throw "In-repo api-schema-tools tool not found." }

        try {
            function global:docker {
                Add-Content -LiteralPath $env:DMS_F9_DOCKER_CAPTURE -Value ($args -join " ")
                if ($args -contains "run") {
                    Write-Output '{"valid":true,"database":"edfi_datamanagementservice"}'
                }
                $global:LASTEXITCODE = 0
            }

            $validator = Resolve-DmsConnectionValidator -RequestedPath "" -DmsImage "edfialliance/ed-fi-api:test"
            $validator.Kind | Should -Be 'DockerImage' -Because "no host tool resolves, so the published image bundles the validator"

            $contract = Resolve-EffectiveConfigRuntimeContract `
                -InfrastructureEngine "postgresql" `
                -ConfigServiceIncluded $true `
                -DmsServiceIncluded $true `
                -SeparateConfigDatabase:$false `
                -ResolvedConfigProvider "postgresql" `
                -ResolvedDmsProvider "postgresql" `
                -ResolvedCmsConnectionString "host=dms-postgresql;port=5432;username=postgres;password=x;database=edfi_datamanagementservice;" `
                -SchemaToolPath $validator `
                -ResolvedMssqlSaPassword $null `
                -ResolvedTopologyDatastoreDatabaseName "edfi_datamanagementservice"

            $contract.CmsDatabaseName | Should -Be "edfi_datamanagementservice" -Because "the shared-topology CMS database is the datastore anchor, resolved through the image validator"
        }
        finally {
            if ($null -ne $dockerFn) { Set-Item -LiteralPath function:global:docker -Value $dockerFn } else { Remove-Item -LiteralPath function:global:docker -ErrorAction SilentlyContinue }
            if ($null -eq $priorCapture) { Remove-Item -LiteralPath Env:DMS_F9_DOCKER_CAPTURE -ErrorAction SilentlyContinue } else { $env:DMS_F9_DOCKER_CAPTURE = $priorCapture }
            $global:LASTEXITCODE = $priorLastExitCode
        }

        $joined = (@(Get-Content -LiteralPath $captureFile) -join " `n")
        # Permitted preflight support operation: an offline '--network none' validator container. No
        # stack-lifecycle mutation.
        $joined | Should -Match "\brun\b .*--network none" -Because "the verb only parses the string, so it runs offline via 'docker run --network none'"
        $joined | Should -Match "edfialliance/ed-fi-api:test" -Because "the verb runs inside the resolved DMS image"
        $joined | Should -Not -Match "compose .*\bup\b" -Because "no container may be started"
        $joined | Should -Not -Match "compose .*\bdown\b" -Because "no teardown may run"
        $joined | Should -Not -Match "network create" -Because "no network may be created"
        $joined | Should -Not -Match "volume" -Because "no volume may be created or deleted"
    }
}
