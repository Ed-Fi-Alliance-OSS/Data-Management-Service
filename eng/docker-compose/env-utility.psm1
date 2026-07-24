# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function Test-NativeCommandWithTimeout {
    <#
    .SYNOPSIS
        Runs a native command with a hard timeout and returns whether it exited successfully.

    .DESCRIPTION
        Uses ProcessStartInfo.ArgumentList so every argument retains its exact boundary. When the
        timeout expires, the process tree is terminated before the function returns false. Output
        is captured and discarded because this helper is intended for readiness probes.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList,

        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds = 10
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        $null = $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            return $false
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException] {
                Write-Debug "The process exited between the timeout result and Kill()."
            }
            $process.WaitForExit()
            $null = $standardOutputTask.GetAwaiter().GetResult()
            $null = $standardErrorTask.GetAwaiter().GetResult()
            return $false
        }

        $null = $standardOutputTask.GetAwaiter().GetResult()
        $null = $standardErrorTask.GetAwaiter().GetResult()
        return $process.ExitCode -eq 0
    }
    catch {
        return $false
    }
    finally {
        $process.Dispose()
    }
}

function ReadValuesFromEnvFile {
    param (
        [string]$EnvironmentFile
    )

    if (-Not (Test-Path $EnvironmentFile)) {
        throw "Environment file not found: $EnvironmentFile"
    }
    $envFile = @{}

    try {
        Get-Content $EnvironmentFile | ForEach-Object {
            if ($_ -match "^\s*#") { return }
            $split = $_.Split('=', 2)
            if ($split.Length -eq 2) {
                $key = $split[0].Trim()
                $value = $split[1].Trim()
                $envFile[$key] = $value
            }
        }
    }
    catch {
         Write-Error "Please provide valid .env file."
    }
    return $envFile
}

function Resolve-LocalSettingsEnvironmentFile {
    <#
    .SYNOPSIS
    Single source of truth for resolving the -EnvironmentFile parameter that every story-aligned
    phase command (start, configure, provision, seed) accepts. Returns the absolute path to a
    readable env file or throws if it cannot be located.

    .DESCRIPTION
    Resolution precedence (highest first):
      1. The supplied -Path, when non-empty:
         - absolute paths are kept as-is;
         - relative paths are resolved against the caller's current working directory.
      2. <docker-compose>/.env when present.
      3. When .env is absent, it is seeded once as a copy of <docker-compose>/.env.example
         and the new .env is returned. .env.example itself is never consumed at runtime:
         it stays a pure, tracked example, while .env (gitignored) is the live local
         settings file the user can edit durably.

    A missing file always throws. This is intentionally narrower than ReadValuesFromEnvFile
    so phase commands fail fast on a typo rather than silently fall through to ambient process
    environment defaults.

    .PARAMETER Path
    Caller-supplied env file path. May be empty (use defaults) or relative.

    .PARAMETER DockerComposeRoot
    Optional override for the docker-compose root directory used for default lookup. Defaults
    to this module's directory (eng/docker-compose). Tests pass an isolated copy.
    #>
    param(
        [string]$Path,
        [string]$DockerComposeRoot
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $defaultEnv = Join-Path $DockerComposeRoot ".env"
        if (-not (Test-Path -LiteralPath $defaultEnv -PathType Leaf)) {
            $exampleEnv = Join-Path $DockerComposeRoot ".env.example"
            if (Test-Path -LiteralPath $exampleEnv -PathType Leaf) {
                Copy-Item -LiteralPath $exampleEnv -Destination $defaultEnv
                Write-Information "No .env found; created $defaultEnv from .env.example. Edit it to customize local settings." -InformationAction Continue
            }
        }
        $Path = $defaultEnv
    }
    elseif (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Environment file not found: $Path."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-EnvValue {
    <#
    .SYNOPSIS
    Shared helper that returns the value of an env-file key when present and non-blank,
    otherwise the documented default. Equivalent to the duplicated Get-EnvValueOrDefault
    helpers in configure-local-data-store.ps1 and provision-dms-schema.ps1, lifted into
    the shared module so the precedence rule is single-sourced.

    Precedence: explicit env-file value > documented default. Process environment variables
    are deliberately not consulted - direct phase invocation must not depend on ambient state.
    #>
    param(
        [hashtable]$EnvValues,
        [Parameter(Mandatory)]
        [string]$Name,
        [string]$DefaultValue = ""
    )

    if ($null -eq $EnvValues) {
        return $DefaultValue
    }

    if ($EnvValues.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$Name])) {
        return [string]$EnvValues[$Name]
    }

    return $DefaultValue
}


function Resolve-BootstrapAdminClient {
    <#
    .SYNOPSIS
        Returns the bootstrap admin client id and secret used by configure-local-data-store.ps1
        and provision-dms-schema.ps1 to acquire a CMS admin token. Reads
        DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET from the env file and
        falls back to the historical local-dev defaults so the standard developer flow needs no
        env-file changes. Single-sources the two values so configure (which registers) and
        provision (which authenticates) always agree on the client.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param(
        [hashtable]$EnvValues
    )

    return [pscustomobject]@{
        ClientId     = Get-EnvValue -EnvValues $EnvValues -Name "DMS_BOOTSTRAP_ADMIN_CLIENT_ID" -DefaultValue "dms-data-store-admin"
        ClientSecret = Get-EnvValue -EnvValues $EnvValues -Name "DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
    }
}

function Resolve-IdentityClientSecretConfiguration {
    <#
    .SYNOPSIS
        Returns the parameters used to register the local identity clients so that both the
        secrets and the length-validation bounds match the env-file values DMS and CMS use.

        - DmsConfigurationService (full_access) is registered with
          DMS_CONFIG_IDENTITY_CLIENT_SECRET (the CMS IdentitySettings:ClientSecret).
        - CMSReadOnlyAccess (readonly_access) is registered with CONFIG_SERVICE_CLIENT_SECRET
          (the DMS ConfigurationServiceSettings:ClientSecret used at runtime to obtain CMS tokens).
        - ClientSecretMinimumLength / ClientSecretMaximumLength come from
          DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH / _MAXIMUM_LENGTH, which also configure
          CMS IdentitySettings:ClientSecretValidation. They are passed to setup-keycloak.ps1 /
          setup-openiddict.ps1 so a CMS-valid secret is not rejected by the setup scripts' own
          default 32/128 bounds.

        All values fall back to the historical local-dev defaults so the standard developer flow
        needs no env-file changes. Previously the setup scripts registered every client with the
        hard-coded default secret and validated against the default 32/128 bounds, so overriding
        CONFIG_SERVICE_CLIENT_SECRET / DMS_CONFIG_IDENTITY_CLIENT_SECRET (or the length bounds)
        produced a mismatch and CMS token acquisition or local registration failed. Single-sources
        the mapping so registration and runtime always agree.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param(
        [hashtable]$EnvValues
    )

    return [pscustomobject]@{
        DmsConfigurationServiceClientSecret = Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
        CmsReadOnlyAccessClientSecret       = Get-EnvValue -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SECRET" -DefaultValue "ValidClientSecret1234567890!Abcd"
        ClientSecretMinimumLength           = [int](Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH" -DefaultValue "32")
        ClientSecretMaximumLength           = [int](Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH" -DefaultValue "128")
    }
}

Set-Alias -Name Resolve-IdentityClientSecrets -Value Resolve-IdentityClientSecretConfiguration

function Resolve-CmsBaseUrl {
    <#
    .SYNOPSIS
        Returns the CMS base URL derived from the supplied env-file values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param (
        [hashtable]$EnvValues
    )

    $port = $EnvValues['DMS_CONFIG_ASPNETCORE_HTTP_PORTS']
    if (-not [string]::IsNullOrWhiteSpace($port)) {
        return "http://localhost:$port"
    }
    return "http://localhost:8081"
}

function Resolve-DockerLocalDmsBaseUrl {
    <#
    .SYNOPSIS
        Returns the Docker-local DMS base URL derived from the supplied env-file values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    #>
    param (
        [hashtable]$EnvValues
    )

    $port = $EnvValues['DMS_HTTP_PORTS']
    if (-not [string]::IsNullOrWhiteSpace($port)) {
        return "http://localhost:$port"
    }
    return "http://localhost:8080"
}

function Resolve-DmsRouteUrl {
    <#
    .SYNOPSIS
        Composes the tenant- and qualifier-prefixed DMS base URL for data writes. The canonical
        shape is `{base}[/{tenant}][/{qualifier-values}]/data/{**dmsPath}` (see
        CoreEndpointModule.BuildRoutePattern). This function returns the portion up to (but
        excluding) `/data/...`; callers append the data suffix.
        /health is registered only at the unqualified root, so health probes must use the bare
        base URL and must not pass through this composer.
    .PARAMETER BaseUrl
        The DMS base URL (e.g. http://localhost:8080).
    .PARAMETER Tenant
        Optional tenant identifier. When non-empty, becomes the first path segment after the base.
    .PARAMETER RouteQualifierValues
        Ordered route-qualifier values (e.g. school year) appended after the tenant segment.
        Order must match the server's appsettings RouteQualifierSegments configuration.
    #>
    param (
        [Parameter(Mandatory)] [string]$BaseUrl,
        [string]$Tenant = "",
        [string[]]$RouteQualifierValues = @()
    )

    $segments = @()
    if (-not [string]::IsNullOrWhiteSpace($Tenant)) {
        $segments += $Tenant
    }
    foreach ($value in $RouteQualifierValues) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $segments += [string]$value
        }
    }
    $normalizedBaseUrl = $BaseUrl.TrimEnd('/')
    if ($segments.Count -eq 0) {
        return $normalizedBaseUrl
    }
    return "$normalizedBaseUrl/" + ($segments -join "/")
}

function Resolve-IdentityProvider {
    <#
    .SYNOPSIS
        Returns the active identity provider name.
        Resolution order: -OverrideProvider, env DMS_CONFIG_IDENTITY_PROVIDER, default self-contained.
        Throws for unsupported values.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    .PARAMETER OverrideProvider
        Caller-supplied provider string that wins over the env-file value when non-empty.
    #>
    param (
        [hashtable]$EnvValues,
        [string]$OverrideProvider = ""
    )

    $supported = @("keycloak", "self-contained")

    if (-not [string]::IsNullOrWhiteSpace($OverrideProvider)) {
        if ($supported -notcontains $OverrideProvider) {
            throw "Unsupported identity provider '$OverrideProvider'. Supported values: $($supported -join ', ')."
        }
        return $OverrideProvider
    }

    $fromEnv = $EnvValues['DMS_CONFIG_IDENTITY_PROVIDER']
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        if ($supported -notcontains $fromEnv) {
            throw "Unsupported identity provider '$fromEnv' (from env file). Supported values: $($supported -join ', ')."
        }
        return $fromEnv
    }

    return "self-contained"
}

function Resolve-OAuthTokenUrl {
    <#
    .SYNOPSIS
        Returns the host-side OAuth token endpoint URL for the selected identity provider.
        BulkLoadClient and other host processes call OAuth from the host, so URLs are built
        from the published port env-vars (DMS_CONFIG_ASPNETCORE_HTTP_PORTS, KEYCLOAK_PORT)
        with localhost, not from container-flavored *_OAUTH_TOKEN_ENDPOINT env-vars which
        resolve only inside the Docker network.
        For self-contained with a school year, appends /{schoolYear} to the /connect/token path.
        Throws for unsupported providers.
    .PARAMETER EnvValues
        Hashtable returned by ReadValuesFromEnvFile.
    .PARAMETER IdentityProvider
        The resolved identity provider name (keycloak or self-contained).
    .PARAMETER SchoolYear
        Optional school year integer. When supplied with self-contained, the year is appended
        to the token endpoint path (e.g. http://localhost:8081/connect/token/2024).
        Ignored for keycloak.
    #>
    param (
        [hashtable]$EnvValues,
        [string]$IdentityProvider,
        [System.Nullable[int]]$SchoolYear = $null
    )

    switch ($IdentityProvider) {
        "keycloak" {
            $port = $EnvValues['KEYCLOAK_PORT']
            if ([string]::IsNullOrWhiteSpace($port)) {
                $port = "8045"
            }
            return "http://localhost:$port/realms/edfi/protocol/openid-connect/token"
        }
        "self-contained" {
            $port = $EnvValues['DMS_CONFIG_ASPNETCORE_HTTP_PORTS']
            if ([string]::IsNullOrWhiteSpace($port)) {
                $port = "8081"
            }
            $base = "http://localhost:$port/connect/token"
            if ($null -ne $SchoolYear) {
                return "$base/$SchoolYear"
            }
            return $base
        }
        default {
            throw "Unsupported identity provider '$IdentityProvider'. Supported values: keycloak, self-contained."
        }
    }
}

function Write-DerivedEnvFile {
    <#
    .SYNOPSIS
        Materializes a derived environment file from a base env file, applying scalar key
        overrides. The base file is left untouched. Used by the bootstrap wrapper to produce
        a per-run profile (e.g. a loose circuit-breaker for bulk loads) without mutating the
        developer's checked-in env files.

    .PARAMETER BaseEnvironmentFile
        Path to the source env file (e.g. eng/docker-compose/.env or .env.example).

    .PARAMETER TargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .PARAMETER KeyOverrides
        Hashtable of KEY=VALUE entries to set. If the key exists in the base file, the existing line
        is replaced; if not, a new line is appended. Values are written verbatim (caller is responsible
        for quoting if the value needs it).

    .OUTPUTS
        None. Writes the derived file to TargetPath as UTF-8 without BOM, with LF line endings and
        a final newline.

    .EXAMPLE
        Write-DerivedEnvFile `
            -BaseEnvironmentFile ./.env `
            -TargetPath ./.bootstrap/.env.derived `
            -KeyOverrides @{ FAILURE_RATIO = "0.95" }
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$TargetPath,
        [hashtable]$KeyOverrides = @{}
    )

    if (-not (Test-Path -LiteralPath $BaseEnvironmentFile -PathType Leaf)) {
        throw "Write-DerivedEnvFile: base environment file not found: $BaseEnvironmentFile"
    }

    $content = Get-Content -LiteralPath $BaseEnvironmentFile -Raw
    if ($null -eq $content) { $content = "" }

    # 1) Apply scalar key overrides. Replace `^KEY=...$` lines, or append if missing.
    foreach ($key in $KeyOverrides.Keys) {
        $value = [string]$KeyOverrides[$key]
        $linePattern = "(?m)^[ \t]*$([Regex]::Escape($key))=.*$"
        $newLine = "$key=$value"
        if ([Regex]::IsMatch($content, $linePattern)) {
            $content = [Regex]::Replace($content, $linePattern, $newLine)
        }
        else {
            if ($content.Length -gt 0 -and -not $content.EndsWith("`n")) { $content += "`n" }
            $content += "$newLine`n"
        }
    }

    # 2) Normalize line endings (LF) and ensure final newline.
    $content = $content -replace "`r`n", "`n"
    if (-not $content.EndsWith("`n")) { $content += "`n" }

    $targetDir = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($targetDir) -and -not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($TargetPath, $content, $utf8NoBom)
}

function Resolve-BootstrapDerivedEnv {
    <#
    .SYNOPSIS
        Materializes the per-run derived env file with the canonical bootstrap seed-loading profile.
        Always sets FAILURE_RATIO=0.95 so the circuit breaker tolerates bulk-load failures.
        The base env file is left untouched. Shared by bootstrap-{local,published}-dms.ps1
        wrappers so the two stay in lockstep.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the source env file. Must exist.

    .PARAMETER DerivedTargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .OUTPUTS
        [string] Returns the DerivedTargetPath on success.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$DerivedTargetPath
    )

    Write-DerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -TargetPath $DerivedTargetPath `
        -KeyOverrides @{
            FAILURE_RATIO = "0.95"
        }

    return $DerivedTargetPath
}

function Remove-EnvFileKeys {
    <#
    .SYNOPSIS
        Returns the base env-file lines with every entry for the supplied keys removed. Handles both
        single-line scalars (KEY=value) and multi-line quoted values (e.g. the SCHEMA_PACKAGES JSON
        block written as KEY='[ ... ]' across several lines). Comments and unrelated lines are kept.

    .PARAMETER Lines
        The base env file content, one element per line.

    .PARAMETER Keys
        The key names to remove (case-insensitive).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure helper: returns a filtered copy of the lines and does not change system state.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'The helper removes a set of keys.')]
    param(
        [string[]]$Lines,
        $Keys
    )

    $keySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $Keys) {
        [void]$keySet.Add([string]$key)
    }

    $result = [System.Collections.Generic.List[string]]::new()
    $index = 0
    while ($index -lt $Lines.Count) {
        $line = $Lines[$index]
        $match = [regex]::Match($line, "^[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]*=(.*)$")

        if ($match.Success -and $keySet.Contains($match.Groups[1].Value)) {
            $value = $match.Groups[2].Value.TrimStart()
            $openingQuote = if ($value.StartsWith("'")) { "'" } elseif ($value.StartsWith('"')) { '"' } else { $null }

            # A quoted value with no matching closing quote on the same line spans multiple lines;
            # skip continuation lines through the one that closes the quote.
            if ($null -ne $openingQuote -and $value.IndexOf($openingQuote, 1) -lt 0) {
                $index++
                while ($index -lt $Lines.Count -and -not $Lines[$index].Contains($openingQuote)) {
                    $index++
                }
                if ($index -lt $Lines.Count) {
                    $index++
                }
            }
            else {
                $index++
            }
            continue
        }

        $result.Add($line)
        $index++
    }

    return , $result.ToArray()
}

function New-DataStandardDerivedEnvFile {
    <#
    .SYNOPSIS
        Composes a base environment file with a data-standard overlay (e.g. .env.ds52, .env.ds61)
        into a single derived env file, so callers keep passing one -EnvironmentFile / --env-file
        while selecting a data standard version. The base and overlay files are left untouched.

    .DESCRIPTION
        Overlay keys (e.g. SCHEMA_PACKAGES, DATABASE_TEMPLATE_PACKAGE, DMS_CONFIG_DATA_STANDARD_VERSION)
        replace the matching entries from the base file; every other base line is preserved. Authoring
        the overlay's SCHEMA_PACKAGES on a single line keeps overlay parsing trivial; the base file's
        multi-line SCHEMA_PACKAGES block is removed wholesale before the overlay is appended.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file (e.g. .env.e2e). Must exist.

    .PARAMETER OverlayEnvironmentFile
        Absolute path to the overlay env file (e.g. .env.ds61). Must exist.

    .PARAMETER TargetPath
        Path where the derived file is written. Parent directory is created if missing.

    .OUTPUTS
        [string] Returns the TargetPath on success.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Local-dev helper, no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [Parameter(Mandatory)] [string]$OverlayEnvironmentFile,
        [Parameter(Mandatory)] [string]$TargetPath
    )

    if (-not (Test-Path -LiteralPath $BaseEnvironmentFile -PathType Leaf)) {
        throw "New-DataStandardDerivedEnvFile: base environment file not found: $BaseEnvironmentFile"
    }
    if (-not (Test-Path -LiteralPath $OverlayEnvironmentFile -PathType Leaf)) {
        throw "New-DataStandardDerivedEnvFile: data standard overlay file not found: $OverlayEnvironmentFile"
    }

    $overlayKeys = (ReadValuesFromEnvFile $OverlayEnvironmentFile).Keys
    $baseLines = @(Get-Content -LiteralPath $BaseEnvironmentFile)
    $baseWithoutOverlayKeys = Remove-EnvFileKeys -Lines $baseLines -Keys $overlayKeys

    $overlayContent = (Get-Content -LiteralPath $OverlayEnvironmentFile -Raw) -replace "`r`n", "`n"

    $merged = (($baseWithoutOverlayKeys -join "`n").TrimEnd("`n")) + "`n`n" + $overlayContent.TrimEnd("`n") + "`n"

    $targetDir = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($targetDir) -and -not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($TargetPath, $merged, $utf8NoBom)

    return $TargetPath
}

function Get-DataStandardOverlayToken {
    <#
    .SYNOPSIS
        Normalizes a data standard version (e.g. "5.2", "6.1", "ds52") to its overlay token
        ("ds52", "ds61"), used to locate the .env.<token> overlay file.
    #>
    param(
        [Parameter(Mandatory)] [string]$DataStandardVersion
    )

    $value = $DataStandardVersion.Trim().ToLowerInvariant()
    if ($value -match '^ds[0-9]+$') {
        return $value
    }

    $digits = ($value -replace '[^0-9]', '')
    if ([string]::IsNullOrWhiteSpace($digits)) {
        throw "Get-DataStandardOverlayToken: '$DataStandardVersion' is not a recognizable data standard version (expected e.g. '5.2', '6.1', or 'ds52')."
    }

    return "ds$digits"
}

function Resolve-DataStandardEnvironmentFile {
    <#
    .SYNOPSIS
        Returns the effective environment file path for a requested data standard version. With no
        version (the default) the base file is returned unchanged, preserving DS 5.2 default behavior.
        With a version, the matching .env.<token> overlay is composed onto the base into a derived
        file under <DockerComposeRoot>/.derived/ and that path is returned.

    .PARAMETER DataStandardVersion
        e.g. "5.2", "6.1", "ds52", "ds61"; empty/whitespace selects the default (base file unchanged).

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file.

    .PARAMETER DockerComposeRoot
        Directory holding the .env.<token> overlays and the .derived output. Defaults to this module's
        directory (eng/docker-compose).

    .PARAMETER OverlayPrefix
        Overlay file-name prefix. Defaults to ".env" (the shared E2E/SDK-surface overlays,
        e.g. .env.ds61). The bootstrap wrapper passes ".env.bootstrap" to compose the
        local-bootstrap surfaces (e.g. .env.bootstrap.ds61) instead. A non-default prefix is
        reflected in the derived file name (e.g. <base>.bootstrap.<token>) so both derivations
        can coexist under .derived/.
    #>
    param(
        [string]$DataStandardVersion,
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [string]$OverlayPrefix = ".env"
    )

    if ([string]::IsNullOrWhiteSpace($DataStandardVersion)) {
        return $BaseEnvironmentFile
    }

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    $token = Get-DataStandardOverlayToken $DataStandardVersion
    $overlayPath = Join-Path $DockerComposeRoot "$OverlayPrefix.$token"
    if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
        $available = @(Get-ChildItem -Path $DockerComposeRoot -Filter "$OverlayPrefix.ds*" -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name) -join ", "
        throw "Resolve-DataStandardEnvironmentFile: no overlay for data standard version '$DataStandardVersion' (expected '$overlayPath'). Available overlays: $available."
    }

    # A non-default prefix contributes its distinguishing segment(s) to the derived name
    # (".env.bootstrap" -> "<base>.bootstrap.<token>"); the default ".env" contributes nothing
    # ("<base>.<token>", the pre-existing naming).
    $prefixSegment = ($OverlayPrefix -replace '^\.env\.?', '').Trim('.')
    $derivedName = if ([string]::IsNullOrEmpty($prefixSegment)) {
        "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).$token"
    } else {
        "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).$prefixSegment.$token"
    }
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName

    return New-DataStandardDerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -OverlayEnvironmentFile $overlayPath `
        -TargetPath $derivedPath
}

function Convert-TemplatePackageToken {
    <#
    .SYNOPSIS
        Rewrites the engine segment of a DATABASE_TEMPLATE_PACKAGE-shaped package id, leaving
        every other segment (including the template and version) untouched.

    .DESCRIPTION
        Package ids follow the shape <prefix>.<template>.Template.<engine>.<version>, e.g.
        EdFi.Api.Populated.Template.PostgreSql.5.2.0 or EdFi.Dms.Minimal.Template.MsSql.6.1.0.
        <prefix> varies (EdFi.Api, EdFi.Dms, ...) and is preserved verbatim, as are the
        template segment (Minimal/Populated/Smoke) and <version>. When PackageId does not
        match the expected shape (blank, or an unrecognized format), it is returned unchanged.

    .PARAMETER PackageId
        The package id to rewrite.

    .PARAMETER Engine
        Target engine token ("PostgreSql" or "MsSql") to replace the existing engine segment.

    .OUTPUTS
        [string] The rewritten package id, or PackageId unchanged when it is blank or does not
        match the expected <template>.Template.<engine>.<version> shape.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$PackageId,
        [Parameter(Mandatory)]
        [ValidateSet("PostgreSql", "MsSql")]
        [string]$Engine
    )

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        return $PackageId
    }

    $match = [regex]::Match($PackageId, '^(?<prefix>.+)\.(?<template>Minimal|Populated|Smoke)\.Template\.(?<engine>PostgreSql|MsSql)\.(?<version>.+)$')
    if (-not $match.Success) {
        return $PackageId
    }

    return "$($match.Groups['prefix'].Value).$($match.Groups['template'].Value).Template.$Engine.$($match.Groups['version'].Value)"
}

function ConvertTo-CanonicalDatabaseEngine {
    <#
    .SYNOPSIS
        The single engine-token boundary for the PowerShell start/bootstrap scripts. Accepts the two
        supported engines case-insensitively - so publicly documented variants such as 'MSSQL' and
        'PostgreSQL' resolve to the canonical 'mssql' / 'postgresql' - and throws for anything else,
        including surrounding-whitespace variants, which are not canonical engines. Mirrors the
        api-schema-tools 'connection validate' CLI boundary so a case variant produces the same
        canonical engine on both sides.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$Engine
    )

    if ([string]::Equals($Engine, 'postgresql', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'postgresql'
    }
    if ([string]::Equals($Engine, 'mssql', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'mssql'
    }
    throw "Unsupported database engine '$Engine'. Use exactly 'postgresql' or 'mssql' (case-insensitive)."
}

function Get-DatabaseNameComparer {
    <#
    .SYNOPSIS
        Returns the StringComparer that expresses database-name identity for an engine and is the single
        equivalence policy every database-target comparison and collision guard routes through.
        PostgreSQL uses Ordinal (case-sensitive: 'SchoolDb' and 'schooldb' are distinct physical
        databases). SQL Server uses OrdinalIgnoreCase - conservative for its supported/default
        case-insensitive identifier semantics, so case variants are treated as the same database.
    #>
    param(
        [Parameter(Mandatory)][string]$Engine
    )

    $canonicalEngine = ConvertTo-CanonicalDatabaseEngine -Engine $Engine
    if ($canonicalEngine -eq 'mssql') {
        return [System.StringComparer]::OrdinalIgnoreCase
    }
    return [System.StringComparer]::Ordinal
}

function Test-DatabaseNameEquivalent {
    <#
    .SYNOPSIS
        Provider-aware database-name equality: returns $true when two names refer to the same physical
        database under the engine's identity semantics (Get-DatabaseNameComparer). PostgreSQL compares
        ordinally (case-sensitive); SQL Server compares case-insensitively. All datastore/CMS target
        comparisons and the separate-topology collision guard use this one policy rather than ad hoc
        OrdinalIgnoreCase comparers.
    #>
    param(
        [Parameter(Mandatory)][string]$Engine,
        [AllowEmptyString()][AllowNull()][string]$Left,
        [AllowEmptyString()][AllowNull()][string]$Right
    )

    $comparer = Get-DatabaseNameComparer -Engine $Engine
    return $comparer.Equals([string]$Left, [string]$Right)
}

function Test-SqlServerConnectionString {
    <#
    .SYNOPSIS
        Structural shape check used ONLY by the MSSQL engine-overlay composition to decide whether a
        base-file connection string is already SQL Server-shaped (keep it) or a PostgreSQL value to be
        replaced by the overlay: a SQL Server connection carries no PostgreSQL 'host' key. This is a
        quoting-correct marker check for OVERLAY COMPOSITION, not connection-string validation - the
        runtime contract parses with the exact provider via the SchemaTools 'connection validate' verb
        (Get-CmsConnectionStringDatabaseName), never a generic or non-runtime builder.
    #>
    param([AllowEmptyString()][AllowNull()][string]$ConnectionString)
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $false
    }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($ConnectionString)
    }
    catch {
        return $false
    }
    return -not $builder.ContainsKey('host')
}

function Invoke-ConnectionStringValidation {
    <#
    .SYNOPSIS
        Invokes the SchemaTools `connection validate` verb - the single connection-string parsing
        authority - passing the connection string on stdin (never an argument, so a password stays out of
        the process listing) and returning the parsed { valid; database; error } result. The verb parses
        with the EXACT runtime provider builders (Npgsql / Microsoft.Data.SqlClient), so alias
        canonicalization, last-wins synonyms, and rejection of unsupported keywords match runtime exactly.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ConnectionString,
        [Parameter(Mandatory)][object]$SchemaToolPath
    )
    # Canonicalize before the token crosses to the CLI: ValidateSet accepts and preserves a case
    # variant ('MSSQL'), so route it through the single engine-token boundary to forward the canonical
    # 'postgresql'/'mssql' the verb parses with.
    $Engine = ConvertTo-CanonicalDatabaseEngine -Engine $Engine

    # -SchemaToolPath is either a host executable path (string) or a validator descriptor from
    # Resolve-DmsConnectionValidator. A DockerImage descriptor runs the SAME 'connection validate' verb
    # inside the DMS image, so a clean Docker/PowerShell-only host parses with the exact runtime providers
    # without a host tool. Either way the verb reads the connection string from stdin (no password in argv).
    $validator = if ($SchemaToolPath -is [string]) {
        [pscustomobject]@{ Kind = 'HostExe'; Path = $SchemaToolPath }
    }
    else {
        $SchemaToolPath
    }

    if ([string]$validator.Kind -eq 'DockerImage') {
        # --network none: the verb only parses the string, never connects, so it must run offline.
        $validatorDescription = "image '$($validator.Image)'"
        $output = $ConnectionString | & docker run --rm -i --network none --entrypoint dotnet $validator.Image $validator.ToolPath connection validate --engine $Engine 2>$null
    }
    else {
        $validatorDescription = "tool at '$($validator.Path)'"
        $output = $ConnectionString | & $validator.Path connection validate --engine $Engine 2>$null
    }
    if ($LASTEXITCODE -ne 0) {
        throw "The connection-string validator (api-schema-tools 'connection validate') exited $LASTEXITCODE for engine '$Engine'. Ensure the $validatorDescription is runnable; a build that predates the 'connection validate' verb exits non-zero, so rebuild or re-publish api-schema-tools (dotnet publish src/dms/clis/EdFi.DataManagementService.SchemaTools -c Release -o eng/docker-compose/.bootstrap/tools/api-schema-tools) or repull the DMS image."
    }
    $json = ($output | Out-String)
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "The connection-string validator produced no output for engine '$Engine'."
    }
    try {
        return ($json | ConvertFrom-Json)
    }
    catch {
        throw "The connection-string validator produced unparseable output for engine '$Engine': $($_.Exception.Message)"
    }
}

function Invoke-ConnectionStringInspection {
    <#
    .SYNOPSIS
        Invokes the SchemaTools `connection inspect` verb - the single connection-string parsing authority -
        passing the connection string on stdin (never an argument, so a password stays out of the process
        listing) and returning the parsed { valid; database; host; port; username; error } of NON-SECRET
        canonical coordinates. Like Invoke-ConnectionStringValidation it parses with the EXACT runtime
        provider builders (Npgsql / Microsoft.Data.SqlClient), so alias canonicalization, last-wins synonyms,
        and rejection of unsupported keywords match runtime exactly.

        THROWS only on a TOOL-CONTRACT / VERSION failure - never as a datastore error: a non-zero exit (which,
        for a canonical engine, means the `inspect` verb is unavailable in a tool that predates it); blank,
        null, or unparseable output; or a result - valid OR invalid - that violates the typed/state contract
        (a missing field, a non-boolean `valid`, an object-valued coordinate, a wrong-type or wrong-state port,
        or an incoherent valid/error pairing). Only a COMPLETE, TYPED, COHERENT { valid = $false } result is
        RETURNED to the caller (which classifies stale vs invalid vs incomplete); a malformed invalid result
        throws as a tool-contract failure, not a datastore error.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ConnectionString,
        [Parameter(Mandatory)][object]$SchemaToolPath
    )
    # Canonicalize before the token crosses to the CLI, mirroring the validate verb boundary.
    $Engine = ConvertTo-CanonicalDatabaseEngine -Engine $Engine

    # -SchemaToolPath is either a host executable path (string) or a validator descriptor from
    # Resolve-DmsConnectionValidator. A DockerImage descriptor runs the SAME 'connection inspect' verb inside
    # the DMS image. Either way the verb reads the connection string from stdin (no password in argv).
    $validator = if ($SchemaToolPath -is [string]) {
        [pscustomobject]@{ Kind = 'HostExe'; Path = $SchemaToolPath }
    }
    else {
        $SchemaToolPath
    }

    if ([string]$validator.Kind -eq 'DockerImage') {
        $output = $ConnectionString | & docker run --rm -i --network none --entrypoint dotnet $validator.Image $validator.ToolPath connection inspect --engine $Engine 2>$null
    }
    elseif (([string]$validator.Path).EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        # A .ps1 tool path (e.g. a test double or wrapper) is invoked via `pwsh -File`, mirroring
        # Invoke-DmsSchemaProvision, so the connection string on stdin reaches the process instead of trying
        # to bind to the script's parameters.
        $output = $ConnectionString | & pwsh -NoLogo -NoProfile -File $validator.Path connection inspect --engine $Engine 2>$null
    }
    else {
        $output = $ConnectionString | & $validator.Path connection inspect --engine $Engine 2>$null
    }
    if ($LASTEXITCODE -ne 0) {
        throw "The connection-string inspector (api-schema-tools 'connection inspect') exited $LASTEXITCODE for engine '$Engine'. A build that predates the 'connection inspect' verb exits non-zero, so rebuild or re-publish api-schema-tools (dotnet publish src/dms/clis/EdFi.DataManagementService.SchemaTools -c Release -o eng/docker-compose/.bootstrap/tools/api-schema-tools) or repull the DMS image."
    }
    $json = ($output | Out-String)
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "The connection-string inspector produced no output for engine '$Engine'; rebuild or re-publish api-schema-tools."
    }
    try {
        $result = $json | ConvertFrom-Json
    }
    catch {
        throw "The connection-string inspector produced unparseable output for engine '$Engine' ($($_.Exception.Message)); rebuild or re-publish api-schema-tools."
    }
    if ($null -eq $result) {
        throw "The connection-string inspector produced a null result for engine '$Engine'; rebuild or re-publish api-schema-tools."
    }

    # Contract shape: EVERY result (valid OR invalid) must carry the complete field set, and 'valid' must be a
    # real boolean. The C# `connection inspect` always emits { valid, database, host, port, username, error };
    # a missing field, or a 'valid' that is not a [bool] (e.g. the STRING "false", which is truthy in
    # PowerShell), is a tool-contract/version failure - never a datastore result.
    $fieldNames = @($result.PSObject.Properties.Name)
    foreach ($requiredField in 'valid', 'database', 'host', 'port', 'username', 'error') {
        if ($fieldNames -notcontains $requiredField) {
            throw "The connection-string inspector output for engine '$Engine' is missing the '$requiredField' field; rebuild or re-publish api-schema-tools."
        }
    }
    if ($result.valid -isnot [bool]) {
        throw "The connection-string inspector output for engine '$Engine' has a non-boolean 'valid' field; rebuild or re-publish api-schema-tools."
    }

    # Typed coordinates: database/host/username/error are string-or-null; port is integer-or-null. An
    # object-valued or non-integral field is a tool-contract/version failure, never a datastore result.
    foreach ($stringField in 'database', 'host', 'username', 'error') {
        $fieldValue = $result.$stringField
        if ($null -ne $fieldValue -and $fieldValue -isnot [string]) {
            throw "The connection-string inspector output for engine '$Engine' has a non-string '$stringField' field; rebuild or re-publish api-schema-tools."
        }
    }
    if ($null -ne $result.port -and $result.port -isnot [int] -and $result.port -isnot [long]) {
        throw "The connection-string inspector output for engine '$Engine' has a non-integer 'port' field; rebuild or re-publish api-schema-tools."
    }

    # Coherent valid/error state: a valid result carries no error; an invalid result carries a nonblank error.
    if ($result.valid) {
        if ($null -ne $result.error) {
            throw "The connection-string inspector reported a valid result with a non-null 'error' for engine '$Engine'; rebuild or re-publish api-schema-tools."
        }
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$result.error)) {
        throw "The connection-string inspector reported an invalid result with no 'error' message for engine '$Engine'; rebuild or re-publish api-schema-tools."
    }

    # Engine/state-specific coordinate rules (the C# contract): a VALID PostgreSQL result carries a concrete
    # integer port in 1-65535; a VALID SQL Server result carries a NULL port (the port stays encoded in the
    # data source); an INVALID result carries null coordinates. Any violation is a tool-contract/version
    # failure - never a datastore result (e.g. a valid PostgreSQL result with a null port must NOT reach
    # provisioning and get a fabricated default).
    if ($result.valid) {
        if ($Engine -eq 'postgresql') {
            if ($result.port -isnot [int] -and $result.port -isnot [long]) {
                throw "The connection-string inspector reported a valid PostgreSQL result with no integer 'port' for engine '$Engine'; rebuild or re-publish api-schema-tools."
            }
            if ($result.port -lt 1 -or $result.port -gt 65535) {
                throw "The connection-string inspector reported a valid PostgreSQL 'port' ($($result.port)) outside 1-65535; rebuild or re-publish api-schema-tools."
            }
        }
        elseif ($null -ne $result.port) {
            throw "The connection-string inspector reported a valid SQL Server result with a non-null 'port' (the port belongs inside the data source); rebuild or re-publish api-schema-tools."
        }
    }
    else {
        foreach ($coordinate in 'database', 'host', 'port', 'username') {
            if ($null -ne $result.$coordinate) {
                throw "The connection-string inspector reported an invalid result with a non-null '$coordinate' field; rebuild or re-publish api-schema-tools."
            }
        }
    }
    return $result
}

function Get-CmsConnectionStringDatabaseName {
    <#
    .SYNOPSIS
        Returns the canonical target database of an ALREADY-RESOLVED connection string, parsed by the
        EXACT runtime provider (Npgsql 8.0.4 / Microsoft.Data.SqlClient 6.1.4) via the SchemaTools
        `connection validate` verb - the single connection-string parsing authority. No keyword vocabulary
        or generic builder is used, so alias canonicalization, last-wins duplicate synonyms, and rejection
        of keywords the provider does not support match exactly what the Configuration Service does at
        runtime. Throws when the connection is not valid for -Engine (a wrong-engine or unsupported
        keyword); returns an empty array when the connection targets no database.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$ConnectionString,
        # Host executable path (string) OR a validator descriptor from Resolve-DmsConnectionValidator;
        # passed through to Invoke-ConnectionStringValidation, which dispatches host-exe vs container.
        [Parameter(Mandatory)][object]$SchemaToolPath
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return @()
    }

    $result = Invoke-ConnectionStringValidation -Engine $Engine -ConnectionString $ConnectionString -SchemaToolPath $SchemaToolPath
    if (-not $result.valid) {
        throw "The effective connection string is not a valid '$Engine' connection: $($result.error)"
    }
    if ([string]::IsNullOrEmpty($result.database)) {
        return @()
    }
    return @([string]$result.database)
}

function Get-ComposeResolvedConfiguration {
    <#
    .SYNOPSIS
        Resolves the effective Configuration Service runtime values by asking Docker Compose itself
        (`docker compose ... config --format json`), rather than re-implementing interpolation in
        PowerShell. Compose applies shell-over-env-file precedence, ${VAR:-default}, nested substitution,
        quoting, and single-quote opacity exactly as the ensuing `up` will, so the returned values ARE
        what the containers receive and cannot drift from a second interpolation model.

    .DESCRIPTION
        `docker compose config` needs no started containers, no pulled images, and no pre-existing
        external network, so it is safe to run before any Docker or Keycloak mutation. Compose renders a
        literal '$' as '$$' in its output; this unescapes it, so a value carrying an opaque, unexpanded
        ${...} - a shell-substituted terminal Compose does not re-expand - is returned as the literal
        text the container receives and is compared literally by the runtime contract.

        Returns a record { ConfigProvider; DmsProvider; CmsConnectionString; MssqlSaPassword;
        DmsAdminConnectionString; TopologyDatastoreDatabaseName; DmsImage }. ConfigProvider is the
        Configuration Service (config) service's AppSettings__Datastore (interpolated from DMS_CONFIG_DATASTORE);
        DmsProvider is the DMS (dms) service's AppSettings__Datastore (interpolated INDEPENDENTLY from
        DMS_DATASTORE). The two are deliberately separate fields so a consumer can never confuse the CMS runtime
        provider with the DMS runtime provider - a shell DMS_DATASTORE could otherwise point the DMS container at
        a different engine than the one that starts, unnoticed. A field is $null when its service or key is
        absent from the composed set (e.g. the standalone-CMS lane composes no dms service, so DmsProvider and
        DmsAdminConnectionString are $null). TopologyDatastoreDatabaseName is the AUTHORITATIVE DMS datastore
        database name: the db service's engine-specific datastore key (POSTGRES_DB_NAME or MSSQL_DB_NAME,
        selected by -InfrastructureEngine, never positionally), which Compose resolves with the compose-file
        default and shell-over-env-file precedence. It is deliberately NOT read from DmsAdminConnectionString
        (DATABASE_CONNECTION_STRING_ADMIN), which run.sh consumes only for a readiness probe (host/port/username)
        and whose database can legitimately differ; that admin connection is never the datastore-name oracle.

    .PARAMETER ComposeFiles
        The docker compose "-f <file>" arguments, exactly as the ensuing `up` uses them.

    .PARAMETER EnvironmentFile
        The --env-file path, exactly as the ensuing `up` uses it.

    .PARAMETER ProjectName
        The compose project name (-p), exactly as the ensuing `up` uses it.

    .PARAMETER ConfigServiceName
        The Configuration Service service name to read (default "config").

    .PARAMETER DbServiceName
        The database service name to read for the SQL Server SA password (default "db").

    .PARAMETER DmsServiceName
        The DMS service name to read for the datastore admin connection (default "dms").

    .PARAMETER InfrastructureEngine
        The engine the caller selected ('postgresql' | 'mssql'), used to choose the db-service datastore key
        for TopologyDatastoreDatabaseName ('postgresql' -> POSTGRES_DB_NAME, 'mssql' -> MSSQL_DB_NAME). The
        anchor is never chosen positionally, so an unrelated engine's key can never become the anchor. When
        omitted (callers that do not consume the anchor), TopologyDatastoreDatabaseName is $null.
    #>
    param(
        [Parameter(Mandatory)][string[]]$ComposeFiles,
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string]$ProjectName,
        [string]$ConfigServiceName = "config",
        [string]$DbServiceName = "db",
        [string]$DmsServiceName = "dms",
        [ValidateSet('postgresql', 'mssql')][string]$InfrastructureEngine
    )

    function Get-ComposeEnvironmentValue {
        param([object]$EnvironmentObject, [string]$Key)
        if ($null -eq $EnvironmentObject) {
            return $null
        }
        if ($EnvironmentObject.PSObject.Properties.Name -contains $Key) {
            $raw = $EnvironmentObject.$Key
            if ($null -eq $raw) {
                return $null
            }
            return ([string]$raw -replace '\$\$', '$')
        }
        return $null
    }

    $composeArguments = @($ComposeFiles) + @("--env-file", $EnvironmentFile, "-p", $ProjectName, "config", "--format", "json")
    $output = & docker compose @composeArguments 2>$null
    $json = ($output | Out-String)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "Unable to resolve the effective docker-compose configuration for project '$ProjectName' (docker compose config exited $LASTEXITCODE). The runtime contract is validated against Compose's own resolution, so a compose-file or environment-file error must be corrected before any container starts."
    }

    try {
        $parsed = $json | ConvertFrom-Json
    }
    catch {
        throw "Unable to parse 'docker compose config --format json' output for project '$ProjectName': $($_.Exception.Message)"
    }

    $configEnvironment = $null
    $dbEnvironment = $null
    $dmsEnvironment = $null
    $dmsImage = $null
    $services = $parsed.services
    if ($null -ne $services) {
        if ($services.PSObject.Properties.Name -contains $ConfigServiceName) {
            $configEnvironment = $services.$ConfigServiceName.environment
        }
        if ($services.PSObject.Properties.Name -contains $DbServiceName) {
            $dbEnvironment = $services.$DbServiceName.environment
        }
        if ($services.PSObject.Properties.Name -contains $DmsServiceName) {
            $dmsEnvironment = $services.$DmsServiceName.environment
            # The concrete DMS image Compose resolved - the same image the ensuing `up` runs. On a clean
            # Docker/PowerShell-only host it bundles the api-schema-tools CLI, so the runtime contract can
            # run the exact-provider 'connection validate' verb inside it without a host tool or SDK.
            if ($services.$DmsServiceName.PSObject.Properties.Name -contains "image") {
                $dmsImage = [string]$services.$DmsServiceName.image
            }
        }
    }

    # The topology datastore anchor: the engine-specific datastore database name the db service is
    # initialized with (POSTGRES_DB_NAME on postgresql.yml, MSSQL_DB_NAME on mssql.yml), resolved by Docker
    # Compose with the compose-file default and shell-over-env-file precedence. This is the AUTHORITATIVE
    # datastore-name source - never DATABASE_CONNECTION_STRING_ADMIN, which run.sh consumes only for a
    # readiness probe (host/port/username) and whose database can legitimately differ (a documented admin
    # connection). The key is selected by the EXPLICIT engine, never positionally, so an unrelated engine's
    # key (should both ever appear through composition) can never become the anchor. Callers that do not
    # consume the anchor omit -InfrastructureEngine and receive $null.
    $topologyDatastoreDatabaseName = $null
    if (-not [string]::IsNullOrWhiteSpace($InfrastructureEngine)) {
        $datastoreKeyName = if ((ConvertTo-CanonicalDatabaseEngine -Engine $InfrastructureEngine) -eq 'mssql') { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
        $topologyDatastoreDatabaseName = Get-ComposeEnvironmentValue -EnvironmentObject $dbEnvironment -Key $datastoreKeyName
    }

    return [pscustomobject]@{
        ConfigProvider                = Get-ComposeEnvironmentValue -EnvironmentObject $configEnvironment -Key "AppSettings__Datastore"
        DmsProvider                   = Get-ComposeEnvironmentValue -EnvironmentObject $dmsEnvironment -Key "AppSettings__Datastore"
        CmsConnectionString           = Get-ComposeEnvironmentValue -EnvironmentObject $configEnvironment -Key "DatabaseSettings__DatabaseConnection"
        MssqlSaPassword               = Get-ComposeEnvironmentValue -EnvironmentObject $dbEnvironment -Key "MSSQL_SA_PASSWORD"
        DmsAdminConnectionString      = Get-ComposeEnvironmentValue -EnvironmentObject $dmsEnvironment -Key "DATABASE_CONNECTION_STRING_ADMIN"
        TopologyDatastoreDatabaseName = $topologyDatastoreDatabaseName
        DmsImage                      = $dmsImage
    }
}

function Resolve-EffectiveConfigRuntimeContract {
    <#
    .SYNOPSIS
        Computes and validates the effective local Configuration Service runtime contract exactly once,
        from values Docker Compose itself resolved (Get-ComposeResolvedConfiguration), and returns the one
        object every consumer (standalone/local/published startup, database readiness, OpenIddict
        init/insert, datastore registration) reads instead of independently re-resolving anything.

    .DESCRIPTION
        The engine the start script selected (-InfrastructureEngine) is authoritative and is NEVER
        inferred from a connection string. There are three finite components, each with an explicit
        participation authority (decided by the caller from its own compose-file selection, never inferred
        from null Compose fields) and a Compose-resolved runtime value:
          * database infrastructure - always present; the SQL Server SA password is validated when the
            selected engine is MSSQL;
          * the DMS service - validated only when -DmsServiceIncluded (its dms compose file is in the set);
          * the Configuration Service - validated only when -ConfigServiceIncluded (its config compose file
            is in the set).
        The DMS and Configuration Service runtime providers are INDEPENDENTLY interpolated by Compose
        (AppSettings__Datastore from DMS_DATASTORE and DMS_CONFIG_DATASTORE respectively), so each is checked
        against -InfrastructureEngine separately - a shell override of either cannot point its container at a
        different engine than the one that starts, unnoticed. The contract enforces, all fail-fast and before
        any Docker or Keycloak mutation:
          * on SQL Server, the SA password resolves to a non-blank value (stack invariant, always);
          * (when the DMS service participates) the DMS provider (Compose-resolved dms AppSettings__Datastore)
            is EXACTLY 'postgresql' or 'mssql' and equals -InfrastructureEngine; and the DMS topology datastore
            database (POSTGRES_DB_NAME / MSSQL_DB_NAME, resolved by Compose on the db service - NOT the
            readiness/admin connection) is a single nonblank, concrete value;
          * (when the Configuration Service participates) the CMS provider (Compose-resolved config
            AppSettings__Datastore) is EXACTLY 'postgresql' or 'mssql' and equals -InfrastructureEngine; and
            the CMS connection string parses under the selected provider's own builder (a wrong-engine string
            is rejected by the builder, not by keyword classification) and targets a concrete database that
            equals the INDEPENDENTLY expected configuration database - the DMS datastore anchor in shared
            topology, or the dedicated 'edfi_configurationservice' under -SeparateConfigDatabase - so a
            caller-authored connection can never redefine the topology; it must agree. In the standalone lane
            (no DMS service) the connection's own single target IS the effective name. Separate topology
            additionally requires the datastore and configuration databases to be different physical databases.

        The Compose-resolved values are passed in (-ResolvedConfigProvider / -ResolvedDmsProvider /
        -ResolvedCmsConnectionString / -ResolvedMssqlSaPassword / -ResolvedTopologyDatastoreDatabaseName); the
        start scripts obtain them from Get-ComposeResolvedConfiguration. Connection strings are parsed by the
        exact runtime providers via the SchemaTools `connection validate` verb (Get-CmsConnectionStringDatabaseName),
        located by -SchemaToolPath. Unit tests mock Get-CmsConnectionStringDatabaseName to exercise the
        contract logic without the tool; the provider-oracle tests exercise the verb directly.

    .PARAMETER InfrastructureEngine
        The engine the start script actually selected ('postgresql' | 'mssql'); drives the Compose
        database file and OpenIddict, and is the authoritative engine.

    .PARAMETER ConfigServiceIncluded
        Whether the Configuration Service participates in this compose set, decided by the caller from its
        own compose-file selection (never inferred from null Compose fields). When $true the CMS invariants
        (provider enum and engine agreement, connection string and configuration database, OpenIddict target)
        are validated; when $false they are skipped - a Keycloak run that omits the local config service, or
        one pointed at an external CONFIG_SERVICE_URL, has no local CMS to validate.

    .PARAMETER DmsServiceIncluded
        Whether the DMS service participates in this compose set, decided by the caller from its own
        compose-file selection (never inferred from null Compose fields). When $true the DMS provider
        (enum + engine agreement) and the DMS topology datastore anchor (a single nonblank, concrete value)
        are validated, and the configuration database is expected against that anchor; when $false they are
        skipped - the standalone Configuration Service lane composes no dms service. The stack SA-password
        invariant is validated regardless of both participation flags.

    .PARAMETER SeparateConfigDatabase
        The topology the start script selected (never inferred from a name). When set, the expected
        configuration database is the dedicated 'edfi_configurationservice' and must be a different physical
        database from the DMS datastore anchor; when omitted (shared) the expected configuration database IS
        the datastore anchor.

    .PARAMETER ResolvedConfigProvider
        The Compose-resolved Configuration Service AppSettings__Datastore value (from DMS_CONFIG_DATASTORE).

    .PARAMETER ResolvedDmsProvider
        The Compose-resolved DMS service AppSettings__Datastore value (from DMS_DATASTORE), interpolated
        independently of the CMS provider.

    .PARAMETER ResolvedCmsConnectionString
        The Compose-resolved DatabaseSettings__DatabaseConnection value (final text the container receives).

    .PARAMETER SchemaToolPath
        The connection-string validator: either a host executable path (string) or a descriptor from
        Resolve-DmsConnectionValidator ({ Kind = 'HostExe' | 'DockerImage' ... }). Connection strings are
        parsed with the exact runtime providers via the api-schema-tools 'connection validate' verb, run on
        the host or - on a clean Docker/PowerShell-only published host - inside the DMS image that bundles
        the tool. Passed through to Get-CmsConnectionStringDatabaseName / Invoke-ConnectionStringValidation.

    .PARAMETER ResolvedMssqlSaPassword
        The Compose-resolved db-service MSSQL_SA_PASSWORD value (SQL Server stacks).

    .PARAMETER ResolvedTopologyDatastoreDatabaseName
        The Compose-resolved DMS topology datastore database name (POSTGRES_DB_NAME / MSSQL_DB_NAME on the db
        service, from Get-ComposeResolvedConfiguration.TopologyDatastoreDatabaseName). When the DMS service
        participates it is validated for a single nonblank, concrete value and is the expected configuration
        database in shared topology. Omitted (null) in the standalone-CMS lane.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'ResolvedMssqlSaPassword', Justification = 'The SQL Server SA password is the local docker-compose plaintext value resolved by Compose (mssql.yml MSSQL_SA_PASSWORD); it is passed as a string throughout these compose scripts by design.')]
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$InfrastructureEngine,
        [Parameter(Mandatory)][bool]$ConfigServiceIncluded,
        [Parameter(Mandatory)][bool]$DmsServiceIncluded,
        [switch]$SeparateConfigDatabase,
        [AllowEmptyString()][AllowNull()][string]$ResolvedConfigProvider,
        [AllowEmptyString()][AllowNull()][string]$ResolvedDmsProvider,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$ResolvedCmsConnectionString,
        [Parameter(Mandatory)][object]$SchemaToolPath,
        [AllowEmptyString()][AllowNull()][string]$ResolvedMssqlSaPassword,
        [AllowEmptyString()][AllowNull()][string]$ResolvedTopologyDatastoreDatabaseName
    )

    # Canonicalize the selected engine once at entry through the single engine-token boundary, so every
    # comparison below, the CLI forwarding, and the returned contract use the canonical 'postgresql' /
    # 'mssql' even when a caller supplied a case variant ('MSSQL') through ValidateSet.
    $InfrastructureEngine = ConvertTo-CanonicalDatabaseEngine -Engine $InfrastructureEngine

    # (STACK INVARIANT, always) SQL Server SA password, keyed on the AUTHORITATIVE engine the start script
    # selected - not on the CMS provider - so it is enforced even when no local Configuration Service
    # participates (a Keycloak run without the local config service still starts the SQL Server datastore).
    # Compose already resolved ${MSSQL_SA_PASSWORD:-<default>} at shell-over-file precedence; a blank value
    # cannot authenticate CMS, OpenIddict, or the datastore registration.
    $mssqlSaPassword = $null
    if ($InfrastructureEngine -eq 'mssql') {
        $mssqlSaPassword = $ResolvedMssqlSaPassword
        if ([string]::IsNullOrWhiteSpace($mssqlSaPassword)) {
            throw "Configuration runtime-contract error: MSSQL_SA_PASSWORD resolves to a blank value on a SQL Server stack, so the Configuration Service connection and OpenIddict initialization cannot authenticate. Set MSSQL_SA_PASSWORD (or the variable it references)."
        }
    }

    # DMS INVARIANTS - validated only when the DMS service participates in this compose set (its dms compose
    # file is included). The DMS runtime provider (dms AppSettings__Datastore) is interpolated by Compose from
    # DMS_DATASTORE, INDEPENDENTLY of the CMS provider, so it must be checked against the selected engine on
    # its own - otherwise a shell DMS_DATASTORE could point the DMS container at a different engine than the
    # one that starts even though the CMS provider matches, or no local CMS participates at all. The datastore
    # database the DMS container receives is validated here too (same participation authority). Skipped for the
    # standalone Configuration Service lane, which composes no dms service. Participation is decided by the
    # caller from its own compose-file selection and is NEVER inferred from null Compose fields.
    $dmsProviderCanonical = $null
    $topologyDatastoreName = $null
    if ($DmsServiceIncluded) {
        # (1) DMS provider is an EXACT enum (case-insensitively, no surrounding whitespace); anything else -
        # 'mysql', blank, ' mssql ' - fails fast rather than being coerced.
        $dmsProviderCanonical =
            if ([string]::Equals($ResolvedDmsProvider, 'mssql', [System.StringComparison]::OrdinalIgnoreCase)) { 'mssql' }
            elseif ([string]::Equals($ResolvedDmsProvider, 'postgresql', [System.StringComparison]::OrdinalIgnoreCase)) { 'postgresql' }
            else { $null }
        if ($null -eq $dmsProviderCanonical) {
            throw "Configuration runtime-contract error: the effective DMS runtime provider (AppSettings__Datastore, resolved by Docker Compose) is '$ResolvedDmsProvider', which is not a supported engine. Set DMS_DATASTORE to exactly 'postgresql' or 'mssql'."
        }

        # (2) DMS provider MUST equal the infrastructure engine the start script selected (which starts that
        # Compose database file). A shell DMS_DATASTORE that differs cannot silently point the DMS container
        # at a different engine than the one that starts.
        if ($dmsProviderCanonical -ne $InfrastructureEngine) {
            throw "Configuration runtime-contract mismatch: the start script selected the '$InfrastructureEngine' infrastructure engine, but the effective DMS runtime provider (AppSettings__Datastore, resolved by Docker Compose at shell-over-env-file precedence) is '$dmsProviderCanonical'. Unset the conflicting DMS_DATASTORE shell override, or select that engine with -DatabaseEngine."
        }

        # (3) Topology datastore anchor. The DMS datastore database is the engine-specific datastore name
        # (POSTGRES_DB_NAME / MSSQL_DB_NAME) Docker Compose resolved on the db service - the authoritative
        # source, never DATABASE_CONNECTION_STRING_ADMIN (a readiness/admin connection whose database can
        # legitimately differ). A participating DMS service must have exactly one nonblank, concrete target
        # (not an unexpanded shell terminal). The container-vs-host-tooling agreement - that configure /
        # provision register this same database - is guaranteed by the host-side tooling converging on this
        # Compose-resolved anchor (wired in a follow-up commit), not by comparing an env-file projection here.
        $topologyDatastoreName = $ResolvedTopologyDatastoreDatabaseName
        if ([string]::IsNullOrWhiteSpace($topologyDatastoreName)) {
            throw "Configuration runtime-contract error: the DMS topology datastore database is blank, so a participating DMS service has no database target. Set POSTGRES_DB_NAME (PostgreSQL) or MSSQL_DB_NAME (SQL Server), or the variable it references."
        }
        if ($topologyDatastoreName -match '\$\{') {
            throw "Configuration runtime-contract error: the DMS topology datastore database resolves to '$topologyDatastoreName', which still contains an unexpanded variable reference. Docker Compose substitutes a shell-provided value verbatim without re-expanding it; set the referenced variable in the environment file, not only in the shell."
        }
    }

    # CMS INVARIANTS - validated only when the Configuration Service participates in this compose set. When
    # it does not (a Keycloak run that omits the local config service, or one pointed at an external
    # CONFIG_SERVICE_URL), Compose exposes no config-service provider/connection to resolve, so validating
    # them would fail on values that are legitimately absent. Participation is decided by the caller from its
    # own compose-file selection and is NEVER inferred from null Compose fields.
    $configProviderCanonical = $null
    $effectiveDatabaseName = $null
    $openIddict = $null
    if ($ConfigServiceIncluded) {
        # (1) CMS provider is an EXACT enum, read from the Compose-resolved config AppSettings__Datastore. Only
        # the two supported engines are accepted (case-insensitively, no surrounding whitespace); anything
        # else - 'mysql', blank, ' mssql ' - fails fast rather than being coerced, because Compose passes the
        # raw value straight to the Configuration Service.
        $configProviderCanonical =
            if ([string]::Equals($ResolvedConfigProvider, 'mssql', [System.StringComparison]::OrdinalIgnoreCase)) { 'mssql' }
            elseif ([string]::Equals($ResolvedConfigProvider, 'postgresql', [System.StringComparison]::OrdinalIgnoreCase)) { 'postgresql' }
            else { $null }
        if ($null -eq $configProviderCanonical) {
            throw "Configuration runtime-contract error: the effective Configuration Service provider (AppSettings__Datastore, resolved by Docker Compose) is '$ResolvedConfigProvider', which is not a supported engine. Set DMS_CONFIG_DATASTORE to exactly 'postgresql' or 'mssql'."
        }

        # (2) CMS provider MUST equal the infrastructure engine the start script selected (which starts that
        # Compose database file and initializes OpenIddict for it). A shell DMS_CONFIG_DATASTORE that differs
        # cannot silently point the Configuration Service at a different engine than the one that starts.
        if ($configProviderCanonical -ne $InfrastructureEngine) {
            throw "Configuration runtime-contract mismatch: the start script selected the '$InfrastructureEngine' infrastructure engine, but the effective Configuration Service provider (AppSettings__Datastore, resolved by Docker Compose at shell-over-env-file precedence) is '$configProviderCanonical'. Unset the conflicting DMS_CONFIG_DATASTORE shell override, or select that engine with -DatabaseEngine."
        }

        # (3) The effective CMS connection must be present, parse under the selected provider (wrong-engine
        # strings are rejected by the provider's own builder), and target a concrete database.
        if ([string]::IsNullOrWhiteSpace($ResolvedCmsConnectionString)) {
            throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING (resolved by Docker Compose) is empty on a '$InfrastructureEngine' stack. On a SQL Server stack this occurs when no connection string is set and Compose would substitute the PostgreSQL-only compose-file fallback. Set a '$InfrastructureEngine' connection string targeting the effective configuration database."
        }
        $targetDatabases = @(Get-CmsConnectionStringDatabaseName -Engine $InfrastructureEngine -ConnectionString $ResolvedCmsConnectionString -SchemaToolPath $SchemaToolPath)
        if ($targetDatabases.Count -eq 0) {
            throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING targets no database (set Database or Initial Catalog), so the Configuration Service would connect to the engine default instead of the effective configuration database."
        }

        # (4) Effective configuration database name and topology relationship.
        if ($DmsServiceIncluded) {
            # FULL-STACK: the expected configuration database is computed INDEPENDENTLY of the connection -
            # shared topology uses the DMS datastore anchor, -SeparateConfigDatabase the dedicated
            # configuration database - so a caller-authored connection can never redefine the topology (the
            # DMS_CONFIG_DATABASE_NAME seam remains definitive); the connection must AGREE with it.
            $separateConfigDatabaseName = 'edfi_configurationservice'
            $expectedConfigDatabaseName = if ($SeparateConfigDatabase) { $separateConfigDatabaseName } else { $topologyDatastoreName }
            foreach ($target in $targetDatabases) {
                if (-not (Test-DatabaseNameEquivalent -Engine $InfrastructureEngine -Left $target -Right $expectedConfigDatabaseName)) {
                    throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING targets database '$target', but the effective configuration database is '$expectedConfigDatabaseName' (shared topology uses the DMS datastore database; -SeparateConfigDatabase uses the dedicated configuration database). Align the connection string, or the shell variable it routes through."
                }
            }
            $effectiveDatabaseName = $expectedConfigDatabaseName

            # Separate topology requires the DMS datastore and the configuration database to be DIFFERENT
            # physical databases under the engine's identity policy, so the topology is not "separate" in
            # name only. (Shared topology deliberately makes them the same - already enforced by the equality
            # above, since the expected name IS the datastore anchor.)
            if ($SeparateConfigDatabase -and (Test-DatabaseNameEquivalent -Engine $InfrastructureEngine -Left $topologyDatastoreName -Right $expectedConfigDatabaseName)) {
                throw "Configuration runtime-contract error: -SeparateConfigDatabase selects the dedicated configuration database '$expectedConfigDatabaseName', but the DMS datastore database ('$topologyDatastoreName') is the same physical database under $InfrastructureEngine identity semantics, so the topology would not be separate. Choose a different DMS datastore name, or omit -SeparateConfigDatabase for the shared topology."
            }
        }
        else {
            # STANDALONE (no DMS service participates): there is no datastore anchor to expect against, so the
            # resolved connection is authoritative and its single target IS the effective configuration
            # database (which OpenIddict initialization then uses).
            $distinctTargets = [System.Collections.Generic.HashSet[string]]::new((Get-DatabaseNameComparer -Engine $InfrastructureEngine))
            foreach ($target in $targetDatabases) { [void]$distinctTargets.Add($target) }
            if ($distinctTargets.Count -gt 1) {
                throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING specifies conflicting database targets ($($targetDatabases -join ', ')). Set a single database so OpenIddict initialization and the Configuration Service agree."
            }
            $effectiveDatabaseName = $targetDatabases[0]
        }

        # A target Compose kept opaque (a shell-substituted ${...} it does not re-expand) is not a real
        # database. In the full-stack lanes the equality check above already rejects it; guard the standalone
        # lane too.
        if ($effectiveDatabaseName -match '\$\{') {
            throw "Configuration runtime-contract error: the effective configuration database resolves to '$effectiveDatabaseName', which still contains an unexpanded variable reference. Docker Compose substitutes a shell-provided value verbatim without re-expanding it; set the referenced variable in the environment file, not only in the shell."
        }

        # (5) OpenIddict host-side target, built from the EXPLICIT engine and the resolved name/password -
        # never inferred from a string. Read only by the self-contained identity path (which always includes
        # the config service), so it is populated exactly when a caller will consume it.
        $openIddict = [pscustomobject]@{
            DbType     = if ($InfrastructureEngine -eq 'mssql') { 'MSSQL' } else { 'Postgresql' }
            DbUser     = if ($InfrastructureEngine -eq 'mssql') { 'sa' } else { 'postgres' }
            DbPort     = if ($InfrastructureEngine -eq 'mssql') { 'ENV:MSSQL_PORT' } else { 'ENV:POSTGRES_PORT' }
            DbName     = $effectiveDatabaseName
            DbPassword = if ($InfrastructureEngine -eq 'mssql') { $mssqlSaPassword } else { $null }
        }
    }

    return [pscustomobject]@{
        InfrastructureEngine          = $InfrastructureEngine
        ConfigProvider                = $configProviderCanonical
        DmsProvider                   = $dmsProviderCanonical
        CmsConnectionString           = $ResolvedCmsConnectionString
        CmsDatabaseName               = $effectiveDatabaseName
        TopologyDatastoreDatabaseName = $topologyDatastoreName
        MssqlSaPassword               = $mssqlSaPassword
        OpenIddict                    = $openIddict
    }
}

function Get-NormalizedEnvValue {
    <#
    .SYNOPSIS
        Trims an env-file value and removes one surrounding matching single- or double-quote pair,
        returning the unquoted content. Single source of the unquoting used by the reference expander
        (Resolve-EnvValueReference) and the reference-key detector (Get-EnvValueReferenceKey). It ONLY
        strips quotes; whether the content is then interpolated depends on the quote kind - callers use
        Test-EnvValueIsSingleQuoted to suppress interpolation for single-quoted values, which docker-compose
        preserves literally.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $normalized = $Value.Trim()
    if (
        $normalized.Length -ge 2 -and
        $normalized[0] -in @("'", '"') -and
        $normalized[-1] -eq $normalized[0]
    ) {
        $normalized = $normalized.Substring(1, $normalized.Length - 2)
    }

    return $normalized
}

function Test-EnvValueIsSingleQuoted {
    <#
    .SYNOPSIS
        Returns $true when a trimmed env-file value is wrapped in a matching pair of SINGLE quotes.
        Docker Compose interpolates unquoted and double-quoted values but preserves single-quoted values
        literally - it does not expand ${...} inside single quotes (verified with `docker compose config`).
        Callers must therefore return the unquoted content verbatim for a single-quoted value rather than
        resolve it as a reference, or the host would initialize a different database than CMS receives.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $trimmed = $Value.Trim()
    return (
        $trimmed.Length -ge 2 -and
        $trimmed[0] -eq "'" -and
        $trimmed[-1] -eq "'"
    )
}

function Get-EnvValueReferenceKey {
    <#
    .SYNOPSIS
        Returns the referenced key name when a value is a single whole-value ${NAME} reference that
        docker-compose would interpolate (unquoted or double-quoted), otherwise $null. A single-quoted
        value is preserved literally by docker-compose, so it is NOT a reference and yields $null.
        Single-sources the whole-value-reference detection so a caller that must recover the referenced
        key for shell-precedence guarding parses a value exactly as Resolve-EnvValueReference expands it -
        a double-quoted "${NAME}" yields NAME, a single-quoted '${NAME}' yields $null.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if (Test-EnvValueIsSingleQuoted -Value $Value) {
        return $null
    }

    $normalized = Get-NormalizedEnvValue -Value $Value
    $referenceMatch = [regex]::Match($normalized, '^\$\{(?<key>[A-Za-z_][A-Za-z0-9_]*)\}$')
    if ($referenceMatch.Success) {
        return $referenceMatch.Groups["key"].Value
    }

    return $null
}

function Resolve-EnvValueReference {
    <#
    .SYNOPSIS
        Resolves an env-file value that is either a literal or a single whole-value ${NAME}
        reference, expanding the reference recursively against the effective environment values
        (cycle-guarded). Partial or embedded ${...} expressions are rejected. A single-quoted value is
        returned verbatim (quotes stripped) without interpolation, because docker-compose preserves
        single-quoted values literally. Engine-agnostic: used for both the SQL Server and PostgreSQL
        configuration-database name seams.

    .PARAMETER TreatUnresolvedReferenceAsEmpty
        Models docker-compose's bare ${NAME} semantics: a reference to an unset or blank variable resolves
        to empty (rather than throwing), so a caller modeling a ${VAR:-default} expression can then apply
        its default. Cyclic and unsupported-expression references still throw. Off by default: an
        unresolved reference is a hard error for callers that require a concrete value.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues,

        [switch]$TreatUnresolvedReferenceAsEmpty,

        [System.Collections.Generic.HashSet[string]]$VisitedKeys
    )

    if (Test-EnvValueIsSingleQuoted -Value $Value) {
        # docker-compose does not interpolate single-quoted values; the content is literal even when it
        # contains ${...}. Return the unquoted content verbatim so the host observes the same literal value
        # CMS receives (rather than expanding it and initializing a different database).
        return Get-NormalizedEnvValue -Value $Value
    }

    $resolvedValue = Get-NormalizedEnvValue -Value $Value

    $referencedKey = Get-EnvValueReferenceKey -Value $Value
    if ($null -eq $referencedKey) {
        if ($resolvedValue -match '\$\{') {
            throw "Environment value '$resolvedValue' uses an unsupported environment expression. Use a literal value or a simple `${NAME} reference."
        }

        return $resolvedValue
    }

    if (-not $EnvValues.ContainsKey($referencedKey)) {
        # docker-compose resolves a bare ${NAME} reference to an unset variable as empty (a ':-' default in
        # the referring expression then applies). Callers modeling that (e.g. ${MSSQL_SA_PASSWORD:-...}) pass
        # -TreatUnresolvedReferenceAsEmpty so an unset reference yields "" instead of aborting.
        if ($TreatUnresolvedReferenceAsEmpty) { return "" }
        throw "Environment reference '`${$referencedKey}' cannot be resolved because '$referencedKey' is absent from the effective environment."
    }

    $referencedValue = [string]$EnvValues[$referencedKey]
    if ([string]::IsNullOrWhiteSpace($referencedValue)) {
        if ($TreatUnresolvedReferenceAsEmpty) { return "" }
        throw "Environment reference '`${$referencedKey}' cannot be resolved because '$referencedKey' is blank."
    }

    if ($null -eq $VisitedKeys) {
        $VisitedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }
    if (-not $VisitedKeys.Add($referencedKey)) {
        throw "Environment reference '`${$referencedKey}' is cyclic."
    }

    try {
        return Resolve-EnvValueReference `
            -Value $referencedValue `
            -EnvValues $EnvValues `
            -TreatUnresolvedReferenceAsEmpty:$TreatUnresolvedReferenceAsEmpty `
            -VisitedKeys $VisitedKeys
    }
    finally {
        $null = $VisitedKeys.Remove($referencedKey)
    }
}

function Resolve-DatabaseEngineEnvironmentFile {
    <#
    .SYNOPSIS
        Returns the effective environment file path for the requested database engine. With the
        default "postgresql" engine the base file is returned unchanged. With "mssql" the
        .env.mssql overlay (DMS_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server admin
        connection string) is composed onto the base into a derived file under
        <DockerComposeRoot>/.derived/ and that path is returned. DATABASE_TEMPLATE_PACKAGE
        (inherited from the base file - .env.mssql never carries it, so DS-version and
        Minimal/Populated variance keep coming from the base file) is rewritten from its
        PostgreSql engine token to MsSql in the returned file.

    .DESCRIPTION
        Reuses New-DataStandardDerivedEnvFile's generic base+overlay composition (it is not
        specific to data-standard overlays despite the name) so DMS_DATASTORE and the
        SQL Server connection strings reach every phase - configure, provision, and the start
        scripts - from one canonical path. Without this, a run could provision an MSSQL data
        store in CMS while the DMS container itself still starts on its postgresql default
        (local-dms.yml AppSettings__Datastore), since that setting comes only from the env file.

        Idempotency guard: when the base file already carries every non-blank key from the current
        .env.mssql overlay, with both datastore discriminators set to mssql, the base file is
        returned unchanged instead of composing a derived-of-derived file. Reading the required
        key set from the overlay keeps this proof current when the overlay gains a new engine-owned
        setting. If DATABASE_TEMPLATE_PACKAGE still carries a stale PostgreSql engine token, a
        corrected derived file is materialized rather than mutating the caller's source file.

        A partial hand-authored MSSQL env is completed from the overlay. Non-blank custom MSSQL
        credentials, database names, and ports are preserved. Connection strings are preserved only
        when they contain a SQL Server data-source keyword; PostgreSQL-shaped values inherited from
        a partially edited base file are replaced by the MSSQL overlay. A caller-authored CMS MSSQL
        connection string must resolve to MSSQL_DB_NAME so CMS and self-contained OpenIddict cannot
        silently target different databases; a mismatch fails before any derived file is written.
        DMS_DATASTORE and DMS_CONFIG_DATASTORE are always forced to mssql.

    .PARAMETER DatabaseEngine
        "postgresql" (default; no-op) or "mssql".

    .PARAMETER BaseEnvironmentFile
        Absolute path to the base env file. Must exist.

    .PARAMETER DockerComposeRoot
        Directory holding .env.mssql and the .derived output. Defaults to this module's
        directory (eng/docker-compose).
    #>
    param(
        [string]$DatabaseEngine = "postgresql",
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot
    )

    if ($DatabaseEngine -ne "mssql") {
        return $BaseEnvironmentFile
    }

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    $derivedName = "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).mssql"
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName

    $overlayPath = Join-Path $DockerComposeRoot ".env.mssql"
    if (-not (Test-Path -LiteralPath $overlayPath -PathType Leaf)) {
        throw "Resolve-DatabaseEngineEnvironmentFile: no MSSQL engine overlay found (expected '$overlayPath')."
    }

    $baseValues = ReadValuesFromEnvFile $BaseEnvironmentFile
    $overlayValues = ReadValuesFromEnvFile $overlayPath
    $templatePackage = Get-EnvValue -EnvValues $baseValues -Name "DATABASE_TEMPLATE_PACKAGE"
    $correctedTemplatePackage = Convert-TemplatePackageToken -PackageId $templatePackage -Engine "MsSql"
    $baseDeclaresMssql =
        (Get-EnvValue -EnvValues $baseValues -Name "DMS_DATASTORE") -eq "mssql" -or
        (Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATASTORE") -eq "mssql"

    # The CMS connection-string / OpenIddict database invariant is no longer enforced here. The start
    # scripts resolve and validate the whole Configuration Service runtime contract once, up front,
    # against Docker Compose's own resolution (Resolve-EffectiveConfigRuntimeContract), so there is a
    # single engine/database policy rather than a second one embedded in overlay composition.

    # A fixed three-key signal can become stale when .env.mssql gains another required setting.
    # Prove that every current overlay key exists and is non-blank before treating a file as an
    # already-composed handoff from an earlier phase.
    $overlayAlreadyComposed =
        (Get-EnvValue -EnvValues $baseValues -Name "DMS_DATASTORE") -eq "mssql" -and
        (Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATASTORE") -eq "mssql"
    if ($overlayAlreadyComposed) {
        foreach ($overlayKey in $overlayValues.Keys) {
            $overlayKeyName = [string]$overlayKey
            $baseValue = Get-EnvValue -EnvValues $baseValues -Name $overlayKeyName
            $isConnectionString = $overlayKeyName -match 'CONNECTION_STRING'
            if (
                [string]::IsNullOrWhiteSpace($baseValue) -or
                ($isConnectionString -and -not (Test-SqlServerConnectionString -ConnectionString $baseValue))
            ) {
                $overlayAlreadyComposed = $false
                break
            }
        }
    }

    if ($overlayAlreadyComposed) {
        if ($correctedTemplatePackage -eq $templatePackage) {
            return $BaseEnvironmentFile
        }

        Write-DerivedEnvFile `
            -BaseEnvironmentFile $BaseEnvironmentFile `
            -TargetPath $derivedPath `
            -KeyOverrides @{ DATABASE_TEMPLATE_PACKAGE = $correctedTemplatePackage }

        return $derivedPath
    }

    # Preserve caller-authored MSSQL values when completing a partial MSSQL file. Connection
    # strings require an MSSQL shape so a base file with only one edited discriminator cannot
    # retain its PostgreSQL admin/CMS targets. The overlay still owns both engine discriminators.
    $keyOverrides = @{}
    if ($baseDeclaresMssql) {
        foreach ($overlayKey in $overlayValues.Keys) {
            $overlayKeyName = [string]$overlayKey
            if ($overlayKeyName -in @("DMS_DATASTORE", "DMS_CONFIG_DATASTORE")) {
                continue
            }

            $baseValue = Get-EnvValue -EnvValues $baseValues -Name $overlayKeyName
            $isConnectionString = $overlayKeyName -match 'CONNECTION_STRING'
            if (
                -not [string]::IsNullOrWhiteSpace($baseValue) -and
                (-not $isConnectionString -or (Test-SqlServerConnectionString -ConnectionString $baseValue))
            ) {
                $keyOverrides[$overlayKeyName] = $baseValue
            }
        }
    }

    $composedPath = New-DataStandardDerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -OverlayEnvironmentFile $overlayPath `
        -TargetPath $derivedPath

    # The overlay never carries DATABASE_TEMPLATE_PACKAGE (see .env.mssql's header), so the
    # composed file's value is still exactly the base file's value at this point.
    if ($correctedTemplatePackage -ne $templatePackage) {
        $keyOverrides["DATABASE_TEMPLATE_PACKAGE"] = $correctedTemplatePackage
    }

    if ($keyOverrides.Count -gt 0) {
        Write-DerivedEnvFile `
            -BaseEnvironmentFile $composedPath `
            -TargetPath $composedPath `
            -KeyOverrides $keyOverrides
    }

    return $composedPath
}

function Resolve-ConfigDatabaseTopologyEnvironmentFile {
    <#
    .SYNOPSIS
        Resolves the local database-topology contract onto an (already engine-composed) environment
        file and returns the effective file path. Engine-agnostic: applies to PostgreSQL and SQL
        Server identically. The topology is never inferred from the engine.

    .DESCRIPTION
        DMS_CONFIG_DATABASE_NAME is the single configuration-database-name seam that both engines'
        DMS_CONFIG_DATABASE_CONNECTION_STRING interpolate. This function materializes the effective seam
        value into a derived file under <DockerComposeRoot>/.derived/ WITHOUT interpolating any datastore
        value in PowerShell - Docker Compose remains the single interpolation authority.

        Shared (default): the seam is materialized as a REFERENCE to the engine's datastore key
        (${POSTGRES_DB_NAME} / ${MSSQL_DB_NAME}); Compose resolves it (with the compose-file default,
        shell-over-env-file precedence, and any indirection), so the configuration database follows the DMS
        datastore. Separate (-SeparateConfigDatabase): the seam is the dedicated edfi_configurationservice
        literal, without changing the DMS datastore selection. The effective configuration database, the
        datastore concreteness, and the separate-topology distinctness are all validated downstream by
        Resolve-EffectiveConfigRuntimeContract against Compose's own resolution; the resolved concrete name
        every host-side consumer uses (e.g. setup-openiddict.ps1) comes from that contract, not from re-reading
        this seam.

        Idempotency: when the base file already carries exactly this effective value (the seam reference in
        shared mode, the literal in separate mode) the base file is returned unchanged. The effective value is
        a pure function of -SeparateConfigDatabase, not of any name the base file already carries. Separate
        mode stays separate across re-resolution because every phase is passed the same switch (so a
        re-resolution with the switch supplied no-ops here), not by preserving an existing name.

        This function only computes and materializes the effective name. The Configuration Service
        engine / connection / database agreement is validated once, up front, by the start scripts via
        Resolve-EffectiveConfigRuntimeContract (against Docker Compose's own resolution), so there is a
        single validation policy rather than a second one embedded here.

    .PARAMETER BaseEnvironmentFile
        Absolute path to the (engine-composed) base env file. Must exist.

    .PARAMETER DockerComposeRoot
        Directory holding the .derived output. Defaults to this module's directory.

    .PARAMETER DatabaseEngine
        "postgresql" (default) or "mssql"; selects the datastore-name key for the shared default.

    .PARAMETER SeparateConfigDatabase
        Selects the dedicated edfi_configurationservice configuration database.
    #>
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [string]$DatabaseEngine = "postgresql",
        [switch]$SeparateConfigDatabase
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    # Canonicalize through the single engine-token boundary so the datastore-key selection below uses the
    # canonical engine even under a case variant.
    $DatabaseEngine = ConvertTo-CanonicalDatabaseEngine -Engine $DatabaseEngine

    $separateConfigDatabaseName = "edfi_configurationservice"

    $baseValues = ReadValuesFromEnvFile $BaseEnvironmentFile

    # The effective DMS_CONFIG_DATABASE_NAME is a pure function of -SeparateConfigDatabase, and is NEVER
    # interpolated in PowerShell - Docker Compose is the single interpolation authority:
    #   * Shared (default): a REFERENCE to the engine's datastore key (${POSTGRES_DB_NAME} / ${MSSQL_DB_NAME}).
    #     Compose resolves it with the compose-file default and shell-over-env-file precedence, and through
    #     any indirection (e.g. ${LOCAL_DB:-...}), so the configuration database always follows the DMS
    #     datastore without a second PowerShell interpolation model.
    #   * Separate (-SeparateConfigDatabase): the dedicated 'edfi_configurationservice' literal.
    # The datastore concreteness and the separate-topology datastore-vs-configuration distinctness ("separate"
    # is not separate in name only) are enforced by the runtime contract against Compose's own resolution of
    # the topology datastore anchor - not here against an env-file projection a shell override could bypass.
    # Shared mode materializes a DEFAULT-BEARING reference (${KEY:-edfi_datamanagementservice}) whose default
    # equals the db service's own datastore default (postgresql.yml POSTGRES_DB_NAME / mssql.yml MSSQL_DB_NAME).
    # A bare ${KEY} would resolve to EMPTY when the key is omitted from the env file - Compose cannot reach a
    # default declared inside another service's environment while interpolating this env-file value - which
    # would blank the CMS seam while the db anchor still defaulted. The matching default keeps the CMS
    # configuration database identical to the datastore anchor even when the key is fully omitted.
    $datastoreKey = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    $effectiveName = if ($SeparateConfigDatabase) { $separateConfigDatabaseName } else { "`${${datastoreKey}:-edfi_datamanagementservice}" }

    # The raw DMS_CONFIG_DATABASE_NAME the base file already carries (used only for the idempotent no-op).
    # Idempotency in separate mode comes from forwarding the switch to every phase (so a re-resolution stays
    # separate and no-ops here), not from preserving an existing name.
    $existingRaw = Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATABASE_NAME"

    # Idempotent no-op when the base file already carries exactly this effective value (the seam reference in
    # shared mode, the literal in separate mode).
    if ([string]::Equals($existingRaw, $effectiveName, [System.StringComparison]::Ordinal)) {
        return $BaseEnvironmentFile
    }

    $derivedName = "$([System.IO.Path]::GetFileName($BaseEnvironmentFile)).config-db"
    $derivedPath = Join-Path (Join-Path $DockerComposeRoot ".derived") $derivedName
    Write-DerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -TargetPath $derivedPath `
        -KeyOverrides @{ DMS_CONFIG_DATABASE_NAME = $effectiveName }

    return $derivedPath
}

function Resolve-RegisteredDatastoreTarget {
    <#
    .SYNOPSIS
        The single authority for the DMS datastore database a host-side phase registers in CMS. Both
        configure-local-data-store.ps1 and start-published-dms.ps1's in-process registration call it BEFORE
        any mutation (CMS client/tenant creation, container start, OpenIddict init), so an invalid effective
        target fails in preflight rather than after initialization. Pure (no Docker/IO): the caller supplies
        the already-Compose-resolved topology anchor.

    .DESCRIPTION
        One normalization rule, one place:
          * Null / empty / whitespace -DataStoreDatabaseName is treated as OMITTED (the bootstrap chain's
            convention - build-dms forwards an empty default mechanically), so it converges on the
            Compose-resolved topology anchor.
          * A nonblank -DataStoreDatabaseName is an intentional replacement (e.g. the E2E database).
        The resulting EFFECTIVE target (replacement or anchor) must be a single nonblank value, and in
        separate topology must NOT identify the dedicated configuration database (edfi_configurationservice)
        under the engine's provider-specific identity, or the topology would collapse. That collision is
        validated on the effective target regardless of whether it came from a replacement or the anchor.
        The datastore registration is skipped entirely for -NoDataStore (it selects an existing CMS record and
        creates nothing), so callers do not resolve a target in that case.

    .PARAMETER InfrastructureEngine
        The engine the caller selected ('postgresql' | 'mssql').

    .PARAMETER RequestedDatabaseName
        The explicit -DataStoreDatabaseName value (blank = omitted, converge on the anchor).

    .PARAMETER TopologyDatastoreDatabaseName
        The Compose-resolved topology datastore anchor (Get-ComposeResolvedConfiguration.TopologyDatastoreDatabaseName).

    .PARAMETER SeparateConfigDatabase
        The topology; when set, the EFFECTIVE target (replacement or anchor) must be a different physical
        database from edfi_configurationservice.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$InfrastructureEngine,
        [AllowEmptyString()][AllowNull()][string]$RequestedDatabaseName,
        [AllowEmptyString()][AllowNull()][string]$TopologyDatastoreDatabaseName,
        [switch]$SeparateConfigDatabase
    )

    $InfrastructureEngine = ConvertTo-CanonicalDatabaseEngine -Engine $InfrastructureEngine

    # 1. Determine the effective registered target: an explicit replacement (blank = omitted) else the anchor.
    $effectiveTarget = if (-not [string]::IsNullOrWhiteSpace($RequestedDatabaseName)) { $RequestedDatabaseName } else { $TopologyDatastoreDatabaseName }

    # 2. The effective target must be a single nonblank concrete value.
    if ([string]::IsNullOrWhiteSpace($effectiveTarget)) {
        throw "The DMS topology datastore database resolved by Docker Compose is blank, so there is no database to register. Set POSTGRES_DB_NAME (PostgreSQL) or MSSQL_DB_NAME (SQL Server), or the variable it references."
    }

    # 2a. The effective target must carry no leading/trailing whitespace. The exact providers (Npgsql /
    #     Microsoft.Data.SqlClient) TRIM whitespace around a keyword value when parsing, so 'Database= edfi_configurationservice'
    #     parses as 'edfi_configurationservice'. An untrimmed value would therefore fail the collision
    #     comparison below (compared ordinally/case-insensitively but NOT trimmed) while the constructed
    #     connection silently targets the trimmed name - the same collapse the collision check is meant to
    #     prevent. Reject it rather than silently trimming; internal spaces (e.g. 'edfi data-store') are fine.
    if ($effectiveTarget -ne $effectiveTarget.Trim()) {
        throw "The DMS datastore database '$effectiveTarget' has leading or trailing whitespace. The connection-string providers trim it when parsing, so it would not compare equal to the dedicated configuration database here yet target the trimmed name at runtime. Remove the surrounding whitespace (internal spaces are allowed)."
    }

    # 2b. The effective target is concatenated into the CMS/DMS connection string as the Database keyword value
    #     (Dms-Management New-DataStoreConnectionString / Add-DataStore), and the exact providers use last-wins
    #     keyword semantics - so a value carrying a connection-string delimiter, a quoting/interpolation
    #     character, or a control character could inject a second Database/Host/Server keyword and silently
    #     redirect the stored connection (collapsing a separate topology past a green preflight, or the
    #     equivalence check below). Reject those here, once, for the effective target (replacement OR anchor),
    #     before the collision comparison and before any connection is built. This is an identifier-safety
    #     check, not a connection-string parser: ordinary names - letters (including Unicode), digits, spaces,
    #     '_', '-', '.' - pass unchanged; only connection-string-hostile characters are rejected.
    if ($effectiveTarget -match '[;={}"''`\x00-\x1F\x7F]') {
        throw "The DMS datastore database '$effectiveTarget' contains a character that is not valid in a database identifier (a connection-string delimiter ';' or '=', a quoting/interpolation character, or a control character). It is used verbatim as the connection-string Database keyword, so it must not be able to inject additional keywords. Use a plain database name (letters, digits, spaces, '_', '-', '.')."
    }

    # 3. Separate topology: the EFFECTIVE target (replacement OR anchor) must not identify the dedicated
    #    configuration database, or the topology would collapse - validated regardless of the source, so a
    #    colliding anchor is caught in direct/manual configure without depending on another caller having run
    #    the runtime contract first.
    if ($SeparateConfigDatabase -and (Test-DatabaseNameEquivalent -Engine $InfrastructureEngine -Left $effectiveTarget -Right 'edfi_configurationservice')) {
        throw "The effective DMS datastore database '$effectiveTarget' is the same physical database as the dedicated configuration database 'edfi_configurationservice' under $InfrastructureEngine identity semantics, so the separate topology would collapse. Choose a different DMS datastore database (or -DataStoreDatabaseName)."
    }

    return $effectiveTarget
}
