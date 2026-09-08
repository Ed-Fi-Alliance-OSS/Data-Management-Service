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
    # Returns the sorted, unique set of project directories whose COPY *source* argument ends in
    # the given file pattern. Parses actual COPY instructions line by line: only logical lines whose
    # instruction keyword is COPY are considered, flag tokens (--from=, --chmod=, ...) are dropped,
    # and the final operand (the COPY destination) is excluded. A comment, a RUN command, or a
    # destination path that merely contains a matching fragment therefore cannot be mistaken for a
    # copied source. Assumes the shell (space-separated) COPY form, which both Dockerfiles use.
    function Get-CopySourceDir {
        param(
            [Parameter(Mandatory)][string] $DockerfileContent,
            [Parameter(Mandatory)][string] $FilePattern
        )

        # Join backslash line-continuations so a multi-line COPY is one logical instruction.
        $logicalLines = ($DockerfileContent -replace '\\\r?\n', ' ') -split '\r?\n'

        $dirs = foreach ($line in $logicalLines) {
            $tokens = @($line.Trim() -split '\s+' | Where-Object { $_ -ne '' })
            if ($tokens.Count -eq 0 -or $tokens[0] -ine 'COPY') { continue }

            # Drop the COPY keyword and any --flag options, then drop the final operand (the
            # destination); what remains are the source arguments.
            $operands = @($tokens | Select-Object -Skip 1 | Where-Object { $_ -notlike '--*' })
            if ($operands.Count -lt 2) { continue }

            foreach ($source in $operands[0..($operands.Count - 2)]) {
                $match = [regex]::Match($source, "^(.+)/$FilePattern`$")
                if ($match.Success) { $match.Groups[1].Value.TrimStart("./") }
            }
        }

        $dirs | Sort-Object -Unique
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
    # restore-relevant metadata (IncludeAssets, PrivateAssets). The id alone is not
    # enough -- dropping PrivateAssets=all on an analyzer would let it flow transitively
    # and change the restore graph while leaving the id set identical. Parsing via [xml]
    # (rather than regex) is robust to attribute order, quote style, and the child-element
    # vs attribute form of IncludeAssets/PrivateAssets; it also fails loudly on malformed
    # content, which is correct here -- a props file that is not well-formed XML would not
    # build, so it should never silently drop a reference from the comparison.
    function Get-PackageReferenceSignature {
        param([Parameter(Mandatory)][AllowEmptyString()][string] $Content)

        if ([string]::IsNullOrWhiteSpace($Content)) { return @() }

        ([xml] $Content).SelectNodes('//PackageReference') |
            ForEach-Object {
                $id = $_.GetAttribute('Include')
                $includeNode = $_.SelectSingleNode('IncludeAssets')
                $privateNode = $_.SelectSingleNode('PrivateAssets')
                $includeRaw = if ($includeNode) { $includeNode.InnerText } else { $_.GetAttribute('IncludeAssets') }
                $privateRaw = if ($privateNode) { $privateNode.InnerText } else { $_.GetAttribute('PrivateAssets') }
                $includeAssets = ($includeRaw -replace '\s+', ' ').Trim()
                $privateAssets = ($privateRaw -replace '\s+', ' ').Trim()
                "$id|IncludeAssets=$includeAssets|PrivateAssets=$privateAssets"
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
