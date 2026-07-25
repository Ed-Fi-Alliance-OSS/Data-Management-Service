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
    `-d -v -DatabaseEngine <postgresql|mssql>` primitives, which compose the engine-correct
    compose set and run `docker compose ... -p <project> down --remove-orphans -v`. Both
    primitives run on every teardown, one per compose project an E2E run can create:
    start-local-dms.ps1 owns `dms-local` and start-published-dms.ps1 owns `dms-published`
    (created by `build-dms.ps1 E2ETest -UsePublishedImage`). A `down` for a project that the run
    never created is a successful no-op, so covering both is what makes teardown complete in
    either image mode rather than leaving the other mode's stack and volumes running.

    The compose projects are the sole authority for which containers, networks, and volumes are
    removed; this module never enumerates or removes resources by dangling state, by
    container-name pattern, by unprefixed volume name, or by removing the shared external `dms`
    network. The only removals outside the compose projects are the two known locally-built images,
    matched by their exact repository names so published and unrelated images are never touched.

    Parameters are forwarded to the primitives by name (hashtable splatting), so switches such as
    -d and -v bind as switches rather than positional argument strings.
#>

Set-StrictMode -Version Latest

# The two locally-built image names pinned on the build services in local-dms.yml / local-config.yml.
# Exact names only: removing these never touches the published edfialliance/* images, the CI
# ghcr.io/*-ci images, or any unrelated image.
$script:KnownLocalImageNames = @('ed-fi-api-local', 'ed-fi-api-config-local')

# One project-scoped teardown primitive per compose project an E2E run can create: start-local-dms.ps1
# owns `dms-local` and start-published-dms.ps1 owns `dms-published`. Teardown runs both because the
# suite wrappers are not told which image mode the run used, and a `down` for an absent project
# succeeds as a no-op.
$script:TeardownPrimitiveScripts = @('start-local-dms.ps1', 'start-published-dms.ps1')

function Get-E2ETeardownPlan {
    <#
    .SYNOPSIS
        Builds the teardown plan (one step per compose project, with its primitive path and named
        parameters, plus the known local images) for the selected engine and environment file. Pure
        except that it fails fast when the selected environment file does not exist. No Docker or
        filesystem mutation.
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
    # name/relative path resolved against the compose root.
    $environmentFilePath =
        if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
            $EnvironmentFile
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $resolvedComposeRoot $EnvironmentFile))
        }

    # Fail fast on a missing environment file (matching the Resolve-LocalSettingsEnvironmentFile
    # fail-fast-on-typo contract). Tearing down with a different compose/identity/engine file than
    # setup used would leave resources behind, so a typo must not silently fall back to another file.
    if (-not (Test-Path -LiteralPath $environmentFilePath -PathType Leaf)) {
        throw "E2E teardown environment file not found: '$environmentFilePath'. Pass -EnvironmentFile with the file the suite setup used (resolved against '$resolvedComposeRoot')."
    }

    # Named parameters for the project-scoped teardown primitives. Splatted as a hashtable so
    # -d / -v / -RemoveBootstrap bind as switches. -RemoveBootstrap clears the .bootstrap
    # workspace only after a successful down (the primitive gates it), so a subsequent setup
    # does not trip fingerprint-mismatch fail-fast.
    $startParameters = @{
        d               = $true
        v               = $true
        DatabaseEngine  = $DatabaseEngine
        EnvironmentFile = $environmentFilePath
        RemoveBootstrap = $true
    }

    [pscustomobject]@{
        DatabaseEngine       = $DatabaseEngine
        ComposeRoot          = $resolvedComposeRoot
        EnvironmentFilePath  = $environmentFilePath
        # Every compose project is torn down with the same engine, environment file, and switches.
        # Each step gets its own parameter copy so a caller inspecting or adjusting one step cannot
        # silently change another.
        TeardownSteps        = @(
            foreach ($primitiveScript in $script:TeardownPrimitiveScripts) {
                [pscustomobject]@{
                    StartScript     = Join-Path $resolvedComposeRoot $primitiveScript
                    StartParameters = $startParameters.Clone()
                }
            }
        )
        KnownLocalImageNames = $script:KnownLocalImageNames
    }
}

function Invoke-ComposeProjectTeardown {
    <#
    .SYNOPSIS
        Invokes one project-scoped teardown primitive (start-local-dms.ps1 or
        start-published-dms.ps1) with named parameters. Isolated so tests can exercise the
        parameter binding against a fake primitive.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $StartScript,

        [Parameter(Mandatory)]
        [hashtable] $StartParameters
    )

    & $StartScript @StartParameters

    if ($LASTEXITCODE -ne 0) {
        $primitiveName = [System.IO.Path]::GetFileName($StartScript)
        throw "Engine-aware teardown failed: $primitiveName exited with code $LASTEXITCODE."
    }
}

function Remove-KnownLocalImage {
    <#
    .SYNOPSIS
        Removes a single locally-built image by its exact repository name, if present. Never
        touches published or unrelated images. Throws (naming the image) if removal fails.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory)]
        [string] $ImageName
    )

    $imageId = docker images -q $ImageName 2>$null
    if ($imageId -and $PSCmdlet.ShouldProcess($ImageName, "Remove locally-built image")) {
        docker rmi $ImageName -f 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to remove locally-built image '$ImageName' (docker rmi exit code $LASTEXITCODE)."
        }
    }
}

function Invoke-E2EEngineAwareTeardown {
    <#
    .SYNOPSIS
        Tears down an E2E suite's Docker environment safely: delegates to the project-scoped
        primitive for every compose project an E2E run can create, then removes only the two known
        locally-built images.
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

    # Attempt every compose project even when an earlier down fails, so one project's failure never
    # leaves another project's containers and volumes running, then report the failures together.
    # Image removal still waits until every down succeeded: an image in use by a surviving container
    # cannot be removed anyway.
    $teardownFailures = @()
    foreach ($step in $plan.TeardownSteps) {
        try {
            Invoke-ComposeProjectTeardown -StartScript $step.StartScript -StartParameters $step.StartParameters
        }
        catch {
            $teardownFailures += $_.Exception.Message
        }
    }

    if ($teardownFailures.Count -gt 0) {
        throw ($teardownFailures -join [System.Environment]::NewLine)
    }

    if (-not $SkipLocalImageRemoval) {
        foreach ($imageName in $plan.KnownLocalImageNames) {
            Remove-KnownLocalImage -ImageName $imageName
        }
    }

    $plan
}

Export-ModuleMember -Function `
    Get-E2ETeardownPlan, `
    Invoke-ComposeProjectTeardown, `
    Remove-KnownLocalImage, `
    Invoke-E2EEngineAwareTeardown
