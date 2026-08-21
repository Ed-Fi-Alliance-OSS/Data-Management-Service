# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Script-level checks for the endpoint-binding helpers in invoke-traditional-baseline.ps1.

.DESCRIPTION
    Parses the capture wrapper, extracts its pure helper functions, and exercises endpoint
    resolution and connection-string endpoint rewriting against known-good and failure
    inputs. Touches no docker daemon and no database. Throws (nonzero exit) on the first
    failed check.

.EXAMPLE
    pwsh -NoProfile -File ./eng/performance/test-traditional-baseline-endpoint-binding.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$wrapperPath = Join-Path $PSScriptRoot 'invoke-traditional-baseline.ps1'
$parseErrors = $null
$wrapperAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $wrapperPath, [ref] $null, [ref] $parseErrors)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    throw "The wrapper does not parse: $($parseErrors -join '; ')"
}

# The helpers under test are pure (no docker, no environment access), so extracting the
# function definitions from the wrapper exercises exactly the committed code without
# running the wrapper's mandatory-parameter entry point.
$helperNames = @(
    'Resolve-ContainerEndpointFromPortBindingJson',
    'ConvertTo-PostgresEndpointPinnedConnectionString',
    'ConvertTo-MssqlEndpointPinnedConnectionString'
)
$functionAsts = $wrapperAst.FindAll(
    { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
foreach ($helperName in $helperNames) {
    $helperAst = $functionAsts | Where-Object { $_.Name -eq $helperName }
    if (-not $helperAst) {
        throw "Helper function '$helperName' was not found in the wrapper."
    }
    . ([scriptblock]::Create($helperAst.Extent.Text))
}

$script:checkCount = 0

function Assert-Check {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Description
    )
    if (-not $Condition) {
        throw "FAILED: $Description"
    }
    $script:checkCount++
    Write-Output "ok: $Description"
}

function Assert-Throw {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Action,
        [Parameter(Mandatory = $true)][string] $Description
    )
    $threw = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $threw = $true
    }
    Assert-Check -Condition $threw -Description $Description
}

function Get-ParsedConnectionString {
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    # PSBase reaches the real ConnectionString property: PowerShell adapts this type
    # through its type descriptor, which exposes keywords as properties instead.
    $builder.PSBase.ConnectionString = $ConnectionString
    return $builder
}

# --- Endpoint resolution from docker port-binding JSON ---

$endpoint = Resolve-ContainerEndpointFromPortBindingJson `
    -PortBindingJson '{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":"5435"}]}' `
    -ContainerPort '5432/tcp' -ContainerName 'pg'
Assert-Check ($endpoint.Host -eq '127.0.0.1') 'explicit loopback bind is used verbatim'
Assert-Check ($endpoint.Port -eq 5435) 'published host port is resolved'

$endpoint = Resolve-ContainerEndpointFromPortBindingJson `
    -PortBindingJson '{"1433/tcp":[{"HostIp":"0.0.0.0","HostPort":"14333"},{"HostIp":"::","HostPort":"14333"}]}' `
    -ContainerPort '1433/tcp' -ContainerName 'ms'
Assert-Check ($endpoint.Host -eq 'localhost') 'IPv4 wildcard bind normalizes to localhost'
Assert-Check ($endpoint.Port -eq 14333) 'first published binding wins'

$endpoint = Resolve-ContainerEndpointFromPortBindingJson `
    -PortBindingJson '{"1433/tcp":[{"HostIp":"::","HostPort":"14333"}]}' `
    -ContainerPort '1433/tcp' -ContainerName 'ms'
Assert-Check ($endpoint.Host -eq 'localhost') 'IPv6 wildcard bind normalizes to localhost'

$endpoint = Resolve-ContainerEndpointFromPortBindingJson `
    -PortBindingJson '{"5432/tcp":[{"HostIp":"","HostPort":"5435"}]}' `
    -ContainerPort '5432/tcp' -ContainerName 'pg'
Assert-Check ($endpoint.Host -eq 'localhost') 'blank host bind normalizes to localhost'

Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson '' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'empty port-binding JSON is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson 'null' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'null port-binding JSON is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson '{}' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'missing container port is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson '{"5432/tcp":null}' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'exposed-but-unpublished container port is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson '{"5432/tcp":[]}' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'empty binding list is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson `
        -PortBindingJson '{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":"abc"}]}' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'non-numeric host port is refused'
Assert-Throw { Resolve-ContainerEndpointFromPortBindingJson `
        -PortBindingJson '{"5432/tcp":[{"HostIp":"127.0.0.1","HostPort":"0"}]}' `
        -ContainerPort '5432/tcp' -ContainerName 'pg' } `
    'out-of-range host port is refused'

# --- PostgreSQL connection-string endpoint rewriting ---

$rewritten = ConvertTo-PostgresEndpointPinnedConnectionString `
    -ConnectionString 'host=elsewhere;port=9999;username=postgres;password=secret;database=edfi;pooling=true' `
    -EndpointHost '127.0.0.1' -EndpointPort 5435
$parsed = Get-ParsedConnectionString -ConnectionString $rewritten
Assert-Check ([string]$parsed['host'] -eq '127.0.0.1') 'postgres host is rewritten to the container binding'
Assert-Check ([string]$parsed['port'] -eq '5435') 'postgres port is rewritten to the container binding'
Assert-Check ([string]$parsed['username'] -eq 'postgres') 'postgres username is preserved'
Assert-Check ([string]$parsed['password'] -eq 'secret') 'postgres password is preserved'
Assert-Check ([string]$parsed['database'] -eq 'edfi') 'postgres database is preserved'
Assert-Check ([string]$parsed['pooling'] -eq 'true') 'postgres pooling option is preserved'

$rewritten = ConvertTo-PostgresEndpointPinnedConnectionString `
    -ConnectionString 'Server=elsewhere;Username=postgres;Database=edfi' `
    -EndpointHost 'localhost' -EndpointPort 5435
$parsed = Get-ParsedConnectionString -ConnectionString $rewritten
Assert-Check (-not $parsed.ContainsKey('server')) 'postgres Server synonym is removed'
Assert-Check ([string]$parsed['host'] -eq 'localhost') 'postgres Server synonym is replaced by host'
Assert-Check ([string]$parsed['port'] -eq '5435') 'postgres port is added when the template omits it'

$rewritten = ConvertTo-PostgresEndpointPinnedConnectionString `
    -ConnectionString "username=postgres;password='se;cret=1';database=edfi" `
    -EndpointHost 'localhost' -EndpointPort 5435
$parsed = Get-ParsedConnectionString -ConnectionString $rewritten
Assert-Check ([string]$parsed['password'] -eq 'se;cret=1') 'quoted postgres password survives the rewrite round trip'
Assert-Check ([string]$parsed['host'] -eq 'localhost') 'postgres host is added when the template omits it'

Assert-Throw { ConvertTo-PostgresEndpointPinnedConnectionString `
        -ConnectionString 'this is not a connection string' `
        -EndpointHost 'localhost' -EndpointPort 5435 } `
    'malformed postgres template is refused'

# --- SQL Server connection-string endpoint rewriting ---

$rewritten = ConvertTo-MssqlEndpointPinnedConnectionString `
    -ConnectionString 'Server=elsewhere,1433;User Id=sa;Password=secret;TrustServerCertificate=true;Encrypt=false' `
    -EndpointHost 'localhost' -EndpointPort 14333
$parsed = Get-ParsedConnectionString -ConnectionString $rewritten
Assert-Check ([string]$parsed['data source'] -eq 'localhost,14333') 'mssql endpoint is rewritten to the container binding'
Assert-Check (-not $parsed.ContainsKey('server')) 'mssql Server synonym is removed'
Assert-Check ([string]$parsed['user id'] -eq 'sa') 'mssql user id is preserved'
Assert-Check ([string]$parsed['password'] -eq 'secret') 'mssql password is preserved'
Assert-Check ([string]$parsed['trustservercertificate'] -eq 'true') 'mssql trust option is preserved'
Assert-Check ([string]$parsed['encrypt'] -eq 'false') 'mssql encryption option is preserved'

$rewritten = ConvertTo-MssqlEndpointPinnedConnectionString `
    -ConnectionString 'Data Source=tcp:elsewhere,1433;Network Address=other;addr=third;address=fourth;User Id=sa' `
    -EndpointHost 'localhost' -EndpointPort 14333
$parsed = Get-ParsedConnectionString -ConnectionString $rewritten
Assert-Check ([string]$parsed['data source'] -eq 'localhost,14333') 'mssql data source is replaced'
Assert-Check (-not $parsed.ContainsKey('network address')) 'mssql Network Address synonym is removed'
Assert-Check (-not $parsed.ContainsKey('addr')) 'mssql addr synonym is removed'
Assert-Check (-not $parsed.ContainsKey('address')) 'mssql address synonym is removed'
Assert-Check ([string]$parsed['user id'] -eq 'sa') 'mssql credentials survive synonym removal'

Assert-Throw { ConvertTo-MssqlEndpointPinnedConnectionString `
        -ConnectionString 'this is not a connection string' `
        -EndpointHost 'localhost' -EndpointPort 14333 } `
    'malformed mssql template is refused'

Write-Output "All $script:checkCount checks passed."
