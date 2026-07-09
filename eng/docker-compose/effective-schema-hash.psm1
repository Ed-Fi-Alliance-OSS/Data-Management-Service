# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Get-EffectiveSchemaHashFromOutput {
    <#
    .SYNOPSIS
    Extracts the effective schema hash from bootstrap or runtime log output.

    .DESCRIPTION
    Scans each log line for either the legacy plain-text form or a JSON-formatted
    Serilog console event and returns the last effective schema hash found.

    .PARAMETER Output
    The log lines emitted by the schema tool or DMS container.

    .OUTPUTS
    System.String

    .EXAMPLE
    Get-EffectiveSchemaHashFromOutput -Output $logLines
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]
        $Output
    )

    $effectiveSchemaHash = $null

    foreach ($line in $Output) {
        $lineText = [string]$line
        if ([string]::IsNullOrWhiteSpace($lineText)) {
            continue
        }

        $lineHash = Get-EffectiveSchemaHashFromLogLine -LineText $lineText
        if (-not [string]::IsNullOrWhiteSpace($lineHash)) {
            $effectiveSchemaHash = $lineHash
        }
    }

    return $effectiveSchemaHash
}

function Get-EffectiveSchemaHashFromLogLine {
    param(
        [Parameter(Mandatory)]
        [string]
        $LineText
    )

    if ($LineText -match '(?i)Effective schema hash:\s*([a-f0-9]{64})') {
        return $Matches[1].ToLowerInvariant()
    }

    $trimmedLine = $LineText.TrimStart()
    if ($trimmedLine.Length -lt 2 -or $trimmedLine[0] -ne '{')
    {
        return $null
    }

    try
    {
        $payload = $LineText | ConvertFrom-Json -AsHashtable -Depth 32
    }
    catch
    {
        return $null
    }

    # ConvertFrom-Json -AsHashtable yields hashtables for JSON objects, so plain
    # dictionary indexing covers every shape this parser can receive.
    if ($payload -isnot [System.Collections.IDictionary]) {
        return $null
    }

    $messageTemplate = $payload['MessageTemplate']
    $renderedMessage = $payload['RenderedMessage']

    $isEffectiveSchemaEvent =
        ($messageTemplate -is [string] -and $messageTemplate -match '(?i)Effective schema hash') -or
        ($renderedMessage -is [string] -and $renderedMessage -match '(?i)Effective schema hash')

    if (-not $isEffectiveSchemaEvent) {
        return $null
    }

    $hash = Get-ValidEffectiveSchemaHash -Value $payload['Hash']
    if (-not [string]::IsNullOrWhiteSpace($hash)) {
        return $hash
    }

    $properties = $payload['Properties']
    if ($properties -is [System.Collections.IDictionary]) {
        $hash = Get-ValidEffectiveSchemaHash -Value $properties['Hash']
        if (-not [string]::IsNullOrWhiteSpace($hash)) {
            return $hash
        }
    }

    if ($renderedMessage -is [string] -and $renderedMessage -match '(?i)\b([a-f0-9]{64})\b') {
        return $Matches[1].ToLowerInvariant()
    }

    return $null
}

function Get-ValidEffectiveSchemaHash {
    param(
        [object]
        $Value
    )

    if ($Value -is [string] -and $Value -match '(?i)^[a-f0-9]{64}$') {
        return $Value.ToLowerInvariant()
    }

    return $null
}

Export-ModuleMember -Function Get-EffectiveSchemaHashFromOutput
