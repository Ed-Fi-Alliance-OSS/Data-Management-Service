# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Shared engine-aware teardown for the DMS E2E suites.
.DESCRIPTION
    Provides a single, safe teardown path shared by the standard DMS E2E and Instance
    Management E2E suites. Teardown delegates to the project-scoped
    `start-local-dms.ps1 -d -v -DatabaseEngine <postgresql|mssql>` primitive, which composes
    the engine-correct compose set and runs `docker compose ... -p dms-local down --remove-orphans -v`.
    The compose project (`dms-local`) is the sole authority for which containers, networks, and
    volumes are removed; this module never enumerates or removes resources by dangling state, by
    container-name pattern, by unprefixed volume name, or by removing the shared external `dms`
    network. The only removals outside the compose project are the two known locally-built images,
    matched by their exact repository names so published and unrelated images are never touched.
#>

Set-StrictMode -Version Latest

# The two locally-built image names pinned on the build services in local-dms.yml / local-config.yml.
# Exact names only: removing these never touches the published edfialliance/* images, the CI
# ghcr.io/*-ci images, or any unrelated image.
$script:KnownLocalImageNames = @('ed-fi-api-local', 'ed-fi-api-config-local')

function Get-E2ETeardownPlan {
    <#
    .SYNOPSIS
        Builds the teardown plan (primitive path, forwarded arguments, and known local images)
        for the selected engine and environment file. Pure: no Docker or filesystem mutation.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('postgresql', 'mssql')]
        [string] $DatabaseEngine = 'postgresql',

        [Parameter(Mandatory)]
        [string] $EnvironmentFile,

        [string] $ComposeRoot = $PSScriptRoot
    )

    $resolvedComposeRoot = [System.IO.Path]::GetFullPath($ComposeRoot)

    # Resolve the environment file the suite setup used. A caller may pass an absolute path or a
    # name/relative path resolved against the compose root. If the requested file is absent, fall
    # back to the shared .env so a clean checkout still tears down, matching the primitive's own
    # local-settings default.
    $environmentFilePath =
        if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
            $EnvironmentFile
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $resolvedComposeRoot $EnvironmentFile))
        }

    if (-not (Test-Path -LiteralPath $environmentFilePath)) {
        $fallbackEnvironmentFile = Join-Path $resolvedComposeRoot '.env'
        if (Test-Path -LiteralPath $fallbackEnvironmentFile) {
            $environmentFilePath = [System.IO.Path]::GetFullPath($fallbackEnvironmentFile)
        }
    }

    [pscustomobject]@{
        DatabaseEngine        = $DatabaseEngine
        ComposeRoot           = $resolvedComposeRoot
        EnvironmentFilePath   = $environmentFilePath
        StartScript           = Join-Path $resolvedComposeRoot 'start-local-dms.ps1'
        # Project-scoped teardown via the existing primitive. -RemoveBootstrap clears the
        # .bootstrap workspace only after a successful down (the primitive gates it), so a
        # subsequent setup does not trip fingerprint-mismatch fail-fast.
        StartArguments        = @(
            '-d'
            '-v'
            '-DatabaseEngine', $DatabaseEngine
            '-EnvironmentFile', $environmentFilePath
            '-RemoveBootstrap'
        )
        KnownLocalImageNames  = $script:KnownLocalImageNames
    }
}

function Invoke-StartLocalDmsTeardown {
    <#
    .SYNOPSIS
        Invokes the project-scoped start-local-dms.ps1 teardown primitive. Isolated so tests can
        mock the primitive invocation without running the full stack.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $StartScript,

        [Parameter(Mandatory)]
        [string[]] $StartArguments
    )

    & $StartScript @StartArguments

    if ($LASTEXITCODE -ne 0) {
        throw "Engine-aware teardown failed: start-local-dms.ps1 exited with code $LASTEXITCODE."
    }
}

function Remove-KnownLocalImage {
    <#
    .SYNOPSIS
        Removes a single locally-built image by its exact repository name, if present. Never
        touches published or unrelated images.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string] $ImageName
    )

    $imageId = docker images -q $ImageName 2>$null
    if ($imageId -and $PSCmdlet.ShouldProcess($ImageName, "Remove locally-built image")) {
        docker rmi $ImageName -f 2>&1 | Out-Null
    }
}

function Invoke-E2EEngineAwareTeardown {
    <#
    .SYNOPSIS
        Tears down an E2E suite's Docker environment safely: delegates to the project-scoped
        start-local-dms.ps1 primitive, then removes only the two known locally-built images.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('postgresql', 'mssql')]
        [string] $DatabaseEngine = 'postgresql',

        [Parameter(Mandatory)]
        [string] $EnvironmentFile,

        [string] $ComposeRoot = $PSScriptRoot,

        # Skip removing the two known locally-built images (leaves them cached for reuse).
        [switch] $SkipLocalImageRemoval
    )

    $plan = Get-E2ETeardownPlan -DatabaseEngine $DatabaseEngine -EnvironmentFile $EnvironmentFile -ComposeRoot $ComposeRoot

    Invoke-StartLocalDmsTeardown -StartScript $plan.StartScript -StartArguments $plan.StartArguments

    if (-not $SkipLocalImageRemoval) {
        foreach ($imageName in $plan.KnownLocalImageNames) {
            Remove-KnownLocalImage -ImageName $imageName
        }
    }

    $plan
}

Export-ModuleMember -Function `
    Get-E2ETeardownPlan, `
    Invoke-StartLocalDmsTeardown, `
    Remove-KnownLocalImage, `
    Invoke-E2EEngineAwareTeardown
