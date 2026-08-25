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
    OAuth token endpoint used to obtain a bearer token. This must be the Configuration Service
    token endpoint, http://localhost:8081/connect/token by default, because the credentials are
    sent in the request body. The DMS endpoint http://localhost:8080/oauth/token will not do:
    it requires HTTP Basic authentication and forwards only grant_type upstream, so a body-borne
    client id and secret reach the identity provider as an unauthenticated request.

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
        -TokenUrl http://localhost:8081/connect/token -ClientId id -ClientSecret secret -WhatIf

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

    if ($null -eq $manifest -or
        -not ($manifest.PSObject.Properties.Name -ccontains "documents") -or
        $null -eq $manifest.documents -or $manifest.documents.Count -eq 0) {
        throw "Manifest '$Path' declares no documents."
    }

    foreach ($document in $manifest.documents) {
        foreach ($required in @("order", "label", "endpoint", "body")) {
            if (-not ($document.PSObject.Properties.Name -ccontains $required)) {
                throw "Manifest entry is missing required property '$required'."
            }
        }
    }

    # The label is the key the final verification uses to find the body it sent, and the key of the
    # result record. Two documents sharing one would make that lookup return both bodies and fail the
    # comparison with a field diff that names the wrong cause. Uniqueness is checked without regard to
    # case even though the lookup is case-sensitive: two labels a reader cannot tell apart in the
    # record are one label.
    $seenLabel = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($document in $manifest.documents) {
        if (-not $seenLabel.Add([string]$document.label)) {
            throw "Manifest '$Path' uses label '$($document.label)' more than once (compared without regard to case). Every document needs a label of its own."
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
# _lastModifiedDate, so a whole-body equality check would fail for the wrong reason. Reference
# enrichment and numeric normalisation are handled inline below for the same reason.
function Compare-SentField {
    [CmdletBinding()]
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [object] $Sent,
        [Parameter(Mandatory)] [object] $Fetched
    )

    $mismatch = [System.Collections.Generic.List[string]]::new()

    foreach ($property in $Sent.PSObject.Properties) {
        $name = $property.Name

        if (-not ($Fetched.PSObject.Properties.Name -ccontains $name)) {
            $mismatch.Add("$name absent from the fetched document")
            continue
        }

        $sentValue = $property.Value
        $fetchedValue = $Fetched.$name

        # JSON numeric normalisation renders a sent 1.0 as 1, so numbers compare by value not text.
        if ($sentValue -is [ValueType] -and $sentValue -isnot [bool] -and
            $fetchedValue -is [ValueType] -and $fetchedValue -isnot [bool]) {
            if ([double]$sentValue -ne [double]$fetchedValue) {
                $mismatch.Add("$name sent=$sentValue fetched=$fetchedValue")
            }
            continue
        }

        # The server enriches every reference object with a hypermedia "link" member, so a reference
        # is compared member by member over what was actually sent rather than as a whole object.
        if ($sentValue -is [psobject] -and $fetchedValue -is [psobject] -and
            @($sentValue.PSObject.Properties).Count -gt 0) {
            foreach ($member in $sentValue.PSObject.Properties) {
                $sentMember = ConvertTo-Json -InputObject $member.Value -Depth 32 -Compress
                if (-not ($fetchedValue.PSObject.Properties.Name -ccontains $member.Name)) {
                    $mismatch.Add("$name.$($member.Name) sent=$sentMember fetched=<missing>")
                    continue
                }

                $fetchedMember = ConvertTo-Json -InputObject $fetchedValue.($member.Name) -Depth 32 -Compress
                if ($sentMember -cne $fetchedMember) {
                    $mismatch.Add("$name.$($member.Name) sent=$sentMember fetched=$fetchedMember")
                }
            }
            continue
        }

        $sentJson = ConvertTo-Json -InputObject $sentValue -Depth 32 -Compress
        $fetchedJson = ConvertTo-Json -InputObject $fetchedValue -Depth 32 -Compress

        if ($sentJson -cne $fetchedJson) {
            $mismatch.Add("$name sent=$sentJson fetched=$fetchedJson")
        }
    }

    # Comma operator so an empty result stays a collection rather than unrolling to $null, which would
    # break .Count on the clean path -- the only path where there is nothing to report. Callers assign
    # the result directly: wrapping it in @() re-wraps the list and always yields a count of 1.
    return ,$mismatch
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
        # Deliberately not fatal here. A resource whose read authorization derives from a relationship
        # is legitimately unreadable until the document carrying that relationship exists, and the
        # manifest creates those later by design -- Staff is authorized through its education
        # organization association. The pass after the whole manifest is posted decides.
        Write-Output "  (deferred: re-checked once the whole manifest exists)"
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

# Re-read every created document now that the whole manifest exists. The GET issued immediately after
# a POST tests a moment the manifest ordering guarantees is incomplete, so a document that is entirely
# correct can answer 403 there. This pass tests the finished state instead.
Write-Output ""
Write-Output "Re-verifying every created document now that the whole manifest exists..."

foreach ($row in $result) {
    if ($row.PostStatus -ne 201 -or [string]::IsNullOrWhiteSpace($row.Location)) {
        continue
    }

    $recheckUri = if ($row.Location -match '^https?://') {
        $row.Location
    }
    else {
        "$($DmsBaseUrl.TrimEnd('/'))$($row.Location)"
    }

    $recheck = Invoke-WebRequest -Method Get -Uri $recheckUri -Headers $header -SkipHttpErrorCheck
    $recheckStatus = [int]$recheck.StatusCode
    Write-Output ("  {0,-52} GET -> {1}" -f $row.Label, $recheckStatus)

    # The record carries the final status. The GET recorded during the manifest tested an unfinished
    # state, so a deferred 403 left there would read, in the evidence, as a failed read of a document
    # this pass verified.
    $row.GetStatus = $recheckStatus

    if ($recheckStatus -ne 200) {
        $failure.Add("$($row.Label): final GET-by-id returned $recheckStatus, expected 200")
        continue
    }

    # Compared for every document, every time, and FieldMatch is set from this comparison alone. The
    # mid-manifest comparison ran against an unfinished state, so treating an earlier match as
    # sufficient would leave the finished body unverified for exactly the documents that looked fine
    # at the wrong moment.
    $sentBody = ($document | Where-Object { $_.label -ceq $row.Label }).body
    $mismatch = Compare-SentField -Sent $sentBody -Fetched ($recheck.Content | ConvertFrom-Json)

    if ($mismatch.Count -eq 0) {
        $row.FieldMatch = $true
        Write-Output "     fields: all sent fields match"
    }
    else {
        $row.FieldMatch = $false
        foreach ($text in $mismatch) { Write-Output "     field mismatch: $text" }
        $failure.Add("$($row.Label): $($mismatch.Count) field mismatch(es) on final verification")
    }
}

# Exported only now: the pass above is what sets FieldMatch, so a file written before it would
# record the mid-manifest verdict as though it were the final one.
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputParent = Split-Path -Path $OutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($outputParent) -and -not (Test-Path -LiteralPath $outputParent)) {
        New-Item -Path $outputParent -ItemType Directory -Force | Out-Null
    }
    $result | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
    Write-Output ""
    Write-Output "Result record written to $OutputPath"
}

$createdCount = @($result | Where-Object { $_.PostStatus -eq 201 }).Count
$verifiedCount = @($result | Where-Object { $_.FieldMatch }).Count

Write-Output ""
Write-Output "Requested: $($document.Count)   created (201): $createdCount   verified: $verifiedCount"

if ($failure.Count -gt 0) {
    foreach ($item in $failure) { Write-Output "FAIL: $item" }
    throw "Document creation failed: $($failure -join '; ')"
}

Write-Output "PASS: every document was created and verified by GET-by-id."
Write-Output "Run the count reconciliation now -- this result is not acceptance evidence on its own."
