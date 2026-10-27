# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Pack, inspect, install and run Security Metadata Tools from a fresh local-only feed.
.DESCRIPTION
    Reusable local/CI smoke action. Uses the shared version calculation and locked restore,
    checks the shared claims validator/schema,
    then compares installed conversion output with the reviewed representative fixture.
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
$toolVersion = Get-SecurityMetadataToolsVersion -AppVersion $AppVersion
$packageVersion = ($toolVersion -split '\+', 2)[0]
$packageId = "EdFi.Api.SecurityMetadataTools"
$packageFile = Join-Path $repoRoot "$packageId.$packageVersion.nupkg"
$projectDirectory = Join-Path $repoRoot "src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools"
$fixtureDirectory = "$projectDirectory.Tests.Unit/Fixtures/OdsSecurityMetadata"

# A child process contains Invoke-Main's exit and allows every native failure to be checked.
pwsh -NoProfile -File (Join-Path $repoRoot "build-config.ps1") Package `
    -Configuration $Configuration -DmsCSVersion $AppVersion -PackageTarget SecurityMetadataTools -LockedMode
if ($LASTEXITCODE -ne 0) { throw "Security Metadata Tools packaging failed ($LASTEXITCODE)." }

$scratch = Join-Path ([IO.Path]::GetTempPath()) "security-metadata-tools-$([guid]::NewGuid())"
$originalPackages = $env:NUGET_PACKAGES
try {
    $feed = New-Item -ItemType Directory -Path (Join-Path $scratch "feed")
    Copy-Item -LiteralPath $packageFile -Destination $feed.FullName
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
        if ($embeddedResources -cnotcontains "EdFi.DmsConfigurationService.Claims.Claims.JsonSchemaForClaims.json") {
            throw "Packaged shared claims library lacks the real CMS claims schema."
        }
    }
    finally {
        $peReader.Dispose()
        $claimsStream.Dispose()
    }
    $env:NUGET_PACKAGES = Join-Path $scratch "nuget-packages"
    $toolPath = Join-Path $scratch "tool"
    $nugetConfig = Join-Path $scratch "NuGet.Config"
    # Clear inherited sources, fallback folders and source mappings as well as the global cache.
    $escapedFeed = [Security.SecurityElement]::Escape($feed.FullName)
    Set-Content -LiteralPath $nugetConfig -Value "<configuration><packageSources><clear /><add key=""local"" value=""$escapedFeed"" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders><packageSourceMapping><clear /></packageSourceMapping></configuration>"
    dotnet tool install $packageId --tool-path $toolPath --source $feed.FullName `
        --configfile $nugetConfig --version $packageVersion --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Local-only tool installation failed ($LASTEXITCODE)." }

    $executable = Join-Path $toolPath $(if ($IsWindows) { "security-metadata-tools.exe" } else { "security-metadata-tools" })
    $reportedVersion = & $executable --version
    $revisionSeparator = if ($toolVersion.Contains("+")) { "." } else { "+" }
    $expectedVersionPattern = '^' + [regex]::Escape($toolVersion) + '(' + [regex]::Escape($revisionSeparator) + '[0-9a-f]{40})?$'
    if ($LASTEXITCODE -ne 0 -or $reportedVersion -cnotmatch $expectedVersionPattern) {
        throw "Installed command reports '$reportedVersion', expected '$toolVersion'."
    }
    $output = Join-Path $scratch "Claims.json"
    & $executable convert-ods-xml --input (Join-Path $fixtureDirectory "representative.xml") --output $output
    if ($LASTEXITCODE -ne 0) { throw "Installed conversion failed ($LASTEXITCODE)." }
    $actual = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($output))
    $expected = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText((Join-Path $fixtureDirectory "expected-claims.json")))
    if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($expected, $actual)) {
        throw "Installed conversion differs from the reviewed expected document."
    }

    Write-Output "Verified $packageId $toolVersion; installed version $reportedVersion; representative conversion matches."
    Write-Output "Package: $packageFile"
    Write-Output "SHA256: $((Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash)"
}
finally {
    $env:NUGET_PACKAGES = $originalPackages
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
