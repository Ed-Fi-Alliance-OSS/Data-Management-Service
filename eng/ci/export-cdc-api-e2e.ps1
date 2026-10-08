# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param([string] $RawDirectory, [string] $Destination, [string] $RunnerPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'cdc-qualification.psm1')
$runner = Get-Content -LiteralPath $RunnerPath -Raw | ConvertFrom-Json -AsHashtable
# Runner records Passed only after this bounded process returns successfully.
$runner.Stages.Export = 'Passed'
Export-CdcQualificationEvidence -RawDirectory $RawDirectory -Destination $Destination -ApiRunner $runner
