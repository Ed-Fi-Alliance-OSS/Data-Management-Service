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

function Test-MssqlConnectionStringValue {
    param(
        [AllowEmptyString()]
        [string]$ConnectionString
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $false
    }

    # ReadValuesFromEnvFile preserves surrounding quotes, but docker-compose strips one surrounding
    # single- or double-quote pair before the container uses the value. Normalize the same way so a
    # quoted-but-valid connection string classifies correctly. Quote KIND does not matter here: engine
    # detection only inspects which connection-string keys are present, not interpolated content.
    $normalizedConnectionString = Get-NormalizedEnvValue -Value $ConnectionString

    # Classify by connection-string KEYS using a generic DbConnectionStringBuilder - the same parser
    # Get-CmsConnectionStringDatabaseName and provision-dms-schema.ps1's Resolve-TargetDialect use - mirroring
    # that authoritative dialect resolver rather than a raw regex. Parsing understands connection-string
    # quoting, so a ';Server=' appearing inside a quoted value is treated as part of that value, not as a
    # spurious key match. Use the explicit set_/ContainsKey accessors: PowerShell's indexer/property sugar
    # misbehaves on this IDictionary-implementing type and silently fails to parse.
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($normalizedConnectionString)
    }
    catch {
        # Not a parseable connection string, so not identifiable as a SQL Server connection.
        return $false
    }

    # The PostgreSQL-definitive keys win FIRST: none of host/username/port/sslmode is a valid SqlClient
    # keyword, so their presence rules out SQL Server even when the string uses Server= (a legal Npgsql alias
    # for Host) or User Id= (a legal Npgsql alias for Username). This keeps engine detection in lock-step with
    # Resolve-TargetDialect (provision-dms-schema.ps1); the two marker lists must stay identical.
    foreach ($postgresqlMarker in @("host", "username", "port", "sslmode")) {
        if ($builder.ContainsKey($postgresqlMarker)) {
            return $false
        }
    }

    # SQL Server data-source / catalog / credential keys. 'server' and 'user id' are themselves legal Npgsql
    # aliases, so they only decide here - after the definitive PostgreSQL keys above have been ruled out. A
    # string carrying only these ambiguous keys is genuinely indistinguishable and classifies as SQL Server
    # (matching Resolve-TargetDialect's residual default).
    foreach ($mssqlMarker in @("server", "data source", "initial catalog", "user id", "trusted_connection")) {
        if ($builder.ContainsKey($mssqlMarker)) {
            return $true
        }
    }

    # Neither vocabulary present: not identifiable as SQL Server.
    return $false
}

# === Cross-engine connection-string classification (single source) ==================================
# One classifier replaces the duplicated Test-MssqlConnectionStringValue / Resolve-TargetDialect marker
# lists. The SQL Server vocabulary is NOT hand-listed: it is read authoritatively from the built-in
# System.Data.SqlClient.SqlConnectionStringBuilder (ships with PowerShell; already used by
# setup-openiddict.ps1), so every SqlClient keyword and alias - Server, Data Source, Address, Addr,
# Network Address, Initial Catalog, User Id, UID, Password, PWD, Integrated Security,
# TrustServerCertificate, ... - is recognized without drift. There is no built-in Npgsql builder in this
# PowerShell surface (verified), so PostgreSQL keywords are the documented table below. Keys shared with
# SqlClient (server, database, user id, password, trustservercertificate, application name, pooling, ...)
# MUST appear here so a connection using only shared keys classifies as Ambiguous and is evaluated against
# the already-selected provider, never forced to SQL Server.
$script:NpgsqlConnectionStringKeyword = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        'host', 'server', 'port', 'database',
        'username', 'userid', 'user',
        'password', 'passfile',
        'sslmode', 'sslcertificate', 'sslkey', 'sslpassword', 'rootcertificate', 'sslrootcertificate',
        'channelbinding', 'trustservercertificate', 'sslnegotiation',
        'applicationname', 'enlist', 'searchpath', 'clientencoding', 'encoding', 'timezone', 'options',
        'integratedsecurity', 'kerberosservicename', 'includerealm', 'persistsecurityinfo',
        'pooling', 'minpoolsize', 'maxpoolsize', 'minimumpoolsize', 'maximumpoolsize',
        'connectionidlelifetime', 'connectionpruninginterval', 'connectionlifetime',
        'timeout', 'commandtimeout', 'cancellationtimeout', 'internalcommandtimeout',
        'keepalive', 'tcpkeepalive', 'tcpkeepalivetime', 'tcpkeepaliveinterval',
        'maxautoprepare', 'autoprepareminusages', 'noresetonclose',
        'readbuffersize', 'writebuffersize', 'socketreceivebuffersize', 'socketsendbuffersize',
        'servercompatibilitymode', 'convertinfinitydatetime', 'includeerrordetail', 'logparameters',
        'loadtablecomposites', 'targetsessionattributes', 'multiplexing', 'hostrecheckseconds',
        'arraynullabilitymode'
    ),
    [System.StringComparer]::OrdinalIgnoreCase)

function Get-NormalizedConnectionStringKeyword {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Keyword)
    return ($Keyword.ToLowerInvariant() -replace '\s', '')
}

function Test-SqlServerConnectionStringKeyword {
    <#
    .SYNOPSIS
        Returns $true when $Keyword is a recognized SQL Server (SqlClient) connection-string keyword or
        alias, per the built-in System.Data.SqlClient.SqlConnectionStringBuilder. ContainsKey reports
        keyword validity independent of any value, and is case-insensitive, so the generic builder's
        lowercased-with-spaces key form (e.g. 'network address') is accepted.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Keyword)
    if ([string]::IsNullOrWhiteSpace($Keyword)) { return $false }
    return ([System.Data.SqlClient.SqlConnectionStringBuilder]::new()).ContainsKey($Keyword)
}

function Resolve-ConnectionStringDialect {
    <#
    .SYNOPSIS
        Classifies a connection string as 'PostgreSql', 'SqlServer', 'Ambiguous', or 'Invalid' by which
        keys it carries - never a lossy Boolean. Parsing is done with the generic DbConnectionStringBuilder
        so connection-string quoting is respected (key-looking text inside a quoted value is not a key).

    .DESCRIPTION
        A key is SQL-Server-valid per System.Data.SqlClient.SqlConnectionStringBuilder and PostgreSQL-valid
        per $script:NpgsqlConnectionStringKeyword. A key valid for exactly one engine is a definitive marker
        for that engine; a key valid for both (server, user id, database, password, ...) is a shared alias
        and carries no signal; a key valid for neither is unknown and ignored. Definitive markers for both
        engines in the same string is contradictory -> 'Invalid'. Only shared/unknown keys -> 'Ambiguous',
        to be resolved against the already-selected provider by Test-ConnectionStringMatchesEngine (an
        ambiguous string is never forced to one engine).
    #>
    param([AllowEmptyString()][AllowNull()][string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) { return 'Invalid' }

    $normalized = Get-NormalizedEnvValue -Value $ConnectionString
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($normalized)
    }
    catch {
        return 'Invalid'
    }

    $keys = @($builder.get_Keys())
    if ($keys.Count -eq 0) {
        return 'Invalid'
    }

    $hasPostgresqlOnly = $false
    $hasSqlServerOnly = $false
    foreach ($key in $keys) {
        $isSqlServer = Test-SqlServerConnectionStringKeyword -Keyword $key
        $isPostgresql = $script:NpgsqlConnectionStringKeyword.Contains((Get-NormalizedConnectionStringKeyword -Keyword $key))
        if ($isPostgresql -and -not $isSqlServer) {
            $hasPostgresqlOnly = $true
        }
        elseif ($isSqlServer -and -not $isPostgresql) {
            $hasSqlServerOnly = $true
        }
    }

    if ($hasPostgresqlOnly -and $hasSqlServerOnly) {
        return 'Invalid'
    }
    if ($hasPostgresqlOnly) {
        return 'PostgreSql'
    }
    if ($hasSqlServerOnly) {
        return 'SqlServer'
    }
    return 'Ambiguous'
}

function Test-ConnectionStringMatchesEngine {
    <#
    .SYNOPSIS
        Returns $true when $ConnectionString is compatible with the already-selected engine
        ('postgresql'|'mssql'). An Ambiguous string (only shared aliases such as Server=/User Id=) matches
        either engine and is accepted for the selected one; a string that is definitively the other engine,
        or Invalid, is rejected. Engine detection is by keys only - it does NOT prove the target database.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
        [AllowEmptyString()][AllowNull()][string]$ConnectionString
    )
    $dialect = Resolve-ConnectionStringDialect -ConnectionString $ConnectionString
    if ($dialect -eq 'Invalid') {
        return $false
    }
    if ($dialect -eq 'Ambiguous') {
        return $true
    }
    $selectedDialect = if ($Engine -eq 'mssql') { 'SqlServer' } else { 'PostgreSql' }
    return ($dialect -eq $selectedDialect)
}

# === Provenance-tracking value resolution (single source & interpolation model) =====================
# Every resolved value carries a Source so provenance is set once and never re-inferred after merging:
#   Shell         - a process-environment (docker-compose shell-over-file) value, FINAL opaque text.
#   EnvFile       - an env-file value (quote-parsed and interpolated per Compose).
#   ComposeDefault- a compose-file ${VAR:-default} default.
#   Materialized  - a value the start script generated (never mistaken for a caller override).

function Get-ResolvedValue {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][ValidateSet('Shell', 'EnvFile', 'ComposeDefault', 'Materialized')][string]$Source
    )
    return [pscustomobject]@{ Value = $Value; Source = $Source }
}

function Resolve-EnvFileValueWithProvenance {
    <#
    .SYNOPSIS
        Resolves an ORIGINAL env-file value to a { Value; Source } record, modeling Compose exactly:
        single-quoted values are literal; a whole-value ${NAME} reference is interpolated through OTHER
        env-file values; but when a reference terminates at a process-environment (shell) value, that
        terminal is returned VERBATIM with Source='Shell' and is never re-parsed as env-file syntax; a bare
        ${NAME} to an unset variable is empty. Partial/embedded ${...} is rejected (as before).
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][hashtable]$EnvValues,
        [Parameter(Mandatory)][hashtable]$ProcessEnvironment,
        [System.Collections.Generic.HashSet[string]]$VisitedKeys
    )

    if (Test-EnvValueIsSingleQuoted -Value $Value) {
        return Get-ResolvedValue -Value (Get-NormalizedEnvValue -Value $Value) -Source 'EnvFile'
    }

    $referencedKey = Get-EnvValueReferenceKey -Value $Value
    if ($null -eq $referencedKey) {
        $normalized = Get-NormalizedEnvValue -Value $Value
        if ($normalized -match '\$\{') {
            throw "Environment value '$normalized' uses an unsupported environment expression. Use a literal value or a simple `${NAME} reference."
        }
        return Get-ResolvedValue -Value $normalized -Source 'EnvFile'
    }

    # A reference terminating at a shell value is that value VERBATIM (Compose substitutes a shell value as
    # final text; it is not re-interpolated or re-quote-parsed).
    if ($ProcessEnvironment.ContainsKey($referencedKey)) {
        return Get-ResolvedValue -Value ([string]$ProcessEnvironment[$referencedKey]) -Source 'Shell'
    }

    # Bare ${NAME} to an unset variable -> empty.
    if (-not $EnvValues.ContainsKey($referencedKey)) {
        return Get-ResolvedValue -Value '' -Source 'EnvFile'
    }

    if ($null -eq $VisitedKeys) {
        $VisitedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }
    if (-not $VisitedKeys.Add($referencedKey)) {
        throw "Environment reference '`${$referencedKey}' is cyclic."
    }
    try {
        return Resolve-EnvFileValueWithProvenance `
            -Value ([string]$EnvValues[$referencedKey]) `
            -EnvValues $EnvValues `
            -ProcessEnvironment $ProcessEnvironment `
            -VisitedKeys $VisitedKeys
    }
    finally {
        $null = $VisitedKeys.Remove($referencedKey)
    }
}

function Resolve-ComposeVariable {
    <#
    .SYNOPSIS
        Models a Compose ${Name:-Default} interpolation at shell-over-env-file precedence, returning
        { Value; Source }. A non-empty shell value (including whitespace-only) is used VERBATIM
        (Source='Shell'); an exactly-empty or unset value selects -Default when supplied (Source=
        'ComposeDefault'), matching ':-' (which treats unset OR exactly-empty, NOT whitespace, as default);
        otherwise the env-file value is resolved with provenance. Omit -Default to model a bare ${Name}
        (unset/empty -> empty).
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$EnvValues,
        [hashtable]$ProcessEnvironment,
        [string]$Default
    )

    $hasDefault = $PSBoundParameters.ContainsKey('Default')
    if ($null -eq $ProcessEnvironment) {
        $ProcessEnvironment = @{}
        foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
            $ProcessEnvironment[[string]$entry.Key] = [string]$entry.Value
        }
    }

    if ($ProcessEnvironment.ContainsKey($Name)) {
        $shellValue = [string]$ProcessEnvironment[$Name]
        if ([string]::IsNullOrEmpty($shellValue)) {
            if ($hasDefault) { return Get-ResolvedValue -Value $Default -Source 'ComposeDefault' }
            return Get-ResolvedValue -Value '' -Source 'Shell'
        }
        # Non-empty (whitespace-only included) shell value: verbatim, never trimmed/unquoted/re-interpolated.
        return Get-ResolvedValue -Value $shellValue -Source 'Shell'
    }

    if ($EnvValues.ContainsKey($Name)) {
        $resolved = Resolve-EnvFileValueWithProvenance -Value ([string]$EnvValues[$Name]) -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment
        if ([string]::IsNullOrEmpty($resolved.Value) -and $hasDefault) {
            return Get-ResolvedValue -Value $Default -Source 'ComposeDefault'
        }
        return $resolved
    }

    if ($hasDefault) { return Get-ResolvedValue -Value $Default -Source 'ComposeDefault' }
    return Get-ResolvedValue -Value '' -Source 'EnvFile'
}

function Resolve-EffectiveConfigRuntimeContract {
    <#
    .SYNOPSIS
        Computes the effective local Compose runtime contract for the Configuration Service exactly once,
        and enforces the cross-cutting agreement invariants. Every consumer (standalone/local/published
        startup, database readiness, OpenIddict init/insert, CMS connection materialization, datastore
        registration) reads its values from this single object instead of independently re-resolving them.

    .DESCRIPTION
        Given the engine the start script ACTUALLY selected (-InfrastructureEngine; drives the Compose DB
        file and OpenIddict), the raw env-file values, a process-environment snapshot, and the already
        computed effective configuration database name (-ConfigDatabaseName, from the topology resolution),
        this returns { InfrastructureEngine; CmsProviderEngine; CmsConnectionString{Value;Source};
        CmsDatabaseName; MssqlSaPassword{Value;Source}; OpenIddict{DbType;DbUser;DbPort;DbName;DbPassword};
        DatastoreDatabaseName; DatastoreConnectionString }.

        Invariants (fail-fast, before Docker):
          * CmsProviderEngine (AppSettings__Datastore = ${DMS_CONFIG_DATASTORE:-postgresql}, shell over
            file) MUST equal -InfrastructureEngine. A shell DMS_CONFIG_DATASTORE that differs - even when a
            paired shell connection agrees with it - cannot silently redirect only the Compose-facing
            provider while the start script starts the other engine.
          * The effective CMS connection string must be compatible with -InfrastructureEngine (per the
            selected provider, not a lossy heuristic) and target -ConfigDatabaseName.
          * The datastore-name key (POSTGRES_DB_NAME/MSSQL_DB_NAME) must resolve to the same database with
            shell precedence (containers) as from the env file (host-side tooling).

        Provenance: a shell value (direct or reference-terminal) is final opaque text; a value the script
        must generate because Compose would otherwise substitute the wrong-engine PostgreSQL fallback is
        Source='Materialized'.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'MssqlSaPasswordDefault', Justification = 'The SQL Server SA password default is the local docker-compose plaintext default (mssql.yml MSSQL_SA_PASSWORD:-abcdefgh1!); it is resolved and passed as a string throughout these compose scripts by design.')]
    param(
        [Parameter(Mandatory)][hashtable]$EnvValues,
        [hashtable]$ProcessEnvironment,
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$InfrastructureEngine,
        [Parameter(Mandatory)][string]$ConfigDatabaseName,
        [switch]$ConfigDatabaseNameMaterialized,
        [string]$MssqlSaPasswordDefault,
        [string]$DatastoreDatabaseName
    )

    if ($null -eq $ProcessEnvironment) {
        $ProcessEnvironment = @{}
        foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
            $ProcessEnvironment[[string]$entry.Key] = [string]$entry.Value
        }
    }
    $hasSaDefault = $PSBoundParameters.ContainsKey('MssqlSaPasswordDefault')

    # (1) CMS provider engine - the container's AppSettings__Datastore.
    $providerRaw = (Resolve-ComposeVariable -Name 'DMS_CONFIG_DATASTORE' -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment -Default 'postgresql').Value
    $cmsProviderEngine = if ([string]::Equals($providerRaw, 'mssql', [System.StringComparison]::OrdinalIgnoreCase)) { 'mssql' } else { 'postgresql' }

    # (2) Engine-agreement invariant (fail-fast).
    if ($cmsProviderEngine -ne $InfrastructureEngine) {
        throw "Configuration runtime-contract mismatch: the start script selected the '$InfrastructureEngine' infrastructure engine (which starts that Compose database file and initializes OpenIddict for it), but the Configuration Service provider DMS_CONFIG_DATASTORE resolves - with the shell environment applied at Docker Compose precedence - to '$cmsProviderEngine'. A shell DMS_CONFIG_DATASTORE override cannot silently point the Configuration Service at a different engine than the one that starts; unset it, or select that engine with -DatabaseEngine."
    }

    # (3) MSSQL SA password, modeling ${MSSQL_SA_PASSWORD:-<default>}.
    $mssqlSaPassword = $null
    if ($InfrastructureEngine -eq 'mssql') {
        $saArgs = @{ Name = 'MSSQL_SA_PASSWORD'; EnvValues = $EnvValues; ProcessEnvironment = $ProcessEnvironment }
        if ($hasSaDefault) { $saArgs.Default = $MssqlSaPasswordDefault }
        $mssqlSaPassword = Resolve-ComposeVariable @saArgs
        if ([string]::IsNullOrWhiteSpace($mssqlSaPassword.Value)) {
            throw "Configuration runtime-contract error: MSSQL_SA_PASSWORD resolves to a blank value on a SQL Server stack, so the Configuration Service connection and OpenIddict initialization cannot authenticate. Set MSSQL_SA_PASSWORD (or the variable it references), or unset an empty shell export."
        }
    }

    # (4) Effective CMS connection string ${DMS_CONFIG_DATABASE_CONNECTION_STRING:-<compose fallback>}.
    $connName = 'DMS_CONFIG_DATABASE_CONNECTION_STRING'
    $shellHasConn = $ProcessEnvironment.ContainsKey($connName)
    $shellConnValue = [string]$ProcessEnvironment[$connName]
    $connState = $null
    $forcedFallback = $false
    $fallbackForcedByShell = $false
    if ($shellHasConn -and -not [string]::IsNullOrEmpty($shellConnValue)) {
        # A non-empty shell value wins and is substituted verbatim (never re-interpolated).
        $connState = Get-ResolvedValue -Value $shellConnValue -Source 'Shell'
    }
    elseif ($shellHasConn -and [string]::IsNullOrEmpty($shellConnValue)) {
        # ':-' treats an exactly-empty shell value as unset -> Compose substitutes the fallback.
        $forcedFallback = $true
        $fallbackForcedByShell = $true
    }
    else {
        # An env-file connection string carries embedded ${...} references (Compose interpolates them at
        # shell-over-file precedence); it is NOT a whole-value reference, so keep it as the env-file value
        # and let the database-name check below resolve the embedded references against the merged view.
        $rawEnvConnectionString = [string](Get-EnvValue -EnvValues $EnvValues -Name $connName)
        if (-not [string]::IsNullOrEmpty($rawEnvConnectionString)) {
            $connState = Get-ResolvedValue -Value $rawEnvConnectionString -Source 'EnvFile'
        }
        else {
            $forcedFallback = $true
        }
    }

    if ($forcedFallback) {
        # The compose-file fallback (local-config.yml) is a hardcoded PostgreSQL connection whose database
        # is ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} at shell-over-file precedence.
        $fallbackDbState = Resolve-ComposeVariable -Name 'DMS_CONFIG_DATABASE_NAME' -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment `
            -Default (Resolve-ComposeVariable -Name 'POSTGRES_DB_NAME' -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment).Value
        $fallbackDatabase = $fallbackDbState.Value

        if ($InfrastructureEngine -eq 'mssql') {
            if ($fallbackForcedByShell) {
                throw "Configuration runtime-contract error: DMS_CONFIG_DATABASE_CONNECTION_STRING is exported empty on a SQL Server stack. Docker Compose's ':-' would substitute the compose-file fallback - a hardcoded PostgreSQL connection to dms-postgresql - instead of a SQL Server connection to '$ConfigDatabaseName'. Unset it to use the env-file connection string, or set a valid SQL Server connection."
            }
            # No connection string anywhere on a SQL Server stack: Compose would use the PostgreSQL fallback,
            # so materialize the SQL Server connection the running container actually needs.
            $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
            $builder['Server'] = 'dms-mssql,1433'
            $builder['Database'] = $ConfigDatabaseName
            $builder['User Id'] = 'sa'
            $builder['Password'] = $mssqlSaPassword.Value
            $builder['TrustServerCertificate'] = 'true'
            $connState = Get-ResolvedValue -Value $builder.ConnectionString -Source 'Materialized'
        }
        else {
            $pgPassword = (Resolve-ComposeVariable -Name 'POSTGRES_PASSWORD' -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment).Value
            $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
            $builder['host'] = 'dms-postgresql'
            $builder['port'] = '5432'
            $builder['username'] = 'postgres'
            $builder['password'] = $pgPassword
            $builder['database'] = $fallbackDatabase
            $connState = Get-ResolvedValue -Value $builder.ConnectionString -Source 'ComposeDefault'
        }
    }

    # (5) Validate the effective connection: engine-compatible and targeting the effective database.
    if (-not (Test-ConnectionStringMatchesEngine -Engine $InfrastructureEngine -ConnectionString $connState.Value)) {
        $runtimeDialect = Resolve-ConnectionStringDialect -ConnectionString $connState.Value
        throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING (source $($connState.Source)) is a $runtimeDialect connection, but the selected infrastructure engine is '$InfrastructureEngine'. The Configuration Service provider cannot use a connection of the wrong engine. Set a connection string for the selected engine, or unset the shell override to use the env-file value."
    }

    # Compose interpolates ${...} inside an env-file connection but substitutes a shell value verbatim, so a
    # shell connection's database is compared literally. In the full-stack lane DMS_CONFIG_DATABASE_NAME is
    # materialized to a literal; in the standalone lane it is not, so the merged view leaves the raw seam to
    # re-resolve with shell precedence (catching an override the connection routes through).
    $mergedValues = @{}
    foreach ($entry in $EnvValues.GetEnumerator()) { $mergedValues[[string]$entry.Key] = [string]$entry.Value }
    if ($ConfigDatabaseNameMaterialized) { $mergedValues['DMS_CONFIG_DATABASE_NAME'] = $ConfigDatabaseName }
    foreach ($entry in $ProcessEnvironment.GetEnumerator()) { $mergedValues[[string]$entry.Key] = [string]$entry.Value }

    $targetDatabases = @(Get-CmsConnectionStringDatabaseName -ConnectionString $connState.Value -EnvValues $mergedValues -DoNotResolveReferences:($connState.Source -eq 'Shell'))
    if ($targetDatabases.Count -eq 0) {
        throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING (source $($connState.Source)) targets no database (set Database or Initial Catalog), so the Configuration Service would connect to the engine default instead of the effective configuration database '$ConfigDatabaseName'."
    }
    foreach ($targetDatabase in $targetDatabases) {
        if (-not [string]::Equals($targetDatabase, $ConfigDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING (source $($connState.Source)) targets database '$targetDatabase', but the effective configuration database is '$ConfigDatabaseName'. Align the connection string (or the shell variable it routes through), or pass -SeparateConfigDatabase to select the dedicated configuration database."
        }
    }

    # (6) Datastore-name agreement: the containers (Compose, shell-over-file) and host-side tooling (env
    # file) must resolve the datastore key to the same database, in both topologies.
    $datastoreKey = if ($InfrastructureEngine -eq 'mssql') { 'MSSQL_DB_NAME' } else { 'POSTGRES_DB_NAME' }
    $fileDatastoreRaw = [string]$EnvValues[$datastoreKey]
    if (-not [string]::IsNullOrWhiteSpace($fileDatastoreRaw)) {
        $fileDatastore = (Resolve-EnvFileValueWithProvenance -Value $fileDatastoreRaw -EnvValues $EnvValues -ProcessEnvironment @{}).Value
        $runtimeDatastore = (Resolve-ComposeVariable -Name $datastoreKey -EnvValues $EnvValues -ProcessEnvironment $ProcessEnvironment).Value
        if (-not [string]::Equals($runtimeDatastore, $fileDatastore, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Configuration runtime-contract error: $datastoreKey resolves to '$runtimeDatastore' with the shell environment at Compose precedence but to '$fileDatastore' from the env file, so the containers would target one datastore database while host-side configuration and provisioning use another. Unset $datastoreKey in your shell, or align it with the env file."
        }
    }

    # (7) OpenIddict target (what host-side -InitDb/-InsertData must use).
    $openIddict = [pscustomobject]@{
        DbType     = if ($InfrastructureEngine -eq 'mssql') { 'MSSQL' } else { 'Postgresql' }
        DbUser     = if ($InfrastructureEngine -eq 'mssql') { 'sa' } else { 'postgres' }
        DbPort     = if ($InfrastructureEngine -eq 'mssql') { 'ENV:MSSQL_PORT' } else { 'ENV:POSTGRES_PORT' }
        DbName     = $ConfigDatabaseName
        DbPassword = if ($InfrastructureEngine -eq 'mssql') { $mssqlSaPassword.Value } else { $null }
    }

    # (8) DMS datastore connection (SQL Server registration lanes) when a datastore database is supplied.
    $datastoreConnectionString = $null
    if (-not [string]::IsNullOrWhiteSpace($DatastoreDatabaseName) -and $InfrastructureEngine -eq 'mssql') {
        $datastoreBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $datastoreBuilder['Server'] = 'dms-mssql,1433'
        $datastoreBuilder['Database'] = $DatastoreDatabaseName
        $datastoreBuilder['User Id'] = 'sa'
        $datastoreBuilder['Password'] = $mssqlSaPassword.Value
        $datastoreBuilder['TrustServerCertificate'] = 'true'
        $datastoreConnectionString = Get-ResolvedValue -Value $datastoreBuilder.ConnectionString -Source 'Materialized'
    }

    return [pscustomobject]@{
        InfrastructureEngine      = $InfrastructureEngine
        CmsProviderEngine         = $cmsProviderEngine
        CmsConnectionString       = $connState
        CmsDatabaseName           = $ConfigDatabaseName
        MssqlSaPassword           = $mssqlSaPassword
        OpenIddict                = $openIddict
        DatastoreDatabaseName     = $DatastoreDatabaseName
        DatastoreConnectionString = $datastoreConnectionString
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
        to empty (rather than throwing), so a caller modeling a ${VAR:-default} expression - e.g.
        Resolve-ComposeVariable for ${MSSQL_SA_PASSWORD:-abcdefgh1!} - can then apply its default.
        Cyclic and unsupported-expression references still throw. Off by default: an unresolved reference is
        a hard error for callers that require a concrete value.
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

function Get-CmsConnectionStringDatabaseName {
    <#
    .SYNOPSIS
        Extracts the target database name(s) from a CMS connection string for either engine and
        resolves any ${NAME} reference against the effective environment. Reads the Database and
        Initial Catalog keys (the SqlClient aliases) as well as the PostgreSQL Database/database
        key via a generic DbConnectionStringBuilder. Surrounding quotes are normalized per docker-compose
        quote semantics first (ReadValuesFromEnvFile preserves them; compose strips one pair), so a quoted
        .env value parses. Returns an array of resolved names (empty when the connection string carries no
        database key). Throws when the string cannot be parsed.

    .PARAMETER DoNotResolveReferences
        Returns the database value verbatim instead of expanding a ${NAME} reference. docker-compose
        interpolates ${...} inside an ENV-FILE value but substitutes a SHELL-provided value as final text
        (verified with `docker compose config`), so a caller validating a shell-exported connection string
        must pass this to model that the container receives the literal ${...}, not the resolved name.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues,

        [switch]$DoNotResolveReferences
    )

    # ReadValuesFromEnvFile preserves surrounding quotes, but docker-compose strips one surrounding single-
    # or double-quote pair from a .env value before the container uses it. Normalize the whole connection
    # string the same way before parsing, so a quoted-but-valid value (e.g. "host=...;database=x;") is not
    # rejected. docker-compose does not interpolate ${...} inside a SINGLE-quoted value, so a database
    # extracted from one is returned literally; unquoted and double-quoted values interpolate as usual.
    $connectionStringIsSingleQuoted = Test-EnvValueIsSingleQuoted -Value $ConnectionString
    $normalizedConnectionString = Get-NormalizedEnvValue -Value $ConnectionString

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($normalizedConnectionString)
    }
    catch {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING is not a valid connection string."
    }

    return @(
        foreach ($key in $builder.get_Keys()) {
            if ([string]$key -imatch '^(database|initial\s+catalog)$') {
                $databaseValue = [string]$builder.get_Item($key)
                if ($connectionStringIsSingleQuoted -or $DoNotResolveReferences) {
                    $databaseValue
                }
                else {
                    Resolve-EnvValueReference -Value $databaseValue -EnvValues $EnvValues
                }
            }
        }
    )
}

function Resolve-CmsConfigurationDatabaseName {
    <#
    .SYNOPSIS
        Resolves the Configuration Service database name from a caller-authored CMS connection string
        for the standalone Configuration Service lane (start-local-config.ps1), where the connection
        string - not a topology switch - is authoritative for the target database.

    .DESCRIPTION
        Returns $null when no connection string is supplied, so the caller may fall back to the
        environment default. When a connection string IS supplied it must yield exactly one concrete
        database name: an unparseable string throws from Get-CmsConnectionStringDatabaseName, and a
        database-less string (no Database / Initial Catalog) is rejected here. This fails the lane fast
        with a clear diagnostic rather than creating or initializing a default or mismatched database
        before the Configuration Service starts against a different target.

        Database and Initial Catalog are SqlClient synonyms; when both are present SqlClient uses the
        last-listed one. This returns that last-listed alias so OpenIddict initialization targets the
        same database the Configuration Service will connect to. Two aliases that resolve to different
        databases are ambiguous and fail fast rather than silently picking one.
    #>
    param(
        [AllowEmptyString()]
        [AllowNull()]
        [string]$ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $null
    }

    $databaseNames = @(
        Get-CmsConnectionStringDatabaseName -ConnectionString $ConnectionString -EnvValues $EnvValues |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($databaseNames.Count -eq 0) {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING must target a configuration database (set Database or Initial Catalog) so the Configuration Service database can be created and initialized before CMS starts."
    }

    $distinctNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($databaseName in $databaseNames) { [void]$distinctNames.Add($databaseName) }
    if ($distinctNames.Count -gt 1) {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING specifies conflicting database aliases ($($databaseNames -join ', ')). SqlClient would use the last-listed alias, so OpenIddict initialization and the Configuration Service could target different databases. Set a single Database (or Initial Catalog) so both target the same configuration database."
    }

    # SqlClient uses the last-listed alias when synonyms repeat; mirror that so OpenIddict and CMS agree.
    return $databaseNames[-1]
}

function Resolve-StandaloneCmsConfigurationDatabaseTarget {
    <#
    .SYNOPSIS
        Resolves the effective Configuration Service database target for the standalone lane
        (start-local-config.ps1), mirroring how docker-compose resolves the CMS connection:
        ${DMS_CONFIG_DATABASE_CONNECTION_STRING:-...;database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}};}.

    .DESCRIPTION
        Returns an object with the database name that OpenIddict must initialize AND that the process-
        environment guard must validate, so both agree with the database CMS actually connects to. It also
        returns the datastore key (when applicable) whose shell override would redirect the compose fallback.

        - When the env file sets a connection string, its target database is authoritative (an unparseable
          or database-less string throws via Resolve-CmsConfigurationDatabaseName).
        - Otherwise docker-compose uses the compose-file fallback, whose database is
          ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}. This returns that name - NOT setup-openiddict.ps1's
          POSTGRES_DB_NAME default, which ignores DMS_CONFIG_DATABASE_NAME. When the name comes from
          POSTGRES_DB_NAME (no DMS_CONFIG_DATABASE_NAME), DatastoreKey is POSTGRES_DB_NAME so the guard
          also rejects a shell POSTGRES_DB_NAME override that would redirect the fallback; when it comes from
          DMS_CONFIG_DATABASE_NAME, the guard's bare-name check already covers a shell override.

        Throws when none of the three can be determined.

    .PARAMETER EnvValues
        The env-file values for the standalone Configuration Service lane.
    #>
    param([Parameter(Mandatory)] [hashtable]$EnvValues)

    $fromConnectionString = Resolve-CmsConfigurationDatabaseName `
        -ConnectionString (Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING") `
        -EnvValues $EnvValues
    if (-not [string]::IsNullOrWhiteSpace($fromConnectionString)) {
        return [pscustomobject]@{ DatabaseName = $fromConnectionString; DatastoreKey = $null }
    }

    $configuredName = Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_NAME"
    if (-not [string]::IsNullOrWhiteSpace($configuredName)) {
        # When DMS_CONFIG_DATABASE_NAME is itself a whole-value ${KEY} reference (e.g. ${POSTGRES_DB_NAME}),
        # the compose fallback resolves through KEY, so a shell override of KEY would redirect CMS while
        # OpenIddict initializes the env-file value. Preserve KEY as the datastore key so the guard
        # validates it; a literal name needs no datastore key (the guard's shell-name check covers it).
        # Get-EnvValueReferenceKey unquotes with the same normalization Resolve-EnvValueReference uses to
        # expand the value, so a quoted "${KEY}" keeps its guard key instead of silently dropping it.
        $datastoreKey = Get-EnvValueReferenceKey -Value $configuredName
        return [pscustomobject]@{
            DatabaseName = (Resolve-EnvValueReference -Value $configuredName -EnvValues $EnvValues)
            DatastoreKey = $datastoreKey
        }
    }

    $datastoreName = Get-EnvValue -EnvValues $EnvValues -Name "POSTGRES_DB_NAME"
    if (-not [string]::IsNullOrWhiteSpace($datastoreName)) {
        return [pscustomobject]@{
            DatabaseName = (Resolve-EnvValueReference -Value $datastoreName -EnvValues $EnvValues)
            DatastoreKey = "POSTGRES_DB_NAME"
        }
    }

    throw "The standalone Configuration Service database cannot be determined. Set DMS_CONFIG_DATABASE_CONNECTION_STRING, DMS_CONFIG_DATABASE_NAME, or POSTGRES_DB_NAME in the environment file."
}

function Get-ProcessEnvironmentVariableSnapshot {
    <#
    .SYNOPSIS
        Captures the current state of a process environment variable - its value, or that it is unset -
        so Restore-ProcessEnvironmentVariable can later return it to exactly that state.

    .DESCRIPTION
        start-local-config.ps1 exports a materialized SQL Server DMS_CONFIG_DATABASE_CONNECTION_STRING into
        the process environment so docker-compose - which gives shell state precedence over --env-file -
        resolves the Configuration Service against the running SQL Server container. That process-level
        export outlives the script and would leak into a later invocation in the same shell, where
        Resolve-EffectiveConfigRuntimeContract honors an already-set connection string as a
        caller-authored override: reusing the prior database, carrying a SQL Server connection into a
        PostgreSQL run, or slipping past the database-name-only agreement guard when the names match.
        Snapshotting the prior state before the export lets the caller restore it in a finally block on
        every success, failure, and teardown path.

    .PARAMETER Name
        The environment variable to snapshot.

    .PARAMETER ProcessEnvironment
        The process-level environment as a name/value map. Defaults to the live process environment;
        injectable so tests capture a snapshot without depending on real shell state.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [hashtable]$ProcessEnvironment
    )

    if ($null -eq $ProcessEnvironment) {
        $wasSet = Test-Path -Path "Env:$Name"
        $originalValue = if ($wasSet) { (Get-Item -Path "Env:$Name").Value } else { $null }
    }
    else {
        $wasSet = $ProcessEnvironment.ContainsKey($Name)
        $originalValue = if ($wasSet) { [string]$ProcessEnvironment[$Name] } else { $null }
    }

    return [pscustomobject]@{
        Name          = $Name
        WasSet        = $wasSet
        OriginalValue = $originalValue
    }
}

function Restore-ProcessEnvironmentVariable {
    <#
    .SYNOPSIS
        Restores a process environment variable to the state captured by
        Get-ProcessEnvironmentVariableSnapshot: its prior value, or truly unset when it was not previously
        set. Idempotent, so it is safe to call from a finally block on any exit path.

    .DESCRIPTION
        When the variable was not previously set it is deleted with Remove-Item rather than
        [Environment]::SetEnvironmentVariable(name, $null), which can leave a BLANK value that downstream
        compose interpolation and the agreement guard would read as an empty override; otherwise the
        captured value is written back verbatim.

    .PARAMETER Snapshot
        The snapshot object returned by Get-ProcessEnvironmentVariableSnapshot.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot
    )

    if ($Snapshot.WasSet) {
        Set-Item -Path "Env:$($Snapshot.Name)" -Value $Snapshot.OriginalValue
    }
    else {
        Remove-Item -Path "Env:$($Snapshot.Name)" -ErrorAction SilentlyContinue
    }
}

function Assert-CmsConnectionStringTargetsConfigDatabase {
    <#
    .SYNOPSIS
        Engine-neutral guard that a caller-authored CMS connection string targets the effective
        configuration-database name. Fails early on an unparseable string, a missing database key,
        or a database target that conflicts with the effective name so CMS and self-contained
        OpenIddict cannot silently initialize different databases.

    .PARAMETER DoNotResolveReferences
        Compares the connection string's database value verbatim instead of expanding a ${NAME} reference.
        docker-compose substitutes a SHELL-provided connection string as final text without re-interpolating
        it, so a caller validating a shell-exported value must pass this - otherwise the guard would resolve
        a ${...} the container receives literally into a false match.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString,

        [Parameter(Mandatory)]
        [string]$ExpectedDatabaseName,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues,

        [switch]$DoNotResolveReferences
    )

    $databaseValues = Get-CmsConnectionStringDatabaseName -ConnectionString $ConnectionString -EnvValues $EnvValues -DoNotResolveReferences:$DoNotResolveReferences
    if ($databaseValues.Count -eq 0) {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING must include a database (Database or Initial Catalog) targeting the effective configuration database '$ExpectedDatabaseName'."
    }

    foreach ($actualDatabaseName in $databaseValues) {
        if (-not [string]::Equals(
            $actualDatabaseName,
            $ExpectedDatabaseName,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            throw "Configuration-database mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets '$actualDatabaseName', but the effective configuration database is '$ExpectedDatabaseName'. Align DMS_CONFIG_DATABASE_CONNECTION_STRING with DMS_CONFIG_DATABASE_NAME, or pass -SeparateConfigDatabase to select the dedicated configuration database."
        }
    }
}

function Assert-ConfigDatabaseProcessEnvironmentAgreement {
    <#
    .SYNOPSIS
        Extends the caller-authored configuration-database agreement guarantee to process-level
        (shell-exported) environment values, modeling docker-compose's shell-over-env-file
        interpolation precedence for every referenced variable - not only DMS_CONFIG_DATABASE_NAME.

    .DESCRIPTION
        The topology resolver materializes the effective configuration database into a derived env file
        that both docker-compose (via --env-file) and the host-side setup-openiddict.ps1 read. Docker
        Compose, however, gives the caller's shell environment precedence over --env-file when it
        interpolates ${...}, and it applies that precedence to EVERY variable. A stale or conflicting
        shell-exported value can therefore redirect the Configuration Service container - through any
        variable the connection string resolves (DMS_CONFIG_DATABASE_NAME or a custom key) - or the
        DMS datastore - through POSTGRES_DB_NAME / MSSQL_DB_NAME - while setup-openiddict.ps1 still
        initializes the file-derived database, a split-brain the file-only check cannot see.

        This guard reconstructs the environment view docker-compose would interpolate against - the
        env-file values with DMS_CONFIG_DATABASE_NAME pinned to the effective name (as the full-stack
        derived file materializes it; -ConfigDatabaseNameNotMaterialized skips the pin for the standalone
        lane, which hands docker-compose the raw env file), then the entire process environment overlaid so
        shell state wins for every key, including over the pinned name - and:
          * resolves the runtime CMS connection string (the shell value when exported, otherwise the
            env-file value) against that view and requires every database it targets to equal the
            effective configuration name; and
          * requires the runtime CMS connection string's engine (PostgreSQL vs SQL Server) to match the
            provider selected by DMS_CONFIG_DATASTORE (AppSettings__Datastore = ${DMS_CONFIG_DATASTORE:-postgresql},
            resolved at shell-over-file precedence), so a shell-exported connection of the wrong engine cannot
            be paired with a provider of the other engine even when the database name matches; and
          * when a datastore key is supplied, requires it to resolve to the same database whether read
            with shell precedence (docker-compose, for the container) or from the env file (host-side
            configuration, schema provisioning, and OpenIddict initialization) - in BOTH topologies, so a
            shell POSTGRES_DB_NAME / MSSQL_DB_NAME override cannot point the container at one database
            while the host provisions another.
        A shell-exported connection string is handled by exact ':-' semantics: a whitespace-only value is
        handed to the container verbatim and is always rejected, while an EXACTLY-empty value selects the
        compose-file fallback (a hardcoded PostgreSQL connection) - wrong on a SQL Server stack (rejected),
        and on PostgreSQL validated against the fallback's OWN resolved database
        (${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} at shell precedence), NOT the env-file connection
        compose ignores, so a shell DMS_CONFIG_DATABASE_NAME / POSTGRES_DB_NAME override that redirects the
        fallback is caught. A
        connection string that targets no
        database at all is likewise rejected (it would leave CMS on the engine default). When no
        connection string is resolvable, the guard falls back to comparing the bare shell-exported
        DMS_CONFIG_DATABASE_NAME against the effective name. It is a no-op when the process environment
        carries no conflicting override.

    .PARAMETER ExpectedDatabaseName
        The effective configuration database name the derived env file carries and setup-openiddict.ps1
        initializes.

    .PARAMETER EnvValues
        The effective (env-file) values: the interpolation base, and the source of the CMS connection
        string when the shell exports none.

    .PARAMETER ProcessEnvironment
        The process-level environment as a name/value map. Defaults to a snapshot of the current
        process environment; injectable so tests exercise the guard without mutating the real process
        environment.

    .PARAMETER DatastoreKey
        The datastore-name key (POSTGRES_DB_NAME or MSSQL_DB_NAME) whose value docker-compose resolves
        with shell precedence for the container while the host-side tooling (configure-local-data-store.ps1,
        schema provisioning, setup-openiddict.ps1) reads it from the env file. When supplied, the guard
        requires the two to agree in BOTH topologies: in shared mode the datastore is also the
        configuration database; in separate mode it is distinct but must still be internally consistent,
        or bootstrap provisions one database while the DMS container targets another.

    .PARAMETER ConfigDatabaseNameNotMaterialized
        Marks the standalone Configuration Service lane (start-local-config.ps1), which passes the RAW
        env file to docker-compose instead of a derived file with DMS_CONFIG_DATABASE_NAME materialized
        to a literal. It skips pinning DMS_CONFIG_DATABASE_NAME in the interpolation view, so a shell
        override flowing through a ${...} reference the raw file still carries is modeled and caught
        rather than hidden behind the pinned literal.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedDatabaseName,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues,

        [hashtable]$ProcessEnvironment,

        [AllowEmptyString()]
        [AllowNull()]
        [string]$DatastoreKey,

        [switch]$ConfigDatabaseNameNotMaterialized
    )

    if ($null -eq $ProcessEnvironment) {
        $ProcessEnvironment = @{}
        foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
            $ProcessEnvironment[[string]$entry.Key] = [string]$entry.Value
        }
    }

    # Reconstruct docker-compose's interpolation view: the env-file values, then the whole process
    # environment overlaid so shell state takes precedence for EVERY key. Only keys a value actually
    # references are consulted, so unrelated process variables are inert.
    #
    # DMS_CONFIG_DATABASE_NAME pinning: the full-stack lane materializes the effective name into a
    # derived env file docker-compose reads, so the interpolation view pins it to that literal
    # (otherwise a ${...} reference the base file still carries would wrongly re-resolve). The standalone
    # Configuration Service lane passes the RAW env file - it does NOT materialize a literal - so compose
    # re-resolves DMS_CONFIG_DATABASE_NAME (e.g. ${POSTGRES_DB_NAME}) with shell precedence;
    # -ConfigDatabaseNameNotMaterialized skips the pin so the connection-string check below models that
    # re-resolution and catches a shell override that flows through it. The process overlay is applied
    # after the pin either way, so a direct shell DMS_CONFIG_DATABASE_NAME override still wins.
    $mergedValues = @{}
    foreach ($entry in $EnvValues.GetEnumerator()) {
        $mergedValues[[string]$entry.Key] = [string]$entry.Value
    }
    if (-not $ConfigDatabaseNameNotMaterialized) {
        $mergedValues["DMS_CONFIG_DATABASE_NAME"] = $ExpectedDatabaseName
    }
    foreach ($entry in $ProcessEnvironment.GetEnumerator()) {
        $mergedValues[[string]$entry.Key] = [string]$entry.Value
    }

    # The Configuration Service provider is AppSettings__Datastore: ${DMS_CONFIG_DATASTORE:-postgresql}
    # (local-config.yml / published-config.yml), resolved at the same shell-over-file precedence. It - not
    # the connection string - selects whether the container runs the SQL Server or PostgreSQL provider, so
    # it is the authoritative engine signal (matching Resolve-EffectiveConfigRuntimeContract,
    # which reads DMS_CONFIG_DATASTORE) for both the empty-connection fallback check and the runtime
    # connection-engine check below. An unset or empty value resolves to the compose default, PostgreSQL.
    $configDatastore = Resolve-EnvValueReference -Value ([string]$mergedValues["DMS_CONFIG_DATASTORE"]) -EnvValues $mergedValues
    $providerIsSqlServer = [string]::Equals($configDatastore, "mssql", [System.StringComparison]::OrdinalIgnoreCase)

    # The runtime CMS connection string is authoritative: docker-compose resolves it (the shell value
    # when exported, otherwise the env-file value) against the same shell-over-file view below, so
    # whatever database it targets is the database the Configuration Service container connects to. This
    # naturally accounts for whichever variable a connection string routes through - the seam, the
    # datastore key, or a custom key - so a shell override that does not change the resolved database is
    # correctly treated as a no-op.
    $processConnectionString = [string]$ProcessEnvironment["DMS_CONFIG_DATABASE_CONNECTION_STRING"]
    $processDatabaseName = [string]$ProcessEnvironment["DMS_CONFIG_DATABASE_NAME"]

    # Docker Compose resolves the CMS connection as ${DMS_CONFIG_DATABASE_CONNECTION_STRING:-<compose
    # fallback>}, and the shell value wins over --env-file. A shell export is handled by exact ':-' semantics
    # (verified with `docker compose config`):
    #   * WHITESPACE-only is non-empty to ':-', so compose hands the Configuration Service that value verbatim
    #     (a malformed connection) - always rejected;
    #   * EXACTLY-EMPTY is treated as unset, so compose selects the compose-file fallback, a hardcoded
    #     PostgreSQL connection whose database resolves to ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} -
    #     the effective configuration database. On SQL Server the PostgreSQL fallback is the wrong engine, so
    #     it is rejected; on PostgreSQL the fallback's OWN resolved database is validated below (via
    #     $shellForcesFallback), NOT the env-file connection that compose ignores, so a shell
    #     DMS_CONFIG_DATABASE_NAME / POSTGRES_DB_NAME override that redirects the fallback is caught.
    if ($ProcessEnvironment.ContainsKey("DMS_CONFIG_DATABASE_CONNECTION_STRING")) {
        if (-not [string]::IsNullOrEmpty($processConnectionString) -and
            [string]::IsNullOrWhiteSpace($processConnectionString)) {
            throw "Configuration-database mismatch: the process environment exports a whitespace-only DMS_CONFIG_DATABASE_CONNECTION_STRING. Docker Compose's ':-' treats a whitespace value as set, so it would hand the Configuration Service that value verbatim instead of the env-file connection string - a malformed connection that does not target the effective configuration database '$ExpectedDatabaseName'. Unset DMS_CONFIG_DATABASE_CONNECTION_STRING in your shell to use the env-file connection string, or set it to a valid connection string."
        }
        elseif ([string]::IsNullOrEmpty($processConnectionString) -and $providerIsSqlServer) {
            throw "Configuration-database mismatch: the process environment exports an empty DMS_CONFIG_DATABASE_CONNECTION_STRING on a SQL Server stack (DMS_CONFIG_DATASTORE=mssql). Docker Compose's ':-' treats an empty value as unset and would substitute the compose-file fallback - a hardcoded PostgreSQL connection to dms-postgresql - instead of the SQL Server connection targeting the effective configuration database '$ExpectedDatabaseName'. Unset DMS_CONFIG_DATABASE_CONNECTION_STRING in your shell to use the env-file connection string, or set it to a valid SQL Server connection."
        }
        # A PostgreSQL empty export is handled below: compose uses the fallback (not the env-file connection),
        # so $shellForcesFallback validates the fallback's OWN resolved database against the effective name.
    }

    # When the shell exports an EXACTLY-empty connection string, docker-compose's ':-' substitutes the
    # compose-file fallback rather than the env-file connection. The SQL Server case already threw above;
    # for PostgreSQL, validate the fallback's OWN database - ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}
    # at shell-over-file precedence - not the env-file connection, which compose does not use. This catches a
    # shell DMS_CONFIG_DATABASE_NAME or POSTGRES_DB_NAME override that redirects the fallback even though the
    # env-file connection still targets the effective database (the split the env-file check cannot see).
    $shellForcesFallback = $ProcessEnvironment.ContainsKey("DMS_CONFIG_DATABASE_CONNECTION_STRING") -and
        [string]::IsNullOrEmpty($processConnectionString)
    if ($shellForcesFallback) {
        $fallbackNameReference = [string]$mergedValues["DMS_CONFIG_DATABASE_NAME"]
        $fallbackDatabase =
            if (-not [string]::IsNullOrEmpty($fallbackNameReference)) {
                Resolve-EnvValueReference -Value $fallbackNameReference -EnvValues $mergedValues
            }
            else {
                $fallbackDatastoreName = [string]$mergedValues["POSTGRES_DB_NAME"]
                if (-not [string]::IsNullOrWhiteSpace($fallbackDatastoreName)) {
                    Resolve-EnvValueReference -Value $fallbackDatastoreName -EnvValues $mergedValues
                }
                else {
                    ""
                }
            }
        if ([string]::IsNullOrWhiteSpace($fallbackDatabase)) {
            throw "Configuration-database mismatch: the process environment exports an empty DMS_CONFIG_DATABASE_CONNECTION_STRING, so docker-compose's ':-' substitutes the compose-file fallback, but its database (DMS_CONFIG_DATABASE_NAME, or POSTGRES_DB_NAME when that is unset) resolves to no usable database while setup-openiddict.ps1 initializes '$ExpectedDatabaseName'. Set DMS_CONFIG_DATABASE_NAME or POSTGRES_DB_NAME so the fallback resolves to '$ExpectedDatabaseName', or unset DMS_CONFIG_DATABASE_CONNECTION_STRING in your shell to use the env-file connection string."
        }
        if (-not [string]::Equals($fallbackDatabase, $ExpectedDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Configuration-database mismatch: the process environment exports an empty DMS_CONFIG_DATABASE_CONNECTION_STRING, so docker-compose's ':-' substitutes the compose-file fallback, whose database resolves at shell precedence to '$fallbackDatabase', but setup-openiddict.ps1 initializes '$ExpectedDatabaseName'. Docker Compose gives the shell environment precedence over --env-file, so a shell DMS_CONFIG_DATABASE_NAME or POSTGRES_DB_NAME override redirects the fallback even when the env-file connection still targets the effective database; unset or align the conflicting shell variable, or unset DMS_CONFIG_DATABASE_CONNECTION_STRING to use the env-file connection string."
        }
    }

    $connectionString =
        if (-not [string]::IsNullOrWhiteSpace($processConnectionString)) { $processConnectionString } else { [string](Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING") }

    # docker-compose interpolates ${...} inside an env-file connection string but hands a SHELL-exported
    # value to the Configuration Service as final text without re-interpolating it (verified with
    # `docker compose config`). When the shell value wins, extract its database LITERALLY so an unexpanded
    # ${...} the container would receive verbatim is compared as-is and fails fast, rather than being
    # resolved into a false match that violates the caller-connection agreement contract.
    $connectionStringIsShellExported = -not [string]::IsNullOrWhiteSpace($processConnectionString)

    if (-not $shellForcesFallback -and -not [string]::IsNullOrWhiteSpace($connectionString)) {
        # The Configuration Service provider (AppSettings__Datastore = ${DMS_CONFIG_DATASTORE:-postgresql})
        # and its connection string (DatabaseSettings__DatabaseConnection) are SEPARATE compose variables,
        # resolved independently at shell-over-file precedence, so the runtime connection string can carry a
        # different engine than the provider - a PostgreSQL connection on a SQL Server stack, or the reverse.
        # The database-name checks below still pass when the name matches, but the Configuration Service
        # cannot parse a connection of the wrong engine. Require the runtime connection's engine to match the
        # provider selected by DMS_CONFIG_DATASTORE, validating the engine as well as the database name. Keyed
        # off the provider (not the env-file connection string) so it also fires when the env file omits the
        # connection string and compose would fall back, and when a shell DMS_CONFIG_DATASTORE flips it.
        $runtimeIsSqlServer = Test-MssqlConnectionStringValue -ConnectionString $connectionString
        if ($providerIsSqlServer -ne $runtimeIsSqlServer) {
            $providerEngine = if ($providerIsSqlServer) { "SQL Server" } else { "PostgreSQL" }
            $runtimeEngine = if ($runtimeIsSqlServer) { "SQL Server" } else { "PostgreSQL" }
            throw "Configuration-database engine mismatch: DMS_CONFIG_DATASTORE selects the $providerEngine Configuration Service provider (AppSettings__Datastore), but the effective DMS_CONFIG_DATABASE_CONNECTION_STRING is a $runtimeEngine connection. Docker Compose resolves the provider and the connection string independently and gives the shell environment precedence over --env-file, so the Configuration Service would parse a $runtimeEngine connection with the $providerEngine provider - which cannot connect even when the database name matches. Set DMS_CONFIG_DATABASE_CONNECTION_STRING to a $providerEngine connection (or align DMS_CONFIG_DATASTORE in your shell), or unset it to use the env-file connection string."
        }

        try {
            $runtimeDatabaseNames = @(
                Get-CmsConnectionStringDatabaseName -ConnectionString $connectionString -EnvValues $mergedValues -DoNotResolveReferences:$connectionStringIsShellExported
            )
        }
        catch {
            throw "The process environment redirects DMS_CONFIG_DATABASE_CONNECTION_STRING away from the effective configuration database '$ExpectedDatabaseName'. Docker Compose gives the shell environment precedence over --env-file for every variable, so a shell-exported connection string, or a shell-exported variable it references, would move the Configuration Service away from the database setup-openiddict.ps1 initializes. Align or unset the conflicting shell variable. Underlying check: $($_.Exception.Message)"
        }

        # A connection string that targets no database at all would leave CMS on the engine default while
        # setup-openiddict.ps1 initializes the effective database - the same split-brain, so reject it
        # rather than pass (mirrors the env-file check and Resolve-CmsConfigurationDatabaseName).
        if ($runtimeDatabaseNames.Count -eq 0) {
            throw "Configuration-database mismatch: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING targets no database (set Database or Initial Catalog), so the Configuration Service would connect to the engine default instead of the effective configuration database '$ExpectedDatabaseName' that setup-openiddict.ps1 initializes. Docker Compose gives the shell environment precedence over --env-file, so a shell-exported connection string without a database causes this split-brain; set a database in it, or unset it to use the env-file connection string."
        }

        foreach ($runtimeDatabaseName in $runtimeDatabaseNames) {
            if (-not [string]::Equals($runtimeDatabaseName, $ExpectedDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
                $shadowingKeys = @(
                    foreach ($key in $EnvValues.Keys) {
                        $keyName = [string]$key
                        if ($ProcessEnvironment.ContainsKey($keyName) -and
                            -not [string]::Equals([string]$EnvValues[$keyName], [string]$ProcessEnvironment[$keyName], [System.StringComparison]::Ordinal)) {
                            $keyName
                        }
                    }
                )
                $shadowingKeys = @($shadowingKeys | Sort-Object)
                $shadowingHint = if ($shadowingKeys.Count -gt 0) { " Shell variables overriding the env file: $($shadowingKeys -join ', ')." } else { "" }
                throw "Configuration-database mismatch: with the shell environment applied at docker-compose precedence, DMS_CONFIG_DATABASE_CONNECTION_STRING resolves to '$runtimeDatabaseName', but the effective configuration database is '$ExpectedDatabaseName'.$shadowingHint Docker Compose gives the shell environment precedence over --env-file for every variable, so this would redirect the Configuration Service away from the database setup-openiddict.ps1 initializes. Unset or align the conflicting shell variable, or pass -SeparateConfigDatabase to select the dedicated configuration database."
            }
        }
    }
    elseif (-not $shellForcesFallback -and $ProcessEnvironment.ContainsKey("DMS_CONFIG_DATABASE_NAME")) {
        # No connection string is resolvable, so docker-compose builds the fallback connection whose
        # database is ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}. The shell value wins over --env-file.
        # Compose's ':-' substitutes the fallback ONLY when the value is unset or EXACTLY empty; a
        # whitespace-only value is non-empty to ':-' and is handed to the Configuration Service verbatim,
        # so the three cases are distinguished exactly rather than collapsed by IsNullOrWhiteSpace.
        if ([string]::IsNullOrEmpty($processDatabaseName)) {
            # Exactly empty: ':-' treats it as unset, so docker-compose falls back to POSTGRES_DB_NAME
            # (resolved with shell precedence) - which the file-only derivation does not see. Model that
            # fallback: it must resolve to a non-blank name equal to the effective database. A missing or
            # blank POSTGRES_DB_NAME leaves CMS with no usable database, so reject that rather than pass.
            $fallbackRaw = [string]$mergedValues["POSTGRES_DB_NAME"]
            $fallbackDatabase = if (-not [string]::IsNullOrWhiteSpace($fallbackRaw)) {
                Resolve-EnvValueReference -Value $fallbackRaw -EnvValues $mergedValues
            }
            else {
                ""
            }
            if ([string]::IsNullOrWhiteSpace($fallbackDatabase)) {
                throw "Configuration-database mismatch: the process environment exports an empty DMS_CONFIG_DATABASE_NAME and no usable POSTGRES_DB_NAME, so docker-compose's ':-' fallback would leave the Configuration Service without a database while setup-openiddict.ps1 initializes '$ExpectedDatabaseName'. Set POSTGRES_DB_NAME so the fallback resolves to '$ExpectedDatabaseName', or unset DMS_CONFIG_DATABASE_NAME in your shell to use the env-file value."
            }
            if (-not [string]::Equals($fallbackDatabase, $ExpectedDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Configuration-database mismatch: the process environment exports an empty DMS_CONFIG_DATABASE_NAME, so docker-compose's ':-' fallback resolves the Configuration Service database through POSTGRES_DB_NAME to '$fallbackDatabase', but setup-openiddict.ps1 initializes '$ExpectedDatabaseName'. Unset DMS_CONFIG_DATABASE_NAME in your shell (or align it with the effective name), and ensure POSTGRES_DB_NAME matches the env file."
            }
        }
        elseif ([string]::IsNullOrWhiteSpace($processDatabaseName)) {
            # Whitespace-only: ':-' does NOT treat it as empty, so docker-compose hands the Configuration
            # Service the whitespace value verbatim as its database target. That can never be the effective
            # configuration database, so reject it rather than model a fallback that never happens.
            throw "Configuration-database mismatch: the process environment sets DMS_CONFIG_DATABASE_NAME to a whitespace-only value, but the effective configuration database is '$ExpectedDatabaseName'. Docker Compose's ':-' substitutes the fallback only for an unset or empty value, so it would hand the Configuration Service the whitespace value verbatim as its database target. Unset DMS_CONFIG_DATABASE_NAME in your shell (or set it to '$ExpectedDatabaseName'), or pass -SeparateConfigDatabase to select the dedicated configuration database."
        }
        else {
            # A non-blank shell DMS_CONFIG_DATABASE_NAME is used verbatim.
            if (-not [string]::Equals($processDatabaseName, $ExpectedDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Configuration-database mismatch: the process environment sets DMS_CONFIG_DATABASE_NAME='$processDatabaseName', but the effective configuration database is '$ExpectedDatabaseName'. Docker Compose gives the shell environment precedence over --env-file, so this would redirect the Configuration Service away from the database setup-openiddict.ps1 initializes. Unset DMS_CONFIG_DATABASE_NAME in your shell (or align it with the effective name), or pass -SeparateConfigDatabase to select the dedicated configuration database."
            }
        }
    }

    # The datastore-name key must resolve to the same database whether docker-compose reads it
    # (shell-over-file, for the container) or the host-side tooling reads it (env file only, for
    # configure-local-data-store.ps1 / schema provisioning / setup-openiddict.ps1). A shell override of
    # POSTGRES_DB_NAME / MSSQL_DB_NAME would point the container at one database while the host provisions
    # another - true in BOTH topologies (the pinned DMS_CONFIG_DATABASE_NAME shields the CMS connection
    # string, but the datastore connection string still resolves through this key).
    if (-not [string]::IsNullOrWhiteSpace($DatastoreKey)) {
        $fileDatastoreRaw = [string]$EnvValues[$DatastoreKey]
        if (-not [string]::IsNullOrWhiteSpace($fileDatastoreRaw)) {
            $fileDatastore = Resolve-EnvValueReference -Value $fileDatastoreRaw -EnvValues $EnvValues
            $runtimeDatastore = Resolve-EnvValueReference -Value ([string]$mergedValues[$DatastoreKey]) -EnvValues $mergedValues
            if (-not [string]::Equals($runtimeDatastore, $fileDatastore, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Database-name mismatch: the process environment resolves $DatastoreKey to '$runtimeDatastore', but the env file resolves it to '$fileDatastore'. Docker Compose gives the shell environment precedence over --env-file, so the containers would target '$runtimeDatastore' while host-side configuration, schema provisioning, and OpenIddict initialization use the env-file value '$fileDatastore'. Unset $DatastoreKey in your shell, or align it with the env file."
            }
        }
    }
}

function Assert-MssqlCmsDatabaseIsShared {
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]$EnvValues
    )

    $expectedDatabaseName = Resolve-EnvValueReference `
        -Value (Get-EnvValue -EnvValues $EnvValues -Name "MSSQL_DB_NAME") `
        -EnvValues $EnvValues
    if ([string]::IsNullOrWhiteSpace($expectedDatabaseName)) {
        throw "MSSQL_DB_NAME must be non-blank when preserving a caller-authored CMS MSSQL connection string."
    }

    $databaseValues = Get-CmsConnectionStringDatabaseName -ConnectionString $ConnectionString -EnvValues $EnvValues
    if ($databaseValues.Count -eq 0) {
        throw "DMS_CONFIG_DATABASE_CONNECTION_STRING must include Database or Initial Catalog and target MSSQL_DB_NAME ('$expectedDatabaseName')."
    }

    foreach ($actualDatabaseName in $databaseValues) {
        if (-not [string]::Equals(
            $actualDatabaseName,
            $expectedDatabaseName,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            throw "MSSQL configuration-database mismatch: DMS_CONFIG_DATABASE_CONNECTION_STRING targets '$actualDatabaseName', but MSSQL_DB_NAME resolves to '$expectedDatabaseName'. In the shared topology CMS and self-contained OpenIddict use MSSQL_DB_NAME; align the values, or pass -SeparateConfigDatabase to use a dedicated configuration database."
        }
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

    .PARAMETER SkipMssqlCmsDatabaseValidation
        Skips this function's shared-topology MSSQL CMS/OpenIddict database invariant. It is left off
        only by the full-stack shared-topology start-script path, which owns that check. It is set by
        the datastore-only composition phases (configure, provision, and the bootstrap wrapper - they
        compose the overlay solely for DMS datastore settings and never read the CMS connection string,
        so the start script owns the CMS check), by database-only diagnostic startup and teardown, and
        by the start scripts in separate topology (where Resolve-ConfigDatabaseTopologyEnvironmentFile
        performs the topology-aware check instead).
    #>
    param(
        [string]$DatabaseEngine = "postgresql",
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [switch]$SkipMssqlCmsDatabaseValidation
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

    if ($baseDeclaresMssql -and -not $SkipMssqlCmsDatabaseValidation) {
        $baseCmsConnectionString = Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        if (Test-MssqlConnectionStringValue -ConnectionString $baseCmsConnectionString) {
            # Overlay values establish defaults; caller values then win, matching the actual
            # composition order used below. Validate the resulting shared-database identity before
            # either the idempotent return or partial-value preservation can accept this string.
            $effectiveMssqlValues = @{}
            foreach ($entry in $overlayValues.GetEnumerator()) {
                $effectiveMssqlValues[[string]$entry.Key] = [string]$entry.Value
            }
            foreach ($entry in $baseValues.GetEnumerator()) {
                $effectiveMssqlValues[[string]$entry.Key] = [string]$entry.Value
            }

            Assert-MssqlCmsDatabaseIsShared `
                -ConnectionString $baseCmsConnectionString `
                -EnvValues $effectiveMssqlValues
        }
    }

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
                ($isConnectionString -and -not (Test-MssqlConnectionStringValue -ConnectionString $baseValue))
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
                (-not $isConnectionString -or (Test-MssqlConnectionStringValue -ConnectionString $baseValue))
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
        DMS_CONFIG_DATABASE_CONNECTION_STRING interpolate. This function computes the effective
        name and materializes it as a concrete literal into a derived file under
        <DockerComposeRoot>/.derived/ so that both docker-compose interpolation and every host-side
        consumer (e.g. setup-openiddict.ps1) observe the same resolved value.

        Shared (default): the name resolves to the DMS datastore database for the engine
        (POSTGRES_DB_NAME or MSSQL_DB_NAME). Separate (-SeparateConfigDatabase): the name resolves
        to edfi_configurationservice without changing the DMS datastore selection.

        Idempotency: when the base file already carries the concrete effective name the base file is
        returned unchanged. The effective name is a pure function of -SeparateConfigDatabase, not of
        any name the base file already carries: without the switch it is always the datastore
        database, with it always edfi_configurationservice. Separate mode stays separate across
        re-resolution because every phase is passed the same switch (so a re-resolution with the
        switch supplied no-ops here), not by preserving an existing name.

        Validation: a caller-authored DMS_CONFIG_DATABASE_CONNECTION_STRING must target the effective
        name (references are resolved, and the SQL Server Database/Initial Catalog aliases and the
        PostgreSQL Database key are honored). Invalid, database-less, or conflicting connection
        strings fail before any derived file is written. Because docker-compose gives the caller's
        shell environment precedence over --env-file for every variable, the same agreement is also
        enforced against the process environment: a shell-exported value that would redirect the
        Configuration Service (through DMS_CONFIG_DATABASE_NAME, the connection string, or any variable
        it references) or the DMS datastore (through POSTGRES_DB_NAME / MSSQL_DB_NAME, in both shared and
        separate topology) fails fast (see Assert-ConfigDatabaseProcessEnvironmentAgreement).

    .PARAMETER BaseEnvironmentFile
        Absolute path to the (engine-composed) base env file. Must exist.

    .PARAMETER DockerComposeRoot
        Directory holding the .derived output. Defaults to this module's directory.

    .PARAMETER DatabaseEngine
        "postgresql" (default) or "mssql"; selects the datastore-name key for the shared default.

    .PARAMETER SeparateConfigDatabase
        Selects the dedicated edfi_configurationservice configuration database.

    .PARAMETER SkipCmsDatabaseValidation
        Skips both caller-authored agreement checks - the env-file connection-string check and the
        process-environment (shell-exported) precedence guard - only for a database-only diagnostic
        startup or teardown, where neither CMS nor OpenIddict is initialized.
    #>
    param(
        [Parameter(Mandatory)] [string]$BaseEnvironmentFile,
        [string]$DockerComposeRoot,
        [string]$DatabaseEngine = "postgresql",
        [switch]$SeparateConfigDatabase,
        [switch]$SkipCmsDatabaseValidation
    )

    if ([string]::IsNullOrWhiteSpace($DockerComposeRoot)) {
        $DockerComposeRoot = $PSScriptRoot
    }

    $separateConfigDatabaseName = "edfi_configurationservice"

    $baseValues = ReadValuesFromEnvFile $BaseEnvironmentFile

    # Shared-topology default: the DMS datastore database for the selected engine.
    $datastoreKey = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    $datastoreRaw = Get-EnvValue -EnvValues $baseValues -Name $datastoreKey
    $datastoreName = ""
    if (-not [string]::IsNullOrWhiteSpace($datastoreRaw)) {
        $datastoreName = Resolve-EnvValueReference -Value $datastoreRaw -EnvValues $baseValues
    }

    # The concrete DMS_CONFIG_DATABASE_NAME the base file already carries (used only for the
    # idempotent no-op below).
    $existingRaw = Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATABASE_NAME"

    # The topology is a pure function of the switch: with -SeparateConfigDatabase the effective name
    # is the dedicated configuration database; without it the effective name is always the DMS
    # datastore database, regardless of any separate or custom DMS_CONFIG_DATABASE_NAME the base file
    # already carries. Idempotency in separate mode comes from forwarding the switch to every phase
    # (so a re-resolution stays separate and no-ops below), not from preserving an existing name.
    if ($SeparateConfigDatabase) {
        $effectiveName = $separateConfigDatabaseName
    }
    else {
        $effectiveName = $datastoreName
    }

    if ([string]::IsNullOrWhiteSpace($effectiveName)) {
        throw "Resolve-ConfigDatabaseTopologyEnvironmentFile: could not determine the effective configuration database name ('$datastoreKey' is blank and DMS_CONFIG_DATABASE_NAME is unset)."
    }

    if (-not $SkipCmsDatabaseValidation) {
        $cmsConnectionString = Get-EnvValue -EnvValues $baseValues -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        if (-not [string]::IsNullOrWhiteSpace($cmsConnectionString)) {
            # Resolve the connection string with the effective name in scope so a
            # ${DMS_CONFIG_DATABASE_NAME} reference agrees by construction, while a caller literal
            # or a reference to a different key is validated against the effective name.
            $validationValues = @{}
            foreach ($entry in $baseValues.GetEnumerator()) {
                $validationValues[[string]$entry.Key] = [string]$entry.Value
            }
            $validationValues["DMS_CONFIG_DATABASE_NAME"] = $effectiveName

            Assert-CmsConnectionStringTargetsConfigDatabase `
                -ConnectionString $cmsConnectionString `
                -ExpectedDatabaseName $effectiveName `
                -EnvValues $validationValues
        }

        # Docker Compose gives the caller's shell environment precedence over --env-file for every
        # variable, so a stale or conflicting shell-exported value could redirect the Configuration
        # Service - via DMS_CONFIG_DATABASE_NAME, the connection string, or any variable it references -
        # or the DMS datastore - via POSTGRES_DB_NAME / MSSQL_DB_NAME - while the host-side tooling reads
        # the env file. Extend the agreement guarantee to that process source. The datastore key is passed
        # in BOTH topologies: in shared mode it is also the configuration database; in separate mode it is
        # distinct but must still be internally consistent, or configure/provision one database while the
        # DMS container targets another.
        $processGuardArgs = @{
            ExpectedDatabaseName = $effectiveName
            EnvValues            = $baseValues
            DatastoreKey         = $datastoreKey
        }
        Assert-ConfigDatabaseProcessEnvironmentAgreement @processGuardArgs
    }

    # Idempotent no-op when the base file already carries the concrete effective name.
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
