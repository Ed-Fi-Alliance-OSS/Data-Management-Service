# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Read-only attachment check. Reuse governed inventory/hash validation; never reproduce its hashes in C#.
param([Parameter(Mandatory)][string]$HandoffPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    Import-Module (Join-Path $PSScriptRoot 'e2e-cdc.psm1') -DisableNameChecking
    Assert-E2ECdcApiAttachment -HandoffPath $HandoffPath

}
catch {
    # Never echo raw settings, docker environment, credentials or filesystem diagnostics.
    [Console]::Error.WriteLine('CDC_API_ATTACHMENT_PROVENANCE_OR_HTTP_CONFIGURATION')
    exit 1
}
