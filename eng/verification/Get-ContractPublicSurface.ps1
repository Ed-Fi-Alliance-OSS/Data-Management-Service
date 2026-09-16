# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Emits the externally consumable surface of a contract assembly as sorted, normalized lines.

.DESCRIPTION
    The comparison the publish lane makes before it republishes a contract at a version already on
    the feed. Two assemblies whose surfaces are identical produce identical output; any change an
    implementer outside the assembly could bind to produces different output.

    This exists because the assertion the packed-package scripts make is narrower than "public
    surface" suggests: they select T: members out of the shipped XML documentation, so they compare
    type names only. A member added to a public type, a parameter type changed, a protected member
    removed, a return type changed and an indexer renamed all leave that list identical.

    The surface is read from assembly metadata rather than from the XML file, and nothing here loads
    an assembly into an execution context. Two consequences matter. A package downloaded from the
    feed can be read without resolving its references, which the plugin contract needs because its
    signatures name IConfiguration and IServiceCollection. And no code from the assembly under
    inspection ever runs in the process inspecting it.

    Comparison by the caller is ordinal. Lines are sorted with StringComparer.Ordinal here, and
    callers must compare them the same way: PowerShell's default string equality is
    case-insensitive, under which a rename differing only in case reads as no change at all.

.EXAMPLE
    $left = ./eng/verification/Get-ContractPublicSurface.ps1 -AssemblyPath ./published/lib/net10.0/EdFi.Api.Plugins.dll
    $right = ./eng/verification/Get-ContractPublicSurface.ps1 -AssemblyPath ./packed/lib/net10.0/EdFi.Api.Plugins.dll
    $null -eq (Compare-Object $left $right -CaseSensitive -SyncWindow 0)
#>
[CmdletBinding()]
[OutputType([string[]])]
param(
    # The managed assembly to read.
    [Parameter(Mandatory)]
    [string]
    $AssemblyPath
)

$ErrorActionPreference = "Stop"

# Add-Type is per-process and permanent, so a second call in the same session throws rather than
# recompiling. Callers run this once per assembly and the reader is stateless, so the guard is what
# makes the second call work at all.
if (-not ("EdFi.Verification.ContractSurfaceReader" -as [type])) {
    $sourcePath = Join-Path $PSScriptRoot "ContractSurface.cs"

    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Cannot read a contract surface: $sourcePath does not exist."
    }

    # An explicit reference set replaces the default one, so everything the source uses is named
    # here. System.Reflection.Metadata ships in the shared framework;
    # System.Reflection.MetadataLoadContext does not, which is why the reader is written against the
    # metadata reader rather than against a reflection-only load.
    Add-Type -Path $sourcePath -ReferencedAssemblies @(
        "System.Runtime",
        "System.Collections",
        "System.Collections.Immutable",
        "System.Linq",
        "System.IO.FileSystem",
        "System.Reflection.Metadata",
        "System.Text.Encoding.Extensions",
        "netstandard",
        "System.Private.CoreLib"
    )
}

$resolved = [System.IO.Path]::GetFullPath($AssemblyPath)

if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "Cannot read a contract surface: $resolved does not exist."
}

return [EdFi.Verification.ContractSurfaceReader]::Read($resolved)
