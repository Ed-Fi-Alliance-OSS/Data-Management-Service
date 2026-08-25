# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [string]
    $DMSVersion = "8.0.0",

    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Release",

    [string]
    $ToolPath = (Join-Path ([System.IO.Path]::GetTempPath()) "dms-document-cache-tool"),

    [switch]
    $SkipPackageBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$projectPath = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/EdFi.DataManagementService.DocumentCacheAdmin.csproj"
$readmePath = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md"
$buildScriptPath = Join-Path $repoRoot "build-dms.ps1"
$ToolPath = [System.IO.Path]::GetFullPath($ToolPath)

function Assert-RequiredText {
    param(
        [string]
        $Text,

        [string]
        $Expected,

        [string]
        $Context
    )

    if (-not $Text.Contains($Expected, [System.StringComparison]::Ordinal)) {
        throw "$Context does not contain '$Expected'."
    }
}

function Assert-RequiredTextIgnoreCase {
    param(
        [string]
        $Text,

        [string]
        $Expected,

        [string]
        $Context
    )

    if ($Text.IndexOf($Expected, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Context does not contain '$Expected'."
    }
}

function Invoke-NativeCommand {
    param(
        [string]
        $FilePath,

        [string[]]
        $Arguments,

        [string]
        $WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        Write-Information "> $FilePath $($Arguments -join ' ')" -InformationAction Continue
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            throw "$FilePath failed with exit code $exitCode."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-CapturedNativeCommand {
    param(
        [string]
        $FilePath,

        [string[]]
        $Arguments,

        [string]
        $WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        Write-Information "> $FilePath $($Arguments -join ' ')" -InformationAction Continue
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | ForEach-Object { $_.ToString() }) -join [System.Environment]::NewLine

        if ($exitCode -ne 0) {
            throw "$FilePath failed with exit code $exitCode. Output: $text"
        }

        return $text
    }
    finally {
        Pop-Location
    }
}

function Get-ProjectProperty {
    param(
        [xml]
        $Project,

        [string]
        $Name
    )

    $node = $Project.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node) {
        throw "Project property '$Name' was not found in '$projectPath'."
    }

    return $node.InnerText
}

function Get-PackageEntryName {
    param(
        [string]
        $PackagePath
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        return @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackageEntry {
    param(
        [string[]]
        $Entries,

        [string]
        $ExpectedEntry,

        [string]
        $Context
    )

    if ($Entries -notcontains $ExpectedEntry) {
        throw "$Context does not contain package entry '$ExpectedEntry'."
    }
}

function Invoke-WithProcessEnvironment {
    param(
        [hashtable]
        $Values,

        [scriptblock]
        $ScriptBlock
    )

    $previousValues = @{}
    foreach ($name in $Values.Keys) {
        $previousValues[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
    }

    try {
        foreach ($name in $Values.Keys) {
            [System.Environment]::SetEnvironmentVariable($name, $Values[$name], "Process")
        }

        & $ScriptBlock
    }
    finally {
        foreach ($name in $previousValues.Keys) {
            [System.Environment]::SetEnvironmentVariable($name, $previousValues[$name], "Process")
        }
    }
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$packageId = Get-ProjectProperty -Project $project -Name "PackageId"
$toolCommandName = Get-ProjectProperty -Project $project -Name "ToolCommandName"

if ($packageId -ne "EdFi.Api.DocumentCacheAdmin") {
    throw "Unexpected DocumentCacheAdmin package ID '$packageId'."
}

if ($toolCommandName -ne "dms-document-cache") {
    throw "Unexpected DocumentCacheAdmin tool command name '$toolCommandName'."
}

$readmeText = Get-Content -LiteralPath $readmePath -Raw
Assert-RequiredText -Text $readmeText -Expected $packageId -Context $readmePath
Assert-RequiredText -Text $readmeText -Expected $toolCommandName -Context $readmePath

if (-not $SkipPackageBuild) {
    Invoke-NativeCommand `
        -FilePath "pwsh" `
        -Arguments @(
            "-NoProfile",
            "-File",
            $buildScriptPath,
            "-Command",
            "Package",
            "-Configuration",
            $Configuration,
            "-DMSVersion",
            $DMSVersion,
            "-PackageTarget",
            "DocumentCacheAdmin"
        )
}

$packagePath = Join-Path $repoRoot "$packageId.$DMSVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Expected generated package was not found: $packagePath"
}

$packageEntryNames = Get-PackageEntryName -PackagePath $packagePath
Assert-PackageEntry `
    -Entries $packageEntryNames `
    -ExpectedEntry "tools/net10.0/any/ApiSchema/bootstrap-api-schema-manifest.json" `
    -Context $packagePath
Assert-PackageEntry `
    -Entries $packageEntryNames `
    -ExpectedEntry "tools/net10.0/any/ApiSchema/Packages/EdFi.DataStandard52.ApiSchema/ApiSchema.json" `
    -Context $packagePath
Assert-PackageEntry `
    -Entries $packageEntryNames `
    -ExpectedEntry "tools/net10.0/any/ApiSchema/Packages/EdFi.DataStandard52.TPDM.ApiSchema/ApiSchema.json" `
    -Context $packagePath

if (Test-Path -LiteralPath $ToolPath) {
    Remove-Item -LiteralPath $ToolPath -Recurse -Force -ErrorAction Stop
}

New-Item -ItemType Directory -Path $ToolPath -Force | Out-Null

$nugetConfigPath = Join-Path $ToolPath "nuget.config"
$escapedRepoRoot = [System.Security.SecurityElement]::Escape($repoRoot)
Set-Content -LiteralPath $nugetConfigPath -Encoding utf8NoBOM -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="document-cache-admin-local" value="$escapedRepoRoot" />
  </packageSources>
</configuration>
"@

$previousNugetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $ToolPath ".nuget-packages"

try {
    Invoke-NativeCommand `
        -FilePath "dotnet" `
        -Arguments @(
            "tool",
            "install",
            "--tool-path",
            $ToolPath,
            $packageId,
            "--configfile",
            $nugetConfigPath,
            "--version",
            $DMSVersion
        )

    $toolExecutableName = if ($IsWindows) { "$toolCommandName.exe" } else { $toolCommandName }
    $toolExecutablePath = Join-Path $ToolPath $toolExecutableName
    if (-not (Test-Path -LiteralPath $toolExecutablePath -PathType Leaf)) {
        throw "Installed command was not found: $toolExecutablePath"
    }

    $toolList = Invoke-CapturedNativeCommand `
        -FilePath "dotnet" `
        -Arguments @("tool", "list", "--tool-path", $ToolPath)
    Assert-RequiredTextIgnoreCase -Text $toolList -Expected $packageId -Context "dotnet tool list"
    Assert-RequiredText -Text $toolList -Expected $DMSVersion -Context "dotnet tool list"
    Assert-RequiredText -Text $toolList -Expected $toolCommandName -Context "dotnet tool list"

    $rootHelp = Invoke-CapturedNativeCommand -FilePath $toolExecutablePath -Arguments @("--help")
    Assert-RequiredText -Text $rootHelp -Expected "Usage:" -Context "$toolCommandName --help"
    Assert-RequiredText -Text $rootHelp -Expected $toolCommandName -Context "$toolCommandName --help"
    Assert-RequiredText -Text $rootHelp -Expected "status" -Context "$toolCommandName --help"
    Assert-RequiredText -Text $rootHelp -Expected "rebuild-online" -Context "$toolCommandName --help"

    $rebuildHelp = Invoke-CapturedNativeCommand -FilePath $toolExecutablePath -Arguments @("rebuild-online", "--help")
    Assert-RequiredText -Text $rebuildHelp -Expected "Usage:" -Context "$toolCommandName rebuild-online --help"
    Assert-RequiredText -Text $rebuildHelp -Expected "$toolCommandName rebuild-online" -Context "$toolCommandName rebuild-online --help"
    Assert-RequiredText -Text $rebuildHelp -Expected "--confirm" -Context "$toolCommandName rebuild-online --help"
    Assert-RequiredText -Text $rebuildHelp -Expected "--command-timeout-seconds" -Context "$toolCommandName rebuild-online --help"

    $bundledSchemaStatus = Invoke-WithProcessEnvironment `
        -Values @{
            "DOTNET_ENVIRONMENT" = ""
            "ASPNETCORE_ENVIRONMENT" = ""
            "AppSettings__AllowIdentityUpdateOverrides" = ""
            "AppSettings__MaximumPageSize" = "500"
            "AppSettings__DefaultPartitionCount" = "10"
            "AppSettings__BypassAuthorization" = "true"
            "AppSettings__UseApiSchemaPath" = "false"
            "AppSettings__ApiSchemaPath" = $null
            "ConfigurationServiceSettings__BaseUrl" = $null
            "ConfigurationServiceSettings__ClientId" = $null
            "ConfigurationServiceSettings__ClientSecret" = $null
            "ConfigurationServiceSettings__Scope" = $null
            "ConfigurationServiceSettings__EncryptionKey" = $null
        } `
        -ScriptBlock {
            Invoke-CapturedNativeCommand `
                -FilePath $toolExecutablePath `
                -Arguments @(
                    "status",
                    "--data-store-id",
                    "1",
                    "--datastore",
                    "postgresql",
                    "--json",
                    "--status-timeout-seconds",
                    "1",
                    "--status-observation-timeout-seconds",
                    "0.1"
                )
        }
    Assert-RequiredText -Text $bundledSchemaStatus -Expected '"status":"unresolved"' -Context "bundled schema status smoke"
    Assert-RequiredText -Text $bundledSchemaStatus -Expected '"reason":"cmsUnavailable"' -Context "bundled schema status smoke"
    if ($bundledSchemaStatus.Contains("bootstrap-api-schema-manifest", [System.StringComparison]::Ordinal)) {
        throw "Bundled schema status smoke reported an ApiSchema manifest failure."
    }
}
finally {
    if ($null -eq $previousNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }
}

Write-Information "DocumentCacheAdmin package smoke passed for $packageId $DMSVersion installed as $toolCommandName at $ToolPath." -InformationAction Continue
