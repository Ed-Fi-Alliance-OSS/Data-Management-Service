# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Pack, inspect, install and run Security Metadata Tools from a fresh local-only feed.
.DESCRIPTION
    Reusable local/CI smoke action. Uses the shared version calculation and locked restore,
    checks the shared claims validator/schema and both bundled base policies,
    then verifies installed conversion, bundled-base and fragment merging, and deterministic resolution
    against existing reviewed fixtures from a working directory outside the checkout.
    Leaves the exact nupkg in the repository root; disposable feed/cache/tool files are removed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AppVersion,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
Import-Module (Join-Path $repoRoot "package-helpers.psm1")
Import-Module (Join-Path $PSScriptRoot "SecurityMetadataToolsPackage.psm1")
$toolVersion = Get-SecurityMetadataToolsVersion -AppVersion $AppVersion
$packageVersion = ($toolVersion -split '\+', 2)[0]
$packageId = "EdFi.Api.SecurityMetadataTools"
$packageFile = Join-Path $repoRoot "$packageId.$packageVersion.nupkg"
$projectDirectory = Join-Path $repoRoot "src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools"
$fixtureDirectory = "$projectDirectory.Tests.Unit/Fixtures/OdsSecurityMetadata"

# Every installed command gets an explicit external working directory and captured streams.
function Invoke-InstalledTool {
    param(
        [string[]] $ToolArguments,
        [int] $ExpectedExitCode = 0,
        [switch] $AllowOutput
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.WorkingDirectory = $workspace
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ToolArguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "Installed $($ToolArguments[0]) returned $($process.ExitCode), expected ${ExpectedExitCode}: $stderr"
        }
        if ((-not $AllowOutput -and $stdout.Length -ne 0) -or
            ($ExpectedExitCode -eq 0 -and $stderr.Length -ne 0)) {
            throw "Installed $($ToolArguments[0]) wrote unexpected console output: $stdout $stderr"
        }
        return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr }
    }
    finally { $process.Dispose() }
}

function Assert-JsonDocument {
    param([string] $ActualPath, [string] $ExpectedPath)

    $actual = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($ActualPath))
    $expected = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($ExpectedPath))
    if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($expected, $actual)) {
        throw "Installed output '$ActualPath' differs from reviewed document '$ExpectedPath'."
    }
}

# A child process contains Invoke-Main's exit and allows every native failure to be checked.
pwsh -NoProfile -File (Join-Path $repoRoot "build-config.ps1") Package `
    -Configuration $Configuration -DmsCSVersion $AppVersion -PackageTarget SecurityMetadataTools -LockedMode
if ($LASTEXITCODE -ne 0) { throw "Security Metadata Tools packaging failed ($LASTEXITCODE)." }

$scratch = Join-Path ([IO.Path]::GetTempPath()) "security-metadata-tools-$([guid]::NewGuid())"
try {
    $extracted = Join-Path $scratch "extracted"
    Expand-Archive -LiteralPath $packageFile -DestinationPath $extracted

    [xml] $nuspec = Get-Content -LiteralPath (Join-Path $extracted "$packageId.nuspec") -Raw
    $metadata = $nuspec.package.metadata
    if ($metadata.id -cne $packageId -or $metadata.version -cne $toolVersion) {
        throw "Unexpected package identity: $($metadata.id) $($metadata.version), expected $packageId $toolVersion."
    }
    if ($metadata.packageTypes.packageType.name -cne "DotnetTool" -or
        $metadata.license.'#text' -cne "Apache-2.0" -or
        $metadata.readme -cne "README.md" -or
        $metadata.title -cne "Ed-Fi API Security Metadata Tools" -or
        $metadata.projectUrl -cne "https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service" -or
        [string]::IsNullOrWhiteSpace($metadata.description)) {
        throw "Required tool package metadata is missing or incorrect."
    }
    if ((Get-FileHash (Join-Path $extracted "README.md")).Hash -cne
        (Get-FileHash (Join-Path $projectDirectory "README.md")).Hash) {
        throw "Packed README differs from the source guide."
    }

    $runtime = Join-Path $extracted "tools/net10.0/any"
    [xml] $settings = Get-Content -LiteralPath (Join-Path $runtime "DotnetToolSettings.xml") -Raw
    $command = $settings.DotNetCliTool.Commands.Command
    if (@($command).Count -ne 1 -or $command.Name -cne "security-metadata-tools" -or
        $command.EntryPoint -cne "security-metadata-tools.dll" -or $command.Runner -cne "dotnet") {
        throw "Unexpected .NET 10 tool command settings."
    }
    foreach ($file in @("security-metadata-tools.runtimeconfig.json", "security-metadata-tools.deps.json",
        "System.CommandLine.dll", "JsonSchema.Net.dll", "EdFi.DmsConfigurationService.Claims.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtime $file))) {
            throw "Tool runtime asset is missing: $file"
        }
    }
    $dependencies = [System.Text.Json.JsonDocument]::Parse(
        [IO.File]::ReadAllText((Join-Path $runtime "security-metadata-tools.deps.json")))
    try {
        foreach ($target in $dependencies.RootElement.GetProperty("targets").EnumerateObject()) {
            foreach ($library in $target.Value.EnumerateObject()) {
                $assets = [System.Text.Json.JsonElement]::new()
                if ($library.Value.TryGetProperty("runtime", [ref] $assets)) {
                    foreach ($asset in $assets.EnumerateObject()) {
                        if (-not (Test-Path -LiteralPath (Join-Path $runtime ([IO.Path]::GetFileName($asset.Name))))) {
                            throw "Declared managed runtime asset is missing from the package: $($asset.Name)"
                        }
                    }
                }
            }
        }
    }
    finally { $dependencies.Dispose() }
    foreach ($file in @("EdFi.DmsConfigurationService.Backend*.dll", "EdFi.DmsConfigurationService.Frontend*.dll")) {
        if (Get-ChildItem -LiteralPath $runtime -Filter $file) {
            throw "Tool package unexpectedly includes a host/backend-only dependency: $file"
        }
    }
    # Read metadata without loading the .NET 10 assembly into PowerShell's runtime or
    # retaining a file lock that would prevent scratch cleanup on Windows.
    $claimsStream = [IO.File]::OpenRead((Join-Path $runtime "EdFi.DmsConfigurationService.Claims.dll"))
    $peReader = [Reflection.PortableExecutable.PEReader]::new($claimsStream)
    try {
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
        $embeddedResources = @($reader.ManifestResources | ForEach-Object {
            $resource = $reader.GetManifestResource($_)
            if ($resource.Implementation.IsNil) { $reader.GetString($resource.Name) }
        })
        foreach ($resourceName in @(
            "EdFi.DmsConfigurationService.Claims.Claims.JsonSchemaForClaims.json",
            "EdFi.DmsConfigurationService.Claims.Claims.Standards.ds52.Claims.json",
            "EdFi.DmsConfigurationService.Claims.Claims.Standards.ds61.Claims.json"
        )) {
            if (@($embeddedResources | Where-Object { $_ -ceq $resourceName }).Count -ne 1) {
                throw "Packaged shared claims library must contain exactly one embedded resource: $resourceName"
            }
        }
    }
    finally {
        $peReader.Dispose()
        $claimsStream.Dispose()
    }
    $executable = Install-SecurityMetadataToolsPackage -PackageFile $packageFile -PackageVersion $packageVersion -ScratchDirectory $scratch
    $workspace = (New-Item -ItemType Directory -Path (Join-Path $scratch "workspace")).FullName
    $relativeWorkspace = [IO.Path]::GetRelativePath($repoRoot, $workspace)
    if (-not [IO.Path]::IsPathRooted($relativeWorkspace) -and $relativeWorkspace -notmatch '^\.\.([/\\]|$)') {
        throw "Installed command workspace must be outside the checkout: $workspace"
    }
    # Copy explicit inputs only; no checkout files are supplied to the installed process.
    Copy-Item -LiteralPath (Join-Path $fixtureDirectory "representative.xml") -Destination $workspace
    Copy-Item -LiteralPath (Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Fixtures/AuthorizationMetadata/inheritance-input.json") -Destination $workspace
    $reportedVersion = (Invoke-InstalledTool -ToolArguments @("--version") -AllowOutput).Stdout.Trim()
    $revisionSeparator = if ($toolVersion.Contains("+")) { "." } else { "+" }
    $expectedVersionPattern = '^' + [regex]::Escape($toolVersion) + '(' + [regex]::Escape($revisionSeparator) + '[0-9a-f]{40})?$'
    if ($reportedVersion -cnotmatch $expectedVersionPattern) {
        throw "Installed command reports '$reportedVersion', expected '$toolVersion'."
    }
    $output = Join-Path $workspace "Claims.json"
    $null = Invoke-InstalledTool -ToolArguments @("convert-ods-xml", "--input", "representative.xml", "--output", $output)
    Assert-JsonDocument -ActualPath $output -ExpectedPath (Join-Path $fixtureDirectory "expected-claims.json")
    Write-Output "Installed conversion matches the reviewed representative fixture."

    $fragments = (New-Item -ItemType Directory -Path (Join-Path $workspace "empty-fragments")).FullName
    foreach ($version in @("5.2", "6.1")) {
        $merged = Join-Path $workspace "merged-$version.json"
        $null = Invoke-InstalledTool -ToolArguments @("merge", "--fragments", $fragments,
            "--data-standard-version", $version, "--output", $merged)
        $basePath = Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Claims/Claims/Standards/ds$($version.Replace('.', ''))/Claims.json"
        Assert-JsonDocument -ActualPath $merged -ExpectedPath $basePath
        Write-Output "Installed merge $version matches the complete bundled base."
    }
    $defaultOutput = Join-Path $workspace "merged-default.json"
    $null = Invoke-InstalledTool -ToolArguments @("merge", "--fragments", $fragments, "--output", $defaultOutput)
    if ((Get-FileHash $defaultOutput).Hash -cne (Get-FileHash (Join-Path $workspace "merged-5.2.json")).Hash) {
        throw "Installed merge default differs from explicit 5.2 output."
    }
    $originalHash = (Get-FileHash $defaultOutput).Hash
    $unsupported = Invoke-InstalledTool -ToolArguments @("merge", "--fragments", $fragments,
        "--data-standard-version", "9.9", "--output", $defaultOutput, "--force") -ExpectedExitCode 1
    if ($unsupported.Stderr -cnotmatch "^Merge failed: Configuration .*data standard version '9\.9'" -or
        (Get-FileHash $defaultOutput).Hash -cne $originalHash) {
        throw "Installed unsupported-version failure must diagnose 9.9 and preserve the destination even with --force."
    }
    Write-Output "Installed merge default equals 5.2; unsupported version exits 1 and preserves destination bytes."

    # One real grant proves the installed composer applies fragments, not just the bundled base.
    $populatedFragments = (New-Item -ItemType Directory -Path (Join-Path $workspace "populated-fragments")).FullName
    Set-Content -LiteralPath (Join-Path $populatedFragments "offline-claims.json") -Value @'
{"name":"Offline","resourceClaims":[{
  "name":"ed-fi/students",
  "authorizationStrategyOverridesForCRUD":[{
    "actionName":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]
  }]
}]}
'@
    $populatedOutput = Join-Path $workspace "merged-fragments.json"
    $populatedMetadata = Join-Path $workspace "resolved-fragments.json"
    $null = Invoke-InstalledTool -ToolArguments @("merge", "--fragments", $populatedFragments,
        "--data-standard-version", "5.2", "--output", $populatedOutput)
    $null = Invoke-InstalledTool -ToolArguments @("resolve", "--input", $populatedOutput, "--output", $populatedMetadata)
    $metadataNodes = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($populatedMetadata))
    $offlineSets = @($metadataNodes | Where-Object { $_["claimSetName"].GetValue[string]() -ceq "Offline" })
    if ($offlineSets.Count -ne 1 -or $offlineSets[0]["claims"].Count -ne 1 -or
        $offlineSets[0]["authorizations"].Count -ne 1) {
        throw "Installed fragment merge must produce exactly one Offline resource grant and authorization."
    }
    $offlineClaim = $offlineSets[0]["claims"][0]
    $offlineAuthorization = $offlineSets[0]["authorizations"][0]
    $expectedActions = [System.Text.Json.Nodes.JsonNode]::Parse(
        '[{"name":"Read","authorizationStrategies":[{"name":"NoFurtherAuthorizationRequired"}]}]')
    if ($offlineClaim["name"].GetValue[string]() -cne "http://ed-fi.org/identity/claims/ed-fi/student" -or
        $offlineClaim["authorizationId"].GetValue[int]() -ne $offlineAuthorization["id"].GetValue[int]() -or
        -not [System.Text.Json.Nodes.JsonNode]::DeepEquals($expectedActions, $offlineAuthorization["actions"])) {
        throw "Installed fragment merge differs from the exact requested student Read grant."
    }
    Write-Output "Installed fragment merge and resolve match the exact requested student Read grant."

    $resolved = Join-Path $workspace "resolved.json"
    $expectedText = [IO.File]::ReadAllText("$projectDirectory.Tests.Unit/Fixtures/ResolveCommand/inheritance-expected.json").TrimEnd([char[]]"`r`n")
    $expectedBytes = [Text.Encoding]::UTF8.GetBytes($expectedText)
    $inputHash = (Get-FileHash (Join-Path $workspace "inheritance-input.json")).Hash
    foreach ($force in @($false, $true)) {
        $resolveArguments = @("resolve", "--input", "inheritance-input.json", "--output", $resolved)
        if ($force) { $resolveArguments += "--force" }
        $null = Invoke-InstalledTool -ToolArguments $resolveArguments
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($resolved)) -cne [Convert]::ToBase64String($expectedBytes)) {
            throw "Installed resolve differs from reviewed canonical UTF-8 metadata (force=$force)."
        }
    }
    if ((Get-FileHash (Join-Path $workspace "inheritance-input.json")).Hash -cne $inputHash) {
        throw "Installed resolve changed its input document."
    }
    Write-Output "Installed resolve matches reviewed inheritance metadata byte-for-byte on both runs."
    Write-Output "Verified $packageId $toolVersion; installed version $reportedVersion; all offline commands ran in $workspace."
    Write-Output "Package: $packageFile"
    Write-Output "SHA256: $((Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash)"
}
finally {
    Remove-SecurityMetadataToolsInstallation -ScratchDirectory $scratch
}
