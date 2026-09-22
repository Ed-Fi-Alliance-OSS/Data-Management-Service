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
    [Parameter(Mandatory)] [string] $CaptureRoot,
    [Parameter(Mandatory)] [string] $BootstrapManifestPath,
    [Parameter(Mandatory)] [string] $HttpLog,
    [string] $AmbientKey,
    [string] $AmbientValue
)

$ErrorActionPreference = 'Stop'
$global:DmsShimPlan = Get-Content -LiteralPath $ShimPlan -Raw | ConvertFrom-Json
$global:DmsShimLog = $ShimLog
$global:DmsWorkspaceRoot = $WorkspaceRoot
$global:DmsCaptureRoot = $CaptureRoot
$global:DmsRuleHits = @{}
$global:DmsHttpLog = $HttpLog

function global:Invoke-DmsShim {
    param([string] $Tool, [string[]] $ShimArgs)

    $line = "$Tool $($ShimArgs -join ' ')"
    Add-Content -LiteralPath $global:DmsShimLog -Value $line

    for ($ruleIndex = 0; $ruleIndex -lt @($global:DmsShimPlan).Count; $ruleIndex++) {
        $rule = @($global:DmsShimPlan)[$ruleIndex]

        if ($line -match $rule.match) {
            # A rule may answer differently on successive matches, which is how a run with four
            # deployments can be given a good manifest first and a bad one later. The last entry
            # repeats once the sequence is exhausted.
            if ($rule.PSObject.Properties.Name -contains 'sequence') {
                $hits = if ($global:DmsRuleHits.ContainsKey($ruleIndex)) { $global:DmsRuleHits[$ruleIndex] } else { 0 }
                $global:DmsRuleHits[$ruleIndex] = $hits + 1
                $step = @($rule.sequence)
                $rule = $step[[Math]::Min($hits, $step.Count - 1)]
            }

            # What the command would have left on disk, created as it runs rather than beforehand.
            # Creating it in advance would materialise the run workspace before the script does, and
            # the script correctly refuses a workspace that already exists.
            if ($rule.PSObject.Properties.Name -contains 'create') {
                foreach ($artifact in @($rule.create)) {
                    $produced = Join-Path $global:DmsWorkspaceRoot $artifact.path
                    New-Item -ItemType Directory -Path (Split-Path -Parent $produced) -Force | Out-Null

                    # A real managed assembly where the script reads an assembly version, because
                    # GetAssemblyName refuses anything else and a placeholder would fail for a
                    # reason that has nothing to do with what is under test.
                    if ($artifact.PSObject.Properties.Name -contains 'asAssembly' -and $artifact.asAssembly) {
                        Copy-Item -LiteralPath ([psobject].Assembly.Location) -Destination $produced -Force
                    }
                    else {
                        $body = if ($artifact.PSObject.Properties.Name -contains 'content') { [string]$artifact.content } else { 'shim' }
                        Set-Content -LiteralPath $produced -Value $body -Encoding utf8
                    }
                }
            }

            # A junction inside the workspace, created at a command boundary rather than up front,
            # because the workspace does not exist until the run creates it. This is how a link can
            # appear after the path was first checked.
            if ($rule.PSObject.Properties.Name -contains 'link') {
                foreach ($link in @($rule.link)) {
                    $at = Join-Path $global:DmsWorkspaceRoot $link.path
                    New-Item -ItemType Directory -Path (Split-Path -Parent $at) -Force | Out-Null
                    New-Item -ItemType Junction -Path $at -Target $link.target | Out-Null
                }
            }

            # What the workspace looked like AT this command, copied out before cleanup removes it.
            # Without this the generated NuGet configuration would never be observed by anything:
            # the shim does not restore, so a wrong feed or a missing mapping would leave every
            # other assertion green.
            if ($rule.PSObject.Properties.Name -contains 'capture') {
                foreach ($wanted in @($rule.capture)) {
                    $source = Join-Path $global:DmsWorkspaceRoot $wanted
                    if (Test-Path -LiteralPath $source) {
                        $destination = Join-Path $global:DmsCaptureRoot ($wanted -replace '[\\/]', '_')
                        New-Item -ItemType Directory -Path $global:DmsCaptureRoot -Force | Out-Null
                        Copy-Item -LiteralPath $source -Destination $destination -Force
                    }
                }
            }

            $global:LASTEXITCODE = [int]$rule.exitCode

            if ($rule.PSObject.Properties.Name -contains 'output' -and -not [string]::IsNullOrEmpty($rule.output)) {
                return $rule.output
            }

            return @()
        }
    }

    $global:LASTEXITCODE = 0
    return @()
}

function global:docker { Invoke-DmsShim -Tool 'docker' -ShimArgs $args }
function global:dotnet { Invoke-DmsShim -Tool 'dotnet' -ShimArgs $args }
function global:pwsh { Invoke-DmsShim -Tool 'pwsh' -ShimArgs $args }

# Only the network. Get-SmokeTestCredential and everything under it - Add-CmsClient, Get-CmsToken,
# Get-DataStore, Add-Vendor, Add-Application - are the real functions from the real modules, so the
# contract under test is the one the proof will run against. Invoke-RestMethod and
# Invoke-WebRequest are defined globally, which is where module code looks when a command is not
# defined in its own module, so Invoke-Api inside Dms-Management.psm1 reaches these.
$global:DmsCmsToken = 'cms-admin-token-value'
$global:DmsClientKey = 'proof-client-key'
$global:DmsClientSecret = 'proof-client-secret-value'
$global:DmsDmsToken = 'dms-access-token-value'

function global:Write-DmsHttp {
    param([string] $Method, [string] $Uri, $Headers, $Body)

    $record = [ordered]@{
        method  = $Method
        uri     = $Uri
        headers = @{}
        body    = "$Body"
    }

    if ($null -ne $Headers) {
        foreach ($key in $Headers.Keys) { $record.headers[[string]$key] = [string]$Headers[$key] }
    }

    Add-Content -LiteralPath $global:DmsHttpLog -Value ($record | ConvertTo-Json -Depth 5 -Compress)
}

# What the Configuration Service answers. The data store listing is the one part a test varies, so
# it comes from the plan; everything else is the ordinary response the helpers need to proceed.
function global:Get-DmsCmsResponse {
    param([string] $Uri)

    if ($Uri -match '/connect/register') { return [pscustomobject]@{ } }
    if ($Uri -match '/connect/token') { return [pscustomobject]@{ access_token = $global:DmsCmsToken } }

    if ($Uri -match '/v3/dataStores') {
        $listing = @($global:DmsShimPlan | Where-Object { $_.match -eq 'cms:dataStores' })

        if ($listing.Count -gt 0) {
            return ($listing[0].output | ConvertFrom-Json)
        }

        return @([pscustomobject]@{ id = 7; name = 'Stock Proof Data Store'; dataStoreContexts = @() })
    }

    if ($Uri -match '/v3/applications') {
        return [pscustomobject]@{ id = 11; key = $global:DmsClientKey; secret = $global:DmsClientSecret }
    }

    if ($Uri -match '/oauth/token') { return [pscustomobject]@{ access_token = $global:DmsDmsToken } }

    return [pscustomobject]@{ }
}

function global:Invoke-RestMethod {
    param([string] $Uri, [string] $Method, [hashtable] $Headers, [string] $ContentType, $Body)
    Add-Content -LiteralPath $global:DmsShimLog -Value "rest $Method $Uri"
    Write-DmsHttp -Method $Method -Uri $Uri -Headers $Headers -Body $Body

    return Get-DmsCmsResponse -Uri $Uri
}

function global:Invoke-WebRequest {
    param([string] $Uri, [string] $Method, [hashtable] $Headers, [string] $ContentType, $Body, [switch] $SkipHttpErrorCheck)
    Add-Content -LiteralPath $global:DmsShimLog -Value "http $Method $Uri"
    Write-DmsHttp -Method $Method -Uri $Uri -Headers $Headers -Body $Body

    # Add-Vendor reads the new vendor's id out of the Location header.
    if ($Uri -match '/v3/vendors') {
        return [pscustomobject]@{ StatusCode = 201; Content = '{}'; Headers = @{ Location = '/v3/vendors/3' } }
    }

    foreach ($rule in $global:DmsShimPlan) {
        if ($rule.match -like 'http:*' -and "$Body" -match ($rule.match -replace '^http:', '')) {
            return [pscustomobject]@{ StatusCode = [int]$rule.exitCode; Content = $rule.output; Headers = @{} }
        }
    }

    # An ordinary document: the passing control.
    return [pscustomobject]@{ StatusCode = 201; Content = '{}'; Headers = @{} }
}

if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
    Set-Item -Path "Env:$AmbientKey" -Value $AmbientValue
}

$failure = '<the script returned without throwing>'
try {
    & $EntryScript -PinFile $PinFile -WorkspaceRoot $WorkspaceRoot -EvidenceRoot $EvidenceRoot -BaseEnvironmentFile $BaseEnvironmentFile -BootstrapManifestPath $BootstrapManifestPath
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

        # What New-PublishedPinFile's schemaPackages renders to, as prepare-dms-schema.ps1 would
        # record it in the staged manifest.
        $script:pinnedIdentity = @('EdFi.DataStandard52.ApiSchema@1.0.335')

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

        # The answers that carry a run past the released-tool install: the version the pin names,
        # and an executable where the install would have left one.
        function script:Get-InstalledToolRule {
            param([string] $Version = '8.0.1-alpha.0.7')

            return @(
                @{ match = 'dotnet tool list'; exitCode = 0; output = "Package Id             Version   Commands`nedfi.api.schematools   $Version   api-schema-tools" }
                @{ match = 'dotnet tool install'; exitCode = 0; create = @(@{ path = 'schema-tools/api-schema-tools' }) }
            )
        }

        # The files a successful fixture publish would have left behind, so a traversal can reach
        # the deployments. The entry assembly is a real one: the script reads its version.
        # What a bootstrap stages: the manifest recording the schema packages it prepared. Written
        # by the bootstrap rule so each deployment restages it, exactly as -d -v then a fresh
        # bootstrap does in a real run.
        function script:Get-BootstrapRule {
            param([string[]] $SelectedPackages, [switch] $OmitSelectedPackages, [switch] $OmitSchema, [switch] $AsScalar)

            $schema = if ($OmitSchema) {
                @{ }
            }
            elseif ($OmitSelectedPackages) {
                @{ schema = @{ effectiveSchemaHash = 'abc' } }
            }
            elseif ($AsScalar) {
                @{ schema = @{ selectedPackages = $SelectedPackages[0] } }
            }
            else {
                @{ schema = @{ selectedPackages = @($SelectedPackages) } }
            }

            return @(
                @{
                    match    = 'bootstrap-published-dms\.ps1 -EnvironmentFile'
                    exitCode = 0
                    create   = @(@{ path = 'bootstrap/bootstrap-manifest.json'; content = ($schema | ConvertTo-Json -Depth 6 -Compress) })
                }
            )
        }

        # Everything Invoke-HappyPath asks the daemon between locating the DMS container and
        # obtaining a client, so a test can reach the credential contract without a stack: the
        # container name, a Ready startup document, an inspect whose image id matches the pinned
        # digest's, and a read-only /app/plugins observed from inside.
        # The two rejections the fixture is supposed to produce, keyed off the reserved token in
        # the request body, so the happy path can run to its end.
        function script:Get-FixtureRejectionRule {
            return @(
                @{ match = 'http:custom-validation-proof-reject-path'; exitCode = 400
                    output = '{"validationErrors":{"$.lastSurname":["This value is the custom-validation proof fixture''s reserved rejection token."]},"errors":[]}'
                }
                @{ match = 'http:custom-validation-proof-reject-resource'; exitCode = 400
                    output = '{"validationErrors":{},"errors":["This document carries the custom-validation proof fixture''s reserved document-level rejection token."]}'
                }
            )
        }

        function script:Get-ReadyStackRule {
            $imageId = 'sha256:' + ('c' * 64)

            return @(
                @{ match = 'label=com\.docker\.compose\.service=dms '; exitCode = 0; output = 'dms-published-dms-1' }
                @{ match = 'exec dms-published-dms-1 cat /tmp/dms-startup-status\.json'; exitCode = 0
                    output = '{"State":"Ready","Phase":"LoadPlugins","Summary":"","ErrorMessage":""}'
                }
                @{ match = 'inspect dms-published-dms-1 --format'; exitCode = 0
                    output = "running|2026-09-21T00:00:00Z|0|$imageId|false"
                }
                @{ match = 'image inspect .*--format'; exitCode = 0; output = $imageId }
                @{ match = 'exec dms-published-dms-1 sh -c cat /proc/mounts'; exitCode = 0
                    output = "/dev/sda1 /app/plugins ext4 ro,relatime 0 0"
                }
                @{ match = 'exec dms-published-dms-1 sh -c touch /app/plugins'; exitCode = 1
                    output = "touch: /app/plugins/.stock-proof-write-probe: Read-only file system"
                }
            )
        }

        function script:Get-PublishedFixtureRule {
            param([string] $AssetsVersion = '1.0.0')

            # The two artifacts a real publish leaves that the script then reads: the entry assembly
            # whose version it records, and the assets file that says what the restore resolved. The
            # assets file lands in the run-owned intermediate path the script passes to MSBuild.
            $assets = @{
                libraries = @{
                    "EdFi.Api.Plugins/$AssetsVersion"          = @{ type = 'package' }
                    "EdFi.Api.CustomValidation/$AssetsVersion" = @{ type = 'package' }
                }
            } | ConvertTo-Json -Depth 5 -Compress

            return @(
                @{
                    match    = 'dotnet publish'
                    exitCode = 0
                    capture  = @('fixture-src/Acme.CustomValidationProof/nuget.config')
                    create   = @(
                        @{ path = 'plugins/Acme.CustomValidationProof/Acme.CustomValidationProof.dll'; asAssembly = $true }
                        @{ path = 'fixture-obj/project.assets.json'; content = $assets }
                    )
                }
            )
        }

        # The rule set a run needs to reach the credential contract and the fixture rejection, and
        # a reader for the HTTP the shim recorded. Defined here rather than in one Context because
        # more than one Context asks for them.
        function script:Get-HappyPathRule {
            param($DataStoreListing)

            $rule = @(Get-MatchingDescriptorRule) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
            @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) + @(Get-ReadyStackRule) +
            @(Get-FixtureRejectionRule)

            if ($null -ne $DataStoreListing) {
                $rule = @(@{ match = 'cms:dataStores'; exitCode = 0; output = $DataStoreListing }) + $rule
            }

            return @($rule | ForEach-Object { $_ })
        }

        function script:Get-HttpCall {
            param($Run, [string] $UriPattern, [string] $Method = 'Post')
            return @($Run.Http | Where-Object { $_.uri -match $UriPattern -and $_.method -eq $Method })
        }

        function script:Invoke-EntryScript {
            param(
                [string] $PinPath,
                [hashtable[]] $ShimRule = @(),
                [string] $AmbientKey,
                [string] $AmbientValue,
                [int[]] $Port,
                [string] $ManifestOverride,
                [string] $PathBase
            )

            $scratch = Join-Path ([IO.Path]::GetTempPath()) "dms1502-entry-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $scratch -Force | Out-Null

            try {
                $wrapper = Join-Path $scratch 'run-entry-under-shim.ps1'
                Set-Content -LiteralPath $wrapper -Value $script:wrapperBody -Encoding utf8

                $planPath = Join-Path $scratch 'shim-plan.json'
                Set-Content -LiteralPath $planPath -Encoding utf8 -Value (
                    ConvertTo-Json -Depth 9 -InputObject @($ShimRule | ForEach-Object { [pscustomobject]$_ }))

                $captureRoot = Join-Path $scratch 'captured'
                $shimLog = Join-Path $scratch 'shim.log'
                New-Item -ItemType File -Path $shimLog -Force | Out-Null
                $httpLog = Join-Path $scratch 'http.log'
                New-Item -ItemType File -Path $httpLog -Force | Out-Null
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
                # A value nothing else in the run produces, so "this secret never appears" is an
                # assertion about this string rather than about a default that might be empty.
                $baseLine += 'DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET=proof-bootstrap-secret-value'

                if ($PSBoundParameters.ContainsKey('PathBase')) {
                    $baseLine = @($baseLine | Where-Object { $_ -notmatch '^PATH_BASE=' })
                    $baseLine += "PATH_BASE=$PathBase"
                }
                Set-Content -LiteralPath $baseEnvironmentFile -Value $baseLine -Encoding utf8

                # The fixture's own assets file, in the shape the restore writes, so the script
                # reads what a build would actually have produced rather than a stub of its own.
                $argument = @(
                    '-NoProfile', '-File', $wrapper
                    '-BaseEnvironmentFile', $baseEnvironmentFile
                    '-EntryScript', $script:entryScript
                    '-PinFile', ($PinPath ? $PinPath : $script:committedPin)
                    '-WorkspaceRoot', $workspace
                    '-EvidenceRoot', $evidenceRoot
                    '-CaptureRoot', $captureRoot
                    '-BootstrapManifestPath', ($ManifestOverride ? $ManifestOverride : (Join-Path $workspace 'bootstrap/bootstrap-manifest.json'))
                    '-ShimPlan', $planPath
                    '-ShimLog', $shimLog
                    '-HttpLog', $httpLog
                    '-ResultFile', $resultFile
                )

                if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
                    $argument += @('-AmbientKey', $AmbientKey, '-AmbientValue', $AmbientValue)
                }

                # The real pwsh, resolved past the shim this session does not have but the child will.
                $pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
                & $pwshPath @argument 2>&1 | Out-Null

                $evidenceFile = Join-Path $evidenceRoot 'stock-image-plugin-proof.json'

                $captured = @{}
                if (Test-Path -LiteralPath $captureRoot) {
                    foreach ($file in (Get-ChildItem -LiteralPath $captureRoot -File)) {
                        $captured[$file.Name] = Get-Content -LiteralPath $file.FullName -Raw
                    }
                }

                return [pscustomobject]@{
                    Captured         = $captured
                    Port             = $Port
                    Failure          = (Test-Path -LiteralPath $resultFile) ? ((Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json).Failure) : '<no result>'
                    ShimCall         = @(Get-Content -LiteralPath $shimLog -ErrorAction SilentlyContinue)
                    Http             = @(Get-Content -LiteralPath $httpLog -ErrorAction SilentlyContinue |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
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

    Context 'the committed fixture tree is an input and nothing else' {
        BeforeAll {
            $script:fixtureTree = [IO.Path]::GetFullPath((Join-Path $script:composeRoot '../fixtures/plugins/Acme.CustomValidationProof'))

            # Path and content of everything there now, including anything under obj/ or bin/ that a
            # developer's own build left. Hashed rather than compared by timestamp, because a copy
            # or a rewrite with identical content is still a write this test should tolerate only if
            # the bytes are unchanged, and a touched timestamp alone is not a corruption.
            function script:Get-FixtureTreeState {
                $state = [ordered]@{}

                foreach ($file in (Get-ChildItem -LiteralPath $script:fixtureTree -Recurse -File -Force | Sort-Object FullName)) {
                    $state[$file.FullName] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
                }

                return $state
            }
        }

        It 'is left byte-for-byte unchanged by a full traversal' {
            # The defect this guards: the harness used to publish the committed project in place, so
            # MSBuild wrote obj/ there, and the tests used to create and then DELETE
            # obj/project.assets.json - destroying state that was never theirs.
            $before = Get-FixtureTreeState

            $pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $pinPath

            try {
                Invoke-EntryScript -PinPath $pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ }) | Out-Null
            }
            finally {
                Remove-Item -LiteralPath $pinPath -Force -ErrorAction SilentlyContinue
            }

            $after = Get-FixtureTreeState

            @($after.Keys) | Should -Be @($before.Keys) -Because 'no file may be added to or removed from the committed fixture tree'

            foreach ($path in $before.Keys) {
                $after[$path] | Should -BeExactly $before[$path] -Because "$path must be byte-for-byte unchanged"
            }
        }

        It 'has no obj directory after a traversal when it had none before' {
            $objDirectory = Join-Path $script:fixtureTree 'obj'

            if (Test-Path -LiteralPath $objDirectory) {
                Set-ItResult -Inconclusive -Because 'this working copy already has a real obj directory, which the byte-for-byte case above covers'
                return
            }

            $pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $pinPath

            try {
                Invoke-EntryScript -PinPath $pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ }) | Out-Null
            }
            finally {
                Remove-Item -LiteralPath $pinPath -Force -ErrorAction SilentlyContinue
            }

            Test-Path -LiteralPath $objDirectory | Should -BeFalse
        }

        It 'restores both contracts only from the pinned published feed' {
            # The shim does not restore, so nothing else here would notice if this file kept the
            # committed local-fixture-feed mapping, named the wrong feed, dropped a contract or left
            # an inherited source active. It is observed at the publish boundary instead.
            $pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $pinPath
            $pinnedFeed = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'

            try {
                $run = Invoke-EntryScript -PinPath $pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

                $generated = $run.Captured['fixture-src_Acme.CustomValidationProof_nuget.config']
                $generated | Should -Not -BeNullOrEmpty -Because 'the publish must run against a generated configuration'

                $xml = [xml]$generated

                # Nothing inherited from a machine or user level configuration. Counted through
                # SelectNodes: an absent element reads as $null, and @($null) has one element, so a
                # missing <clear /> would satisfy a naive count.
                $xml.SelectNodes('/configuration/packageSources/clear').Count | Should -Be 1

                $source = @{}
                foreach ($entry in $xml.configuration.packageSources.add) { $source[$entry.key] = $entry.value }

                $source.Keys | Should -Contain 'published-edfi'
                $source['published-edfi'] | Should -BeExactly $pinnedFeed
                $source.Keys | Should -Not -Contain 'local-fixture-feed'
                # Public dependencies still need somewhere to come from.
                $source.Keys | Should -Contain 'nuget.org'

                # Every source whose patterns match a contract id, and how specific that match is.
                $mapping = @{}
                foreach ($packageSource in $xml.configuration.packageSourceMapping.packageSource) {
                    $mapping[$packageSource.key] = @($packageSource.package | ForEach-Object { $_.pattern })
                }

                foreach ($contract in @('EdFi.Api.Plugins', 'EdFi.Api.CustomValidation')) {
                    $mapping['published-edfi'] | Should -Contain $contract -Because "$contract must be mapped to the pinned feed"

                    # Under source mapping the most specific matching pattern wins, and an exact id
                    # is the most specific there is. No other source may declare one for these ids,
                    # or the contract could resolve from somewhere the pin does not name.
                    foreach ($key in $mapping.Keys) {
                        if ($key -eq 'published-edfi') { continue }
                        $mapping[$key] | Should -Not -Contain $contract -Because "$key must not be able to serve $contract"
                    }
                }
            }
            finally {
                Remove-Item -LiteralPath $pinPath -Force -ErrorAction SilentlyContinue
            }
        }

        It 'leaves the committed fixture nuget.config byte-for-byte unchanged' {
            $committed = Join-Path $script:fixtureTree 'nuget.config'
            $before = (Get-FileHash -Algorithm SHA256 -LiteralPath $committed).Hash

            $pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $pinPath

            try {
                Invoke-EntryScript -PinPath $pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ }) | Out-Null
            }
            finally {
                Remove-Item -LiteralPath $pinPath -Force -ErrorAction SilentlyContinue
            }

            (Get-FileHash -Algorithm SHA256 -LiteralPath $committed).Hash | Should -BeExactly $before
        }

        It 'publishes a copy, with run-owned intermediate and output paths' {
            $pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $pinPath

            try {
                $run = Invoke-EntryScript -PinPath $pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

                $publish = @($run.ShimCall | Where-Object { $_ -like 'dotnet publish*' })

                $publish | Should -Not -BeNullOrEmpty
                $publish[0] | Should -Match 'fixture-src'
                $publish[0] | Should -Not -Match 'eng[\\/]fixtures'
                $publish[0] | Should -Match 'BaseIntermediateOutputPath='
                $publish[0] | Should -Match 'BaseOutputPath='
            }
            finally {
                Remove-Item -LiteralPath $pinPath -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'the schema packages each deployment actually staged' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'reads the real bootstrap workspace by default' {
            # The seam exists so these cases need not write into the repository. The default has to
            # remain the one file the bootstrap wrapper actually stages.
            $script = Get-Content -LiteralPath $script:entryScript -Raw

            $script | Should -Match "Join-Path \`$bootstrapPath 'bootstrap-manifest\.json'"
        }

        It 'refuses a manifest override outside the run workspace, before touching anything' {
            # The override reads one file. A path outside the workspace would let a caller answer
            # the check with a manifest this run's deployment did not stage.
            $outside = Join-Path ([IO.Path]::GetTempPath()) "dms1502-outside-$([guid]::NewGuid().ToString('N')).json"

            $run = Invoke-EntryScript -PinPath $script:pinPath -ManifestOverride $outside

            $run.Failure | Should -Match 'outside the run workspace'
            $run.ShimCall | Should -HaveCount 0
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'refuses an override that walks out of the workspace by relative segment' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ManifestOverride 'C:/does-not-matter/../elsewhere/bootstrap-manifest.json'

            $run.Failure | Should -Match 'outside the run workspace'
            $run.ShimCall | Should -HaveCount 0
        }

        It 'keeps the bootstrap workspace itself out of the override' {
            # The one defect this override must not reintroduce: the path the preflight inspects,
            # the run claims, and cleanup may delete recursively stays a fixed compose-root path.
            $script = Get-Content -LiteralPath $script:entryScript -Raw

            $script | Should -Match "(?m)^\`$bootstrapPath = Join-Path \`$composeRoot '\.bootstrap'"
            $script | Should -Not -Match 'BootstrapWorkspacePath'

            $ast = [Management.Automation.Language.Parser]::ParseFile($script:entryScript, [ref]$null, [ref]$null)
            $assigned = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left.Extent.Text -eq '$bootstrapPath'
                    }, $true))

            $assigned | Should -HaveCount 1

            # And the override reaches the manifest read only: nothing that owns or deletes.
            $override = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.VariableExpressionAst] -and
                        $node.VariablePath.UserPath -eq 'BootstrapManifestPath'
                    }, $true))
            $enclosing = foreach ($use in $override) {
                @($ast.FindAll({
                            param($node)
                            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Extent.StartOffset -le $use.Extent.StartOffset -and
                            $node.Extent.EndOffset -ge $use.Extent.EndOffset
                        }, $true)).Name
            }

            @($enclosing | Where-Object { $_ -ne 'Get-BootstrapManifestPath' }) | Should -BeNullOrEmpty
            $enclosing | Should -Contain 'Get-BootstrapManifestPath'
        }

        It 'refuses an override reached through a junction created after the workspace was' {
            # Lexical containment cannot see this: the override sits under the workspace by name,
            # and the junction only appears once the run has created the workspace, so the preflight
            # walk finds nothing. The read is where it has to be caught, and what lies on the other
            # side is a manifest that would otherwise have been accepted.
            $external = Join-Path ([IO.Path]::GetTempPath()) "dms1502-external-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $external -Force | Out-Null

            try {
                Set-Content -LiteralPath (Join-Path $external 'bootstrap-manifest.json') -Encoding utf8 `
                    -Value (@{ schema = @{ selectedPackages = @($script:pinnedIdentity) } } | ConvertTo-Json -Depth 6 -Compress)

                $junction = @{
                    match    = 'bootstrap-published-dms\.ps1 -EnvironmentFile'
                    exitCode = 0
                    link     = @(@{ path = 'bootstrap'; target = $external })
                }

                $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                    @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                    @($junction) | ForEach-Object { $_ })

                $run.Failure | Should -Match 'is a link or junction'
                $run.Failure | Should -Not -Match 'DMS container could not be located'
            }
            finally {
                # Only this test's own temp tree, and the junction itself lives in the run
                # workspace, which the run removes.
                Remove-Item -LiteralPath $external -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'accepts a deployment that staged exactly the pinned set' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

            # Not a bare "did not fail": a run that never reached the check would satisfy that.
            # This shim carries the run as far as the first scenario body, which is past the check,
            # so the later failure is what proves the check ran and accepted the manifest.
            $run.Failure | Should -Not -Match 'prepared schema packages'
            $run.Failure | Should -Match 'DMS container could not be located'
        }

        It 'refuses a deployment that staged <Case>' -ForEach @(
            @{ Case = 'a missing package'; Staged = @() ; Expect = 'staged no schema packages' }
            @{ Case = 'an extra package'; Staged = @('EdFi.DataStandard52.ApiSchema@1.0.335', 'EdFi.Other@9.9.9'); Expect = 'which the pin does not name' }
            @{ Case = 'a different version'; Staged = @('EdFi.DataStandard52.ApiSchema@9.9.9'); Expect = 'did not stage them' }
            @{ Case = 'a malformed entry'; Staged = @('no-at-sign'); Expect = 'malformed' }
            @{ Case = 'a duplicate'; Staged = @('EdFi.DataStandard52.ApiSchema@1.0.335', 'EdFi.DataStandard52.ApiSchema@1.0.335'); Expect = 'more than once' }
        ) {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $Staged) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'prepared schema packages the pin does not name'
            $run.Failure | Should -Match $Expect
        }

        It 'refuses a manifest whose selectedPackages is a scalar' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity -AsScalar) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'rather than an array'
        }

        It 'refuses a manifest with no <Case>' -ForEach @(
            @{ Case = 'selectedPackages'; Switch = 'OmitSelectedPackages' }
            @{ Case = 'schema section'; Switch = 'OmitSchema' }
        ) {
            $argument = @{ SelectedPackages = $script:pinnedIdentity; $Switch = $true }
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule @argument) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'records no schema.selectedPackages'
        }

        It 'refuses a deployment that staged no manifest at all' {
            # Every rule but the bootstrap one, so the command runs and produces nothing.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'staged no .*bootstrap-manifest\.json'
        }

        It 'runs the check inside the per-deployment path, not once for the run' {
            # The behavioural form of this case - a good manifest in deployment 1 and a bad one in
            # deployment 3 - needs the shim to carry a scenario body through to a second deployment,
            # which it cannot do yet. What is enforceable here is where the call lives: hoisting it
            # into the caller that runs the four scenarios would make one observation stand for all
            # four, and each scenario restages the workspace after a -d -v teardown.
            $ast = [Management.Automation.Language.Parser]::ParseFile($script:entryScript, [ref]$null, [ref]$null)

            $call = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Assert-PreparedSchemaIdentity'
                    }, $true))

            $call | Should -HaveCount 1

            $enclosing = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Extent.StartOffset -le $call[0].Extent.StartOffset -and
                        $node.Extent.EndOffset -ge $call[0].Extent.EndOffset
                    }, $true))

            $enclosing.Name | Should -Contain 'Invoke-ProofScenario'

            # And after the deployment it is asked about, so it reads that deployment's manifest.
            $deployment = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Start-ProofDeployment'
                    }, $true))

            $deployment | Should -HaveCount 1
            $call[0].Extent.StartOffset | Should -BeGreaterThan $deployment[0].Extent.EndOffset
        }

        It 'reaches that per-deployment path once for every scenario' {
            $ast = [Management.Automation.Language.Parser]::ParseFile($script:entryScript, [ref]$null, [ref]$null)

            $scenario = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Invoke-ProofScenario'
                    }, $true))

            $scenario | Should -HaveCount 4
        }
    }

    Context 'the client this proof posts with, and the store it is bound to' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'binds the application to the one route-unqualified data store, by id' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $application = Get-HttpCall -Run $run -UriPattern '/v3/applications'

            $application | Should -Not -BeNullOrEmpty
            $body = $application[0].body | ConvertFrom-Json
            # The exact value, not "some store": the whole point is that it is chosen rather than
            # taken from the head of a listing.
            @($body.dataStoreIds) | Should -Be @(7)
        }

        It 'reaches Add-Application through the real helper, not a stand-in' {
            # The vendor POST and the client registration are Dms-Management's work. If the proof
            # were calling a shim of Get-SmokeTestCredential, none of these would be on the wire.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            Get-HttpCall -Run $run -UriPattern '/connect/register' | Should -Not -BeNullOrEmpty
            Get-HttpCall -Run $run -UriPattern '/connect/token' | Should -Not -BeNullOrEmpty
            Get-HttpCall -Run $run -UriPattern '/v3/dataStores' -Method 'Get' | Should -Not -BeNullOrEmpty
            Get-HttpCall -Run $run -UriPattern '/v3/vendors' | Should -Not -BeNullOrEmpty
        }

        It 'reads the data stores with the bootstrap admin token this deployment was started with' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $listing = Get-HttpCall -Run $run -UriPattern '/v3/dataStores' -Method 'Get'

            $listing[0].headers.Authorization | Should -BeExactly 'Bearer cms-admin-token-value'

            # Once per deployment, by this script. Get-SmokeTestCredential lists the stores itself
            # only when it was given no -DataStoreIds, so a second listing per deployment is what
            # "the id was not passed explicitly" looks like from outside.
            $deployment = @(Get-HttpCall -Run $run -UriPattern '/connect/register').Count
            $deployment | Should -BeGreaterThan 0
            $listing.Count | Should -Be $deployment
        }

        It 'exchanges the key and secret it was given for the DMS token' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $expected = 'Basic ' + [Convert]::ToBase64String(
                [Text.Encoding]::UTF8.GetBytes('proof-client-key:proof-client-secret-value'))

            $token = Get-HttpCall -Run $run -UriPattern '/oauth/token'

            $token | Should -Not -BeNullOrEmpty
            $token[0].headers.Authorization | Should -BeExactly $expected
        }

        It 'sends every Student request with that DMS token as its bearer' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $student = Get-HttpCall -Run $run -UriPattern '/data/ed-fi/students'

            # The control and both rejection arms.
            $student.Count | Should -BeGreaterOrEqual 3
            @($student | Where-Object { $_.headers.Authorization -cne 'Bearer dms-access-token-value' }) |
                Should -BeNullOrEmpty
        }

        It 'refuses <Case> rather than binding to whichever store came first' -ForEach @(
            @{ Case    = 'several data stores'
                Listing = '[{"id":7,"name":"One","dataStoreContexts":[]},{"id":8,"name":"Two","dataStoreContexts":[]}]'
                Expect  = 'requires exactly one'
            }
            @{ Case    = 'a route-qualified store'
                Listing = '[{"id":7,"name":"One","dataStoreContexts":[{"contextKey":"schoolYear","contextValue":"2024"}]}]'
                Expect  = 'route-qualified'
            }
            @{ Case    = 'no data stores'
                Listing = '[]'
                Expect  = 'returned no data stores'
            }
            @{ Case    = 'a store with no id'
                Listing = '[{"name":"One","dataStoreContexts":[]}]'
                Expect  = 'carries no id'
            }
            @{ Case    = 'a store whose id is not a number'
                Listing = '[{"id":"12abc","name":"One","dataStoreContexts":[]}]'
                Expect  = 'not a whole number'
            }
            @{ Case    = 'a store whose contexts are malformed'
                Listing = '[{"id":7,"name":"One","dataStoreContexts":"none"}]'
                Expect  = 'rather than an array'
            }
        ) {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule -DataStoreListing $Listing)

            $run.Failure | Should -Match 'does not offer one data store to bind a client to'
            $run.Failure | Should -Match $Expect

            # And nothing was created on the back of a guess.
            Get-HttpCall -Run $run -UriPattern '/v3/applications' | Should -BeNullOrEmpty
            Get-HttpCall -Run $run -UriPattern '/data/ed-fi/students' | Should -BeNullOrEmpty
        }

        It 'keeps every secret out of the evidence and the command log' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $evidence = $run.Evidence | ConvertTo-Json -Depth 14

            foreach ($secret in @(
                    'proof-bootstrap-secret-value'
                    'cms-admin-token-value'
                    'proof-client-secret-value'
                    'dms-access-token-value'
                )) {
                $evidence | Should -Not -Match ([regex]::Escape($secret))
                @($run.ShimCall | Where-Object { $_ -match [regex]::Escape($secret) }) | Should -BeNullOrEmpty
            }

            # A vacuous pass would satisfy the above, so prove the evidence was written and that the
            # secrets really were in play on the wire.
            $evidence | Should -Match 'dataStoreId'
            Get-HttpCall -Run $run -UriPattern '/oauth/token' | Should -Not -BeNullOrEmpty
        }
    }

    Context 'the four scenarios, traversed end to end under the shim' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'reaches the fixture publish and refuses a restore that resolved another version' {
            # The pin names 1.0.0. A restore that produced anything else is the whole reason the
            # contracts are pinned, and bracketing only asks for a version.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) +
                @(Get-PublishedFixtureRule -AssetsVersion '1.1.0') | ForEach-Object { $_ })

            $run.Failure | Should -Match 'restored as 1\.1\.0'
            @($run.ShimCall | Where-Object { $_ -like 'dotnet publish*' }) | Should -Not -BeNullOrEmpty
        }

        It 'passes bare contract versions to the publish, because the project brackets them itself' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

            $publish = @($run.ShimCall | Where-Object { $_ -like 'dotnet publish*' })

            $publish | Should -Not -BeNullOrEmpty
            $publish[0] | Should -Match 'PluginsPackageVersion=1\.0\.0'
            $publish[0] | Should -Not -Match 'PluginsPackageVersion=\['
        }

        It 'refuses a released tool whose installed version is not the pinned one' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule -Version '9.9.9') | ForEach-Object { $_ })

            $run.Failure | Should -Match 'not the pinned one'
        }

        It 'installs the released tool at the pinned version into its own tool path' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) | ForEach-Object { $_ })

            $install = @($run.ShimCall | Where-Object { $_ -like 'dotnet tool install*' })

            $install | Should -Not -BeNullOrEmpty
            $install[0] | Should -Match '--version 8\.0\.1-alpha\.0\.7'
            $install[0] | Should -Match '--tool-path'
        }

        It 'runs no command that builds an image, across the whole traversal' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

            $run.Evidence.buildCommandAbsent.Verified | Should -BeTrue
        }

        It 'fails the run when the final teardown fails, rather than reporting success' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'Tearing down'
        }

        It 'keeps the run directories when teardown failed, because they are still mounted' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) | ForEach-Object { $_ })

            $run.WorkspaceCreated | Should -BeTrue
        }

        It 'removes the run directories when teardown succeeded' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) + @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) | ForEach-Object { $_ })

            $run.WorkspaceCreated | Should -BeFalse
        }
    }

    Context 'what the run says it was' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'records a mid-run failure as failed evidence, with the reason' {
            # The tag resolving to another digest, which stops the run inside its scenario work.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @{ match = 'buildx imagetools inspect'; exitCode = 0; output = '{"digest":"sha256:' + ('9' * 64) + '"}' }
            )

            $run.Evidence | Should -Not -BeNullOrEmpty
            $run.Evidence.outcome | Should -BeExactly 'failed'
            $run.Evidence.primaryFailure | Should -Match 'has moved'
            @($run.Evidence.failure)[0] | Should -Match '^the run failed:'
            $run.Failure | Should -Match 'did not pass'
        }

        It 'does not let a cleanup failure erase the failure that came first' {
            # Both at once: a schema set the pin does not name, and a teardown that then fails.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages @('EdFi.Wrong@9.9.9')) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) |
                ForEach-Object { $_ })

            $run.Evidence.outcome | Should -BeExactly 'failed'
            $run.Evidence.primaryFailure | Should -Match 'prepared schema packages the pin does not name'

            # Both reasons, and the original first. The cleanup error used to replace it.
            $failure = @($run.Evidence.failure)
            $failure[0] | Should -Match 'prepared schema packages the pin does not name'
            @($failure | Where-Object { $_ -match 'Tearing down' }) | Should -Not -BeNullOrEmpty

            $run.Failure | Should -Match 'prepared schema packages the pin does not name'
            $run.Failure | Should -Match 'Tearing down'
        }

        It 'records that cleanup was attempted, and whether it succeeded' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) |
                ForEach-Object { $_ })

            $run.Evidence.cleanup.ownedRunStarted | Should -BeTrue
            $run.Evidence.cleanup.attempted | Should -BeTrue
            $run.Evidence.cleanup.succeeded | Should -BeFalse
            $run.Evidence.cleanup.teardownFailed | Should -BeTrue
            @($run.Evidence.cleanup.error) | Should -Not -BeNullOrEmpty
        }

        It 'fails the run on a recorded image-building command rather than recording it and stopping' {
            # PATH_BASE reaches the command log through the URL the proof logs its requests to, so
            # this is a build command genuinely present in the record the no-build verdict reads.
            # See the residual note: that an env value can put tokens there at all is its own
            # weakness, and this test is why it is visible.
            $run = Invoke-EntryScript -PinPath $script:pinPath -PathBase 'x docker build .' -ShimRule (Get-HappyPathRule)

            $run.Evidence.buildCommandAbsent.Verified | Should -BeFalse
            $run.Evidence.outcome | Should -BeExactly 'failed'
            @($run.Evidence.failure | Where-Object { $_ -match 'recorded a command that builds an image' }) |
                Should -Not -BeNullOrEmpty
            $run.Failure | Should -Match 'recorded a command that builds an image'
        }

        It 'keeps the structured no-build verdict in the evidence either way' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule (Get-HappyPathRule)

            $run.Evidence.buildCommandAbsent.Verified | Should -BeTrue
            $run.Evidence.buildCommandAbsent.Reason | Should -BeExactly 'no recorded command builds an image'
        }

        It 'computes the outcome in one place, from the verdict it recorded' {
            # The defect this replaces was a verdict written into evidence and then never consulted.
            $ast = [Management.Automation.Language.Parser]::ParseFile($script:entryScript, [ref]$null, [ref]$null)

            $noBuild = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Test-BuildCommandAbsent'
                    }, $true))

            $outcome = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.CommandAst] -and
                        $node.GetCommandName() -eq 'Get-ProofOutcome'
                    }, $true))

            $noBuild | Should -HaveCount 1
            $outcome | Should -Not -BeNullOrEmpty
            foreach ($call in $outcome) { $call.Extent.Text | Should -Match '-NoBuildVerdict \$noBuild' }
        }

        It 'never writes an exception record or a raw request object into evidence' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @{ match = 'buildx imagetools inspect'; exitCode = 0; output = '{"digest":"sha256:' + ('9' * 64) + '"}' }
            )

            $evidence = $run.Evidence | ConvertTo-Json -Depth 14

            # The message and its exception type, not a stack trace or a bound object graph.
            $run.Evidence.primaryFailure | Should -Not -Match 'at <ScriptBlock>'
            $evidence | Should -Not -Match 'ScriptStackTrace'
            $evidence | Should -Not -Match 'InvocationInfo'
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
