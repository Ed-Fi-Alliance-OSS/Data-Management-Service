# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Loads the Data Standard 6.1 educator-preparation sample data that the DMS populated template
# deliberately excludes (Template-Management.psm1, Get-EducatorPreparationSampleFileName), so a
# security-review deployment carries the same Grand Bend data as the ODS/API DS 6.1 populated
# template: 1959 students in 9 schools instead of 960 in 3.

# No -Force on Dms-Management: reuse a loaded instance so -Insecure defaults set on it apply here.
# Template-Management force-reimports Package-Management and Dms-Management as nested modules, which
# drops copies a caller imported earlier, so every helper the entry script needs is wrapped here.
Import-Module (Join-Path $PSScriptRoot "../../../Dms-Management.psm1")
Import-Module (Join-Path $PSScriptRoot "../../../Package-Management.psm1")
Import-Module (Join-Path $PSScriptRoot "../../../DatabaseTemplates/Template-Management.psm1")

function Get-EducatorPrepLoadFile {
    <#
    .SYNOPSIS
        Returns the sample files the populated template excludes, which this load adds back.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][string]$SampleDataDirectory)

    return @(Get-EducatorPreparationSampleFileName -SourceDirectory $SampleDataDirectory | Sort-Object)
}

function Get-EducatorPrepRepostFile {
    <#
    .SYNOPSIS
        Returns the kept (template-loaded) files that reference students defined only in the
        educator-prep files.
    .DESCRIPTION
        The template build tolerates those records as unresolved references because their students
        are not loaded yet, so they are missing from the template. Re-posting the files after the
        educator-prep load creates them; the records already present are idempotent upserts.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][string]$SampleDataDirectory)

    $loadFiles = @(Get-EducatorPrepLoadFile -SampleDataDirectory $SampleDataDirectory)
    $educatorPrepStudents = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($name in $loadFiles) {
        $text = Get-Content -LiteralPath (Join-Path $SampleDataDirectory $name) -Raw
        foreach ($match in [regex]::Matches($text, '<Student\b[^>]*>\s*<StudentUniqueId>([^<]+)</StudentUniqueId>')) {
            [void]$educatorPrepStudents.Add($match.Groups[1].Value)
        }
    }
    if ($educatorPrepStudents.Count -eq 0) { return @() }

    $repost = foreach ($file in Get-ChildItem -LiteralPath $SampleDataDirectory -Filter "*.xml" -File | Sort-Object Name) {
        if ($loadFiles -contains $file.Name) { continue }
        $references = Select-String -LiteralPath $file.FullName -Pattern '<StudentUniqueId>([^<]+)</StudentUniqueId>' -AllMatches
        $hit = $references | ForEach-Object { $_.Matches } | Where-Object { $educatorPrepStudents.Contains($_.Groups[1].Value) } | Select-Object -First 1
        if ($hit) { $file.Name }
    }
    return @($repost)
}

function Get-ReviewEducationOrganizationId {
    <#
    .SYNOPSIS
        Returns every education organization id the sample data defines (Template-Management's parser),
        the scope Build-Template gives its populated-load application.
    #>
    [CmdletBinding()]
    [OutputType([long[]])]
    param([Parameter(Mandatory)][string]$SampleDataDirectory)

    return [long[]]@(Get-EducationOrganizationIdsFromSampleData -SampleDataDirectory $SampleDataDirectory)
}

function Get-ReviewBulkLoadClientDirectory {
    <#
    .SYNOPSIS
        Downloads the repo-pinned BulkLoadClient into WorkDirectory/.packages and returns the folder
        that holds EdFi.BulkLoadClient.Console.dll.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$WorkDirectory)

    # Package-Management downloads into ./.packages relative to the working directory.
    Push-Location $WorkDirectory
    try { $package = (Get-BulkLoadClient).Trim() }
    finally { Pop-Location }
    $packageRoot = if ([System.IO.Path]::IsPathRooted($package)) { $package } else { Join-Path $WorkDirectory $package }
    $dll = Get-ChildItem -Path (Join-Path $packageRoot "tools") -Recurse -Filter "EdFi.BulkLoadClient.Console.dll" | Select-Object -First 1
    if (-not $dll) { throw "EdFi.BulkLoadClient.Console.dll not found under $packageRoot." }
    return $dll.DirectoryName
}

function Remove-ReviewLoaderApplication {
    <#
    .SYNOPSIS
        Deletes one temporary loader application by the id that created it.
    .DESCRIPTION
        Never deletes from a listing: Invoke-RestMethod emits a JSON array as one object, and
        filtering @(Invoke-RestMethod ...) with Where-Object then passes EVERY application. The id
        is re-read first and must still name the loader application.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Medium")]
    param(
        [Parameter(Mandatory)][string]$CmsUrl,
        [Parameter(Mandatory)][hashtable]$Headers,
        [Parameter(Mandatory)][long]$ApplicationId,
        [Parameter(Mandatory)][string]$ExpectedName
    )

    $uri = "$($CmsUrl.TrimEnd('/'))/v3/applications/$ApplicationId"
    $application = Invoke-RestMethod -Method Get -Uri $uri -Headers $Headers
    if ($application.applicationName -ne $ExpectedName) {
        throw "Application $ApplicationId is '$($application.applicationName)', not '$ExpectedName'; refusing to delete it."
    }
    if ($PSCmdlet.ShouldProcess($ExpectedName, "Delete application $ApplicationId")) {
        Invoke-RestMethod -Method Delete -Uri $uri -Headers $Headers | Out-Null
    }
}

function Invoke-BulkLoadClientContainer {
    <#
    .SYNOPSIS
        Runs the repo-pinned BulkLoadClient in a .NET runtime container on the compose network and
        returns its exit code. Plain HTTP to the DMS container, so no gateway certificate is involved.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string]$ClientDirectory,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$WorkDirectory,
        [Parameter(Mandatory)][string]$DmsUrl,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Secret,
        [Parameter(Mandatory)][string]$LogPath,
        # Tuning, as Get-TemplateBulkLoadTuning returns it; no defaults, so a caller cannot drift from it.
        [Parameter(Mandatory)][int]$MaxConcurrentConnections,
        [Parameter(Mandatory)][int]$MaxSimultaneousRequests,
        [Parameter(Mandatory)][int]$MaxBufferedTasks,
        [Parameter(Mandatory)][int]$RetryCount,
        [string]$Network = "dms-sec"
    )

    # The client ships as tools/<tfm>/any; run it on the matching runtime image (net10.0 -> 10.0).
    $framework = Split-Path (Split-Path $ClientDirectory -Parent) -Leaf
    $runtimeTag = $framework -replace '^net', ''
    $xsd = New-Item -ItemType Directory -Force -Path (Join-Path $WorkDirectory "xsd")
    $work = New-Item -ItemType Directory -Force -Path (Join-Path $WorkDirectory "client")
    # The client skips every record listed in the newest *.hash file in its working folder, so a
    # re-run into recreated databases would silently miss records. Clear them as Build-Template's
    # -ForceReloadData does; the host user owns the folder, so it can delete the container's files.
    Get-ChildItem -LiteralPath $work.FullName -Filter "*.hash" -File | Remove-Item -Force -ErrorAction Stop
    docker run --rm --network $Network `
        -v "${ClientDirectory}:/client:ro" -v "${DataDirectory}:/data:ro" -v "$($work.FullName):/work" -v "$($xsd.FullName):/xsd" `
        "mcr.microsoft.com/dotnet/runtime:$runtimeTag" dotnet /client/EdFi.BulkLoadClient.Console.dll `
        -b $DmsUrl -d /data -w /work "--key=$Key" "--secret=$Secret" `
        -c $MaxConcurrentConnections -r $RetryCount -l $MaxSimultaneousRequests -t $MaxBufferedTasks -x /xsd -o "$DmsUrl/oauth/token" -f *> $LogPath
    return $LASTEXITCODE
}

function Invoke-EducatorPrepLoad {
    <#
    .SYNOPSIS
        Loads one directory of sample files into one deployment through its DMS API.
    .DESCRIPTION
        Mirrors Build-Template's populated load: a temporary EdFiSandbox application scoped to the
        sample data's education organizations, the repo-pinned BulkLoadClient at Build-Template's
        PostgreSQL tuning (Get-TemplateBulkLoadTuning), -ForceReloadMetadata and -ForceReloadData.
        The temporary application is removed by id afterwards, also when the load fails.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][hashtable]$Deployment,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$AdminClientId,
        [Parameter(Mandatory)][string]$AdminClientSecret,
        [Parameter(Mandatory)][string]$BulkLoadClientDirectory,
        [long[]]$EducationOrganizationIds = @(),
        [string]$Network = "dms-sec"
    )

    $d = $Deployment
    $token = Get-CmsToken -CmsUrl $d.CmsUrl -ClientId $AdminClientId -ClientSecret $AdminClientSecret
    $headers = @{ Authorization = "Bearer $token" }
    if ($d.Tenant) { $headers["Tenant"] = $d.Tenant }

    # Assign before filtering: Invoke-RestMethod emits a JSON array as one object.
    $allDataStores = Invoke-RestMethod -Uri "$($d.CmsUrl)v3/dataStores?offset=0&limit=500" -Headers $headers
    $dataStore = @($allDataStores | Where-Object { $_.name -eq $d.DataStoreName })
    if ($dataStore.Count -ne 1) { throw "Expected exactly one data store named '$($d.DataStoreName)' in $($d.Label)." }

    $company = "EdPrep Loader Vendor ($($d.Code))"
    $allVendors = Invoke-RestMethod -Uri "$($d.CmsUrl)v3/vendors?offset=0&limit=500" -Headers $headers
    $vendor = @($allVendors | Where-Object { $_.company -eq $company })
    $vendorId = if ($vendor.Count -eq 1) { $vendor[0].id } else {
        Add-Vendor -CmsUrl $d.CmsUrl -Company $company -NamespacePrefixes "uri://ed-fi.org, uri://gbisd.edu" -AccessToken $token -Tenant $d.Tenant
    }
    $edOrgIds = if ($EducationOrganizationIds.Count -gt 0) { $EducationOrganizationIds } else {
        Get-ReviewEducationOrganizationId -SampleDataDirectory $DataDirectory
    }
    $applicationName = "EdPrep Loader ($($d.Code))"
    $application = Add-Application -CmsUrl $d.CmsUrl -ApplicationName $applicationName -ClaimSetName "EdFiSandbox" `
        -VendorId $vendorId -AccessToken $token -EducationOrganizationIds $edOrgIds -DataStoreIds @([long]$dataStore[0].id) -Tenant $d.Tenant

    $logPath = Join-Path $LogDirectory "bulkload-$($d.Code)-$(Split-Path $DataDirectory -Leaf).log"
    $tuning = Get-TemplateBulkLoadTuning -DatabaseEngine "postgresql"
    $started = Get-Date
    try {
        $exitCode = Invoke-BulkLoadClientContainer -ClientDirectory $BulkLoadClientDirectory -DataDirectory $DataDirectory `
            -WorkDirectory (Join-Path $LogDirectory "work-$($d.Code)") -DmsUrl $d.DmsUrl -Key $application.Key -Secret $application.Secret `
            -LogPath $logPath -Network $Network @tuning
    }
    finally {
        Remove-ReviewLoaderApplication -CmsUrl $d.CmsUrl -Headers $headers -ApplicationId $application.Id -ExpectedName $applicationName -Confirm:$false
    }

    return [pscustomobject]@{
        Deployment = $d.Label
        Data       = (Split-Path $DataDirectory -Leaf)
        ExitCode   = $exitCode
        Elapsed    = (Get-Date) - $started
        LogPath    = $logPath
    }
}

Export-ModuleMember -Function Get-EducatorPrepLoadFile, Get-EducatorPrepRepostFile, Get-ReviewEducationOrganizationId, `
    Get-ReviewBulkLoadClientDirectory, Remove-ReviewLoaderApplication, `
    Invoke-BulkLoadClientContainer, Invoke-EducatorPrepLoad
