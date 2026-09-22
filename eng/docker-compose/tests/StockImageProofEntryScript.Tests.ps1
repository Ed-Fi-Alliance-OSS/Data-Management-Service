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
    [Parameter(Mandatory)] [string] $CleanupReceiptPath,
    [Parameter(Mandatory)] [string] $HttpLog,
    [Parameter(Mandatory)] [string] $UnmatchedLog,
    [switch] $Strict,
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
$global:DmsUnmatchedLog = $UnmatchedLog
# Which deployment the run is in. 0 is everything before the first stack comes up; the counter
# advances on the command that brings one up, so a rule written for deployment 3 cannot answer for
# deployment 1 and an answer that arrives in the wrong order is a failure rather than a pass.
$global:DmsPhase = 0
$global:DmsStrict = [bool]$Strict

function global:Write-DmsUnmatched {
    param([string] $Kind, [string] $Detail)
    Add-Content -LiteralPath $global:DmsUnmatchedLog -Value "$Kind`tphase=$($global:DmsPhase)`t$Detail"
}

function global:Invoke-DmsShim {
    param([string] $Tool, [string[]] $ShimArgs)

    $line = "$Tool $($ShimArgs -join ' ')"
    Add-Content -LiteralPath $global:DmsShimLog -Value $line

    # Advanced before the rules are consulted, so the command that brings a stack up is itself part
    # of the deployment it starts.
    if ($line -match 'bootstrap-published-dms\.ps1 -EnvironmentFile') {
        $global:DmsPhase = $global:DmsPhase + 1
    }

    for ($ruleIndex = 0; $ruleIndex -lt @($global:DmsShimPlan).Count; $ruleIndex++) {
        $rule = @($global:DmsShimPlan)[$ruleIndex]
        $hits = if ($global:DmsRuleHits.ContainsKey($ruleIndex)) { $global:DmsRuleHits[$ruleIndex] } else { 0 }

        # A rule pinned to a deployment answers only in it. A rule with no phase answers anywhere,
        # which is how the setup commands and the invariant docker questions are written.
        if ($rule.PSObject.Properties.Name -contains 'phase' -and [int]$rule.phase -ne $global:DmsPhase) {
            continue
        }

        # A rule that may answer a fixed number of times, so a second identical call is not silently
        # served by an answer that was meant for the first.
        if ($rule.PSObject.Properties.Name -contains 'limit' -and $hits -ge [int]$rule.limit) {
            continue
        }

        if ($line -match $rule.match) {
            $global:DmsRuleHits[$ruleIndex] = $hits + 1

            # A rule may answer differently on successive matches. The last entry repeats once the
            # sequence is exhausted.
            if ($rule.PSObject.Properties.Name -contains 'sequence') {
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

    # Nothing answered. Under a strict plan that is a failure that names the call: a permissive
    # default success is how a traversal passes on commands nobody planned for, which is the thing
    # this plan exists to rule out.
    if ($global:DmsStrict) {
        Write-DmsUnmatched -Kind 'unmatched' -Detail $line
        $global:LASTEXITCODE = 125
        return "the strict plan has no answer for: $line"
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

    if ($global:DmsStrict) {
        Write-DmsUnmatched -Kind 'unmatched-cms' -Detail $Uri
    }

    return [pscustomobject]@{ }
}

# Under a strict plan every request is answered from an indexed rule, exactly as a command is, so
# the same phase, limit and cardinality accounting applies to the network. The hardcoded answers
# below it remain for the non-strict Contexts, which are about the script's own sequencing and not
# about the shape of a run.
#
# A rule is 'req:<METHOD> <uri regex>' with an optional body regex, which is what tells the three
# Student posts apart.
function global:Resolve-DmsRequestRule {
    param([string] $Method, [string] $Uri, $Body)

    $nearMiss = @()

    for ($i = 0; $i -lt @($global:DmsShimPlan).Count; $i++) {
        $rule = @($global:DmsShimPlan)[$i]

        if ($rule.match -notlike 'req:*') { continue }

        $spec = ($rule.match -replace '^req:', '') -split ' ', 2

        if ($spec[0] -cne $Method -or $Uri -notmatch $spec[1]) { continue }

        if ($rule.PSObject.Properties.Name -contains 'body' -and "$Body" -notmatch $rule.body) { continue }

        $hits = if ($global:DmsRuleHits.ContainsKey($i)) { $global:DmsRuleHits[$i] } else { 0 }

        # A near miss is only a failure if nothing else answers. Recording it here would make a
        # second deployment's copy of a rule look like a failed call every time the first one
        # answered correctly.
        if ($rule.PSObject.Properties.Name -contains 'phase' -and [int]$rule.phase -ne $global:DmsPhase) {
            $nearMiss += "request-wrong-phase for '$($rule.match)'"
            continue
        }

        if ($rule.PSObject.Properties.Name -contains 'limit' -and $hits -ge [int]$rule.limit) {
            $nearMiss += "request-exhausted for '$($rule.match)'"
            continue
        }

        $global:DmsRuleHits[$i] = $hits + 1
        return $rule
    }

    Write-DmsUnmatched -Kind 'unmatched-request' -Detail "$Method $Uri ($($nearMiss -join '; '))"
    return $null
}

function global:Invoke-RestMethod {
    param([string] $Uri, [string] $Method, [hashtable] $Headers, [string] $ContentType, $Body)
    Add-Content -LiteralPath $global:DmsShimLog -Value "rest $Method $Uri"
    Write-DmsHttp -Method $Method -Uri $Uri -Headers $Headers -Body $Body

    if ($global:DmsStrict) {
        $rule = Resolve-DmsRequestRule -Method $Method -Uri $Uri -Body $Body

        if ($null -eq $rule) { return [pscustomobject]@{ } }

        return ($rule.output | ConvertFrom-Json)
    }

    return Get-DmsCmsResponse -Uri $Uri
}

function global:Invoke-WebRequest {
    param([string] $Uri, [string] $Method, [hashtable] $Headers, [string] $ContentType, $Body, [switch] $SkipHttpErrorCheck)
    Add-Content -LiteralPath $global:DmsShimLog -Value "http $Method $Uri"
    Write-DmsHttp -Method $Method -Uri $Uri -Headers $Headers -Body $Body

    if ($global:DmsStrict) {
        $rule = Resolve-DmsRequestRule -Method $Method -Uri $Uri -Body $Body

        if ($null -eq $rule) {
            return [pscustomobject]@{ StatusCode = 599; Content = '{}'; Headers = @{} }
        }

        $header = @{}
        if ($rule.PSObject.Properties.Name -contains 'location') { $header['Location'] = $rule.location }

        return [pscustomobject]@{ StatusCode = [int]$rule.exitCode; Content = $rule.output; Headers = $header }
    }

    # Add-Vendor reads the new vendor's id out of the Location header.
    if ($Uri -match '/v3/vendors') {
        return [pscustomobject]@{ StatusCode = 201; Content = '{}'; Headers = @{ Location = '/v3/vendors/3' } }
    }

    # Indexed, so an HTTP answer counts towards the same rule-use accounting as a command answer.
    for ($i = 0; $i -lt @($global:DmsShimPlan).Count; $i++) {
        $rule = @($global:DmsShimPlan)[$i]

        if ($rule.match -like 'http:*' -and $rule.match -ne 'http:control' -and
            "$Body" -match ($rule.match -replace '^http:', '')) {
            if ($rule.PSObject.Properties.Name -contains 'phase' -and [int]$rule.phase -ne $global:DmsPhase) {
                Write-DmsUnmatched -Kind 'http-wrong-phase' -Detail "$Method $Uri"
                continue
            }

            $seen = if ($global:DmsRuleHits.ContainsKey($i)) { $global:DmsRuleHits[$i] } else { 0 }
            $global:DmsRuleHits[$i] = $seen + 1
            return [pscustomobject]@{ StatusCode = [int]$rule.exitCode; Content = $rule.output; Headers = @{} }
        }
    }

    # The passing control. Under a strict plan it must be planned for, because "any document this
    # run did not name is accepted" is exactly the permissive default that makes a control vacuous.
    for ($i = 0; $i -lt @($global:DmsShimPlan).Count; $i++) {
        if (@($global:DmsShimPlan)[$i].match -eq 'http:control') {
            $control = @($global:DmsShimPlan)[$i]
            $seen = if ($global:DmsRuleHits.ContainsKey($i)) { $global:DmsRuleHits[$i] } else { 0 }
            $global:DmsRuleHits[$i] = $seen + 1
            return [pscustomobject]@{ StatusCode = [int]$control.exitCode; Content = $control.output; Headers = @{} }
        }
    }

    if ($global:DmsStrict) {
        Write-DmsUnmatched -Kind 'unmatched-http' -Detail "$Method $Uri $Body"
        return [pscustomobject]@{ StatusCode = 599; Content = '{}'; Headers = @{} }
    }

    return [pscustomobject]@{ StatusCode = 201; Content = '{}'; Headers = @{} }
}

if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
    Set-Item -Path "Env:$AmbientKey" -Value $AmbientValue
}

# Which plan entries actually answered something. A rule the plan declared required and nothing
# ever reached is a plan that does not describe this run, so the harness fails on it rather than
# passing because the missing answer happened not to matter.
function global:Write-DmsRuleUse {
    $used = @()
    foreach ($key in $global:DmsRuleHits.Keys) { $used += "$key=$($global:DmsRuleHits[$key])" }
    Set-Content -LiteralPath "$global:DmsUnmatchedLog.used" -Value ($used -join "`n")
}

$failure = '<the script returned without throwing>'
try {
    & $EntryScript -PinFile $PinFile -WorkspaceRoot $WorkspaceRoot -EvidenceRoot $EvidenceRoot -BaseEnvironmentFile $BaseEnvironmentFile -BootstrapManifestPath $BootstrapManifestPath -CleanupReceiptPath $CleanupReceiptPath
}
catch {
    $failure = $_.Exception.Message
}

Write-DmsRuleUse

# The plan's own verdict, reached in the child rather than by an assertion afterwards. A run that
# made a call nobody planned for, used an answer more or fewer times than the plan said, or left a
# required answer untouched did not walk the sequence the plan describes, whatever the script
# itself concluded.
if ($Strict) {
    $planFailure = @()

    foreach ($entry in @(Get-Content -LiteralPath $global:DmsUnmatchedLog -ErrorAction SilentlyContinue |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $planFailure += "the plan had no answer for: $entry"
    }

    for ($i = 0; $i -lt @($global:DmsShimPlan).Count; $i++) {
        $rule = @($global:DmsShimPlan)[$i]
        $hits = if ($global:DmsRuleHits.ContainsKey($i)) { $global:DmsRuleHits[$i] } else { 0 }

        if ($rule.PSObject.Properties.Name -contains 'expect' -and $hits -ne [int]$rule.expect) {
            $planFailure += "the plan expected '$($rule.match)' to answer $([int]$rule.expect) time(s) and it answered $hits"
        }
        elseif ($rule.PSObject.Properties.Name -contains 'required' -and $rule.required -and $hits -eq 0) {
            $planFailure += "the plan required '$($rule.match)' and nothing reached it"
        }
    }

    if ($planFailure.Count -gt 0) {
        $failure = "The run did not walk the planned sequence:" + [Environment]::NewLine +
            (($planFailure | ForEach-Object { "  - $_" }) -join [Environment]::NewLine) +
            [Environment]::NewLine + "The script itself reported: $failure"
    }
}

[pscustomobject]@{ Failure = $failure } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultFile -Encoding utf8

# The child's exit code is the run's, so "it exited successfully" is a thing a test can assert
# rather than infer from the absence of a message.
if ($failure -ne '<the script returned without throwing>') { exit 1 }

exit 0
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

        # ---------------------------------------------------------------------------------------
        # The strict plan: every command the four-deployment run makes, written per deployment.
        #
        # Nothing here is permissive. A call the plan does not answer fails with exit 125 and is
        # recorded, and a rule pinned to a deployment cannot answer in another one, so an answer
        # arriving out of order is a failure rather than a pass. That is the point of the plan: a
        # traversal that is allowed to invent successes for unplanned commands proves nothing about
        # the sequence it claims to have walked.
        # ---------------------------------------------------------------------------------------
        $script:proofImageId = 'sha256:' + ('c' * 64)

        function script:New-StatusDocument {
            param([string] $State = 'Ready', [string] $Phase = 'LoadPlugins', [string] $Summary = '', [string] $ErrorMessage = '')

            return (@{ State = $State; Phase = $Phase; Summary = $Summary; ErrorMessage = $ErrorMessage } |
                ConvertTo-Json -Depth 4 -Compress)
        }

        # A deployment that comes up and serves: recipe1 and recipe2.
        function script:Get-ServingDeploymentRule {
            param([int] $Phase, [string] $SelectedPackage)

            $package = if ([string]::IsNullOrWhiteSpace($SelectedPackage)) { $script:pinnedIdentity[0] } else { $SelectedPackage }

            return @(
                @{ phase = $Phase; match = 'bootstrap-published-dms\.ps1 -EnvironmentFile'; exitCode = 0
                    create = @(@{ path = 'bootstrap/bootstrap-manifest.json'
                            content = (@{ schema = @{ selectedPackages = @($package) } } | ConvertTo-Json -Depth 6 -Compress)
                        })
                }
                @{ phase = $Phase; match = 'ps -a --filter label=com\.docker\.compose\.project=dms-published --filter label=com\.docker\.compose\.service=dms '
                    exitCode = 0; output = 'dms-published-dms-1'
                }
                @{ phase = $Phase; match = 'exec dms-published-dms-1 cat /tmp/dms-startup-status\.json'
                    exitCode = 0; output = (New-StatusDocument)
                }
                @{ phase = $Phase; match = 'inspect dms-published-dms-1 --format'
                    exitCode = 0; output = "running|2026-09-21T09:00:00Z|0|$script:proofImageId|false"
                }
                @{ phase = $Phase; match = 'image inspect edfialliance/ed-fi-api@sha256:a{64} --format'
                    exitCode = 0; output = $script:proofImageId
                }
                @{ phase = $Phase; match = 'exec dms-published-dms-1 sh -c cat /proc/mounts'
                    exitCode = 0; output = "overlay / overlay rw,relatime 0 0`n/dev/sda1 /app/plugins ext4 ro,relatime 0 0"
                }
                @{ phase = $Phase; match = 'exec dms-published-dms-1 sh -c touch /app/plugins'
                    exitCode = 1; output = 'touch: /app/plugins/.stock-proof-write-probe: Read-only file system'
                }
            )
        }

        # Deployment 3: the fetch refuses on the digest comparison and DMS is never started.
        function script:Get-WrongDigestDeploymentRule {
            param([string] $FetchLog = "sha256sum: WARNING: 1 of 1 computed checksums did NOT match", [int] $FetchExit = 1,
                [string] $DmsStartedAt = '0001-01-01T00:00:00Z', [string] $DmsStatus = 'created')

            return @(
                @{ phase = 3; match = 'bootstrap-published-dms\.ps1 -EnvironmentFile'; exitCode = 1
                    output = 'dependency failed to start'
                    create = @(@{ path = 'bootstrap/bootstrap-manifest.json'
                            content = (@{ schema = @{ selectedPackages = @($script:pinnedIdentity[0]) } } | ConvertTo-Json -Depth 6 -Compress)
                        })
                }
                @{ phase = 3; match = 'service=fetch-plugins '; exitCode = 0; output = 'dms-published-fetch-plugins-1' }
                @{ phase = 3; match = 'inspect dms-published-fetch-plugins-1 --format'
                    exitCode = 0; output = "exited|2026-09-21T09:00:00Z|$FetchExit|$script:proofImageId|false"
                }
                @{ phase = 3; match = 'logs dms-published-fetch-plugins-1'; exitCode = 0; output = $FetchLog }
                @{ phase = 3; match = 'service=dms '; exitCode = 0; output = 'dms-published-dms-1' }
                @{ phase = 3; match = 'inspect dms-published-dms-1 --format'
                    exitCode = 0; output = "$DmsStatus|$DmsStartedAt|0|$script:proofImageId|false"
                }
            )
        }

        # Deployment 4: DMS refuses to start, records the refusal, and stays stopped.
        function script:Get-MisspelledDeploymentRule {
            # $Status untyped on purpose: a [string] parameter defaults to '', and '' is the case
            # that means "the container wrote nothing", which is one of the mutations below.
            param($Status, [string] $ContainerState = 'exited', [int] $ExitCode = 1, [switch] $NoDocument)

            $document = if ($null -eq $Status) {
                New-StatusDocument -State 'Failed' -Phase 'LoadPlugins' `
                    -ErrorMessage 'The allowlisted plugin directory /app/plugins/Acme.CustomValidationProof1 does not exist.'
            }
            else { $Status }

            $rule = @(
                @{ phase = 4; match = 'bootstrap-published-dms\.ps1 -EnvironmentFile'; exitCode = 1
                    output = 'dms exited with code 1'
                    create = @(@{ path = 'bootstrap/bootstrap-manifest.json'
                            content = (@{ schema = @{ selectedPackages = @($script:pinnedIdentity[0]) } } | ConvertTo-Json -Depth 6 -Compress)
                        })
                }
                @{ phase = 4; match = 'service=dms '; exitCode = 0; output = 'dms-published-dms-1' }
                @{ phase = 4; match = 'inspect dms-published-dms-1 --format'
                    exitCode = 0; output = "$ContainerState|2026-09-21T09:00:00Z|$ExitCode|$script:proofImageId|false"
                }
            )

            if (-not $NoDocument) {
                $rule += @{ phase = 4; match = 'cp dms-published-dms-1:/tmp/dms-startup-status\.json'; exitCode = 0
                    create = @(@{ path = 'dms-startup-status.json'; content = $document })
                }
            }
            else {
                $rule += @{ phase = 4; match = 'cp dms-published-dms-1:/tmp/dms-startup-status\.json'; exitCode = 1; output = 'no such file' }
            }

            return $rule
        }

        # Every request one serving deployment makes, each answerable exactly once, in that
        # deployment only. A duplicate, a call in the wrong deployment, or a call nobody planned for
        # therefore fails rather than being served again.
        function script:Get-DeploymentRequestRule {
            param([int] $Phase, [string] $DataStoreListing = '[{"id":7,"name":"Stock Proof Data Store","dataStoreContexts":[]}]')

            $reject = "This value is the custom-validation proof fixture's reserved rejection token."
            $document = "This document carries the custom-validation proof fixture's reserved document-level rejection token."

            return @(
                @{ phase = $Phase; match = 'req:Post /connect/register'; exitCode = 200; output = '{}'; limit = 1; expect = 1 }
                # Twice per deployment, deliberately: this script asks for a bootstrap-admin token to
                # read the data stores, and Get-SmokeTestCredential asks for one of its own after
                # registering its client. Both are the same endpoint and neither is a duplicate.
                @{ phase = $Phase; match = 'req:Post /connect/token'; exitCode = 200; limit = 2; expect = 2
                    output = '{"access_token":"cms-admin-token-value"}'
                }
                @{ phase = $Phase; match = 'req:Get /v3/dataStores'; exitCode = 200; limit = 1; expect = 1
                    output = $DataStoreListing
                }
                @{ phase = $Phase; match = 'req:Post /v3/vendors'; exitCode = 201; output = '{}'; limit = 1; expect = 1
                    location = '/v3/vendors/3'
                }
                @{ phase = $Phase; match = 'req:Post /v3/applications'; exitCode = 201; limit = 1; expect = 1
                    output = '{"id":11,"key":"proof-client-key","secret":"proof-client-secret-value"}'
                }
                @{ phase = $Phase; match = 'req:Post /oauth/token'; exitCode = 200; limit = 1; expect = 1
                    output = '{"access_token":"dms-access-token-value"}'
                }
                @{ phase = $Phase; match = 'req:Post /data/ed-fi/students'; body = 'stock-proof-control'
                    exitCode = 201; output = '{"id":"stock-proof"}'; limit = 1; expect = 1
                }
                @{ phase = $Phase; match = 'req:Post /data/ed-fi/students'; body = 'custom-validation-proof-reject-path'
                    exitCode = 400; limit = 1; expect = 1
                    output = (@{ validationErrors = @{ '$.lastSurname' = @($reject) }; errors = @() } | ConvertTo-Json -Depth 5 -Compress)
                }
                @{ phase = $Phase; match = 'req:Post /data/ed-fi/students'; body = 'custom-validation-proof-reject-resource'
                    exitCode = 400; limit = 1; expect = 1
                    output = (@{ validationErrors = @{}; errors = @($document) } | ConvertTo-Json -Depth 5 -Compress)
                }
            )
        }

        # Everything before the first stack comes up, and the answers that are the same in every
        # deployment.
        function script:Get-StrictSetupRule {
            return @(
                @(Get-MatchingDescriptorRule) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) |
                    ForEach-Object { $_ }
                @{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 0; output = 'down' }
                # The host preflight, answered as a clear host: no container, no project container,
                # no project volume, no shared network, and nothing publishing a port.
                @{ match = 'ps -a --format \{\{\.Names\}\}'; exitCode = 0; output = '' }
                @{ match = 'ps -a --filter label=com\.docker\.compose\.project=dms-published --format'; exitCode = 0; output = '' }
                @{ match = 'volume ls'; exitCode = 0; output = '' }
                @{ match = 'network inspect'; exitCode = 1; output = 'Error: No such network: dms' }
                @{ match = 'network ls'; exitCode = 0; output = '' }
                @{ match = 'ps --format \{\{\.Ports\}\}'; exitCode = 0; output = '' }
            )
        }

        function script:Get-StrictTraversalPlan {
            param($Deployment3, $Deployment4, [string] $SecondSelectedPackage)

            $three = if ($null -ne $Deployment3) { $Deployment3 } else { Get-WrongDigestDeploymentRule }
            $four = if ($null -ne $Deployment4) { $Deployment4 } else { Get-MisspelledDeploymentRule }

            return @(
                @(Get-ServingDeploymentRule -Phase 1) + @(Get-DeploymentRequestRule -Phase 1) +
                @(Get-ServingDeploymentRule -Phase 2 -SelectedPackage $SecondSelectedPackage) +
                @(Get-DeploymentRequestRule -Phase 2) +
                @($three) + @($four) + @(Get-StrictSetupRule) | ForEach-Object { $_ }
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
                [string] $PathBase,
                [string] $HttpPortOverride,
                [string] $WorkspaceOverride,
                [string] $EvidenceOverride,
                [switch] $Strict
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
                $unmatchedLog = Join-Path $scratch 'unmatched.log'
                New-Item -ItemType File -Path $unmatchedLog -Force | Out-Null
                $resultFile = Join-Path $scratch 'result.json'
                # Beside the workspace, never inside it: the receipt has to survive a workspace
                # removal that then fails, which is the case it exists for.
                $receiptPath = Join-Path $scratch 'stock-image-proof-cleanup.json'
                $evidenceRoot = if ([string]::IsNullOrEmpty($EvidenceOverride)) { Join-Path $scratch 'evidence' } else { $EvidenceOverride }
                $workspace = if ($PSBoundParameters.ContainsKey('WorkspaceOverride')) { $WorkspaceOverride } else { Join-Path $scratch 'workspace' }

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

                if ($PSBoundParameters.ContainsKey('HttpPortOverride')) {
                    $baseLine = @($baseLine | Where-Object { $_ -notmatch '^DMS_HTTP_PORTS=' })
                    if (-not [string]::IsNullOrEmpty($HttpPortOverride)) {
                        $baseLine += "DMS_HTTP_PORTS=$HttpPortOverride"
                    }
                }

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
                    '-CleanupReceiptPath', $receiptPath
                    '-ShimPlan', $planPath
                    '-ShimLog', $shimLog
                    '-HttpLog', $httpLog
                    '-UnmatchedLog', $unmatchedLog
                    '-ResultFile', $resultFile
                )

                if (-not [string]::IsNullOrWhiteSpace($AmbientKey)) {
                    $argument += @('-AmbientKey', $AmbientKey, '-AmbientValue', $AmbientValue)
                }

                if ($Strict) {
                    $argument += '-Strict'
                }

                # The real pwsh, resolved past the shim this session does not have but the child will.
                $pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
                & $pwshPath @argument 2>&1 | Out-Null
                $exitCode = $LASTEXITCODE

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
                    ExitCode         = $exitCode
                    Receipt          = (Test-Path -LiteralPath $receiptPath) ?
                        (Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json) : $null
                    Unmatched        = @(Get-Content -LiteralPath $unmatchedLog -ErrorAction SilentlyContinue |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                    RuleUse          = @(Get-Content -LiteralPath "$unmatchedLog.used" -ErrorAction SilentlyContinue |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
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

    Context 'the whole run, walked under a plan that answers nothing it was not asked' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
            $script:strict = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule (Get-StrictTraversalPlan)
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'exits successfully and says it passed' {
            $script:strict.ExitCode | Should -Be 0
            $script:strict.Failure | Should -BeExactly '<the script returned without throwing>'
            $script:strict.Evidence.outcome | Should -BeExactly 'passed'
            @($script:strict.Evidence.failure) | Should -HaveCount 0
        }

        It 'asked nothing the plan had no answer for' {
            # The claim this makes possible: every command and every request in the run above was
            # one the plan named. A permissive default would make the rest of this Context vacuous.
            $script:strict.Unmatched | Should -HaveCount 0
        }

        It 'records four deployments, in order, each distinguishable from the others' {
            $name = @($script:strict.Evidence.PSObject.Properties.Name |
                    Where-Object { $_ -in @('recipe1', 'recipe2', 'wrongDigest', 'misspelledAllowlist') })

            $name | Should -Be @('recipe1', 'recipe2', 'wrongDigest', 'misspelledAllowlist')
        }

        It 'proves recipe1 through the committed bind-mount recipe, unedited' {
            $recipe = $script:strict.Evidence.recipe1

            # The committed file, by name, first in the list, with only test-owned overlays after
            # it. A copy edited for the test would show up here as a different path.
            $recipe.pluginComposeFiles | Should -BeExactly 'plugins-dms.yml;tests/plugin-deployment/stock-image-pin-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml'
            $recipe.dmsImage | Should -BeExactly ('edfialliance/ed-fi-api:8.0.1-alpha.0.7@sha256:' + ('a' * 64))
            $recipe.readyState | Should -BeExactly 'Ready'
            $recipe.dmsImageId | Should -BeExactly $script:proofImageId
            $recipe.pluginRootReadOnly | Should -BeTrue
            $recipe.http.controlStatus | Should -Be 201
            $recipe.http.pathStatus | Should -Be 400
        }

        It 'proves recipe2 through the committed fetch recipe, unedited' {
            $recipe = $script:strict.Evidence.recipe2

            $recipe.pluginComposeFiles | Should -BeExactly 'plugins-fetch-dms.yml;tests/plugin-deployment/plugins-feed-dms.yml;tests/plugin-deployment/stock-image-pin-dms.yml;tests/plugin-deployment/plugins-allowed-dms.yml'
            $recipe.dmsImage | Should -BeExactly ('edfialliance/ed-fi-api:8.0.1-alpha.0.7@sha256:' + ('a' * 64))
            $recipe.readyState | Should -BeExactly 'Ready'
            $recipe.dmsImageId | Should -BeExactly $script:proofImageId
            $recipe.pluginRootReadOnly | Should -BeTrue
            $recipe.http.controlStatus | Should -Be 201
            $recipe.http.resourceStatus | Should -Be 400
        }

        It 'proves the wrong digest failed on the comparison, with DMS never started' {
            $recipe = $script:strict.Evidence.wrongDigest

            $recipe.fetchExitCode | Should -Be 1
            $recipe.dmsNeverStarted | Should -BeTrue

            # The controls the conclusions were reached from, so a later reader can ask how rather
            # than only what. A failed enumeration and an absent container reach the same boolean.
            $recipe.checksumVerified | Should -BeTrue
            $recipe.fetchLogExcerpt | Should -Match 'did NOT match'
            $recipe.dmsEnumerationSucceeded | Should -BeTrue
            $recipe.dmsInspectSucceeded | Should -BeTrue
            # Compared as an instant, not as text: this test's own ConvertFrom-Json turns the
            # recorded string into a DateTime. The script keeps it as raw text for exactly the
            # reason that conversion exists, and compares it there.
            ([datetime]$recipe.dmsStartedAtRaw) | Should -Be ([datetime]::MinValue)
            $recipe.dmsNeverStartedReason | Should -Match 'never started'
        }

        It 'proves the misspelled allowlist refused, naming the path it looked for' {
            $recipe = $script:strict.Evidence.misspelledAllowlist

            $recipe.state | Should -BeExactly 'Failed'
            $recipe.phase | Should -BeExactly 'LoadPlugins'
            $recipe.dmsStatus | Should -BeExactly 'exited'
            $recipe.dmsExitCode | Should -Be 1

            # In the artifact, not only inside the harness that checked it. Without these the record
            # says a refusal happened but not that it was about the misspelled entry.
            $recipe.expectedPath | Should -BeExactly '/app/plugins/Acme.CustomValidationProof1'
            $recipe.refusal | Should -Match 'Acme\.CustomValidationProof1'
        }

        It 'checks the prepared schema of all four deployments, not one of them' {
            foreach ($name in @('recipe1', 'recipe2', 'wrongDigest', 'misspelledAllowlist')) {
                @($script:strict.Evidence.$name.preparedSchemaPackages.observed) |
                    Should -Be @('EdFi.DataStandard52.ApiSchema@1.0.335') -Because "$name was checked"
            }
        }

        It 'ran no command that builds an image, across the whole traversal' {
            $script:strict.Evidence.buildCommandAbsent.Verified | Should -BeTrue
            @($script:strict.ShimCall | Where-Object { $_ -match '(?i)docker (compose .*)?build\b' }) | Should -BeNullOrEmpty
        }

        It 'used every answer the plan carried, so the plan describes this run and no other' {
            # A rule nothing reached is an answer the run did not need, which means the plan is
            # describing something else. Reported by index against the plan it was built from.
            $plan = @(Get-StrictTraversalPlan)
            $used = @{}
            foreach ($entry in $script:strict.RuleUse) {
                $part = $entry -split '='
                $used[[int]$part[0]] = [int]$part[1]
            }

            $unused = @(0..($plan.Count - 1) | Where-Object { -not $used.ContainsKey($_) } |
                    ForEach-Object { "$_ $($plan[$_].match)" })

            $unused | Should -HaveCount 0
        }

        It 'gave every deployment back' {
            $script:strict.Evidence.cleanup.attempted | Should -BeTrue
            $script:strict.Evidence.cleanup.succeeded | Should -BeTrue
            $script:strict.WorkspaceCreated | Should -BeFalse
        }
    }

    Context 'a later deployment may not be answered by an earlier one' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'fails at the deployment whose prepared schema does not match, having accepted the first' {
            # R9B behaviourally: deployment 1 stages the pinned set and passes, deployment 2 stages
            # something else. A check hoisted out of the per-deployment path would pass this.
            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict `
                -ShimRule (Get-StrictTraversalPlan -SecondSelectedPackage 'EdFi.Wrong@9.9.9')

            $run.ExitCode | Should -Be 1
            $run.Evidence.outcome | Should -BeExactly 'failed'
            $run.Evidence.primaryFailure | Should -Match 'The recipe2 deployment prepared schema packages the pin does not name'
            $run.Evidence.primaryFailure | Should -Match 'EdFi\.Wrong@9\.9\.9'

            # And the first deployment was accepted, which is what makes this a later-deployment
            # failure rather than a run that never started.
            @($run.Evidence.recipe1.preparedSchemaPackages.observed) |
                Should -Be @('EdFi.DataStandard52.ApiSchema@1.0.335')
        }

        It 'refuses a call once the answer meant for it has been used up' {
            # The teardown runs before and after every deployment. Capped at one, the second call
            # has no answer, and that is a failure rather than a silent success.
            $plan = @(Get-StrictTraversalPlan | ForEach-Object {
                    if ($_.match -eq 'bootstrap-published-dms\.ps1 -d -v') { $_['limit'] = 1 }
                    $_
                })

            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan

            $run.ExitCode | Should -Be 1
            @($run.Unmatched | Where-Object { $_ -match 'bootstrap-published-dms\.ps1 -d -v' }) | Should -Not -BeNullOrEmpty
        }

        It 'fails when a known request is made more often than the plan allows' {
            # This script asks for a bootstrap-admin token and Get-SmokeTestCredential asks for one
            # of its own, so two is correct. Told to expect one, the child fails on the second: the
            # plan counts calls rather than merely recognising them.
            $plan = @(Get-StrictTraversalPlan | ForEach-Object {
                    if ($_.match -eq 'req:Post /connect/token' -and $_['phase'] -eq 1) {
                        $_['limit'] = 1
                        $_['expect'] = 1
                    }
                    $_
                })

            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan

            $run.ExitCode | Should -Be 1
            $run.Failure | Should -Match 'did not walk the planned sequence'
            $run.Failure | Should -Match 'no answer for: unmatched-request.*connect/token'
        }

        It 'fails when a request is answered from another deployment''s entry' {
            # The control POST of deployment 1, offered only for deployment 2.
            $plan = @(Get-StrictTraversalPlan | ForEach-Object {
                    if ($_.ContainsKey('body') -and $_['body'] -eq 'stock-proof-control' -and $_['phase'] -eq 1) {
                        $_['phase'] = 2
                    }
                    $_
                })

            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan

            $run.ExitCode | Should -Be 1
            $run.Failure | Should -Match 'request-wrong-phase'
        }

        It 'fails when the plan carries an answer nothing reaches' {
            $plan = @(Get-StrictTraversalPlan) + @{ match = 'req:Post /v3/claimSets'; exitCode = 200; output = '{}'; required = $true }

            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan

            $run.ExitCode | Should -Be 1
            $run.Failure | Should -Match "required 'req:Post /v3/claimSets' and nothing reached it"
        }

        It 'refuses an answer meant for another deployment rather than accepting it' {
            # The wrong-digest answers, offered while deployment 1 is running. Under the phase rule
            # they cannot answer, so the run fails and the call is recorded as unanswered.
            # Hashtables, so ContainsKey rather than a PSObject property lookup: the latter finds
            # nothing on a hashtable and would quietly leave the plan whole.
            $plan = @(Get-StrictTraversalPlan | Where-Object { -not ($_.ContainsKey('phase') -and $_['phase'] -eq 1) })

            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan

            $run.ExitCode | Should -Be 1
            @($run.Unmatched | Where-Object { $_ -match 'phase=1' }) | Should -Not -BeNullOrEmpty
        }
    }

    Context 'evidence that was altered, refused one mutation at a time' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        function script:Invoke-Mutated {
            param([scriptblock] $Mutate)

            $plan = @(Get-StrictTraversalPlan | ForEach-Object { $_ })
            $plan = @(& $Mutate $plan)

            return Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule $plan
        }

        function script:Set-RequestRule {
            param($Plan, [string] $Body, [int] $Phase, [hashtable] $Change)

            foreach ($rule in $Plan) {
                if ($rule.ContainsKey('body') -and $rule['body'] -eq $Body -and $rule['phase'] -eq $Phase) {
                    foreach ($key in $Change.Keys) { $rule[$key] = $Change[$key] }
                }
            }

            return $Plan
        }

        function script:Set-Rule {
            param($Plan, [string] $Match, [int] $Phase, [hashtable] $Change)

            foreach ($rule in $Plan) {
                if ($rule.match -eq $Match -and $rule.phase -eq $Phase) {
                    foreach ($key in $Change.Keys) { $rule[$key] = $Change[$key] }
                }
            }

            return $Plan
        }

        It 'refuses an inspect that failed, naming that rather than an absent container' {
            # Three facts that used to be one. A failed inspect is not a failed enumeration and
            # neither is an absent container, and the message has to say which happened.
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'inspect dms-published-dms-1 --format' -Phase 3 -Change @{ exitCode = 1; output = 'Error: No such object' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'located but could not be inspected'
            $run.Evidence.primaryFailure | Should -Not -Match 'container list could not be read'
        }

        It 'refuses a container enumeration that failed in the wrong-digest deployment' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'service=dms ' -Phase 3 -Change @{ exitCode = 1; output = 'boom' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'a failed enumeration is not evidence of absence'
        }

        It 'refuses a generic 400 that is not the fixture''s rejection' {
            $run = Invoke-Mutated {
                param($p)
                Set-RequestRule -Plan $p -Body 'custom-validation-proof-reject-path' -Phase 1 -Change @{
                    output = '{"validationErrors":{"$.lastSurname":["lastSurname is required."]},"errors":[]}'
                }
            }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'does not carry the fixture''s exact message'
        }

        It 'refuses the fixture message when it arrives under the wrong arm' {
            # Both arms are present in every DMS write-path 400, so the message sitting under the
            # other one is exactly the confusion the arm check exists for.
            $run = Invoke-Mutated {
                param($p)
                Set-RequestRule -Plan $p -Body 'custom-validation-proof-reject-path' -Phase 1 -Change @{
                    output = '{"validationErrors":{},"errors":["This value is the custom-validation proof fixture''s reserved rejection token."]}'
                }
            }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'carries no validationErrors object|no entry for'
        }

        It 'refuses a control document that did not succeed' {
            $run = Invoke-Mutated {
                param($p)
                Set-RequestRule -Plan $p -Body 'stock-proof-control' -Phase 1 -Change @{ exitCode = 500 }
            }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'passing control POST returned HTTP 500'
        }

        It 'refuses a fetch that failed for transport reasons rather than the checksum' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'logs dms-published-fetch-plugins-1' -Phase 3 -Change @{ output = 'wget: bad address plugin-feed' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'carries no checksum comparison failure'
            $run.Evidence.primaryFailure | Should -Match 'transport failure'
        }

        It 'refuses a fetch that succeeded in the wrong-digest deployment' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'inspect dms-published-fetch-plugins-1 --format' -Phase 3 -Change @{ output = "exited|2026-09-21T09:00:00Z|0|x|false" } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'exited 0, so the wrong digest was accepted'
        }

        It 'refuses a wrong-digest deployment that produced no DMS container at all' {
            # The checksum half is correct. What is missing is the container the claim is about:
            # a project that composed no DMS service is consistent with the dependency working and
            # equally with the service having been dropped, so it establishes nothing.
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'service=dms ' -Phase 3 -Change @{ output = '' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'returned no DMS container'
            $run.Evidence.primaryFailure | Should -Not -Match 'failed enumeration'
        }

        It 'refuses a DMS that started and then exited in the wrong-digest deployment' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'inspect dms-published-dms-1 --format' -Phase 3 -Change @{ output = "exited|2026-09-21T09:00:00Z|1|x|false" } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'started at .* and then exited, which is not the same as never starting'
        }

        It 'refuses a Ready status where a refusal was required' {
            $run = Invoke-Mutated {
                param($p)
                Set-Rule -Plan $p -Match 'cp dms-published-dms-1:/tmp/dms-startup-status\.json' -Phase 4 -Change @{
                    create = @(@{ path = 'dms-startup-status.json'; content = (New-StatusDocument -State 'Ready' -Phase 'LoadPlugins') })
                }
            }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match "State 'Ready'"
        }

        It 'refuses a missing startup document rather than falling back to the log' {
            $run = Invoke-Mutated {
                param($p)
                @($p | Where-Object { -not ($_.match -eq 'cp dms-published-dms-1:/tmp/dms-startup-status\.json' -and $_.phase -eq 4) })
            }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'wrote no readable startup status'
        }

        It 'refuses a DMS that exited 0 where a refusal was required' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'inspect dms-published-dms-1 --format' -Phase 4 -Change @{ output = "exited|2026-09-21T09:00:00Z|0|x|false" } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'exited 0, so the refusal did not stop startup'
        }

        It 'refuses a plugin mount that is not read-only' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'exec dms-published-dms-1 sh -c cat /proc/mounts' -Phase 1 -Change @{ output = '/dev/sda1 /app/plugins ext4 rw,relatime 0 0' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'not read-only; its options are'
        }

        It 'refuses a write probe that failed for a reason other than a read-only file system' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'exec dms-published-dms-1 sh -c touch /app/plugins' -Phase 1 -Change @{ output = 'touch: Permission denied' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'failed for a reason other than a read-only file system'
        }

        It 'refuses a write probe that succeeded' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'exec dms-published-dms-1 sh -c touch /app/plugins' -Phase 1 -Change @{ exitCode = 0; output = '' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'a write into /app/plugins succeeded'
        }

        It 'refuses a mount table that could not be read' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'exec dms-published-dms-1 sh -c cat /proc/mounts' -Phase 1 -Change @{ exitCode = 1; output = '' } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'this control was not verified rather than verified as holding'
        }

        It 'refuses a container running an image other than the pinned digest''s' {
            $run = Invoke-Mutated { param($p) Set-Rule -Plan $p -Match 'image inspect edfialliance/ed-fi-api@sha256:a{64} --format' -Phase 1 -Change @{ output = 'sha256:' + ('d' * 64) } }

            $run.ExitCode | Should -Be 1
            $run.Evidence.primaryFailure | Should -Match 'not the pinned digest'
        }
    }

    Context 'every path this run would own, refused through the script' {
        BeforeAll {
            # The same runner every other Context uses, with the two paths varied. A bespoke runner
            # here would be testing a second harness rather than the script.
            function script:Invoke-WithPath {
                param([string] $Workspace, [string] $Evidence)

                return (Invoke-EntryScript -WorkspaceOverride $Workspace -EvidenceOverride $Evidence).Failure
            }

            $script:freshWorkspace = { Join-Path ([IO.Path]::GetTempPath()) "dms1502-ws-$([guid]::NewGuid().ToString('N'))" }
            $script:freshEvidence = { Join-Path ([IO.Path]::GetTempPath()) "dms1502-ev-$([guid]::NewGuid().ToString('N'))" }
        }

        It 'refuses a workspace that already exists, rather than emptying it' {
            $existing = & $script:freshWorkspace
            New-Item -ItemType Directory -Path $existing -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $existing 'somebody-elses-file.txt') -Value 'x'

            try {
                $failure = Invoke-WithPath -Workspace $existing -Evidence (& $script:freshEvidence)

                $failure | Should -Match 'already exists'
                # And it is still there.
                Test-Path -LiteralPath (Join-Path $existing 'somebody-elses-file.txt') | Should -BeTrue
            }
            finally {
                Remove-Item -LiteralPath $existing -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'judges a rooted path by where it resolves, not by its spelling' {
            # Rooted, so it is not refused for being relative, but carrying '..' segments that
            # resolve to the directory above the repository. Normalization happens first and the
            # resolved location is what is judged.
            $repositoryRoot = Split-Path -Parent (Split-Path -Parent $script:composeRoot)
            $failure = Invoke-WithPath -Workspace (Join-Path $repositoryRoot 'eng/../..') -Evidence (& $script:freshEvidence)

            $failure | Should -Match 'contains the repository'
        }

        It 'refuses a workspace at a filesystem root' {
            $root = [IO.Path]::GetPathRoot([IO.Path]::GetTempPath())

            (Invoke-WithPath -Workspace $root -Evidence (& $script:freshEvidence)) | Should -Match 'not ones this run may own'
        }

        It 'refuses a workspace that contains the repository' {
            # The repository's parent. <repo>/eng is inside the repository, not around it, and would
            # have been refused for a different reason or not at all.
            $repositoryRoot = Split-Path -Parent (Split-Path -Parent $script:composeRoot)
            $failure = Invoke-WithPath -Workspace (Split-Path -Parent $repositoryRoot) -Evidence (& $script:freshEvidence)

            $failure | Should -Match 'contains the repository'
        }

        It 'refuses a <Case> that is relative, even when it names somewhere safe' -ForEach @(
            @{ Case = 'workspace' }
            @{ Case = 'evidence directory' }
        ) {
            # The companion path is absolute and safe, so the refusal is about this one. GetFullPath
            # would have made it absolute against whatever the working directory was, which is a
            # different directory on a different day.
            $relative = "dms1502-relative-$([guid]::NewGuid().ToString('N'))"

            $failure = if ($Case -eq 'workspace') {
                Invoke-WithPath -Workspace $relative -Evidence (& $script:freshEvidence)
            }
            else {
                Invoke-WithPath -Workspace (& $script:freshWorkspace) -Evidence $relative
            }

            $failure | Should -Match 'is not absolute'
            Test-Path -LiteralPath (Join-Path $script:composeRoot $relative) | Should -BeFalse
        }

        It 'refuses an evidence directory whose ancestry passes through a junction' {
            $external = Join-Path ([IO.Path]::GetTempPath()) "dms1502-etarget-$([guid]::NewGuid().ToString('N'))"
            $link = Join-Path ([IO.Path]::GetTempPath()) "dms1502-elink-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $external -Force | Out-Null

            try {
                New-Item -ItemType Junction -Path $link -Target $external | Out-Null
                $workspace = & $script:freshWorkspace

                $run = Invoke-EntryScript -WorkspaceOverride $workspace -EvidenceOverride (Join-Path $link 'evidence')

                $run.Failure | Should -Match 'which is a link or junction'
                $run.ShimCall | Should -HaveCount 0
                $run.WorkspaceCreated | Should -BeFalse
                Test-Path -LiteralPath (Join-Path $external 'evidence') | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $link -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $external -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'refuses an evidence directory inside the workspace' {
            # Cleanup removes the workspace, and the evidence is the record of why the run failed.
            $workspace = & $script:freshWorkspace

            (Invoke-WithPath -Workspace $workspace -Evidence (Join-Path $workspace 'evidence')) |
                Should -Match 'inside the workspace'
        }

        It 'refuses a workspace inside the evidence directory' {
            $evidence = & $script:freshEvidence

            (Invoke-WithPath -Workspace (Join-Path $evidence 'workspace') -Evidence $evidence) |
                Should -Match 'contains the evidence directory'
        }

        It 'refuses a workspace whose ancestry passes through a junction, creating nothing' {
            $external = Join-Path ([IO.Path]::GetTempPath()) "dms1502-target-$([guid]::NewGuid().ToString('N'))"
            $link = Join-Path ([IO.Path]::GetTempPath()) "dms1502-link-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $external -Force | Out-Null

            try {
                New-Item -ItemType Junction -Path $link -Target $external | Out-Null

                (Invoke-WithPath -Workspace (Join-Path $link 'workspace') -Evidence (& $script:freshEvidence)) |
                    Should -Match 'which is a link or junction'
            }
            finally {
                Remove-Item -LiteralPath $link -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $external -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'nothing ambient, and ports that are actually usable' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'refuses an ambient <_>, whichever key it is' -ForEach @(
            'DMS_STOCK_IMAGE_REFERENCE', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_IMAGE_TAG',
            'DMS_CONFIG_DATA_STANDARD_VERSION', 'SCHEMA_PACKAGES', 'DMS_PLUGINS_COMPOSE_FILES',
            'DMS_PLUGINS_ALLOWED', 'DMS_PLUGINS_MOUNT_SOURCE', 'PLUGIN_FEED_SOURCE',
            'PLUGIN_PACKAGE_URL', 'PLUGIN_PACKAGE_SHA256', 'PLUGIN_NAME',
            'DMS_HTTP_PORTS', 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS', 'POSTGRES_PORT'
        ) {
            # Every key Get-AmbientRefusedKey names, through the script, including the three port
            # keys, which are refused ambiently without being rewritten in the file.
            $run = Invoke-EntryScript -PinPath $script:pinPath -AmbientKey $_ -AmbientValue '9'

            $run.Failure | Should -Match ([regex]::Escape($_))
            $run.ShimCall | Should -HaveCount 0
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'covers every key the module refuses, with no test-only list of its own' {
            # The -ForEach above is a literal list, so this is what keeps it honest when the module
            # gains a key.
            $covered = @(
                'DMS_STOCK_IMAGE_REFERENCE', 'DMS_CONFIG_DOCKER_IMAGE', 'DMS_IMAGE_TAG',
                'DMS_CONFIG_DATA_STANDARD_VERSION', 'SCHEMA_PACKAGES', 'DMS_PLUGINS_COMPOSE_FILES',
                'DMS_PLUGINS_ALLOWED', 'DMS_PLUGINS_MOUNT_SOURCE', 'PLUGIN_FEED_SOURCE',
                'PLUGIN_PACKAGE_URL', 'PLUGIN_PACKAGE_SHA256', 'PLUGIN_NAME',
                'DMS_HTTP_PORTS', 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS', 'POSTGRES_PORT'
            )

            @(Get-AmbientRefusedKey | Sort-Object) | Should -Be @($covered | Sort-Object)
        }

        It 'refuses a port that is <Case>' -ForEach @(
            @{ Case = 'missing'; Value = '' ; Expect = 'declares no DMS_HTTP_PORTS' }
            @{ Case = 'zero'; Value = '0'; Expect = 'is 0, which is not a usable TCP port' }
            @{ Case = 'out of range'; Value = '70000'; Expect = 'is 70000, which is not a usable TCP port' }
            @{ Case = 'not a number'; Value = 'eighty'; Expect = "is 'eighty', which is not a number" }
        ) {
            $run = Invoke-EntryScript -PinPath $script:pinPath -HttpPortOverride $Value

            $run.Failure | Should -Match ([regex]::Escape($Expect))
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'sends its requests to the base the environment file declares: <Case>' -ForEach @(
            @{ Case = 'no path base'; PathBase = ''; Prefix = '' }
            @{ Case = 'a path base'; PathBase = 'api'; Prefix = '/api' }
        ) {
            # UsePathBase serves every route at both the root and the prefix, so an empty PATH_BASE
            # is correct rather than missing. What matters is that the guarded port and the address
            # the requests actually went to are the same place.
            $run = Invoke-EntryScript -PinPath $script:pinPath -PathBase $PathBase -Strict -ShimRule (Get-StrictTraversalPlan)

            $run.ExitCode | Should -Be 0

            $student = @($run.Http | Where-Object { $_.uri -match '/data/ed-fi/students' })
            $student | Should -Not -BeNullOrEmpty

            foreach ($call in $student) {
                $call.uri | Should -BeExactly "http://localhost:$($run.Port[0])$Prefix/data/ed-fi/students"
            }
        }
    }

    Context 'the permission an outside caller needs, and when it exists' {
        BeforeEach {
            $script:pinPath = Join-Path ([IO.Path]::GetTempPath()) "dms1502-pin-$([guid]::NewGuid().ToString('N')).json"
            New-PublishedPinFile -Path $script:pinPath
        }

        AfterEach {
            Remove-Item -LiteralPath $script:pinPath -Force -ErrorAction SilentlyContinue
        }

        It 'leaves no receipt when the run put back what it created' {
            # A complete run tore down everything it started, so there is nothing an always() step
            # in a scheduled job may remove.
            $run = Invoke-EntryScript -PinPath $script:pinPath -Strict -ShimRule (Get-StrictTraversalPlan)

            $run.ExitCode | Should -Be 0
            $run.Receipt | Should -BeNullOrEmpty
        }

        It 'leaves no receipt when the preflight refused, because nothing was created' {
            # The case the receipt exists to make safe: somebody else's container holds a name this
            # run wanted, so it claimed nothing. An unconditional teardown would remove their stack.
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) +
                @(@{ match = 'ps -a --format'; exitCode = 0; output = 'ed-fi-api' }) | ForEach-Object { $_ })

            $run.Failure | Should -Match 'This host is not available'
            $run.Receipt | Should -BeNullOrEmpty
            $run.WorkspaceCreated | Should -BeFalse
        }

        It 'leaves a receipt naming the last deployment when its own teardown failed' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) |
                ForEach-Object { $_ })

            $run.ExitCode | Should -Be 1
            $run.Receipt | Should -Not -BeNullOrEmpty
            $run.Receipt.composeProject | Should -BeExactly 'dms-published'
            # The environment file of the deployment that may still be up, not a default.
            $run.Receipt.environmentFile | Should -Match 'recipe1\.env$'
            $run.Receipt.workspaceRoot | Should -Not -BeNullOrEmpty
        }

        It 'writes the receipt before the command that could create the project' {
            $run = Invoke-EntryScript -PinPath $script:pinPath -ShimRule @(
                @((Get-MatchingDescriptorRule)) + @(Get-InstalledToolRule) + @(Get-PublishedFixtureRule) +
                @(Get-BootstrapRule -SelectedPackages $script:pinnedIdentity) +
                @(@{ match = 'bootstrap-published-dms\.ps1 -d -v'; exitCode = 1; output = 'down failed' }) |
                ForEach-Object { $_ })

            # The ordering is the claim. A process killed between the two would leave a stack that
            # nothing has permission to remove.
            $log = @($run.Evidence.commandLog)
            $receiptAt = [array]::FindIndex($log, [Predicate[string]] { param($entry) $entry -like 'receipt *' })
            $upAt = [array]::FindIndex($log, [Predicate[string]] { param($entry) $entry -match 'bootstrap-published-dms\.ps1 -EnvironmentFile' })

            $receiptAt | Should -BeGreaterThan -1
            $upAt | Should -BeGreaterThan -1
            $receiptAt | Should -BeLessThan $upAt
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
