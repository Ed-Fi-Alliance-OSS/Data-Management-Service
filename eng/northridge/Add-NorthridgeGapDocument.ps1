# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    POSTs documents to the DMS API from a manifest and verifies each one with a GET-by-id.

.DESCRIPTION
    The DMS API is the only write path that maintains every invariant at once: relational projection
    rows, dms.ReferentialIdentity, descriptor references, tracked-change rows, sequences, and identity
    stamps. Direct SQL insertion would mean hand-reproducing the write plan per resource, so documents
    are added through the API.

    Two things are deliberately not trusted here. A 2xx is not accepted as proof: every created
    document is re-read by its returned id and the response body is compared field by field against
    what was sent. And a run that reports success is not accepted as proof either -- the count
    reconciliation performed afterwards is the acceptance evidence, because a loader can drop a
    document on a 4xx and still finish with a success status.

    The manifest carries an explicit ordering key so referenced documents are created before the
    documents that reference them; ordering is data, not a property of file order.

.PARAMETER ManifestPath
    JSON manifest. Shape:
    {
      "documents": [
        { "order": 1, "label": "Staff: Example Name", "endpoint": "/data/ed-fi/staffs", "body": { } }
      ]
    }

.PARAMETER DmsBaseUrl
    Base URL of the DMS API.

.PARAMETER TokenUrl
    OAuth token endpoint used to obtain a bearer token.

.PARAMETER ClientId
    API client id.

.PARAMETER ClientSecret
    API client secret. Never logged, and never written to the result file.

.PARAMETER Scope
    OAuth scope requested.

.PARAMETER OutputPath
    Optional path for the per-document result record, used as provenance evidence.

.EXAMPLE
    ./Add-NorthridgeGapDocument.ps1 -ManifestPath /tmp/nr/gap.json -DmsBaseUrl http://localhost:8080 `
        -TokenUrl http://localhost:8080/oauth/token -ClientId id -ClientSecret secret -WhatIf

    Validates and orders the manifest and prints the plan without issuing any write.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]
    $ManifestPath,

    [Parameter(Mandatory)]
    [string]
    $DmsBaseUrl,

    [Parameter(Mandatory)]
    [string]
    $TokenUrl,

    [Parameter(Mandatory)]
    [string]
    $ClientId,

    [Parameter(Mandatory)]
    [string]
    $ClientSecret,

    [string]
    $Scope = "edfi_admin_api/full_access",

    [string]
    $OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-GapDocumentManifest {
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Manifest not found: $Path"
    }

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json

    if ($null -eq $manifest.documents -or $manifest.documents.Count -eq 0) {
        throw "Manifest '$Path' declares no documents."
    }

    foreach ($document in $manifest.documents) {
        foreach ($required in @("order", "label", "endpoint", "body")) {
            if (-not $document.PSObject.Properties.Name.Contains($required)) {
                throw "Manifest entry is missing required property '$required'."
            }
        }
    }

    # Explicit ordering: a referenced document must exist before the document referencing it.
    return $manifest.documents | Sort-Object -Property { [int]$_.order }
}

function Get-DmsAccessToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Secret,
        [Parameter(Mandatory)] [string] $RequestedScope
    )

    $body = @{
        grant_type    = "client_credentials"
        client_id     = $Id
        client_secret = $Secret
        scope         = $RequestedScope
    }

    $response = Invoke-RestMethod -Method Post -Uri $Url -Body $body `
        -ContentType "application/x-www-form-urlencoded"

    if ([string]::IsNullOrWhiteSpace($response.access_token)) {
        throw "Token endpoint '$Url' returned no access_token."
    }

    return $response.access_token
}

# Compares only the fields that were sent. The server legitimately adds id, _etag, and
# _lastModifiedDate, so a whole-body equality check would fail for the wrong reason.
function Compare-SentField {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[string]])]
    param(
        [Parameter(Mandatory)] [object] $Sent,
        [Parameter(Mandatory)] [object] $Fetched
    )

    $mismatch = [System.Collections.Generic.List[string]]::new()

    foreach ($property in $Sent.PSObject.Properties) {
        $name = $property.Name

        if (-not $Fetched.PSObject.Properties.Name.Contains($name)) {
            $mismatch.Add("$name absent from the fetched document")
            continue
        }

        $sentJson = $property.Value | ConvertTo-Json -Depth 32 -Compress
        $fetchedJson = $Fetched.$name | ConvertTo-Json -Depth 32 -Compress

        if ($sentJson -ne $fetchedJson) {
            $mismatch.Add("$name sent=$sentJson fetched=$fetchedJson")
        }
    }

    return $mismatch
}

$document = Get-GapDocumentManifest -Path $ManifestPath

Write-Output "Manifest: $ManifestPath"
Write-Output "DMS: $DmsBaseUrl"
Write-Output "Documents to create, in dependency order:"
foreach ($item in $document) {
    Write-Output ("  {0,2}. {1} -> {2}" -f [int]$item.order, $item.label, $item.endpoint)
}

if (-not $PSCmdlet.ShouldProcess($DmsBaseUrl, "POST $($document.Count) document(s)")) {
    Write-Output ""
    Write-Output "WhatIf: manifest validated and ordered. No token was requested and no write was issued."
    return
}

$token = Get-DmsAccessToken -Url $TokenUrl -Id $ClientId -Secret $ClientSecret -RequestedScope $Scope
$header = @{ Authorization = "Bearer $token" }

$result = [System.Collections.Generic.List[object]]::new()
$failure = [System.Collections.Generic.List[string]]::new()

foreach ($item in $document) {
    $endpoint = "$($DmsBaseUrl.TrimEnd('/'))$($item.endpoint)"
    $payload = $item.body | ConvertTo-Json -Depth 32

    Write-Output ""
    Write-Output "POST $($item.label) -> $endpoint"

    $response = Invoke-WebRequest -Method Post -Uri $endpoint -Headers $header `
        -Body $payload -ContentType "application/json" -SkipHttpErrorCheck

    $statusCode = [int]$response.StatusCode
    Write-Output "  status: $statusCode"

    if ($statusCode -ne 201) {
        # Recorded rather than thrown immediately, so one bad document does not hide the rest.
        $failure.Add("$($item.label): POST returned $statusCode, expected 201")
        $result.Add([pscustomobject]@{
                Label      = $item.label
                Endpoint   = $item.endpoint
                PostStatus = $statusCode
                Location   = $null
                GetStatus  = $null
                FieldMatch = $false
            })
        continue
    }

    $location = $response.Headers["Location"]
    if ($location -is [array]) { $location = $location[0] }

    if ([string]::IsNullOrWhiteSpace($location)) {
        $failure.Add("$($item.label): 201 without a Location header, so the document cannot be verified")
        continue
    }

    $getUri = if ($location -match '^https?://') { $location } else { "$($DmsBaseUrl.TrimEnd('/'))$location" }

    $fetched = Invoke-WebRequest -Method Get -Uri $getUri -Headers $header -SkipHttpErrorCheck
    $getStatus = [int]$fetched.StatusCode
    Write-Output "  GET $getUri -> $getStatus"

    $fieldMatch = $false

    if ($getStatus -ne 200) {
        $failure.Add("$($item.label): GET-by-id returned $getStatus, expected 200")
    }
    else {
        $mismatch = Compare-SentField -Sent $item.body -Fetched ($fetched.Content | ConvertFrom-Json)
        if ($mismatch.Count -eq 0) {
            $fieldMatch = $true
            Write-Output "  fields: all sent fields match"
        }
        else {
            foreach ($text in $mismatch) { Write-Output "  field mismatch: $text" }
            $failure.Add("$($item.label): $($mismatch.Count) field mismatch(es) after create")
        }
    }

    $result.Add([pscustomobject]@{
            Label      = $item.label
            Endpoint   = $item.endpoint
            PostStatus = $statusCode
            Location   = $location
            GetStatus  = $getStatus
            FieldMatch = $fieldMatch
        })
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputParent = Split-Path -Path $OutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($outputParent) -and -not (Test-Path -LiteralPath $outputParent)) {
        New-Item -Path $outputParent -ItemType Directory -Force | Out-Null
    }
    $result | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
    Write-Output ""
    Write-Output "Result record written to $OutputPath"
}

$createdCount = ($result | Where-Object { $_.PostStatus -eq 201 }).Count
$verifiedCount = ($result | Where-Object { $_.FieldMatch }).Count

Write-Output ""
Write-Output "Requested: $($document.Count)   created (201): $createdCount   verified: $verifiedCount"

if ($failure.Count -gt 0) {
    foreach ($item in $failure) { Write-Output "FAIL: $item" }
    throw "Document creation failed: $($failure -join '; ')"
}

Write-Output "PASS: every document was created and verified by GET-by-id."
Write-Output "Run the count reconciliation now -- this result is not acceptance evidence on its own."
