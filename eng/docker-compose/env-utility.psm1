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

function Test-SqlServerConnectionString {
    <#
    .SYNOPSIS
        Returns $true when $ConnectionString parses cleanly under the built-in
        System.Data.SqlClient.SqlConnectionStringBuilder, i.e. it carries no PostgreSQL-exclusive keyword
        (Host=, Username=, ...). Used ONLY by the MSSQL engine-overlay composition to decide whether a
        base-file connection string is already SQL Server-shaped (keep it) or a PostgreSQL value to be
        replaced by the overlay. This is a shape check for overlay composition; it is NOT engine
        inference for the runtime contract, which is never inferred - the engine is given.
    #>
    param([AllowEmptyString()][AllowNull()][string]$ConnectionString)
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $false
    }
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($ConnectionString)
        return $true
    }
    catch {
        return $false
    }
}

function Get-CmsConnectionStringDatabaseName {
    <#
    .SYNOPSIS
        Extracts the target database name(s) from an ALREADY-RESOLVED connection string, parsing in the
        selected provider's context. No interpolation or reference expansion happens here: the value is
        the final text Docker Compose produced.

    .DESCRIPTION
        For 'mssql' the built-in SqlConnectionStringBuilder is authoritative - it recognizes every
        SqlClient alias, collapses the Database / Initial Catalog synonyms with last-wins semantics
        (exactly as the driver does at runtime), and THROWS on a PostgreSQL-exclusive keyword such as
        Host= (a wrong-engine string). For 'postgresql' the generic DbConnectionStringBuilder parses the
        final string and the Npgsql database aliases {database, db} are read; a SqlClient-exclusive
        'initial catalog' key marks a wrong-engine string and is rejected. Returns the distinct database
        name(s), or an empty array when the string carries no database key. Throws when the string cannot
        be parsed under the selected engine.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$ConnectionString
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return @()
    }

    if ($Engine -eq 'mssql') {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
        try {
            $builder.set_ConnectionString($ConnectionString)
        }
        catch {
            throw "The effective Configuration Service connection string is not a valid SQL Server connection for the selected 'mssql' engine ($($_.Exception.Message))."
        }
        if ([string]::IsNullOrEmpty($builder.InitialCatalog)) {
            return @()
        }
        return @($builder.InitialCatalog)
    }

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($ConnectionString)
    }
    catch {
        throw "The effective Configuration Service connection string is not a valid connection for the selected 'postgresql' engine ($($_.Exception.Message))."
    }

    $names = [System.Collections.Generic.List[string]]::new()
    $hasSqlServerOnlyCatalog = $false
    foreach ($key in $builder.get_Keys()) {
        $normalizedKey = ([string]$key).ToLowerInvariant() -replace '\s', ''
        if ($normalizedKey -eq 'initialcatalog') {
            $hasSqlServerOnlyCatalog = $true
        }
        elseif ($normalizedKey -eq 'database' -or $normalizedKey -eq 'db') {
            $names.Add([string]$builder.get_Item($key))
        }
    }
    if ($hasSqlServerOnlyCatalog) {
        throw "The effective Configuration Service connection string uses the SQL Server-only 'Initial Catalog' keyword, but the selected engine is 'postgresql'. Use the PostgreSQL 'Database' keyword, or select the SQL Server engine."
    }

    $distinct = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $names) {
        if ($seen.Add($name)) {
            $distinct.Add($name)
        }
    }
    return @($distinct)
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

        Returns a record { Provider; CmsConnectionString; MssqlSaPassword } read from the resolved
        Configuration Service (config) and database (db) services. A field is $null when its service or
        key is absent from the composed set (e.g. the datastore-only lane composes no config service).

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
    #>
    param(
        [Parameter(Mandatory)][string[]]$ComposeFiles,
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string]$ProjectName,
        [string]$ConfigServiceName = "config",
        [string]$DbServiceName = "db"
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
    $services = $parsed.services
    if ($null -ne $services) {
        if ($services.PSObject.Properties.Name -contains $ConfigServiceName) {
            $configEnvironment = $services.$ConfigServiceName.environment
        }
        if ($services.PSObject.Properties.Name -contains $DbServiceName) {
            $dbEnvironment = $services.$DbServiceName.environment
        }
    }

    return [pscustomobject]@{
        Provider            = Get-ComposeEnvironmentValue -EnvironmentObject $configEnvironment -Key "AppSettings__Datastore"
        CmsConnectionString = Get-ComposeEnvironmentValue -EnvironmentObject $configEnvironment -Key "DatabaseSettings__DatabaseConnection"
        MssqlSaPassword     = Get-ComposeEnvironmentValue -EnvironmentObject $dbEnvironment -Key "MSSQL_SA_PASSWORD"
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
        inferred from a connection string. The contract enforces, all fail-fast and before any Docker or
        Keycloak mutation:
          * the effective provider (Compose-resolved AppSettings__Datastore) is EXACTLY 'postgresql' or
            'mssql' and equals -InfrastructureEngine (a shell DMS_CONFIG_DATASTORE cannot point CMS at a
            different engine than the one that starts);
          * the effective CMS connection string parses under the selected provider's own builder (a
            wrong-engine string is rejected by the builder, not by keyword classification) and targets a
            concrete database - the effective configuration database when -ConfigDatabaseName is supplied
            (full-stack lanes), or the connection's own single target when it is not (standalone lane);
          * on SQL Server, the SA password resolves to a non-blank value;
          * a shell datastore-name override (POSTGRES_DB_NAME / MSSQL_DB_NAME) agrees with the env file,
            so the containers and host-side tooling target the same datastore database.

        Provider, connection string, and SA password are passed in as the values Compose resolved
        (-ResolvedProvider / -ResolvedCmsConnectionString / -ResolvedMssqlSaPassword), so this function is
        pure and unit-testable without invoking Docker; the start scripts obtain them from
        Get-ComposeResolvedConfiguration.

    .PARAMETER InfrastructureEngine
        The engine the start script actually selected ('postgresql' | 'mssql'); drives the Compose
        database file and OpenIddict, and is the authoritative engine.

    .PARAMETER ResolvedProvider
        The Compose-resolved AppSettings__Datastore value.

    .PARAMETER ResolvedCmsConnectionString
        The Compose-resolved DatabaseSettings__DatabaseConnection value (final text the container receives).

    .PARAMETER ResolvedMssqlSaPassword
        The Compose-resolved db-service MSSQL_SA_PASSWORD value (SQL Server stacks).

    .PARAMETER ConfigDatabaseName
        The effective configuration database name the full-stack lanes materialized (the connection must
        target it). Omit for the standalone lane, where the resolved connection's single target is the
        effective name.

    .PARAMETER DatastoreDatabaseName
        The DMS datastore database name, used to build the SQL Server datastore registration connection.

    .PARAMETER EnvValues
        Env-file values, for the datastore-name agreement check.

    .PARAMETER ProcessEnvironment
        Process-level environment as a name/value map, for the datastore-name agreement check. Defaults
        to a snapshot of the current process environment; injectable so tests exercise the check without
        mutating the real process environment.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'ResolvedMssqlSaPassword', Justification = 'The SQL Server SA password is the local docker-compose plaintext value resolved by Compose (mssql.yml MSSQL_SA_PASSWORD); it is passed as a string throughout these compose scripts by design.')]
    param(
        [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$InfrastructureEngine,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$ResolvedProvider,
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$ResolvedCmsConnectionString,
        [AllowEmptyString()][AllowNull()][string]$ResolvedMssqlSaPassword,
        [AllowEmptyString()][AllowNull()][string]$ConfigDatabaseName,
        [AllowEmptyString()][AllowNull()][string]$DatastoreDatabaseName,
        [hashtable]$EnvValues,
        [hashtable]$ProcessEnvironment
    )

    # (1) Provider is an EXACT enum, read from the Compose-resolved AppSettings__Datastore. Only the two
    # supported engines are accepted (case-insensitively, no surrounding whitespace); anything else -
    # 'mysql', blank, ' mssql ' - fails fast rather than being coerced, because Compose passes the raw
    # value straight to the Configuration Service.
    $providerCanonical =
        if ([string]::Equals($ResolvedProvider, 'mssql', [System.StringComparison]::OrdinalIgnoreCase)) { 'mssql' }
        elseif ([string]::Equals($ResolvedProvider, 'postgresql', [System.StringComparison]::OrdinalIgnoreCase)) { 'postgresql' }
        else { $null }
    if ($null -eq $providerCanonical) {
        throw "Configuration runtime-contract error: the effective Configuration Service provider (AppSettings__Datastore, resolved by Docker Compose) is '$ResolvedProvider', which is not a supported engine. Set DMS_CONFIG_DATASTORE to exactly 'postgresql' or 'mssql'."
    }

    # (2) Provider MUST equal the infrastructure engine the start script selected (which starts that
    # Compose database file and initializes OpenIddict for it). A shell DMS_CONFIG_DATASTORE that differs
    # cannot silently point the Configuration Service at a different engine than the one that starts.
    if ($providerCanonical -ne $InfrastructureEngine) {
        throw "Configuration runtime-contract mismatch: the start script selected the '$InfrastructureEngine' infrastructure engine, but the effective Configuration Service provider (AppSettings__Datastore, resolved by Docker Compose at shell-over-env-file precedence) is '$providerCanonical'. Unset the conflicting DMS_CONFIG_DATASTORE shell override, or select that engine with -DatabaseEngine."
    }

    # (3) SQL Server SA password: Compose already resolved ${MSSQL_SA_PASSWORD:-<default>} at
    # shell-over-file precedence. A blank value cannot authenticate CMS or OpenIddict.
    $mssqlSaPassword = $null
    if ($providerCanonical -eq 'mssql') {
        $mssqlSaPassword = $ResolvedMssqlSaPassword
        if ([string]::IsNullOrWhiteSpace($mssqlSaPassword)) {
            throw "Configuration runtime-contract error: MSSQL_SA_PASSWORD resolves to a blank value on a SQL Server stack, so the Configuration Service connection and OpenIddict initialization cannot authenticate. Set MSSQL_SA_PASSWORD (or the variable it references)."
        }
    }

    # (4) The effective CMS connection must be present, parse under the selected provider (wrong-engine
    # strings are rejected by the provider's own builder), and target a concrete database.
    if ([string]::IsNullOrWhiteSpace($ResolvedCmsConnectionString)) {
        throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING (resolved by Docker Compose) is empty on a '$InfrastructureEngine' stack. On a SQL Server stack this occurs when no connection string is set and Compose would substitute the PostgreSQL-only compose-file fallback. Set a '$InfrastructureEngine' connection string targeting the effective configuration database."
    }
    $targetDatabases = @(Get-CmsConnectionStringDatabaseName -Engine $InfrastructureEngine -ConnectionString $ResolvedCmsConnectionString)
    if ($targetDatabases.Count -eq 0) {
        throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING targets no database (set Database or Initial Catalog), so the Configuration Service would connect to the engine default instead of the effective configuration database."
    }

    # (5) Effective configuration database name. Full-stack lanes pass -ConfigDatabaseName (materialized by
    # the topology resolver) and the connection MUST target it. The standalone lane omits it, so the
    # resolved connection is authoritative and its single target IS the effective name.
    $effectiveDatabaseName = $null
    if (-not [string]::IsNullOrWhiteSpace($ConfigDatabaseName)) {
        foreach ($target in $targetDatabases) {
            if (-not [string]::Equals($target, $ConfigDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Configuration runtime-contract error: the effective DMS_CONFIG_DATABASE_CONNECTION_STRING targets database '$target', but the effective configuration database is '$ConfigDatabaseName'. Align the connection string (or the shell variable it routes through), or pass -SeparateConfigDatabase to select the dedicated configuration database."
            }
        }
        $effectiveDatabaseName = $ConfigDatabaseName
    }
    else {
        $distinctTargets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
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

    # (6) Datastore-name agreement: a shell POSTGRES_DB_NAME / MSSQL_DB_NAME override points the containers
    # at one database while host-side tooling (configure / provision / OpenIddict) reads the env file.
    # Compose substitutes a shell value verbatim, so compare the shell value directly to the env-file value.
    if ($null -eq $ProcessEnvironment) {
        $ProcessEnvironment = @{}
        foreach ($entry in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
            $ProcessEnvironment[[string]$entry.Key] = [string]$entry.Value
        }
    }
    if ($null -ne $EnvValues) {
        $datastoreKey = if ($InfrastructureEngine -eq 'mssql') { 'MSSQL_DB_NAME' } else { 'POSTGRES_DB_NAME' }
        if ($ProcessEnvironment.ContainsKey($datastoreKey) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$datastoreKey])) {
            $fileDatastore = Resolve-EnvValueReference -Value ([string]$EnvValues[$datastoreKey]) -EnvValues $EnvValues
            $shellDatastore = [string]$ProcessEnvironment[$datastoreKey]
            if (-not [string]::Equals($shellDatastore, $fileDatastore, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Configuration runtime-contract error: $datastoreKey resolves to '$shellDatastore' from the shell (which Docker Compose gives precedence for the containers) but to '$fileDatastore' from the env file (which host-side configuration and provisioning read). Unset $datastoreKey in your shell, or align it with the env file."
            }
        }
    }

    # (7) OpenIddict host-side target and (8) datastore registration connection are built from the EXPLICIT
    # engine and the resolved name/password - never inferred from a string.
    $openIddict = [pscustomobject]@{
        DbType     = if ($InfrastructureEngine -eq 'mssql') { 'MSSQL' } else { 'Postgresql' }
        DbUser     = if ($InfrastructureEngine -eq 'mssql') { 'sa' } else { 'postgres' }
        DbPort     = if ($InfrastructureEngine -eq 'mssql') { 'ENV:MSSQL_PORT' } else { 'ENV:POSTGRES_PORT' }
        DbName     = $effectiveDatabaseName
        DbPassword = if ($InfrastructureEngine -eq 'mssql') { $mssqlSaPassword } else { $null }
    }

    $datastoreConnectionString = $null
    if ($InfrastructureEngine -eq 'mssql' -and -not [string]::IsNullOrWhiteSpace($DatastoreDatabaseName)) {
        $datastoreBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $datastoreBuilder['Server'] = 'dms-mssql,1433'
        $datastoreBuilder['Database'] = $DatastoreDatabaseName
        $datastoreBuilder['User Id'] = 'sa'
        $datastoreBuilder['Password'] = $mssqlSaPassword
        $datastoreBuilder['TrustServerCertificate'] = 'true'
        $datastoreConnectionString = $datastoreBuilder.ConnectionString
    }

    return [pscustomobject]@{
        InfrastructureEngine      = $InfrastructureEngine
        Provider                  = $providerCanonical
        CmsConnectionString       = $ResolvedCmsConnectionString
        CmsDatabaseName           = $effectiveDatabaseName
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
