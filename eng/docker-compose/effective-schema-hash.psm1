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

    return Find-EffectiveSchemaHashInValue -Value $payload
}

function Find-EffectiveSchemaHashInValue {
    param(
        [object]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        if ($Value -match '(?i)\b([a-f0-9]{64})\b') {
            return $Matches[1].ToLowerInvariant()
        }

        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $messageTemplate = $Value['MessageTemplate']
        $renderedMessage = $Value['RenderedMessage']

        $isEffectiveSchemaEvent = $false
        if ($messageTemplate -is [string] -and $messageTemplate -match '(?i)Effective schema hash') {
            $isEffectiveSchemaEvent = $true
        }
        elseif ($renderedMessage -is [string] -and $renderedMessage -match '(?i)Effective schema hash') {
            $isEffectiveSchemaEvent = $true
        }

        if ($isEffectiveSchemaEvent) {
            $hash = Get-EffectiveSchemaHashFromEventProperty -EventPayload $Value
            if (-not [string]::IsNullOrWhiteSpace($hash)) {
                return $hash
            }

            if ($renderedMessage -is [string] -and $renderedMessage -match '(?i)\b([a-f0-9]{64})\b') {
                return $Matches[1].ToLowerInvariant()
            }
        }

        return $null
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            $hash = Find-EffectiveSchemaHashInValue -Value $item
            if (-not [string]::IsNullOrWhiteSpace($hash)) {
                return $hash
            }
        }

        return $null
    }

    return $null
}

function Get-EffectiveSchemaHashFromEventProperty {
    param(
        [object]
        $EventPayload
    )

    $hash = Get-ValidEffectiveSchemaHash -Value (Get-ObjectPropertyValue -InputObject $EventPayload -PropertyName "Hash")
    if (-not [string]::IsNullOrWhiteSpace($hash)) {
        return $hash
    }

    $properties = Get-ObjectPropertyValue -InputObject $EventPayload -PropertyName "Properties"
    if ($null -eq $properties) {
        return $null
    }

    return Get-ValidEffectiveSchemaHash -Value (Get-ObjectPropertyValue -InputObject $properties -PropertyName "Hash")
}

function Get-ObjectPropertyValue {
    param(
        [object]
        $InputObject,

        [string]
        $PropertyName
    )

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject[$PropertyName]
    }

    $property = $InputObject.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-ValidEffectiveSchemaHash {
    param(
        [object]
        $Value
    )

    if ($Value -is [string] -and $Value -match '^[a-f0-9]{64}$') {
        return $Value.ToLowerInvariant()
    }

    return $null
}

Export-ModuleMember -Function Get-EffectiveSchemaHashFromOutput
