# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The published Docker Hub images are built from Nuget.Dockerfile, not from the source-build
# Dockerfile. Microsoft.Data.SqlClient does not support .NET's globalization-invariant mode, which
# the Alpine base enables by default, so an image without ICU cannot use the SQL Server backend at
# all. The source-build Dockerfiles carry the fix; these tests keep the published ones in step.

param()

BeforeDiscovery {
    $script:images = @(
        @{ Name = "DMS"; Packages = @("icu-libs") }
        @{ Name = "Configuration Service"; Packages = @("icu-libs", "icu-data-full") }
    )
}

BeforeAll {
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:dockerfiles = @{
        "DMS"                   = @{
            Published = Join-Path $script:repoRoot "src/dms/Nuget.Dockerfile"
            Source    = Join-Path $script:repoRoot "src/dms/Dockerfile"
        }
        "Configuration Service" = @{
            Published = Join-Path $script:repoRoot "src/config/Nuget.Dockerfile"
            Source    = Join-Path $script:repoRoot "src/config/Dockerfile"
        }
    }

    # The "runtimebase" stage: from its FROM line up to the next FROM. apk packages installed there
    # and ENV set there or later are what the running container gets.
    function Get-ApkPackages([string]$Path) {
        $lines = Get-Content -LiteralPath $Path
        ($lines | Where-Object { $_ -match '^\s*RUN\s+apk\s' }) -join ' ' -split '\s+' |
            Where-Object { $_ -and $_ -notmatch '^(RUN|apk|add|--no-cache)$' } |
            ForEach-Object { ($_ -split '=')[0] }
    }

    function Get-InvariantSetting([string]$Path) {
        $match = Get-Content -LiteralPath $Path |
            Select-String -Pattern '^\s*ENV\s+DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=(\S+)' |
            Select-Object -Last 1
        if ($match) { $match.Matches[0].Groups[1].Value } else { $null }
    }
}

Describe "Published <Name> image supports the SQL Server backend" -ForEach $script:images {
    It "installs <Packages> in Nuget.Dockerfile" {
        $installed = Get-ApkPackages $script:dockerfiles[$Name].Published
        foreach ($package in $Packages) {
            $installed | Should -Contain $package -Because "Microsoft.Data.SqlClient needs ICU ($package)"
        }
    }

    It "disables globalization-invariant mode in Nuget.Dockerfile" {
        Get-InvariantSetting $script:dockerfiles[$Name].Published | Should -Be "false"
    }

    It "matches the source-build Dockerfile's ICU packages and invariant setting" {
        $source = $script:dockerfiles[$Name].Source
        $published = $script:dockerfiles[$Name].Published
        $sourceIcu = @(Get-ApkPackages $source | Where-Object { $_ -like 'icu-*' } | Sort-Object)
        $publishedIcu = @(Get-ApkPackages $published | Where-Object { $_ -like 'icu-*' } | Sort-Object)

        $publishedIcu | Should -Be $sourceIcu
        Get-InvariantSetting $published | Should -Be (Get-InvariantSetting $source)
    }
}
