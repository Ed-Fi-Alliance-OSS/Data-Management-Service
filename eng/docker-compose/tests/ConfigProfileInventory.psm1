# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Get-ConfigProfileInventory {
    <#
    .SYNOPSIS
        The single authority for classifying every tracked eng/docker-compose/.env* profile. Both the fast
        static seam guard (DatabaseEngineEnvironmentFile.Tests.ps1) and the live docker-compose resolution
        guard (RuntimeConfigContract.Tests.ps1) consume this one result, so there is never a second competing
        list.

    .DESCRIPTION
        The inventory is derived by GLOB from the tracked set (git ls-files), NOT by filtering on the property
        under test: every tracked profile defaults to a switch-capable full-stack base that must carry the
        DMS_CONFIG_DATABASE_NAME seam. Only three explicit, existence-checked exception sets are subtracted, so
        a new full-stack profile that forgets the seam FAILS the guard rather than silently disappearing:

          * StandaloneConfig    - the standalone Configuration Service lane (start-local-config.ps1 /
            build-config.ps1). It has no -SeparateConfigDatabase switch and its CMS connection is authoritative
            for its single target, so it is outside the topology seam. It IS connection-bearing.
          * EngineOverlays      - a connection-bearing engine overlay (.env.mssql) that is composed onto a base
            profile (Resolve-DatabaseEngineEnvironmentFile). It must still route its CMS connection through the
            seam (checked by the static guard), but it is not a standalone full-stack base, so the live
            compose-resolution guard exercises it via a base rather than standalone.
          * DataStandardOverlays - data-standard / bootstrap overlays (.env.ds*, .env.bootstrap.ds*) composed
            onto a base profile. They carry NO standalone CMS connection.

        Fails CLOSED but CONTAINED: git-unavailable, ls-files failure, an empty tracked set, and an empty
        switch-capable set are captured in the returned .Error string (the caller asserts it in one test)
        rather than thrown, so an inventory problem does not abort discovery of unrelated tests. Each exception
        record carries IsTracked (a literal, case-sensitive membership in the tracked set - not merely
        file-exists) and IsConnectionBearing, so a stale or mis-shaped exception is caught by the caller.

    .PARAMETER RepoRoot
        Absolute path to the git repository root. Git is invoked with -C against this path (never the ambient
        working directory) so the tracked pathspec resolves deterministically.

    .PARAMETER DockerComposeRoot
        Absolute path to eng/docker-compose, used to read each profile with the shared env reader.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$DockerComposeRoot
    )

    $result = [pscustomobject]@{
        Error                = $null
        TrackedNames         = @()
        SwitchCapableBases   = @()
        EngineOverlays       = @()
        StandaloneConfig     = @()
        DataStandardOverlays = @()
    }

    try {
        # Import WITHOUT -Force: a -Force reload would re-home env-utility out of a caller's session scope
        # (removing ReadValuesFromEnvFile from a test that imported it for its own use). Load if absent,
        # reuse if present.
        Import-Module (Join-Path $DockerComposeRoot "env-utility.psm1") -ErrorAction Stop

        if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) {
            $result.Error = "git is unavailable; the tracked env-file inventory cannot be established."
            return $result
        }

        # Tracked enumeration relative to the repo root - never the ambient working directory - so generated
        # (.derived) and gitignored (.env) files are excluded and the inventory is exactly what ships.
        $tracked = & git -C $RepoRoot ls-files -- "eng/docker-compose/.env*"
        if ($LASTEXITCODE -ne 0) {
            $result.Error = "'git ls-files' failed (exit $LASTEXITCODE); the tracked env-file inventory cannot be established."
            return $result
        }
        $names = @($tracked | ForEach-Object { [System.IO.Path]::GetFileName($_) })
        if ($names.Count -eq 0) {
            $result.Error = "no tracked 'eng/docker-compose/.env*' files were discovered; the guard must not pass vacuously."
            return $result
        }
        $result.TrackedNames = $names

        $isConnectionBearing = {
            param([string]$Name)
            $path = Join-Path $DockerComposeRoot $Name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                return $false
            }
            $values = ReadValuesFromEnvFile $path
            return (
                $values.ContainsKey('DMS_CONFIG_DATABASE_CONNECTION_STRING') -and
                -not [string]::IsNullOrWhiteSpace([string]$values['DMS_CONFIG_DATABASE_CONNECTION_STRING'])
            )
        }

        # The ONLY hand-maintained sets. Everything else is a switch-capable base and must carry the seam.
        $standaloneDefs = @(
            @{ Name = '.env.config.e2e';                   Reason = 'standalone CMS E2E lane (start-local-config.ps1 / build-config.ps1); no -SeparateConfigDatabase switch; the CMS connection owns its single target' }
            @{ Name = '.env.config.mssql.e2e';             Reason = 'standalone CMS SQL Server E2E lane; no -SeparateConfigDatabase switch' }
            @{ Name = '.env.config.mssql.multitenant.e2e'; Reason = 'standalone CMS multitenant SQL Server E2E lane; no -SeparateConfigDatabase switch' }
        )
        $engineOverlayDefs = @(
            @{ Name = '.env.mssql'; Reason = 'SQL Server engine overlay composed onto a base profile (Resolve-DatabaseEngineEnvironmentFile); it carries a CMS connection and must route through the seam, but is not a standalone full-stack base' }
        )
        $dataStandardOverlayDefs = @(
            @{ Name = '.env.ds52';           Reason = 'data-standard overlay composed onto a base profile; carries no standalone CMS connection' }
            @{ Name = '.env.ds61';           Reason = 'data-standard overlay composed onto a base profile; carries no standalone CMS connection' }
            @{ Name = '.env.bootstrap.ds52'; Reason = 'bootstrap data-standard overlay composed onto a base profile; carries no standalone CMS connection' }
            @{ Name = '.env.bootstrap.ds61'; Reason = 'bootstrap data-standard overlay composed onto a base profile; carries no standalone CMS connection' }
        )

        # Emit hashtables (not [pscustomobject]) so Pester's -ForEach binds Name/Reason/IsTracked/
        # IsConnectionBearing to test variables.
        $toRecord = {
            param($Definition)
            @{
                Name                = $Definition.Name
                Reason              = $Definition.Reason
                IsTracked           = ($names -ccontains $Definition.Name)
                IsConnectionBearing = (& $isConnectionBearing $Definition.Name)
            }
        }
        $result.StandaloneConfig     = @($standaloneDefs         | ForEach-Object { & $toRecord $_ })
        $result.EngineOverlays       = @($engineOverlayDefs      | ForEach-Object { & $toRecord $_ })
        $result.DataStandardOverlays = @($dataStandardOverlayDefs | ForEach-Object { & $toRecord $_ })

        $excludedNames = @($standaloneDefs.Name) + @($engineOverlayDefs.Name) + @($dataStandardOverlayDefs.Name)
        # Case-sensitive subtraction so a case-only rename is not silently treated as excluded.
        $result.SwitchCapableBases = @($names | Where-Object { $excludedNames -cnotcontains $_ })
        if ($result.SwitchCapableBases.Count -eq 0) {
            $result.Error = "no switch-capable full-stack base profiles were discovered; the guard must not pass vacuously."
        }
    }
    catch {
        $result.Error = "config-profile inventory discovery failed: $($_.Exception.Message)"
    }

    return $result
}

Export-ModuleMember -Function Get-ConfigProfileInventory
