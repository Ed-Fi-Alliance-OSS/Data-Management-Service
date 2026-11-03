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

        It "retains CDC snapshots and nested source history after governed volume cleanup" {
            $workspace = Join-Path $TestDrive 'retained-cdc-workspace'
            New-Item -ItemType Directory (Join-Path $workspace 'cdc-runtime') -Force | Out-Null
            New-Item -ItemType Directory (Join-Path $workspace 'custom-state') -Force | Out-Null
            'historical' | Set-Content (Join-Path $workspace 'custom-state/source-history.json')
            Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath $workspace
            Get-Content (Join-Path $workspace 'custom-state/source-history.json') | Should -Be 'historical'
        }

        It "blocks recursive cleanup for inventoried nested state after generated files are gone" {
            $compose = Join-Path $TestDrive 'nested-state-compose'
            $workspace = Join-Path $compose '.bootstrap'
            $state = Join-Path $workspace 'private-source-state'
            New-Item -ItemType Directory $state -Force | Out-Null
            'historical' | Set-Content (Join-Path $state 'history.json')
            $inventory = Join-Path $compose '.cdc-deployments'
            [IO.Directory]::CreateDirectory($inventory, [IO.UnixFileMode]448) | Out-Null
            $path = Join-Path $inventory 'dms-local.json'
            @{ Version = 1; Project = 'dms-local'; Phase = 'RuntimeCleanup'; Entries = @(@{ StatePath = $state }) } | ConvertTo-Json -Depth 5 | Set-Content $path
            [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384)
            { Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath $workspace } | Should -Throw '*surviving protected source-state root*'
            Test-Path (Join-Path $workspace 'cdc-runtime') | Should -BeFalse
            Get-Content (Join-Path $state 'history.json') | Should -Be 'historical'
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

        It 'cleans private API CDC artifacts only after every down succeeds (failure=<FailDown>)' -ForEach @(
            @{ FailDown = $false }, @{ FailDown = $true }
        ) {
            $root = Join-Path $script:composeRoot '.cdc-deployments'
            $null = [IO.Directory]::CreateDirectory($root, [IO.UnixFileMode]448)
            $id = [guid]::NewGuid().ToString('N')
            $paths = @('http', 'handoff' | ForEach-Object { Join-Path $root "api-e2e-$id.$_.json" })
            $unrelated = Join-Path $root 'retained-source.json'
            foreach ($path in $paths + @($unrelated)) {
                '{}' | Set-Content -LiteralPath $path
                [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384)
            }
            if ($FailDown) {
                Mock -ModuleName e2e-teardown Invoke-ComposeProjectTeardown {
                    if ($StartScript -match 'start-published-dms\.ps1$') { throw 'Governed teardown incomplete' }
                }
            }
            try {
                $action = { Invoke-E2EEngineAwareTeardown -DatabaseEngine postgresql -EnvironmentFile '.env.e2e' -ComposeRoot $script:composeRoot -SkipLocalImageRemoval }
                if ($FailDown) { $action | Should -Throw '*Governed teardown incomplete*' }
                else { & $action | Out-Null }
                foreach ($path in $paths) { (Test-Path -LiteralPath $path) | Should -Be $FailDown }
                Test-Path -LiteralPath $unrelated | Should -BeTrue
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force }
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
        $sourceComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        # Execute unchanged primitives in an isolated directory: retained real CDC inventory must
        # never change a recording-Docker test into a managed lifecycle invocation.
        $script:realComposeRoot = Join-Path $TestDrive 'compose'
        New-Item -ItemType Directory -Path $script:realComposeRoot | Out-Null
        Get-ChildItem -LiteralPath $sourceComposeRoot -File -Force |
            Where-Object { $_.Name -match '\.(ps1|psm1|yml)$|^\.env' } |
            Copy-Item -Destination $script:realComposeRoot
        $script:standardE2EEnvironmentFile = Join-Path $script:realComposeRoot ".env.e2e"

        # `docker compose down -v` removes the named volumes of the services in the composed set only,
        # so a compose file omitted from the down set takes its volumes with it. Teardown resolves the
        # identity provider from the environment file, which need not name the provider the running
        # stack was started with, so the down set must not gate keycloak.compose.yml on that value.
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
    [Parameter(Mandatory)] [string] $LogPath,
    # DMS-1652: also render each down/stop client-side, run the shared E2E teardown instead of the
    # primitive alone, forward an identity provider, or fail one project's down.
    [switch] $Render,
    [switch] $E2ETeardown,
    [string] $IdentityProvider,
    [string] $FailProject
)

$ErrorActionPreference = "Stop"

$global:GuardedNames = @('CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY', 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY', 'DATABASE_CONNECTION_STRING_ADMIN')
$global:FailProject = $FailProject
$global:RealDocker = if ($Render) { (Get-Command docker -CommandType Application | Select-Object -First 1).Source } else { $null }

function global:Get-GuardedEnvironment {
    $state = [ordered]@{}
    foreach ($name in $global:GuardedNames) { $state[$name] = [Environment]::GetEnvironmentVariable($name) }
    $state
}

# Global so the shared teardown module resolves it too.
function global:docker {
    $arguments = @($args | ForEach-Object { $_ })
    Add-Content -LiteralPath $env:DMS_TEARDOWN_CAPTURE_LOG -Value ($arguments -join " ")
    $verbIndex = -1
    for ($i = 0; $i -lt $arguments.Count; $i++) { if ($arguments[$i] -in 'down', 'stop') { $verbIndex = $i; break } }
    if ($global:RealDocker -and $arguments[0] -eq 'compose' -and $verbIndex -gt 0) {
        # Render the exact definition the destructive command would act on, without running it.
        # Only names are recorded; the rendered configuration holds secret values.
        $definition = $arguments[0..($verbIndex - 1)]
        $project = $definition[[Array]::IndexOf([string[]]$definition, '-p') + 1]
        $ErrorActionPreference = 'Continue'
        $rendered = & $global:RealDocker @definition config --format json 2>$null
        $renderExitCode = $LASTEXITCODE
        $config = if ($renderExitCode -eq 0) { ($rendered | Out-String) | ConvertFrom-Json } else { $null }
        $namesOf = { param($Section) if ($config -and $config.PSObject.Properties[$Section]) { @($config.$Section.PSObject.Properties.Name) } else { @() } }
        $record = [ordered]@{
            Project        = $project
            Verb           = $arguments[$verbIndex]
            Arguments      = $arguments[$verbIndex..($arguments.Count - 1)] -join ' '
            RenderExitCode = $renderExitCode
            Services       = & $namesOf 'services'
            Networks       = & $namesOf 'networks'
            Volumes        = & $namesOf 'volumes'
            Environment    = Get-GuardedEnvironment
        }
        Add-Content -LiteralPath "$($env:DMS_TEARDOWN_CAPTURE_LOG).renders.jsonl" -Value ($record | ConvertTo-Json -Compress -Depth 4)
        $global:LASTEXITCODE = if ($project -eq $global:FailProject) { 1 } else { 0 }
        return
    }
    $global:LASTEXITCODE = 0
}

$env:DMS_TEARDOWN_CAPTURE_LOG = $LogPath
if (-not $Render) {
    & $StartScript -d -v -EnvironmentFile $EnvironmentFile
    return
}

$teardownError = $null
try {
    if ($E2ETeardown) {
        Import-Module (Join-Path (Split-Path $StartScript) 'e2e-teardown.psm1') -Force
        Invoke-E2EEngineAwareTeardown -EnvironmentFile $EnvironmentFile -ComposeRoot (Split-Path $StartScript) -SkipLocalImageRemoval | Out-Null
    }
    else {
        $parameters = @{ d = $true; v = $true; EnvironmentFile = $EnvironmentFile }
        if ($IdentityProvider) { $parameters.IdentityProvider = $IdentityProvider }
        & $StartScript @parameters
    }
}
catch {
    # Recorded, not rethrown: the cases assert on it, and on the environment it leaves behind.
    $teardownError = $_.Exception.Message
}

# A later startup in this process must still meet the guards: render it after teardown.
$startupErrorFile = "$LogPath.startup.stderr"
Push-Location (Split-Path $StartScript)
try {
    $ErrorActionPreference = 'Continue'
    & $global:RealDocker compose -f postgresql.compose.yml -f published-dms.compose.yml -f published-config.compose.yml --env-file $EnvironmentFile config --quiet 2> $startupErrorFile
    $startupExitCode = $LASTEXITCODE
}
finally { Pop-Location }

[ordered]@{
    TeardownError         = $teardownError
    EnvironmentAfter      = Get-GuardedEnvironment
    StartupRenderExitCode = $startupExitCode
    StartupRenderError    = (Get-Content -Raw -LiteralPath $startupErrorFile) ?? ''
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$LogPath.outcome.json"
'@
    }

    It "keeps the standard E2E environment file on self-contained identity (precondition)" {
        Get-Content -LiteralPath $script:standardE2EEnvironmentFile |
            Should -Contain "CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained"
    }

    It "includes keycloak.compose.yml in the <Project> down set even when the environment file selects self-contained identity" -ForEach @(
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
        $downCommands[0] | Should -Match "-f keycloak\.compose\.yml" `
            -Because "keycloak.compose.yml carries the dms-keycloak named volume, so omitting it leaks ${Project}_dms-keycloak past down -v"
    }

    # DMS-1652: the published files require three values with ${VAR:?}. Teardown composes
    # published-config.compose.yml for every stack, so these cases render each intercepted down
    # with the real Docker CLI (client-side only) to prove removal works without them, still covers
    # the complete definition, and leaves nothing behind that would satisfy a later startup.
    Context "with published-file required values absent (DMS-1652)" {
        BeforeAll {
            $script:guardedNames = @('CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY', 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY', 'DATABASE_CONNECTION_STRING_ADMIN')
            $script:placeholder = 'teardown-placeholder-not-used'
            # Cleared for every case, with the names the cases' env files reference, so the runner's
            # environment can never supply one of them.
            $script:clearedNames = $script:guardedNames + @('DMS1652_LATER_REFERENCE', 'DMS1652_ABSENT_REFERENCE')
            # The shared teardown's cleanup step loads these from the compose root's parent.
            $engCopy = Split-Path $script:realComposeRoot
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../schema-package-utility.psm1') -Destination $engCopy
            New-Item -ItemType Directory -Path (Join-Path $engCopy 'ci') -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../ci/cdc-runbook-diagnostics.ps1') -Destination (Join-Path $engCopy 'ci')

            function Write-TeardownEnvironmentFile {
                # Set keeps its order (pass [ordered]), because a reference sees only earlier lines.
                param([string] $Name, [string[]] $Remove, [System.Collections.IDictionary] $Set = @{})
                $lines = @(Get-Content -LiteralPath $script:standardE2EEnvironmentFile)
                foreach ($key in @($Remove) + @($Set.Keys)) {
                    $lines = @($lines | Where-Object { $_ -notmatch "^\s*(export\s+)?$([regex]::Escape($key))=" })
                }
                foreach ($key in $Set.Keys) { $lines += "$key=$($Set[$key])" }
                $path = Join-Path $TestDrive $Name
                # LF like the committed env files, so no carriage return reaches a Compose value.
                [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
                $path
            }

            function Invoke-RenderedTeardown {
                # StartupFiles, when given, are rendered client-side first, in the same cleared environment.
                param([string] $Name, [string] $EnvironmentFile, [string[]] $Arguments = @(), [hashtable] $Ambient = @{}, [string[]] $StartupFiles = @())
                $logPath = Join-Path $TestDrive "$Name.log"
                # The child inherits this process environment: clear the guarded names so a
                # developer's exported value cannot change the case, and restore them afterwards.
                $saved = @{}
                $startupExitCode = $null
                foreach ($name in $script:clearedNames) {
                    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
                    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                }
                try {
                    foreach ($name in $Ambient.Keys) { [Environment]::SetEnvironmentVariable($name, $Ambient[$name]) }
                    if ($StartupFiles) {
                        $fileArguments = foreach ($file in $StartupFiles) { '-f'; $file }
                        Push-Location $script:realComposeRoot
                        try { & docker compose @fileArguments --env-file $EnvironmentFile config --quiet 2>$null; $startupExitCode = $LASTEXITCODE }
                        finally { Pop-Location }
                    }
                    & pwsh -NoProfile -File $script:captureScript `
                        -StartScript (Join-Path $script:realComposeRoot 'start-published-dms.ps1') `
                        -EnvironmentFile $EnvironmentFile -LogPath $logPath -Render @Arguments | Out-Null
                    $exitCode = $LASTEXITCODE
                }
                finally {
                    foreach ($name in $script:clearedNames) {
                        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
                        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
                    }
                }
                [pscustomobject]@{
                    ExitCode = $exitCode
                    StartupExitCode = $startupExitCode
                    Commands = @(if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath })
                    Renders  = @(if (Test-Path -LiteralPath "$logPath.renders.jsonl") { Get-Content -LiteralPath "$logPath.renders.jsonl" | ForEach-Object { $_ | ConvertFrom-Json } })
                    Outcome  = Get-Content -Raw -LiteralPath "$logPath.outcome.json" | ConvertFrom-Json
                }
            }

            function Assert-CompletePublishedDown {
                param($Render)
                $Render.Project | Should -Be 'dms-published'
                $Render.Arguments | Should -BeExactly 'down --remove-orphans -v'
                $Render.RenderExitCode | Should -Be 0 -Because 'Compose must accept the definition the down acts on'
                $Render.Services | Should -Contain 'dms'
                $Render.Services | Should -Contain 'config' -Because 'published-config.compose.yml stays in the down set'
                $Render.Networks | Should -Contain 'cms-proxy' -Because 'the paired proxy network is removed only when dms-cms-proxy.yml is composed'
                $Render.Volumes | Should -Contain 'dms-postgresql'
                $Render.Volumes | Should -Contain 'dms-keycloak'
            }

            function Assert-NothingLeaked {
                # MissingVariable is a pattern: with several values absent, Compose may name any of them first.
                param($Outcome, [string] $MissingVariable)
                foreach ($name in $script:guardedNames) {
                    $Outcome.EnvironmentAfter.$name | Should -BeNullOrEmpty -Because "teardown must restore $name to absent"
                }
                $Outcome.StartupRenderExitCode | Should -Not -Be 0 -Because 'a later startup must still fail the guard'
                $Outcome.StartupRenderError | Should -Match "required variable ($MissingVariable) is missing a value"
            }
        }

        It "removes a Keycloak deployment that never set the identity key" {
            $environmentFile = Write-TeardownEnvironmentFile -Name 'keycloak-no-identity-key.env' `
                -Remove 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY' -Set @{ CONFIGURATION_SERVICE_IDENTITY_PROVIDER = 'keycloak' }

            $result = Invoke-RenderedTeardown -Name 'keycloak-no-identity-key' -EnvironmentFile $environmentFile -Arguments @('-IdentityProvider', 'keycloak')

            $result.ExitCode | Should -Be 0
            $result.Outcome.TeardownError | Should -BeNullOrEmpty
            $result.Renders.Count | Should -Be 1
            Assert-CompletePublishedDown -Render $result.Renders[0]
            # Only the missing value is supplied; the env file's own values are not overridden.
            $result.Renders[0].Environment.CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY | Should -BeExactly $script:placeholder
            $result.Renders[0].Environment.CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY | Should -BeNullOrEmpty
            $result.Renders[0].Environment.DATABASE_CONNECTION_STRING_ADMIN | Should -BeNullOrEmpty
            Assert-NothingLeaked -Outcome $result.Outcome -MissingVariable 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'
        }

        It "runs the shared E2E teardown with an environment file that has none of the three values" {
            $environmentFile = Write-TeardownEnvironmentFile -Name 'local-only.env' -Remove $script:guardedNames

            $result = Invoke-RenderedTeardown -Name 'local-only' -EnvironmentFile $environmentFile -Arguments @('-E2ETeardown')

            $result.ExitCode | Should -Be 0
            $result.Outcome.TeardownError | Should -BeNullOrEmpty
            @($result.Renders | ForEach-Object Project) | Should -Be @('dms-local', 'dms-published')
            $local = $result.Renders[0]
            $local.RenderExitCode | Should -Be 0
            $local.Arguments | Should -BeExactly 'down --remove-orphans -v'
            Assert-CompletePublishedDown -Render $result.Renders[1]
            foreach ($name in $script:guardedNames) {
                $local.Environment.$name | Should -BeNullOrEmpty -Because 'the local files keep their fallbacks and need no placeholder'
                $result.Renders[1].Environment.$name | Should -BeExactly $script:placeholder
            }
            Assert-NothingLeaked -Outcome $result.Outcome -MissingVariable ($script:guardedNames -join '|')
        }

        It "keeps a value the caller exported instead of replacing it" {
            $environmentFile = Write-TeardownEnvironmentFile -Name 'exported-identity-key.env' -Remove 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'

            $result = Invoke-RenderedTeardown -Name 'exported-identity-key' -EnvironmentFile $environmentFile `
                -Ambient @{ CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY = 'caller-exported-value' }

            $result.ExitCode | Should -Be 0
            $result.Renders.Count | Should -Be 1
            Assert-CompletePublishedDown -Render $result.Renders[0]
            $result.Renders[0].Environment.CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY | Should -BeExactly 'caller-exported-value'
            $result.Outcome.EnvironmentAfter.CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY | Should -BeExactly 'caller-exported-value'
            $result.Outcome.StartupRenderExitCode | Should -Be 0 -Because "the caller's own value satisfies the guard, as Compose precedence allows"
        }

        It "restores the environment and reports the failure when the published down fails" {
            $environmentFile = Write-TeardownEnvironmentFile -Name 'failed-down.env' -Remove $script:guardedNames

            $result = Invoke-RenderedTeardown -Name 'failed-down' -EnvironmentFile $environmentFile -Arguments @('-E2ETeardown', '-FailProject', 'dms-published')

            $result.ExitCode | Should -Be 0 -Because 'the capture script records the teardown failure instead of exiting'
            $result.Outcome.TeardownError | Should -Match 'Failed to shut down Docker environment\. Exit code 1'
            @($result.Renders | ForEach-Object Project) | Should -Be @('dms-local', 'dms-published')
            Assert-CompletePublishedDown -Render $result.Renders[1]
            Assert-NothingLeaked -Outcome $result.Outcome -MissingVariable ($script:guardedNames -join '|')
        }

        It "treats a forward reference as missing, as Compose reads the env file line by line" {
            # Compose resolves each line against the lines before it, so the identity key is empty
            # here even though the file declares the name it references.
            $environmentFile = Write-TeardownEnvironmentFile -Name 'forward-reference.env' -Set ([ordered]@{
                CONFIGURATION_SERVICE_IDENTITY_PROVIDER       = 'keycloak'
                CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY = '${DMS1652_LATER_REFERENCE}'
                DMS1652_LATER_REFERENCE                       = 'public-test-sentinel'
            })

            $result = Invoke-RenderedTeardown -Name 'forward-reference' -EnvironmentFile $environmentFile `
                -Arguments @('-IdentityProvider', 'keycloak') `
                -StartupFiles @('postgresql.compose.yml', 'published-dms.compose.yml', 'keycloak.compose.yml')

            $result.StartupExitCode | Should -Be 0 -Because 'the Keycloak deployment without the Configuration Service starts without the identity key'
            $result.ExitCode | Should -Be 0
            $result.Outcome.TeardownError | Should -BeNullOrEmpty
            $result.Renders.Count | Should -Be 1
            Assert-CompletePublishedDown -Render $result.Renders[0]
            $result.Renders[0].Environment.CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY | Should -BeExactly $script:placeholder
            $result.Renders[0].Environment.CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY | Should -BeNullOrEmpty
            $result.Renders[0].Environment.DATABASE_CONNECTION_STRING_ADMIN | Should -BeNullOrEmpty
            Assert-NothingLeaked -Outcome $result.Outcome -MissingVariable 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'
        }

        It "leaves the environment unchanged and never invokes Docker when evaluating the env file fails" {
            # The identity key's own required reference makes the sequential evaluation throw inside
            # the restoring try, before any placeholder is set and before any Compose command runs.
            $environmentFile = Write-TeardownEnvironmentFile -Name 'resolution-failure.env' `
                -Remove 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' `
                -Set @{ CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY = '${DMS1652_ABSENT_REFERENCE:?required}' }

            $result = Invoke-RenderedTeardown -Name 'resolution-failure' -EnvironmentFile $environmentFile

            $result.ExitCode | Should -Be 0 -Because 'the capture script records the teardown failure instead of exiting'
            $result.Outcome.TeardownError | Should -Not -BeNullOrEmpty
            @($result.Commands | Where-Object { $_ -match '^compose .* (down|stop)( |$)' }) | Should -BeNullOrEmpty
            $result.Renders.Count | Should -Be 0
            foreach ($name in $script:guardedNames) {
                $result.Outcome.EnvironmentAfter.$name | Should -BeNullOrEmpty -Because "teardown must restore $name to absent"
            }
        }
    }
}
