# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Proves plugin loading end to end against a PULLED, published Ed-Fi API image.

.DESCRIPTION
    DESTRUCTIVE TO THE PUBLISHED LOCAL STACK, and it refuses to start rather than being destructive
    to anything it did not create. It deploys under the ordinary published Compose project,
    dms-published, whose compose files hard-code container names that no project name can move:
    dms-postgresql, ed-fi-api-config-service, ed-fi-api-swagger-ui and dms-keycloak. It also uses the
    one shared eng/docker-compose/.bootstrap path. So the preflight refuses when any of those, or a
    leftover container or volume of that project, or a foreign container on the shared external
    network, or a required host port, is already in use. There is no override switch: when something
    else is using the host, the only safe answer is to stop and say what.

    The one thing this proves that Invoke-PluginDeploymentCheck.ps1 cannot is the word "stock". That
    harness builds the image it deploys; this one pulls a published artifact named by
    stock-image-pin.json and never builds an image at all. Building and packing the fixture plugin is
    permitted and is not a build of DMS. The provisioning tool is the RELEASED SchemaTools package at
    the pinned version rather than this worktree's build output, so the database the pinned runtime
    validates is provisioned by that release's own tool.

    In order:

    0. Nothing may quietly decide what this runs against. Any governed key already set in the
       process environment is refused, because Compose prefers a process variable over --env-file.
       The pin is then validated in full; a pending or malformed pin stops here.

    1. The registry is asked what the pinned tag currently resolves to, and it must be the pinned
       digest. A tag and a digest are two claims and only a registry can say they are one artifact.

    2. The host is inventoried and the preflight decides. A gathering command that FAILS is a
       blocker, not an empty inventory.

    3. Only now is anything created, and every resource is claimed before the operation that may
       create it, so cleanup covers a compose up that came halfway.

    4. Four deployments: both committed recipes as happy paths, a wrong digest, and a misspelled
       allowlist. Each asserts through the decision helpers, which separate the outcome claimed from
       the near miss that would otherwise pass for it.

    Evidence is written under .ai-work/verification, which is excluded from the repository, with
    credential-bearing values redacted.

.PARAMETER PinFile
    The published artifacts to run against. Defaults to the committed pin, which ships pending.

.PARAMETER WorkspaceRoot
    Where the fixture, its package and the generated environment file are written.

.PARAMETER EvidenceRoot
    Where the run's evidence and command log are written.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'A verification harness with no interactive surface. Every state change is inside its own scratch workspace or its own Compose project, and the preflight refuses when either is already in use.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside the functions below, which the rule does not follow.')]
[CmdletBinding()]
param(
    [string]
    $PinFile,

    [string]
    $WorkspaceRoot,

    [string]
    $EvidenceRoot,

    # The environment file the deployment is composed from. The ports it declares are the ports the
    # stack binds and therefore the ports the preflight guards, so naming a different one is how a
    # caller runs this against a differently configured stack rather than a way around any check.
    [string]
    $BaseEnvironmentFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$composeRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $composeRoot)

Import-Module (Join-Path $PSScriptRoot 'stock-image-proof.psm1') -Force
Import-Module (Join-Path $composeRoot 'env-utility.psm1') -DisableNameChecking

if ([string]::IsNullOrWhiteSpace($PinFile)) {
    $PinFile = Join-Path $PSScriptRoot 'stock-image-pin.json'
}

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $repositoryRoot '.ai-work/stock-image-proof'
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot '.ai-work/verification'
}

# Absolute before anything reads them: a relative path names a different directory after a
# Push-Location, and every safety check below is about a specific place on disk.
$WorkspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)

$composeProject = 'dms-published'
$bootstrapPath = Join-Path $composeRoot '.bootstrap'
$pluginName = 'Acme.CustomValidationProof'
$fixtureProject = Join-Path $repositoryRoot "eng/fixtures/plugins/$pluginName/$pluginName.csproj"

if ([string]::IsNullOrWhiteSpace($BaseEnvironmentFile)) {
    $BaseEnvironmentFile = Join-Path $composeRoot '.env.e2e'
}

$BaseEnvironmentFile = [IO.Path]::GetFullPath($BaseEnvironmentFile)

# The ports this run's stack actually binds, read from the environment file it runs from rather
# than written out a second time here. The composer governs no port key - DMS_HTTP_PORTS publishes
# the container's own listening port as well as the host's - so what the base declares is what the
# deployment takes, and the preflight has to guard exactly those.
$baseEnvironment = ReadValuesFromEnvFile $BaseEnvironmentFile
$dmsPort = [int]$baseEnvironment['DMS_HTTP_PORTS']
$configurationServicePort = [int]$baseEnvironment['DMS_CONFIG_ASPNETCORE_HTTP_PORTS']
$requiredPort = @($dmsPort, $configurationServicePort, [int]$baseEnvironment['POSTGRES_PORT'])

$script:commandLog = [System.Collections.Generic.List[string]]::new()
$script:ownership = Get-StockProofOwnership
$script:secret = @()
$script:teardownFailed = $false
$script:cleanupError = [System.Collections.Generic.List[string]]::new()

function Write-Phase([string]$text) {
    Write-Information '' -InformationAction Continue
    Write-Information "=== $text ===" -InformationAction Continue
}

function Write-Detail([string]$text) {
    Write-Information "  $text" -InformationAction Continue
}

# One ordered record of what this run did, in sequence, with ownership claims interleaved among the
# commands. The ordering is the evidence that a resource was claimed BEFORE the operation that could
# create it, which a separate list of claims could not show.
function Write-ProofLog([string]$entry) {
    $script:commandLog.Add((Protect-StockProofText -Text $entry -Secret $script:secret))
}

function Add-ProofOwnership([string]$Kind, [string]$Name) {
    Write-ProofLog "own $Kind $Name"
    Add-OwnedResource -Ownership $script:ownership -Kind $Kind -Name $Name | Out-Null
}

# Every external process this run launches. Recorded before it runs, so a command that never
# returned is still in the log, and so the no-build guard reads what was attempted rather than what
# succeeded.
function Invoke-Recorded {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList,
        [switch] $AllowFailure
    )

    Write-ProofLog "$FilePath $($ArgumentList -join ' ')"

    $output = & $FilePath @ArgumentList 2>&1
    $exitCode = $LASTEXITCODE

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "$FilePath $($ArgumentList -join ' ') failed with exit code $exitCode. $(Protect-StockProofText -Text ($output | Out-String) -Secret $script:secret)"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = ($output | Out-String)
    }
}

function Invoke-Docker {
    param([Parameter(Mandatory)] [string[]] $ArgumentList, [switch] $AllowFailure)

    return Invoke-Recorded -FilePath 'docker' -ArgumentList $ArgumentList -AllowFailure:$AllowFailure
}

# An inventory and whether it could be taken at all. The two are returned together because the
# caller must not be able to read a failure as an empty list.
function Get-Inventory {
    param([Parameter(Mandatory)] [string] $What, [Parameter(Mandatory)] [string[]] $ArgumentList)

    $result = Invoke-Docker -ArgumentList $ArgumentList -AllowFailure

    if ($result.ExitCode -ne 0) {
        return [pscustomobject]@{ Item = @(); Failure = "$What (docker $($ArgumentList -join ' ') exited $($result.ExitCode))" }
    }

    $item = @($result.Output -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    return [pscustomobject]@{ Item = $item; Failure = $null }
}

function Write-Evidence {
    param([Parameter(Mandatory)] $Result)

    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    $path = Join-Path $EvidenceRoot 'stock-image-plugin-proof.json'

    $Result | Add-Member -NotePropertyName commandLog -NotePropertyValue @($script:commandLog) -Force
    $Result | Add-Member -NotePropertyName completedUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force

    $json = Protect-StockProofText -Text ($Result | ConvertTo-Json -Depth 10) -Secret $script:secret
    Set-Content -LiteralPath $path -Value $json -Encoding utf8
    Write-Detail "wrote $path"
}

# Only what this run claimed, and only ever that. A run that refused at the preflight claimed
# nothing, so this removes nothing - which is what stops a caller's always() teardown from removing
# a stack somebody else is using. Pulled images are deliberately not removed: they are a shared
# cache this run did not create.
# Recursive removal, permitted only inside something this run claimed. The path is resolved first,
# so a link or a "..\" segment cannot walk out of the owned tree and then be approved by its
# spelling.
function Remove-OwnedTree {
    param([Parameter(Mandatory)] [string] $Path, [string[]] $AlsoOwned = @())

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $full = (Resolve-Path -LiteralPath $Path).Path
    $owned = @(Get-CleanupPlan -Ownership $script:ownership | Where-Object { $_.Kind -eq 'Directory' } | ForEach-Object { $_.Name }) + $AlsoOwned

    if (-not (Test-OwnedDeletionPath -Path $full -OwnedDirectory $owned)) {
        throw "Refusing to remove '$full': this run did not create it."
    }

    Remove-Item -LiteralPath $full -Recurse -Force
}

function Invoke-OwnedCleanup {
    # Wrapped: a function returning an empty collection hands back nothing, and under StrictMode
    # reading .Count off that would throw here in the finally, replacing whatever failure brought
    # the run to cleanup with an error about cleanup.
    $plan = @(Get-CleanupPlan -Ownership $script:ownership)

    if ($plan.Count -eq 0) {
        Write-Detail 'nothing was claimed by this run, so nothing is torn down'
        return
    }

    foreach ($resource in $plan) {
        try {
            switch ($resource.Kind) {
                'ComposeProject' {
                    Push-Location $composeRoot
                    try {
                        Invoke-Recorded -FilePath 'pwsh' -AllowFailure -ArgumentList @(
                            '-NoProfile', '-File', (Join-Path $composeRoot 'bootstrap-published-dms.ps1')
                            '-d', '-v', '-EnvironmentFile', $script:environmentFile
                        ) | Out-Null
                    }
                    finally {
                        Pop-Location
                    }
                }
                'Bootstrap' {
                    # Only after the stack is down. The staged workspace is bind-mounted into the
                    # running containers, so removing it under a surviving stack would leave that
                    # stack reading files that are no longer there.
                    if ($script:teardownFailed) {
                        Write-Detail "keeping $($resource.Name): the stack did not come down, and it is still mounted"
                    }
                    else {
                        Remove-OwnedTree -Path $resource.Name -AlsoOwned @($bootstrapPath)
                    }
                }
                'Directory' {
                    Remove-OwnedTree -Path $resource.Name
                }
            }
        }
        catch {
            # Collected, not swallowed: a run that could not put the host back is not a passing run,
            # and the remaining resources are still attempted so as much is released as can be.
            $script:cleanupError.Add("$($resource.Kind) $($resource.Name): $(Protect-StockProofText -Text $_.Exception.Message -Secret $script:secret)")
        }
    }
}

# -------------------------------------------------------------------------------------------------
# Phase 0: nothing may quietly decide what this runs against
# -------------------------------------------------------------------------------------------------

function Assert-NoAmbientOverride {
    Write-Phase 'Phase 0a: the process environment'

    $processEnvironment = @{}
    foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $processEnvironment[[string]$entry.Key] = [string]$entry.Value
    }

    $override = @(Get-AmbientOverride -ProcessEnvironment $processEnvironment)

    if ($override.Count -gt 0) {
        throw ("The process environment already sets $($override -join ', '). Docker Compose prefers a " +
            'process variable over the same key in an --env-file, so these would decide what this proof runs ' +
            'against and the pin would describe something else. Unset them and run again. Their values are withheld.')
    }

    Write-Detail 'no governed key is set ambiently'
}

function Assert-PathsSafeToOwn {
    Write-Phase 'Phase 0c: the paths this run would own'

    $verdict = Test-ProofPathSafety -WorkspacePath $WorkspaceRoot -EvidencePath $EvidenceRoot `
        -RepositoryRoot $repositoryRoot -ComposeRoot $composeRoot `
        -WorkspaceExists (Test-Path -LiteralPath $WorkspaceRoot)

    if (-not $verdict.Safe) {
        throw ("These paths are not ones this run may own:" + [Environment]::NewLine +
            (($verdict.Blocker | ForEach-Object { "  - $_" }) -join [Environment]::NewLine))
    }

    Write-Detail "workspace: $WorkspaceRoot"
    Write-Detail "evidence:  $EvidenceRoot"
}

function Get-ValidatedPin {
    Write-Phase 'Phase 0b: the pin'

    # The same validator the scheduled lane runs, in its deliberate form: a pending or malformed pin
    # throws here rather than reporting a skip, because somebody asked for this proof.
    & (Join-Path $repositoryRoot 'eng/ci/Test-StockImagePinReadiness.ps1') `
        -PinFile $PinFile -EventName 'workflow_dispatch' -OutputPath '' |
        ForEach-Object { Write-Detail $_ }

    $pin = Get-Content -LiteralPath $PinFile -Raw | ConvertFrom-Json
    Write-Detail "pinned $($pin.edFiApi.repository):$($pin.edFiApi.tag) at $($pin.edFiApi.digest)"

    return $pin
}

# -------------------------------------------------------------------------------------------------
# Phase 1: what the tag points at, according to the registry
# -------------------------------------------------------------------------------------------------

function Assert-RemoteDescriptor {
    param([Parameter(Mandatory)] $Pin)

    Write-Phase 'Phase 1: the registry descriptor'

    $reference = "$($Pin.edFiApi.repository):$($Pin.edFiApi.tag)"
    $inspect = Invoke-Docker -AllowFailure -ArgumentList @(
        'buildx', 'imagetools', 'inspect', $reference, '--format', '{{json .Manifest}}'
    )

    $available = $inspect.ExitCode -eq 0
    $resolved = if ($available) { Get-RemoteImageDigest -ImagetoolsOutput $inspect.Output } else { '' }

    $verdict = Test-RemoteImageDescriptor -Tag $Pin.edFiApi.tag -PinnedDigest $Pin.edFiApi.digest `
        -ResolvedDigest $resolved -ImagetoolsAvailable $available

    if (-not $verdict.Verified) {
        throw "Remote descriptor verification failed: $($verdict.Reason)"
    }

    Write-Detail $verdict.Reason

    return [ordered]@{ reference = $reference; resolvedDigest = $resolved }
}

# -------------------------------------------------------------------------------------------------
# Phase 2: the host, and whether this run may touch it
# -------------------------------------------------------------------------------------------------

function Assert-HostAvailable {
    Write-Phase 'Phase 2: the host'

    $container = Get-Inventory -What 'the container list' -ArgumentList @('ps', '-a', '--format', '{{.Names}}')
    $projectContainer = Get-Inventory -What "the $composeProject project's containers" -ArgumentList @(
        'ps', '-a', '--filter', "label=com.docker.compose.project=$composeProject", '--format', '{{.Names}}'
    )
    $projectVolume = Get-Inventory -What "the $composeProject project's volumes" -ArgumentList @(
        'volume', 'ls', '--filter', "label=com.docker.compose.project=$composeProject", '--format', '{{.Name}}'
    )
    # Absence established by a successful enumeration, not inferred from a failed inspect. A daemon
    # that is down or a permission error fails the inspect too, and reading that as "no neighbours"
    # is how a run starts on a host it was never allowed to see.
    $networkList = Get-Inventory -What 'the network list' -ArgumentList @('network', 'ls', '--format', '{{.Name}}')
    $networkInspect = Get-Inventory -What 'the shared external network' -ArgumentList @(
        'network', 'inspect', 'dms', '--format', '{{range .Containers}}{{println .Name}}{{end}}'
    )

    $attachment = Get-NetworkAttachmentInventory -NetworkName 'dms' `
        -ListExitCode ($null -eq $networkList.Failure ? 0 : 1) -ListedNetwork $networkList.Item `
        -InspectExitCode ($null -eq $networkInspect.Failure ? 0 : 1) -InspectedAttachment $networkInspect.Item

    $port = Get-Inventory -What 'the published host ports' -ArgumentList @('ps', '--format', '{{.Ports}}')

    $boundPort = @(
        foreach ($row in $port.Item) {
            foreach ($match in [regex]::Matches($row, ':(\d+)->')) {
                [int]$match.Groups[1].Value
            }
        }
    )

    # Docker only knows about its own publications. A port held by anything else is invisible to
    # that list and would fail at compose up instead, so each required port is actually bound here.
    $boundPort += @($requiredPort | Where-Object { -not (Test-LocalPortAvailable -Port $_) })

    $failure = @(
        $container.Failure
        $projectContainer.Failure
        $projectVolume.Failure
        $attachment.Failure
        $port.Failure
    ) | Where-Object { $null -ne $_ }

    $result = Get-OccupiedHostResource `
        -ExistingContainerName $container.Item `
        -ProjectContainer $projectContainer.Item `
        -ProjectVolume $projectVolume.Item `
        -NetworkAttachment $attachment.Attachment `
        -BootstrapPresent (Test-Path -LiteralPath $bootstrapPath) `
        -BoundPort $boundPort `
        -RequiredPort $requiredPort `
        -InventoryFailure $failure

    if (-not $result.Available) {
        throw ("This host is not available for the stock-image proof:" + [Environment]::NewLine +
            (($result.Blocker | ForEach-Object { "  - $_" }) -join [Environment]::NewLine) + [Environment]::NewLine +
            'Clear these yourself and run again. This harness does not remove anything it did not create.')
    }

    Write-Detail 'the host is clear'
}

# -------------------------------------------------------------------------------------------------
# Phase 3: everything from here creates something, so everything from here is claimed first
# -------------------------------------------------------------------------------------------------

function New-ProofWorkspace {
    Write-Phase 'Phase 3: the workspace'

    # Claimed before it is created, so a failure part way through still tears it down. Nothing is
    # cleared here: an existing workspace was refused by the safety gate, because naming a directory
    # does not make its contents this run's to delete.
    Add-ProofOwnership -Kind 'Directory' -Name $WorkspaceRoot
    New-Item -ItemType Directory -Path $WorkspaceRoot | Out-Null
}

function Install-ReleasedSchemaTool {
    param([Parameter(Mandatory)] $Pin)

    Write-Phase 'Phase 3a: the released provisioning tool'

    $toolPath = Join-Path $WorkspaceRoot 'schema-tools'
    Add-ProofOwnership -Kind 'Directory' -Name $toolPath

    $feed = @($Pin.provisioning.schemaPackages)[0].feedUrl

    Invoke-Recorded -FilePath 'dotnet' -ArgumentList (Get-SchemaToolInstallArgument `
            -Version $Pin.provisioning.schemaToolsPackageVersion -ToolPath $toolPath -FeedUrl $feed) | Out-Null

    $executable = @(Get-ChildItem -LiteralPath $toolPath -Filter 'api-schema-tools*' -File) | Select-Object -First 1

    if ($null -eq $executable) {
        throw "The released EdFi.Api.SchemaTools $($Pin.provisioning.schemaToolsPackageVersion) installed but produced no api-schema-tools executable under $toolPath."
    }

    # Resolve-DmsSchemaTool honours this ahead of every path that would otherwise find this
    # worktree's build output, which is the whole point of installing the released tool.
    $env:DMS_SCHEMA_TOOL_PATH = $executable.FullName
    Write-Detail "provisioning with the released tool at $($executable.FullName)"

    return $executable.FullName
}

function Build-ProofFixture {
    param([Parameter(Mandatory)] $Pin)

    Write-Phase 'Phase 3b: the fixture, against the published contracts'

    $publishRoot = Join-Path $WorkspaceRoot 'plugins'
    $publishTarget = Join-Path $publishRoot $pluginName
    $nugetCache = Join-Path $WorkspaceRoot 'nuget-cache'

    Add-ProofOwnership -Kind 'Directory' -Name $publishRoot
    Add-ProofOwnership -Kind 'Directory' -Name $nugetCache
    New-Item -ItemType Directory -Path $publishTarget -Force | Out-Null
    New-Item -ItemType Directory -Path $nugetCache -Force | Out-Null

    # BARE identities, not bracketed. Acme.CustomValidationProof.csproj already writes
    # Version="[$(PluginsPackageVersion)]" for both contracts, so passing a bracketed value here
    # would produce [[1.0.0]] and fail the restore. The project supplies the exactness; the
    # verification below establishes that it worked.
    $pluginsVersion = $Pin.contracts.pluginsPackageVersion
    $customValidationVersion = $Pin.contracts.customValidationPackageVersion

    $previousCache = $env:NUGET_PACKAGES
    $cacheWasSet = Test-Path -LiteralPath 'Env:NUGET_PACKAGES'

    try {
        # A throwaway global-packages folder: that folder is consulted before any source, so an
        # already-extracted package of the same version would silently satisfy this restore.
        $env:NUGET_PACKAGES = $nugetCache

        Invoke-Recorded -FilePath 'dotnet' -ArgumentList @(
            'publish', $fixtureProject
            '--configuration', 'Release'
            '--no-self-contained'
            '--force'
            '--output', $publishTarget
            "-p:PluginsPackageVersion=$pluginsVersion"
            "-p:CustomValidationPackageVersion=$customValidationVersion"
            '--nologo'
        ) | Out-Null
    }
    finally {
        if ($cacheWasSet) { $env:NUGET_PACKAGES = $previousCache }
        elseif (Test-Path -LiteralPath 'Env:NUGET_PACKAGES') { Remove-Item -LiteralPath 'Env:NUGET_PACKAGES' }
    }

    # What the restore actually RESOLVED, read from the project's own assets file. A directory in
    # the package cache says a version was extracted at some point, not that this project resolved
    # it; project.assets.json is the record of what the build was given.
    $assetsFile = Join-Path (Split-Path -Parent $fixtureProject) 'obj/project.assets.json'

    if (-not (Test-Path -LiteralPath $assetsFile)) {
        throw "The fixture publish produced no $assetsFile, so what its contract restore resolved is unknown."
    }

    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json

    foreach ($contract in @(
            @{ Id = 'EdFi.Api.Plugins'; Expected = $Pin.contracts.pluginsPackageVersion }
            @{ Id = 'EdFi.Api.CustomValidation'; Expected = $Pin.contracts.customValidationPackageVersion }
        )) {
        $resolved = @(
            foreach ($framework in $assets.libraries.PSObject.Properties.Name) {
                $split = $framework -split '/', 2
                if ($split[0] -ceq $contract.Id) { $split[1] }
            }
        )

        $verdict = Test-RestoredPackageVersion -PackageId $contract.Id -ExpectedVersion $contract.Expected `
            -RestoredVersion (@($resolved) -join ',')

        if (-not $verdict.Verified) {
            throw "The fixture's contract restore did not produce what the pin names: $($verdict.Reason)"
        }

        Write-Detail $verdict.Reason
    }

    $digest = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $publishTarget -File) {
        $digest[$file.Name] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }

    return [pscustomobject]@{
        PublishRoot       = $publishRoot
        PublishedDirectory = $publishTarget
        FileDigest        = $digest
        EntryAssembly     = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $publishTarget "$pluginName.dll")).Version.ToString()
    }
}

# The asset-only package Recipe 2 fetches, laid out as the PackageBaseAddress form the committed
# recipe's URL is an address for.
function Build-ProofPackage {
    param([Parameter(Mandatory)] $Fixture, [string] $Version = '1.0.0')

    Write-Phase 'Phase 3c: the plugin package'

    $stage = Join-Path $WorkspaceRoot 'package-stage'
    $feedRoot = Join-Path $WorkspaceRoot 'feed'
    Add-ProofOwnership -Kind 'Directory' -Name $stage
    Add-ProofOwnership -Kind 'Directory' -Name $feedRoot

    $contentRoot = Join-Path $stage "contentFiles/any/any/$pluginName"
    New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $Fixture.PublishedDirectory '*') -Destination $contentRoot -Recurse -Force

    Set-Content -LiteralPath (Join-Path $stage "$pluginName.nuspec") -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$pluginName</id>
    <version>$Version</version>
    <authors>Ed-Fi Alliance, LLC and contributors</authors>
    <description>Test fixture plugin for the DMS stock image plugin proof. Asset-only.</description>
  </metadata>
</package>
"@
    Set-Content -LiteralPath (Join-Path $stage '[Content_Types].xml') -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
  <Default Extension="xml" ContentType="application/xml" />
  <Default Extension="nuspec" ContentType="application/octet-stream" />
</Types>
"@

    $lowerId = $pluginName.ToLowerInvariant()
    $packageDirectory = Join-Path $feedRoot "$lowerId/$Version"
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    $packagePath = Join-Path $packageDirectory "$lowerId.$Version.nupkg"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $packagePath)

    return [pscustomobject]@{
        FeedRoot = $feedRoot
        Path     = $packagePath
        Version  = $Version
        Sha256   = (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash.ToLowerInvariant()
        Url      = "http://plugin-feed:8080/$lowerId/$Version/$lowerId.$Version.nupkg"
    }
}

# -------------------------------------------------------------------------------------------------
# Phase 4: the deployments
# -------------------------------------------------------------------------------------------------

function Get-ComposeContainerName([string]$service) {
    $result = Invoke-Docker -AllowFailure -ArgumentList @(
        'ps', '-a'
        '--filter', "label=com.docker.compose.project=$composeProject"
        '--filter', "label=com.docker.compose.service=$service"
        '--format', '{{.Names}}'
    )

    if ($result.ExitCode -ne 0) {
        return [pscustomobject]@{ Name = $null; EnumerationSucceeded = $false }
    }

    $name = @($result.Output -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -First 1

    return [pscustomobject]@{ Name = $name; EnumerationSucceeded = $true }
}

function Get-ContainerFact([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) {
        return [pscustomobject]@{ Status = ''; StartedAtRaw = ''; ExitCode = $null; ImageId = ''; Restarting = $false; InspectSucceeded = $true; Absent = $true }
    }

    $result = Invoke-Docker -AllowFailure -ArgumentList @(
        'inspect', $name, '--format', '{{.State.Status}}|{{.State.StartedAt}}|{{.State.ExitCode}}|{{.Image}}|{{.State.Restarting}}'
    )

    # A failed inspect is kept distinct from an absent container. They are different facts, and
    # collapsing them reports a container that exists but could not be read as one that never was.
    if ($result.ExitCode -ne 0) {
        return [pscustomobject]@{ Status = ''; StartedAtRaw = ''; ExitCode = $null; ImageId = ''; Restarting = $false; InspectSucceeded = $false; Absent = $false }
    }

    $part = ($result.Output.Trim() -split '\|', 5)

    return [pscustomobject]@{
        Status           = $part[0]
        StartedAtRaw     = $part[1]
        ExitCode         = [int]$part[2]
        ImageId          = $part[3]
        Restarting       = ($part[4] -ceq 'true')
        InspectSucceeded = $true
        Absent           = $false
    }
}

# A container that has stopped and stays stopped. published-dms.yml carries restart: unless-stopped,
# so a DMS that refuses to start is restarted for as long as the stack is up: it is repeatedly
# "exited" with a non-zero code and then "restarting" again. Reading an exit code once therefore
# says nothing about whether it stopped, and could equally be the previous attempt's. This waits for
# the refusal to be recorded and then stops the service explicitly, so the state asserted afterwards
# is one the restart policy is no longer changing.
function Wait-ForRecordedStartupFailure {
    param([Parameter(Mandatory)] [string] $Container, [Parameter(Mandatory)] [string] $ExpectedPhase, [int] $TimeoutSeconds = 300)

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([datetime]::UtcNow -lt $deadline) {
        $status = Get-StartupStatusDocument $Container -FromStoppedContainer

        if ($null -ne $status -and $status.Phase -ceq $ExpectedPhase -and $status.State -cne 'Ready') {
            # Stop the service so the restart policy cannot move it while it is being asserted about.
            Push-Location $composeRoot
            try {
                Invoke-Docker -AllowFailure -ArgumentList @('compose', '-p', $composeProject, 'stop', 'dms') | Out-Null
            }
            finally {
                Pop-Location
            }

            $fact = Get-ContainerFact $Container

            if (-not $fact.InspectSucceeded) {
                throw 'The DMS container could not be inspected after it was stopped, so its final state is unknown.'
            }

            if ($fact.Restarting -or $fact.Status -ceq 'running') {
                throw "The DMS container is still $($fact.Status) after an explicit stop, so it has not settled and its exit code is not final."
            }

            return [pscustomobject]@{ Status = $status; Fact = $fact }
        }

        Start-Sleep -Seconds 5
    }

    throw "DMS did not record a failed $ExpectedPhase phase within $TimeoutSeconds seconds."
}

function Start-ProofDeployment {
    param([Parameter(Mandatory)] [string] $EnvironmentFile, [switch] $AllowFailure)

    # Both claimed before the up. bootstrap-published-dms.ps1 stages eng/docker-compose/.bootstrap
    # on its way to starting the stack, so a run that fails during startup has already created it.
    Add-ProofOwnership -Kind 'ComposeProject' -Name $composeProject
    Add-ProofOwnership -Kind 'Bootstrap' -Name $bootstrapPath
    $script:environmentFile = $EnvironmentFile

    Push-Location $composeRoot
    try {
        return Invoke-Recorded -FilePath 'pwsh' -AllowFailure:$AllowFailure -ArgumentList @(
            '-NoProfile', '-File', (Join-Path $composeRoot 'bootstrap-published-dms.ps1')
            '-EnvironmentFile', $EnvironmentFile
        )
    }
    finally {
        Pop-Location
    }
}

function Stop-ProofDeployment {
    param([Parameter(Mandatory)] [string] $EnvironmentFile)

    Push-Location $composeRoot
    try {
        $down = Invoke-Recorded -FilePath 'pwsh' -AllowFailure -ArgumentList @(
            '-NoProfile', '-File', (Join-Path $composeRoot 'bootstrap-published-dms.ps1')
            '-d', '-v', '-EnvironmentFile', $EnvironmentFile
        )
    }
    finally {
        Pop-Location
    }

    # A teardown that failed leaves a stack running under the ordinary published project name, with
    # the next scenario's assertions about to be answered by it. Ignoring the exit code here is how
    # a scenario passes against the previous scenario's containers.
    if ($down.ExitCode -ne 0) {
        $script:teardownFailed = $true
        throw "Tearing down the $composeProject stack failed with exit code $($down.ExitCode). The stack is still up; nothing further can be trusted and the staged bootstrap workspace is being kept because it is still mounted."
    }
}

function Get-StartupStatusDocument([string]$container, [switch]$FromStoppedContainer) {
    if ([string]::IsNullOrWhiteSpace($container)) {
        return $null
    }

    if ($FromStoppedContainer) {
        # docker cp, not exec: the misspelled-allowlist scenario asserts that DMS exited, and exec
        # cannot reach a container that is no longer running.
        $destination = Join-Path $WorkspaceRoot 'dms-startup-status.json'
        $copy = Invoke-Docker -AllowFailure -ArgumentList @('cp', "${container}:/tmp/dms-startup-status.json", $destination)

        if ($copy.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $destination)) {
            return $null
        }

        return (Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json)
    }

    $read = Invoke-Docker -AllowFailure -ArgumentList @('exec', $container, 'cat', '/tmp/dms-startup-status.json')

    if ($read.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($read.Output)) {
        return $null
    }

    return ($read.Output | ConvertFrom-Json)
}

function Wait-ForDmsReady {
    param([Parameter(Mandatory)] [string] $Container, [int] $TimeoutSeconds = 600)

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([datetime]::UtcNow -lt $deadline) {
        $status = Get-StartupStatusDocument $Container

        if ($null -ne $status -and $status.State -ceq 'Ready') {
            return $status
        }

        if ($null -ne $status -and $status.State -ceq 'Failed') {
            throw "DMS startup failed in phase $($status.Phase): $($status.Summary) $($status.ErrorMessage)"
        }

        Start-Sleep -Seconds 5
    }

    $final = Get-StartupStatusDocument $Container
    $phase = if ($null -ne $final) { $final.Phase } else { 'unknown' }
    throw "DMS did not reach Ready within $TimeoutSeconds seconds; the startup status last recorded phase '$phase'."
}

function Assert-PluginRootReadOnly {
    param([Parameter(Mandatory)] [string] $Container)

    $mount = Invoke-Docker -AllowFailure -ArgumentList @('exec', $Container, 'sh', '-c', 'cat /proc/mounts')
    $write = Invoke-Docker -AllowFailure -ArgumentList @('exec', $Container, 'sh', '-c', 'touch /app/plugins/.stock-proof-write-probe 2>&1')

    $verdict = Test-PluginMountReadOnly -MountTable $mount.Output -MountTableExitCode $mount.ExitCode `
        -WriteExitCode $write.ExitCode -WriteOutput $write.Output

    if (-not $verdict.Verified) {
        throw "The plugin root is not provably read-only from inside the container: $($verdict.Reason)"
    }

    Write-Detail 'the plugin root is read-only, observed from inside the container'
}

# The smoke-test client the configure phase creates, exchanged for a token at the DMS token
# endpoint with HTTP Basic auth.
function Get-DmsAccessToken {
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] $SmokeClient)

    $script:secret += @($SmokeClient.Secret)
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("$($SmokeClient.Key):$($SmokeClient.Secret)"))

    Write-ProofLog "POST $BaseUrl/oauth/token"

    $response = Invoke-RestMethod -Uri "$BaseUrl/oauth/token" -Method Post `
        -Headers @{ Authorization = "Basic $basic" } `
        -ContentType 'application/x-www-form-urlencoded' -Body 'grant_type=client_credentials'

    $script:secret += @($response.access_token)

    return $response.access_token
}

function Invoke-StudentPost {
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [Parameter(Mandatory)] [string] $Token,
        [Parameter(Mandatory)] [string] $UniqueId,
        [string] $LastSurname = 'Proof'
    )

    $body = [ordered]@{
        studentUniqueId = $UniqueId
        birthDate       = '2010-01-01'
        firstName       = 'Stock'
        lastSurname     = $LastSurname
    } | ConvertTo-Json -Depth 4

    Write-ProofLog "POST $BaseUrl/data/ed-fi/students ($UniqueId)"

    try {
        $response = Invoke-WebRequest -Uri "$BaseUrl/data/ed-fi/students" -Method Post `
            -Headers @{ Authorization = "Bearer $Token" } -ContentType 'application/json' `
            -Body $body -SkipHttpErrorCheck

        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = [string]$response.Content }
    }
    catch {
        return [pscustomobject]@{ StatusCode = $null; Body = $_.Exception.Message }
    }
}

# The fixture's two reserved tokens and their exact messages, taken from the fixture itself.
$script:rejectOnPathToken = 'custom-validation-proof-reject-path'
$script:rejectOnResourceToken = 'custom-validation-proof-reject-resource'
$script:pathArmMessage = "This value is the custom-validation proof fixture's reserved rejection token."
$script:resourceArmMessage = 'This document carries the custom-validation proof fixture''s reserved document-level rejection token.'

function Assert-FixtureRejection {
    param([Parameter(Mandatory)] [string] $BaseUrl, [Parameter(Mandatory)] $SmokeClient)

    $token = Get-DmsAccessToken -BaseUrl $BaseUrl -SmokeClient $SmokeClient

    # The control first. A rejection means nothing unless an otherwise identical document succeeds:
    # without this, a broken write path would look exactly like a working rule.
    $control = Invoke-StudentPost -BaseUrl $BaseUrl -Token $token -UniqueId "stock-proof-control-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

    if ($control.StatusCode -ne 201) {
        throw "The passing control POST returned HTTP $($control.StatusCode) rather than 201, so a rejection below would prove nothing: $($control.Body)"
    }

    $path = Invoke-StudentPost -BaseUrl $BaseUrl -Token $token `
        -UniqueId "stock-proof-path-$([guid]::NewGuid().ToString('N').Substring(0, 8))" -LastSurname $script:rejectOnPathToken

    $pathVerdict = Test-FixtureValidationFailure -StatusCode $path.StatusCode -Body $path.Body `
        -ExpectedMessage $script:pathArmMessage -ExpectedPath '$.lastSurname' -Arm 'Path'

    if (-not $pathVerdict.Verified) {
        throw "The path-arm rejection is not the fixture's: $($pathVerdict.Reason)"
    }

    $resource = Invoke-StudentPost -BaseUrl $BaseUrl -Token $token `
        -UniqueId "stock-proof-resource-$([guid]::NewGuid().ToString('N').Substring(0, 8))" -LastSurname $script:rejectOnResourceToken

    $resourceVerdict = Test-FixtureValidationFailure -StatusCode $resource.StatusCode -Body $resource.Body `
        -ExpectedMessage $script:resourceArmMessage -Arm 'Resource'

    if (-not $resourceVerdict.Verified) {
        throw "The resource-arm rejection is not the fixture's: $($resourceVerdict.Reason)"
    }

    Write-Detail 'both arms of the fixture rejection returned over HTTP, with an otherwise identical control accepted'

    return [ordered]@{
        controlStatus  = $control.StatusCode
        pathStatus     = $path.StatusCode
        resourceStatus = $resource.StatusCode
    }
}

# One deployment: compose its environment file, tear down first, start, run the scenario body, and
# tear down again whether the body held or not. Each scenario gets a fresh deployment because the two
# recipes both end at the single /app/plugins mount target and cannot share one.
function Invoke-ProofScenario {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] $Pin,
        [Parameter(Mandatory)] [string] $BaseContent,
        [Parameter(Mandatory)] [string] $PluginComposeFiles,
        [Parameter(Mandatory)] [scriptblock] $Scenario,
        [string] $PluginMountSource,
        [string] $PluginFeedSource,
        [string] $PluginPackageUrl,
        [string] $PluginPackageSha256,
        [string] $PluginName,
        [string] $AllowedPlugins = '',
        [switch] $ExpectStartFailure
    )

    Write-Phase "Deployment: $Name"

    $environmentFile = Join-Path $WorkspaceRoot "$Name.env"
    Set-Content -LiteralPath $environmentFile -Encoding utf8 -Value (
        Get-StockProofEnvironmentContent -Pin $Pin -BaseContent $BaseContent `
            -PluginComposeFiles $PluginComposeFiles -PluginMountSource $PluginMountSource `
            -PluginFeedSource $PluginFeedSource -PluginPackageUrl $PluginPackageUrl `
            -PluginPackageSha256 $PluginPackageSha256 -PluginName $PluginName `
            -AllowedPlugins $AllowedPlugins)

    $record = $null
    $failure = $null

    try {
        $start = Start-ProofDeployment -EnvironmentFile $environmentFile -AllowFailure:$ExpectStartFailure

        if ($ExpectStartFailure -and $start.ExitCode -eq 0) {
            throw "The $Name deployment was expected to fail to come up and did not."
        }

        $record = & $Scenario $environmentFile
    }
    catch {
        $failure = $_
    }
    finally {
        try {
            Stop-ProofDeployment -EnvironmentFile $environmentFile
        }
        catch {
            if ($null -eq $failure) { throw }
            Write-Detail "teardown after the failure above also failed: $($_.Exception.Message)"
        }
    }

    if ($null -ne $failure) {
        throw $failure
    }

    return $record
}

function Invoke-HappyPath {
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [Parameter(Mandatory)] [string] $ConfigurationServiceUrl,
        [Parameter(Mandatory)] $Pin
    )

    $dms = Get-ComposeContainerName 'dms'

    if (-not $dms.EnumerationSucceeded -or [string]::IsNullOrWhiteSpace($dms.Name)) {
        throw 'The DMS container could not be located in the compose project.'
    }

    $status = Wait-ForDmsReady -Container $dms.Name
    $facts = Get-ContainerFact $dms.Name

    # The image identity, which is the run-time half of the no-build claim: the container ran the
    # artifact the pin names rather than anything built here.
    $expected = Invoke-Docker -AllowFailure -ArgumentList @(
        'image', 'inspect', "$($Pin.edFiApi.repository)@$($Pin.edFiApi.digest)", '--format', '{{.Id}}'
    )

    if ($expected.ExitCode -ne 0 -or $facts.ImageId -cne $expected.Output.Trim()) {
        throw "The DMS container ran image $($facts.ImageId), not the pinned digest's image $($expected.Output.Trim())."
    }

    Assert-PluginRootReadOnly -Container $dms.Name

    # Created here rather than read from disk. configure-local-data-store.ps1's
    # -AddSmokeTestCredentials calls Get-SmokeTestCredential and pipes the result to Out-Null: the
    # key and secret are RETURNED to the caller and never written to a file, so there is nothing on
    # disk for this harness to read. Calling the same module directly is how a caller obtains them.
    Import-Module (Join-Path $repositoryRoot 'eng/smoke_test/modules/SmokeTest.psm1') -Force
    Write-ProofLog "Get-SmokeTestCredential $ConfigurationServiceUrl"
    $smokeClient = Get-SmokeTestCredential -ConfigServiceUrl $ConfigurationServiceUrl

    $http = Assert-FixtureRejection -BaseUrl $BaseUrl -SmokeClient $smokeClient

    return [ordered]@{
        readyState  = $status.State
        dmsImageId  = $facts.ImageId
        pluginRootReadOnly = $true
        http        = $http
    }
}

function Invoke-WrongDigestCheck {
    $fetch = Get-ComposeContainerName 'fetch-plugins'
    $fetchFacts = Get-ContainerFact $fetch.Name
    $fetchLog = if ([string]::IsNullOrWhiteSpace($fetch.Name)) { '' } else { (Invoke-Docker -AllowFailure -ArgumentList @('logs', $fetch.Name)).Output }

    $checksum = Test-FetchFailedOnChecksum -ExitCode $fetchFacts.ExitCode -Log $fetchLog

    if (-not $checksum.Verified) {
        throw "The wrong-digest deployment did not fail on the digest comparison: $($checksum.Reason)"
    }

    $dms = Get-ComposeContainerName 'dms'
    $dmsFacts = Get-ContainerFact $dms.Name

    # A container that exists but could not be inspected is not one that never started, and the
    # earlier successful ps says nothing about this inspect.
    $neverStarted = Test-DmsNeverStarted -Status $dmsFacts.Status -StartedAtRaw $dmsFacts.StartedAtRaw `
        -ExitCode $dmsFacts.ExitCode `
        -EnumerationSucceeded ($dms.EnumerationSucceeded -and $dmsFacts.InspectSucceeded)

    if (-not $neverStarted.Verified) {
        throw "The wrong-digest deployment started DMS: $($neverStarted.Reason)"
    }

    Write-Detail 'the fetch failed on the checksum and DMS never started'

    return [ordered]@{ fetchExitCode = $fetchFacts.ExitCode; dmsNeverStarted = $true }
}

function Invoke-MisspelledAllowlistCheck {
    $dms = Get-ComposeContainerName 'dms'

    if (-not $dms.EnumerationSucceeded -or [string]::IsNullOrWhiteSpace($dms.Name)) {
        throw 'The DMS container could not be located, so the refusal it should have recorded cannot be read.'
    }

    $settled = Wait-ForRecordedStartupFailure -Container $dms.Name -ExpectedPhase 'LoadPlugins'

    $verdict = Test-LoadPluginsFailure -StatusDocument $settled.Status `
        -ExpectedPath "/app/plugins/${pluginName}1" -ExitCode $settled.Fact.ExitCode

    if (-not $verdict.Verified) {
        throw "The misspelled allowlist did not produce the expected refusal: $($verdict.Reason)"
    }

    Write-Detail 'DMS refused to start, recording a failed LoadPlugins phase naming the expected path'

    return [ordered]@{
        dmsExitCode = $settled.Fact.ExitCode
        dmsStatus   = $settled.Fact.Status
        phase       = $settled.Status.Phase
        state       = $settled.Status.State
    }
}

Write-Phase 'Stock image plugin proof'
Write-Detail "pin:      $PinFile"
Write-Detail "evidence: $EvidenceRoot"

$script:environmentFile = $null
$result = [ordered]@{}

try {
    # Nothing above this line has touched Docker, the filesystem or the network.
    Assert-NoAmbientOverride
    Assert-PathsSafeToOwn
    $pin = Get-ValidatedPin
    $result.pin = [ordered]@{
        edFiApi              = "$($pin.edFiApi.repository):$($pin.edFiApi.tag)@$($pin.edFiApi.digest)"
        configurationService = "$($pin.configurationService.repository)@$($pin.configurationService.digest)"
        release              = $pin.release.githubRelease
        sourceCommit         = $pin.release.sourceCommit
    }

    $result.remoteDescriptor = Assert-RemoteDescriptor -Pin $pin
    Assert-HostAvailable

    New-ProofWorkspace
    $result.schemaTool = Install-ReleasedSchemaTool -Pin $pin

    $fixture = Build-ProofFixture -Pin $pin
    $result.fixture = [ordered]@{
        entryAssemblyVersion = $fixture.EntryAssembly
        fileDigest           = $fixture.FileDigest
    }

    $package = Build-ProofPackage -Fixture $fixture
    $result.package = [ordered]@{ version = $package.Version; sha256 = $package.Sha256; url = $package.Url }

    $baseContent = Get-Content -Raw -LiteralPath $BaseEnvironmentFile
    $allowedOverlay = 'tests/plugin-deployment/plugins-allowed-dms.yml'
    $feedOverlay = 'tests/plugin-deployment/plugins-feed-dms.yml'
    $pinOverlay = 'tests/plugin-deployment/stock-image-pin-dms.yml'
    # The same values the preflight refused on, so the guarded ports and the addresses requests go
    # to cannot drift apart.
    $baseUrl = "http://localhost:$dmsPort"
    $configurationServiceUrl = "http://localhost:$configurationServicePort"

    # Recipe 1: the committed plugins-dms.yml, run unedited, with the plugin bind-mounted.
    $result.recipe1 = Invoke-ProofScenario -Name 'recipe1' -Pin $pin -BaseContent $baseContent `
        -PluginComposeFiles "plugins-dms.yml;$pinOverlay;$allowedOverlay" `
        -PluginMountSource $fixture.PublishRoot -AllowedPlugins $pluginName `
        -Scenario { param($environmentFile) Invoke-HappyPath -BaseUrl $baseUrl -ConfigurationServiceUrl $configurationServiceUrl -Pin $pin }

    # Recipe 2: the committed plugins-fetch-dms.yml, run unedited, fetching over HTTP from the
    # digest-pinned static-file container in the test-owned feed overlay.
    $result.recipe2 = Invoke-ProofScenario -Name 'recipe2' -Pin $pin -BaseContent $baseContent `
        -PluginComposeFiles "plugins-fetch-dms.yml;$feedOverlay;$pinOverlay;$allowedOverlay" `
        -PluginFeedSource $package.FeedRoot -PluginPackageUrl $package.Url `
        -PluginPackageSha256 $package.Sha256 -PluginName $pluginName -AllowedPlugins $pluginName `
        -Scenario { param($environmentFile) Invoke-HappyPath -BaseUrl $baseUrl -ConfigurationServiceUrl $configurationServiceUrl -Pin $pin }

    # A digest that does not match the served package: the fetch must fail on the comparison
    # specifically, and DMS must never start.
    $result.wrongDigest = Invoke-ProofScenario -Name 'wrong-digest' -Pin $pin -BaseContent $baseContent `
        -PluginComposeFiles "plugins-fetch-dms.yml;$feedOverlay;$pinOverlay;$allowedOverlay" `
        -PluginFeedSource $package.FeedRoot -PluginPackageUrl $package.Url `
        -PluginPackageSha256 ('0' * 64) -PluginName $pluginName -AllowedPlugins $pluginName `
        -ExpectStartFailure -Scenario { param($environmentFile) Invoke-WrongDigestCheck }

    # One allowlisted name misspelled: DMS must exit with a failed LoadPlugins phase naming the path
    # it looked for.
    $result.misspelledAllowlist = Invoke-ProofScenario -Name 'misspelled-allowlist' -Pin $pin -BaseContent $baseContent `
        -PluginComposeFiles "plugins-dms.yml;$pinOverlay;$allowedOverlay" `
        -PluginMountSource $fixture.PublishRoot -AllowedPlugins "${pluginName}1" `
        -ExpectStartFailure -Scenario { param($environmentFile) Invoke-MisspelledAllowlistCheck }

    Write-Phase 'Result'
    Write-Detail 'the pulled stock image ran third-party code and returned the fixture''s custom-validation 400'
}
finally {
    # Cleanup first, then evidence: the record has to include the teardown commands and any failure
    # they hit, and writing it beforehand omits exactly the part a failed run needs.
    try {
        Invoke-OwnedCleanup
    }
    catch {
        $script:cleanupError.Add((Protect-StockProofText -Text $_.Exception.Message -Secret $script:secret))
    }

    $result.buildCommandAbsent = Test-BuildCommandAbsent -Command @($script:commandLog)
    $result.cleanupError = @($script:cleanupError)
    $result.teardownFailed = $script:teardownFailed

    try {
        Write-Evidence -Result ([pscustomobject]$result)
    }
    catch {
        # Evidence is the record, not the work. Losing it must not also lose the run's verdict.
        Write-Detail "could not write evidence: $(Protect-StockProofText -Text $_.Exception.Message -Secret $script:secret)"
    }

    if ($script:cleanupError.Count -gt 0) {
        throw ("This run did not clean up after itself:" + [Environment]::NewLine +
            (($script:cleanupError | ForEach-Object { "  - $_" }) -join [Environment]::NewLine))
    }
}
