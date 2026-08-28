# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Classifier for the On DMS Pull Request workflow's change detection. The rules used to live in an
# inline bash step, where nothing in the repository could execute them and a mistake was only
# observable by running CI. They are pure - a file list in, a set of flags out - so they belong in a
# module that Pester can call directly. Producing the file list stays in the workflow, because only
# the workflow knows the diff base.

# Paths whose change forces a fresh Docker rebuild. The exact list carries whole-file paths; the
# prefix list carries directory roots, and a prefix match crosses directory separators the same way
# the bash `case` glob it replaces did.
$script:FreshBuildExactPath = @(
    'src/Directory.Packages.props'
    'src/nuget.config'
    'src/dms/Dockerfile'
    'src/config/Dockerfile'
)

$script:FreshBuildPathPrefix = @(
    'eng/docker-compose/'
)

# Paths whose change makes a pull request DMS-relevant. src/config counts: the DMS E2E lanes bring
# up the Configuration Service built from src/config, so a config-only change still has to trigger
# DMS validation. Note that only .github/actions and .github/workflows qualify, not all of .github.
$script:DmsRelevantExactPath = @(
    'build-dms.ps1'
    'src/Directory.Packages.props'
    'src/nuget.config'
)

$script:DmsRelevantPathPrefix = @(
    '.github/actions/'
    '.github/workflows/'
    'eng/'
    'src/dms/'
    'src/config/'
)

function Test-DmsChangedFileMatch {
    <#
    .SYNOPSIS
        Reports whether one repository-relative path matches a category's exact-path or
        directory-prefix list.
    .DESCRIPTION
        Comparisons are ordinal and case-sensitive because Git paths are case-sensitive and the
        bash `case` statement this replaces was too. A case-insensitive comparison would classify
        SRC/dms/Foo.cs as DMS-relevant on a repository that can also contain src/dms/Foo.cs.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [string[]]
        $ExactPath = @(),

        [string[]]
        $PathPrefix = @()
    )

    foreach ($candidate in $ExactPath) {
        if ([string]::Equals($Path, $candidate, [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    foreach ($prefix in $PathPrefix) {
        if ($Path.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

function Get-DmsChangeCategory {
    <#
    .SYNOPSIS
        Classifies an event's changed files into the flags the DMS pull request workflow gates on.
    .DESCRIPTION
        Returns fresh_build_required and dms_relevant. Only pull_request narrows: merge_group
        validates the merged result, so nothing may be skipped there, and every other event runs the
        full suite.
    .PARAMETER EventName
        The GitHub event name - pull_request, merge_group, or anything else (today only
        workflow_dispatch).
    .PARAMETER ChangedFile
        Repository-relative, forward-slash separated paths changed by the event. Blank entries are
        ignored, so a trailing newline in the diff output is harmless.
    .PARAMETER DiffUnavailable
        Set when no trustworthy file list could be produced - a missing merge-group base SHA, or a
        failed git diff. Narrowing requires a trustworthy list, so this forces the full suite.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]
        $EventName,

        [string[]]
        $ChangedFile = @(),

        [switch]
        $DiffUnavailable
    )

    if ($DiffUnavailable -or ($EventName -ne 'pull_request' -and $EventName -ne 'merge_group')) {
        return [pscustomobject]@{
            fresh_build_required = $true
            dms_relevant         = $true
        }
    }

    $freshBuildRequired = $false
    $dmsRelevant = $EventName -ne 'pull_request'

    foreach ($path in $ChangedFile) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if (
            Test-DmsChangedFileMatch `
                -Path $path `
                -ExactPath $script:FreshBuildExactPath `
                -PathPrefix $script:FreshBuildPathPrefix
        ) {
            $freshBuildRequired = $true
        }

        if (
            Test-DmsChangedFileMatch `
                -Path $path `
                -ExactPath $script:DmsRelevantExactPath `
                -PathPrefix $script:DmsRelevantPathPrefix
        ) {
            $dmsRelevant = $true
        }
    }

    return [pscustomobject]@{
        fresh_build_required = $freshBuildRequired
        dms_relevant         = $dmsRelevant
    }
}

Export-ModuleMember -Function Get-DmsChangeCategory
