# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Matches template verifier: sqlcmd requires plaintext SQLCMDPASSWORD at the process boundary; no password is emitted.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedModelManifestPath,
    [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$DatabaseEngine,
    [string]$DatabaseName,
    [string]$ContainerName = $(if ($DatabaseEngine -eq 'mssql') { 'dms-mssql' } else { 'dms-postgresql' }),
    [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? 'abcdefgh1!',
    [switch]$RenderSql
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Compact-Descriptor.psm1') -Force
$dialect = if ($DatabaseEngine -eq 'mssql') { 'mssql' } else { 'pgsql' }
$sql = Get-CompactDescriptorAssertionSql -ExpectedModelManifestPath $ExpectedModelManifestPath -Dialect $dialect
if ($RenderSql) { return $sql }
if ([string]::IsNullOrWhiteSpace($DatabaseName) -or $DatabaseName -notmatch '^[A-Za-z0-9_]+$') {
    throw 'A safe DatabaseName is required for live compact descriptor verification.'
}
if ($DatabaseEngine -eq 'mssql') {
    $sql | & docker exec -i -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $DatabaseName -C -I -b
}
else {
    $sql | & docker exec -i $ContainerName psql -X -U postgres -d $DatabaseName -v ON_ERROR_STOP=1 -f -
}
if ($LASTEXITCODE -ne 0) { throw "Compact descriptor catalog assertions failed for '$DatabaseName'." }
