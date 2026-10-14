# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [string] $ComposeFile = (Join-Path $PSScriptRoot '../docker-compose/postgresql.yml')
)

$ErrorActionPreference = 'Stop'
# Use the checked-in default, independently of process or repository-variable overrides.
$compose = Get-Content -LiteralPath $ComposeFile -Raw
$image = [regex]::Match($compose, '(?m)^\s+image:\s+\$\{POSTGRES_IMAGE:-(?<image>[^}\r\n]+)\}\s*$').Groups['image'].Value
$pin = [regex]::Match($image, '^postgres:(?<major>[0-9]+)(?:[.-][a-zA-Z0-9.-]+)?@sha256:[a-f0-9]{64}$')
if (-not $pin.Success -or [int]$pin.Groups['major'].Value -lt 18) {
    throw 'CDC qualification requires a digest-pinned PostgreSQL 18 or later default in eng/docker-compose/postgresql.yml.'
}
$image
