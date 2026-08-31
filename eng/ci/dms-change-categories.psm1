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

# Promoted-suite categories. Each names one or two integration lanes that a pull request runs only
# when its changed files reach them; the merge queue always runs all of them. The two DMS-API lanes
# share one category because they share one test project and one in-process pipeline, and the two
# SchemaTools lanes share one because they share one CLI - splitting either by dialect would let a
# provider-only change skip the lane that exercises that provider.
$script:CategoryName = @(
    'backend_mssql_relevant'
    'dms_api_relevant'
    'schematools_relevant'
    'cdc_relevant'
)

# Paths that reach every promoted lane: shared source, build and CI infrastructure, and the
# generators and models every suite compiles against. Only DMS-relevant paths reach this table, so
# the broad '.github/' prefix here cannot promote a file the relevance rules already rejected.
$script:AllCategoryExactPath = @(
    'build-dms.ps1'
    'src/Directory.Packages.props'
    'src/nuget.config'
    'src/dms/Directory.Build.props'
    'src/dms/Directory.Build.targets'
    'src/dms/EdFi.DataManagementService.sln'
    'src/dms/EdFi.DataManagementService-Docker.sln'
)

$script:AllCategoryPathPrefix = @(
    '.github/'
    'eng/'
    'src/dms/core/'
    'src/dms/backend/EdFi.DataManagementService.Backend/'
    'src/dms/backend/EdFi.DataManagementService.Backend.External/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/'
    'src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/'
    'src/dms/backend/Fixtures/'
    'src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/'
    'src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Plans/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Ddl/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/'
    'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.PublicContract.CompileCheck/'
    # The SQL Server provider backs every MSSQL lane, and the CDC suite is SQL-Server-only.
    'src/dms/backend/EdFi.DataManagementService.Backend.Mssql/'
)

# Paths that reach only some promoted lanes, or none. First match wins, so the specific entries
# precede the directory catch-alls below them. An empty category list means "known, and no promoted
# lane exercises it" - which is what keeps such a path out of the fail-open rule.
$script:NarrowPathCategory = @(
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/'; Category = @('backend_mssql_relevant') }
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/'; Category = @() }
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/'; Category = @('dms_api_relevant', 'schematools_relevant') }
    # Also reaches the backend MSSQL lane. The MSSQL integration project references
    # Backend.Cdc and holds the only DB-backed CDC tests in the suite, because the CDC lane
    # itself runs with --filter "Category!=DatabaseIntegration". Classifying CDC source as
    # cdc-only would skip every DB-backed CDC test on a change to CDC source.
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/'; Category = @('cdc_relevant', 'backend_mssql_relevant') }
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/'; Category = @('cdc_relevant') }
    @{ Prefix = 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/'; Category = @('cdc_relevant') }
    @{ Prefix = 'src/dms/frontend/'; Category = @('dms_api_relevant') }
    @{ Prefix = 'src/dms/tests/EdFi.DataManagementService.Tests.Integration/'; Category = @('dms_api_relevant') }
    @{ Prefix = 'src/dms/clis/EdFi.DataManagementService.SchemaTools/'; Category = @('schematools_relevant') }
    @{ Prefix = 'src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/'; Category = @('schematools_relevant') }
    @{ Prefix = 'src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/'; Category = @('schematools_relevant') }
    # No promoted lane builds the other CLIs, the E2E and unit test projects, or the Configuration
    # Service. They are still DMS-relevant, so the always-on jobs still run, and dedicated lanes
    # cover some of them: src/config has its own workflow, and the CLI integration matrix runs
    # exactly ApiSchemaDownloader and OpenApiGenerator. DocumentCacheAdmin.Tests.Integration runs
    # in no workflow at all.
    @{ Prefix = 'src/dms/clis/'; Category = @() }
    @{ Prefix = 'src/dms/tests/'; Category = @() }
    @{ Prefix = 'src/config/'; Category = @() }
)

function Get-DmsCategoryDefault {
    <#
    .SYNOPSIS
        A fresh category set with every promoted category at one starting value.
    #>
    param(
        [Parameter(Mandatory)]
        [bool]
        $InitialValue
    )

    $set = [ordered]@{}
    foreach ($name in $script:CategoryName) {
        $set[$name] = $InitialValue
    }

    return $set
}

function Get-DmsCategoryForPath {
    <#
    .SYNOPSIS
        The promoted categories one DMS-relevant path reaches.
    .DESCRIPTION
        Only call this for a path that already classified as DMS-relevant. An unrecognised path
        returns every category, so a new project directory is validated by everything until someone
        classifies it. That is the failure mode this design can afford; the opposite - a new
        directory silently skipping every promoted lane - is the one it cannot. A path that matches
        a known narrow rule with no categories returns nothing, which is how a known-but-unpromoted
        area stays out of the fail-open rule.
    #>
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    if (
        Test-DmsChangedFileMatch `
            -Path $Path `
            -ExactPath $script:AllCategoryExactPath `
            -PathPrefix $script:AllCategoryPathPrefix
    ) {
        return $script:CategoryName
    }

    foreach ($rule in $script:NarrowPathCategory) {
        if ($Path.StartsWith($rule.Prefix, [System.StringComparison]::Ordinal)) {
            return $rule.Category
        }
    }

    return $script:CategoryName
}

function ConvertTo-DmsChangeCategoryResult {
    <#
    .SYNOPSIS
        Flattens the flags into the object the workflow wrapper turns into step outputs.
    #>
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [bool]
        $FreshBuildRequired,

        [Parameter(Mandatory)]
        [bool]
        $DmsRelevant,

        [Parameter(Mandatory)]
        [bool]
        $Draft,

        [Parameter(Mandatory)]
        [System.Collections.Specialized.OrderedDictionary]
        $Category
    )

    $result = [ordered]@{
        fresh_build_required = $FreshBuildRequired
        dms_relevant         = $DmsRelevant
        draft                = $Draft
    }

    foreach ($name in $Category.Keys) {
        $result[$name] = [bool] $Category[$name]
    }

    return [pscustomobject] $result
}

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
        Returns fresh_build_required, dms_relevant, draft, and one flag per promoted-suite category.
        Only pull_request narrows: merge_group validates the merged result, so nothing may be
        skipped there, and every other event runs the full suite.
    .PARAMETER EventName
        The GitHub event name - pull_request, merge_group, or anything else (today only
        workflow_dispatch).
    .PARAMETER ChangedFile
        Repository-relative, forward-slash separated paths changed by the event. Blank entries are
        ignored, so a trailing newline in the diff output is harmless.
    .PARAMETER DiffUnavailable
        Set when no trustworthy file list could be produced - a missing merge-group base SHA, or a
        failed git diff. Narrowing requires a trustworthy list, so this forces the full suite.
    .PARAMETER IsDraft
        Set when the event is a pull request still in draft. Reported as the draft flag, which the
        expensive jobs gate on. It is independent of the file classification: a draft is a statement
        about the pull request, not about what it changed, so it survives the full-suite paths below.
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
        $DiffUnavailable,

        [switch]
        $IsDraft
    )

    # Only a pull request can be a draft. Guarded here as well as in the workflow so a payload that
    # carries a stale value on another event cannot silently gate the merge queue.
    $draft = $IsDraft.IsPresent -and $EventName -eq 'pull_request'

    if ($DiffUnavailable -or ($EventName -ne 'pull_request' -and $EventName -ne 'merge_group')) {
        return ConvertTo-DmsChangeCategoryResult `
            -FreshBuildRequired $true `
            -DmsRelevant $true `
            -Draft $draft `
            -Category (Get-DmsCategoryDefault -InitialValue $true)
    }

    # merge_group validates the merged result, so every promoted lane runs there too; only
    # pull_request narrows to the changed area.
    $narrows = $EventName -eq 'pull_request'
    $freshBuildRequired = $false
    $dmsRelevant = -not $narrows
    $category = Get-DmsCategoryDefault -InitialValue (-not $narrows)

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
            -not (
                Test-DmsChangedFileMatch `
                    -Path $path `
                    -ExactPath $script:DmsRelevantExactPath `
                    -PathPrefix $script:DmsRelevantPathPrefix
            )
        ) {
            # Not DMS-relevant at all - documentation, editor configuration and the like. It cannot
            # make a promoted suite relevant either, so it must not reach the fail-open rule.
            continue
        }

        $dmsRelevant = $true

        foreach ($name in (Get-DmsCategoryForPath -Path $path)) {
            $category[$name] = $true
        }
    }

    return ConvertTo-DmsChangeCategoryResult `
        -FreshBuildRequired $freshBuildRequired `
        -DmsRelevant $dmsRelevant `
        -Draft $draft `
        -Category $category
}

Export-ModuleMember -Function Get-DmsChangeCategory
