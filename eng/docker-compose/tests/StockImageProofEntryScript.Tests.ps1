# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1502: these run the real entry script, in a child process whose docker, dotnet and pwsh are
# shims. What they assert is the script's own sequencing rather than the helpers it calls: that
# nothing decides what the run is about behind the pin, that a refusal happens before anything is
# created, that a failed inventory refuses rather than reading as a clear host, and that a resource
# is claimed before the operation that could create it.
#
# No daemon, no registry, no stack. The shim also means a regression in the ordering cannot start
# something on the developer's machine mid-test.

Describe 'Stock image proof entry script' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $script:entryScript = Join-Path $script:composeRoot 'tests/plugin-deployment/Invoke-StockImagePluginProof.ps1'
        Import-Module (Join-Path $PSScriptRoot 'plugin-deployment/stock-image-proof.psm1') -Force
        $script:committedPin = Join-Path $script:composeRoot 'tests/plugin-deployment/stock-image-pin.json'

        # Every external command the script launches is answered from a plan file, so a scenario
        # says what the host looks like rather than what the helper concluded about it. Anything the
        # plan does not name exits zero with no output, which is the permissive direction: a
        # scenario that refuses has to refuse for the reason it set up.
        $script:wrapperBody = @'
param(
    [Parameter(Mandatory)] [string] $EntryScript,
    [Parameter(Mandatory)] [string] $PinFile,
    [Parameter(Mandatory)] [string] $WorkspaceRoot,
    [Parameter(Mandatory)] [string] $EvidenceRoot,
    [Parameter(Mandatory)] [string] $ShimPlan,
    [Parameter(Mandatory)] [string] $ShimLog,
    [Parameter(Mandatory)] [string] $ResultFile,
    [Parameter(Mandatory)] [string] $BaseEnvironmentFile,
    [string] $AmbientKey,
    [string] $AmbientValue
)

$ErrorActionPreference = 'Stop'
$global:DmsShimPlan = Get-Content -LiteralPath $ShimPlan -Raw | ConvertFrom-Json
$global:DmsShimLog = $ShimLog

function global:Invoke-DmsShim {
    param([string] $Tool, [string[]] $ShimArgs)

    $line = "$Tool $($ShimArgs -join ' ')"
    Add-Content -LiteralPath $global:DmsShimLog -Value $line

    foreach ($rule in $global:DmsShimPlan) {
        if ($line -match $rule.match) {
            $global:LASTEXITCODE = [int]$rule.exitCode
            if (-not [string]::IsNullOrEmpty($rule.output)) { return $rule.output }
            return @()
        }
    }

    $global:LASTEXITCODE = 0
    return @()
}

function global:docker { Invoke-DmsShim -Tool 'docker' -ShimArgs $args }
function global:dotnet { Invoke-DmsShim -Tool 'dotnet' -ShimArgs $args }
function global:pwsh { Invoke-DmsShim -Tool 'pwsh' -ShimArgs $args }

if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
    Set-Item -Path "Env:$AmbientKey" -Value $AmbientValue
}

$failure = '<the script returned without throwing>'
try {
    & $EntryScript -PinFile $PinFile -WorkspaceRoot $WorkspaceRoot -EvidenceRoot $EvidenceRoot -BaseEnvironmentFile $BaseEnvironmentFile
}
catch {
    $failure = $_.Exception.Message
}

[pscustomobject]@{ Failure = $failure } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultFile -Encoding utf8
'@

        function script:New-PublishedPinFile {
            param([string] $Path)

            $pin = [ordered]@{
                status               = 'published'
                edFiApi              = [ordered]@{
                    repository = 'edfialliance/ed-fi-api'
                    tag        = '8.0.1-alpha.0.7'
                    digest     = 'sha256:' + ('a' * 64)
                }
                configurationService = [ordered]@{
                    repository = 'edfialliance/ed-fi-api-configuration-service'
                    digest     = 'sha256:' + ('b' * 64)
                }
                release              = [ordered]@{
                    githubRelease     = 'dms-pre-8.0.1-alpha.0.7'
                    sourceCommit      = 'c' * 40
                    publicationRunUrl = 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/1'
                }
                provisioning         = [ordered]@{
                    schemaToolsPackageVersion = '8.0.1-alpha.0.7'
                    dataStandardVersion       = '5.2'
                    schemaPackages            = @(
                        [ordered]@{
                            name    = 'EdFi.DataStandard52.ApiSchema'
                            version = '1.0.335'
                            feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
                        }
                    )
                }
                contracts            = [ordered]@{
                    pluginsPackageVersion          = '1.0.0'
                    customValidationPackageVersion = '1.0.0'
                }
            }

            Set-Content -LiteralPath $Path -Value ($pin | ConvertTo-Json -Depth 8) -Encoding utf8
        }

        function script:Get-FreePort {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            return $port
        }

        function script:New-PendingPinFile {
            param([string] $Path)

            $pin = [ordered]@{
                status               = 'pending'
                edFiApi              = [ordered]@{ repository = 'edfialliance/ed-fi-api'; tag = $null; digest = $null }
                configurationService = [ordered]@{ repository = 'edfialliance/ed-fi-api-configuration-service'; digest = $null }
                release              = [ordered]@{ githubRelease = $null; sourceCommit = $null; publicationRunUrl = $null }
                provisioning         = [ordered]@{ schemaToolsPackageVersion = $null; dataStandardVersion = $null; schemaPackages = $null }
                contracts            = [ordered]@{ pluginsPackageVersion = $null; customValidationPackageVersion = $null }
            }

            Set-Content -LiteralPath $Path -Value ($pin | ConvertTo-Json -Depth 8) -Encoding utf8
        }

        # The shim answers that make the registry agree with a published pin, so a scenario that is
        # about the host is not also about the descriptor.
        function script:Get-MatchingDescriptorRule {
            return @{
                match    = 'buildx imagetools inspect'
                exitCode = 0
                output   = '{"digest":"sha256:' + ('a' * 64) + '"}'
            }
        }

        function script:Invoke-EntryScript {
            param(
                [string] $PinPath,
                [hashtable[]] $ShimRule = @(),
                [string] $AmbientKey,
                [string] $AmbientValue,
                [int[]] $Port
            )

            $scratch = Join-Path ([IO.Path]::GetTempPath()) "dms1502-entry-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $scratch -Force | Out-Null

            try {
                $wrapper = Join-Path $scratch 'run-entry-under-shim.ps1'
                Set-Content -LiteralPath $wrapper -Value $script:wrapperBody -Encoding utf8

                $planPath = Join-Path $scratch 'shim-plan.json'
                Set-Content -LiteralPath $planPath -Encoding utf8 -Value (
                    ConvertTo-Json -Depth 5 -InputObject @($ShimRule | ForEach-Object { [pscustomobject]$_ }))

                $shimLog = Join-Path $scratch 'shim.log'
                New-Item -ItemType File -Path $shimLog -Force | Out-Null
                $resultFile = Join-Path $scratch 'result.json'
                $evidenceRoot = Join-Path $scratch 'evidence'
                $workspace = Join-Path $scratch 'workspace'

                # A base environment file whose ports are free on this machine, so a scenario about
                # the host is not also about whatever else happens to be listening here. The entry
                # script guards exactly the ports this file declares.
                if ($null -eq $Port -or $Port.Count -lt 3) {
                    $Port = @((Get-FreePort), (Get-FreePort), (Get-FreePort))
                }

                $baseEnvironmentFile = Join-Path $scratch 'base.env'
                $baseLine = @(Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.e2e'))
                $baseLine += "DMS_HTTP_PORTS=$($Port[0])"
                $baseLine += "DMS_CONFIG_ASPNETCORE_HTTP_PORTS=$($Port[1])"
                $baseLine += "POSTGRES_PORT=$($Port[2])"
                Set-Content -LiteralPath $baseEnvironmentFile -Value $baseLine -Encoding utf8

                $argument = @(
                    '-NoProfile', '-File', $wrapper
                    '-BaseEnvironmentFile', $baseEnvironmentFile
                    '-EntryScript', $script:entryScript
                    '-PinFile', ($PinPath ? $PinPath : $script:committedPin)
                    '-WorkspaceRoot', $workspace
                    '-EvidenceRoot', $evidenceRoot
                    '-ShimPlan', $planPath
                    '-ShimLog', $shimLog
                    '-ResultFile', $resultFile
                )

                if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
                    $argument += @('-AmbientKey', $AmbientKey, '-AmbientValue', $AmbientValue)
                }

                # The real pwsh, resolved past the shim this session does not have but the child will.
                $pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
                & $pwshPath @argument 2>&1 | Out-Null

                $evidenceFile = Join-Path $evidenceRoot 'stock-image-plugin-proof.json'

                return [pscustomobject]@{
                    Port             = $Port
                    Failure          = (Test-Path -LiteralPath $resultFile) ? ((Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json).Failure) : '<no result>'
                    ShimCall         = @(Get-Content -LiteralPath $shimLog -ErrorAction SilentlyContinue)
                    Evidence         = (Test-Path -LiteralPath $evidenceFile) ? (Get-Content -LiteralPath $evidenceFile -Raw | ConvertFrom-Json) : $null
                    WorkspaceCreated = (Test-Path -LiteralPath $workspace)
                }
            }
            finally {
                Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'nothing may quietly decide what the run is about' {
        It 'refuses an ambient <_> before touching anything' -ForEach @(
            'SCHEMA_PACKAGES', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_IMAGE_TAG'
        ) {
            # Compose prefers a process variable over --env-file, so an ambient one of these would
            # decide what the proof ran against while the pin described something else.
            $run = Invoke-EntryScript -AmbientKey $_ -AmbientValue 'anything'

            $run.Failure | Should -Match ([regex]::Escape($_))
            $run.Failure | Should -Match 'prefers a process variable'
            $run.ShimCall | Should -HaveCount 0
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'withholds the ambient value rather than printing it' {
            $run = Invoke-EntryScript -AmbientKey 'SCHEMA_PACKAGES' -AmbientValue 'a-value-nobody-should-see'

            $run.Failure | Should -Not -Match 'a-value-nobody-should-see'
            $run.Failure | Should -Match 'withheld'
        }
    }

    Context 'the pin gates everything after it' {
        It 'refuses a pending pin, and touches nothing' {
            # Synthetic, not the committed document: filling the real pin is a required step of this
            # ticket, and a test that needs it to stay pending would fail the lane the moment it was.
            $scratch = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pending-$([guid]::NewGuid().ToString('N')).json"
            New-PendingPinFile -Path $scratch

            try {
                # Somebody asked for the proof, so a pending pin is a failure rather than a skip.
                $run = Invoke-EntryScript -PinPath $scratch

                $run.Failure | Should -Match 'still pending'
                $run.ShimCall | Should -HaveCount 0
                $run.WorkspaceCreated | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue
            }
        }

        It 'validates the committed pin in whichever legitimate state it is in' {
            # What stays true across that change: the shipped document is one the script accepts or
            # refuses for the right reason, not one this test requires to be pending.
            $committed = Get-Content -LiteralPath $script:committedPin -Raw | ConvertFrom-Json
            $run = Invoke-EntryScript -ShimRule @((Get-MatchingDescriptorRule))

            if ($committed.status -ceq 'pending') {
                $run.Failure | Should -Match 'still pending'
                $run.ShimCall | Should -HaveCount 0
            }
            else {
                $run.Failure | Should -Not -Match 'pending'
                $run.Failure | Should -Not -Match 'does not match'
            }
        }

        It 'refuses a malformed pin before touching anything' {
            $scratch = Join-Path ([IO.Path]::GetTempPath()) "dms1502-badpin-$([guid]::NewGuid().ToString('N')).json"
            Set-Content -LiteralPath $scratch -Value 'not json {' -Encoding utf8

            try {
                $run = Invoke-EntryScript -PinPath $scratch

                $run.Failure | Should -Match 'not valid JSON'
                $run.ShimCall | Should -HaveCount 0
            }
            finally {
                Remove-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'the registry descriptor, before the host is touched' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'refuses when the tag resolves to a different digest' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @{ match = 'buildx imagetools inspect'; exitCode = 0; output = '{"digest":"sha256:' + ('9' * 64) + '"}' }
            )

            $run.Failure | Should -Match 'has moved'
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'refuses with an actionable diagnostic when buildx is unavailable' {
            # Not a degraded pass on the digest alone: this proof claims a tag AND a digest.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @{ match = 'buildx imagetools inspect'; exitCode = 1; output = 'unknown command' }
            )

            $run.Failure | Should -Match 'setup-buildx-action'
            $run.WorkspaceCreated | Should -BeFalse
        }
    }

    Context 'the host preflight, and what a refusal must not do' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'refuses when a fixed container name is already in use' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker ps -a --format'; exitCode = 0; output = "dms-postgresql`nhophome-db" }
            )

            $run.Failure | Should -Match 'dms-postgresql'
            $run.Failure | Should -Match 'does not remove anything it did not create'
        }

        It 'creates nothing and tears nothing down when it refuses' {
            # The case that matters on a shared machine: a refused run must leave the stack somebody
            # else is using exactly where it was.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker ps -a --format'; exitCode = 0; output = 'dms-postgresql' }
            )

            $run.WorkspaceCreated | Should -BeFalse
            @($run.ShimCall | Where-Object { $_ -match 'bootstrap-published-dms' }) | Should -HaveCount 0
            @($run.Evidence.commandLog | Where-Object { $_ -like 'own *' }) | Should -HaveCount 0
        }

        It 'refuses when an inventory command fails, rather than reading it as a clear host' {
            # An empty result from a command that failed is not an empty inventory.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker ps -a --format'; exitCode = 1; output = 'Cannot connect to the Docker daemon' }
            )

            $run.Failure | Should -Match 'not an empty one'
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'refuses a leftover volume of its own compose project' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker volume ls'; exitCode = 0; output = 'dms-published_plugins' }
            )

            $run.Failure | Should -Match 'carry state'
        }

        It 'refuses a foreign container attached to the shared external network' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker network ls'; exitCode = 0; output = "bridge`ndms" }
                @{ match = 'docker network inspect dms'; exitCode = 0; output = 'someone-elses-api' }
            )

            $run.Failure | Should -Match 'was not created by this run'
        }

        It 'refuses when the network exists but cannot be inspected' {
            # A permission or daemon error fails the inspect too, and reading that as no neighbours
            # is how a run starts on a host it was never allowed to see.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker network ls'; exitCode = 0; output = 'dms' }
                @{ match = 'docker network inspect dms'; exitCode = 1; output = 'permission denied' }
            )

            $run.Failure | Should -Match 'could not be inspected'
        }

        It 'proceeds when a successful enumeration shows the network is simply absent' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker network ls'; exitCode = 0; output = "bridge`nhost" }
                @{ match = 'docker network inspect dms'; exitCode = 1; output = 'no such network' }
            )

            $run.Failure | Should -Not -Match 'could not be inspected'
            $run.Failure | Should -Not -Match 'not available for the stock-image proof'
        }

        It 'refuses a host port Docker already publishes' {
            $port = @((Get-FreePort), (Get-FreePort), (Get-FreePort))
            $run = Invoke-EntryScript -PinPath $script:pinPath -Port $port -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker ps --format'; exitCode = 0; output = "0.0.0.0:$($port[0])->8080/tcp" }
            )

            $run.Failure | Should -Match "host port $($port[0])"
        }

        It 'ignores a published port this run does not want' {
            $port = @((Get-FreePort), (Get-FreePort), (Get-FreePort))
            $run = Invoke-EntryScript -PinPath $script:pinPath -Port $port -ShimRule @(
                (Get-MatchingDescriptorRule)
                @{ match = 'docker ps --format'; exitCode = 0; output = '0.0.0.0:59999->8080/tcp' }
            )

            $run.Failure | Should -Not -Match 'host port'
        }

        It 'refuses a port held by a non-Docker listener, which Docker cannot see' {
            # The container inventory only knows Docker's own publications. This is the case it
            # misses entirely, and the deployment would fail at compose up instead.
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $held = $listener.LocalEndpoint.Port

            try {
                $run = Invoke-EntryScript -PinPath $script:pinPath `
                    -Port @($held, (Get-FreePort), (Get-FreePort)) `
                    -ShimRule @((Get-MatchingDescriptorRule))

                $run.Failure | Should -Match "host port $held"
            }
            finally {
                $listener.Stop()
            }
        }
    }

    Context 'ownership, claimed before the thing it covers exists' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'claims the workspace before creating it, on a host that is clear' {
            # The run goes on to fail further in, because the shim cannot produce a released tool.
            # What is asserted here is the order of the first claim against the preflight that
            # precedes it, which no amount of later failure changes.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @((Get-MatchingDescriptorRule))

            $log = @($run.Evidence.commandLog)
            $firstClaim = [array]::FindIndex([string[]]$log, [Predicate[string]] { param($x) $x -like 'own Directory*' })
            $preflight = [array]::FindIndex([string[]]$log, [Predicate[string]] { param($x) $x -like 'docker ps -a --format*' })

            $firstClaim | Should -BeGreaterThan -1
            $preflight | Should -BeGreaterThan -1
            $firstClaim | Should -BeGreaterThan $preflight -Because 'nothing is claimed until the preflight has allowed the run'
        }

        It 'records the workspace claim before any command that writes into it' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @((Get-MatchingDescriptorRule))

            $log = @($run.Evidence.commandLog)
            $claim = [array]::FindIndex([string[]]$log, [Predicate[string]] { param($x) $x -like 'own Directory*' })
            $install = [array]::FindIndex([string[]]$log, [Predicate[string]] { param($x) $x -like 'dotnet tool install*' })

            if ($install -ge 0) {
                $claim | Should -BeLessThan $install
            }
            else {
                Set-ItResult -Inconclusive -Because 'the run did not reach the tool install, so there is no ordering to compare'
            }
        }

        It 'writes evidence even when the run fails' {
            # The command log is the record of what was attempted, so it has to survive the failure
            # it is evidence about.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @((Get-MatchingDescriptorRule))

            $run.Evidence | Should -Not -BeNullOrEmpty
            $run.Evidence.commandLog | Should -Not -BeNullOrEmpty
        }

        It 'records no image-building command in anything it ran' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @((Get-MatchingDescriptorRule))

            $run.Evidence.buildCommandAbsent.Verified | Should -BeTrue
        }
    }
}
