# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Guards the two hand-maintained mirrors that the committed packages.lock.json files depend on
# (see docs/NUGET-LOCK-FILES.md). Each is otherwise caught only by a full image build (T1) or by
# nothing at all (T2), so a drift would pass review and surface late.
#   T1 - each Dockerfile must COPY a packages.lock.json for every project whose *.csproj it COPYs,
#        or the in-image --locked-mode restore breaks.
#   T2 - the Directory.Build.props that build-{dms,config}.ps1 regenerate must reproduce the
#        restore-relevant content of the committed props (RestorePackagesWithLockFile + the analyzer
#        PackageReferences), or a build-script restore resolves a different graph than CI / Docker.

param()

$LockFileAreas = @(
    @{
        Area        = "dms"
        Dockerfile  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/Dockerfile"))
        BuildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-dms.ps1"))
        BuildProps  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/dms/Directory.Build.props"))
    }
    @{
        Area        = "config"
        Dockerfile  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/config/Dockerfile"))
        BuildScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../build-config.ps1"))
        BuildProps  = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../src/config/Directory.Build.props"))
    }
)

BeforeAll {
    # Returns the sorted, unique set of project directories whose COPY source matches the given
    # file pattern. [^\s]+ stops at whitespace, so a COPY destination (e.g. "./frontend/X/") never
    # matches -- only source specs ending in the pattern do.
    function Get-CopySourceDir {
        param(
            [Parameter(Mandatory)][string] $DockerfileContent,
            [Parameter(Mandatory)][string] $FilePattern
        )

        [regex]::Matches($DockerfileContent, "([^\s]+)/$FilePattern") |
            ForEach-Object { $_.Groups[1].Value.TrimStart("./") } |
            Sort-Object -Unique
    }

    # Extracts the Directory.Build.props here-string that the build script feeds to
    # Invoke-RegenerateFile. Returns $null if the expected call is not found.
    function Get-GeneratedPropsBlock {
        param([Parameter(Mandatory)][string] $BuildScriptContent)

        $match = [regex]::Match(
            $BuildScriptContent,
            '(?s)Invoke-RegenerateFile\s+"\$solutionRoot/Directory\.Build\.props"\s+@"(.*?)\r?\n"@'
        )
        if (-not $match.Success) { return $null }
        return $match.Groups[1].Value
    }

    # Returns a normalized signature per PackageReference: the Include id plus the
    # restore-relevant child metadata (IncludeAssets, PrivateAssets). The id alone is
    # not enough -- dropping PrivateAssets=all on an analyzer would let it flow
    # transitively and change the restore graph while leaving the id set identical.
    # Handles both the block form (<PackageReference ...>...</PackageReference>) and
    # the self-closing form (<PackageReference ... />), for which the body is empty.
    function Get-PackageReferenceSignature {
        param([Parameter(Mandatory)][AllowEmptyString()][string] $Content)

        [regex]::Matches($Content, '(?s)<PackageReference\s+Include="([^"]+)"\s*(?:/>|>(.*?)</PackageReference>)') |
            ForEach-Object {
                $body = $_.Groups[2].Value
                $includeAssets = (([regex]::Match($body, '(?s)<IncludeAssets>(.*?)</IncludeAssets>')).Groups[1].Value -replace '\s+', ' ').Trim()
                $privateAssets = (([regex]::Match($body, '(?s)<PrivateAssets>(.*?)</PrivateAssets>')).Groups[1].Value -replace '\s+', ' ').Trim()
                "$($_.Groups[1].Value)|IncludeAssets=$includeAssets|PrivateAssets=$privateAssets"
            } |
            Sort-Object -Unique
    }

    function Test-EnablesLockFile {
        param([Parameter(Mandatory)][AllowEmptyString()][string] $Content)

        return $Content -match "<RestorePackagesWithLockFile>\s*true\s*</RestorePackagesWithLockFile>"
    }
}

Describe "DMS-1133 Dockerfile lock-file COPY parity" {
    It "<Area>: COPYs a packages.lock.json for every project whose .csproj it COPYs" -ForEach $LockFileAreas {
        $content = Get-Content -LiteralPath $Dockerfile -Raw

        $csprojDirs = @(Get-CopySourceDir -DockerfileContent $content -FilePattern '\*\.csproj')
        $lockDirs = @(Get-CopySourceDir -DockerfileContent $content -FilePattern 'packages\.lock\.json')

        $csprojDirs |
            Should -Not -BeNullOrEmpty -Because "the $Area Dockerfile is expected to COPY project files (parser found none)"

        $missingLock = @($csprojDirs | Where-Object { $_ -notin $lockDirs })
        $missingCsproj = @($lockDirs | Where-Object { $_ -notin $csprojDirs })

        $missingLock |
            Should -BeNullOrEmpty -Because "each project whose *.csproj is COPYed must also COPY its packages.lock.json or the in-image --locked-mode restore breaks (missing lock COPY for: $($missingLock -join ', '))"
        $missingCsproj |
            Should -BeNullOrEmpty -Because "a packages.lock.json is COPYed for a project whose *.csproj is not, leaving a stale COPY entry (missing csproj COPY for: $($missingCsproj -join ', '))"
    }
}

Describe "DMS-1133 build-script Directory.Build.props parity" {
    It "<Area>: the regenerated Directory.Build.props mirrors the committed one's restore-relevant content" -ForEach $LockFileAreas {
        $committed = Get-Content -LiteralPath $BuildProps -Raw
        $scriptText = Get-Content -LiteralPath $BuildScript -Raw

        $generated = Get-GeneratedPropsBlock -BuildScriptContent $scriptText
        $generated |
            Should -Not -BeNullOrEmpty -Because "$Area build script must regenerate Directory.Build.props via Invoke-RegenerateFile"

        # Both must enable lock files; a regenerated props without it would drop the lock graph.
        Test-EnablesLockFile -Content $committed |
            Should -BeTrue -Because "the committed $Area Directory.Build.props sets RestorePackagesWithLockFile"
        Test-EnablesLockFile -Content $generated |
            Should -BeTrue -Because "the $Area build-script template must keep RestorePackagesWithLockFile"

        # The analyzer PackageReferences must match on id AND on their restore-relevant metadata
        # (IncludeAssets / PrivateAssets), or a regenerated props restores a different graph and
        # dirties/breaks the committed lock files.
        $committedRefs = @(Get-PackageReferenceSignature -Content $committed)
        $generatedRefs = @(Get-PackageReferenceSignature -Content $generated)

        ($generatedRefs -join "`n") |
            Should -Be ($committedRefs -join "`n") -Because "the $Area build-script template and committed Directory.Build.props must declare the same PackageReferences with the same IncludeAssets/PrivateAssets"
    }
}
