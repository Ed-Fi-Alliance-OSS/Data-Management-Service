#!/usr/bin/env pwsh
# Claim Upload Authorization Debug Script
# ========================================
# This script tests the claim upload process and helps debug authorization issues

param(
    [switch]$SkipSetup,
    [switch]$Verbose,
    [switch]$MonitorLogs
)

$ErrorActionPreference = "Stop"

# Configuration
$dmsPort = 8080
$configPort = 8081
$keycloakPort = 8045

$dmsUrl = "http://localhost:$dmsPort"
$configUrl = "http://localhost:$configPort"
$keycloakUrl = "http://localhost:$keycloakPort"

# Colors for output
function Write-Step { Write-Host "`n==> $args" -ForegroundColor Cyan }
function Write-Success { Write-Host "    ✓ $args" -ForegroundColor Green }
function Write-Fail { Write-Host "    ✗ $args" -ForegroundColor Red }
function Write-Info { Write-Host "    ℹ $args" -ForegroundColor Yellow }
function Write-Debug { if ($Verbose) { Write-Host "    [DEBUG] $args" -ForegroundColor Gray } }

# Helper function to make HTTP requests
function Invoke-HttpRequest {
    param(
        [string]$Uri,
        [string]$Method = "GET",
        [hashtable]$Headers = @{},
        [object]$Body = $null
    )
    
    try {
        $params = @{
            Uri = $Uri
            Method = $Method
            Headers = $Headers
            ContentType = "application/json"
        }
        
        if ($Body) {
            $params.Body = ($Body | ConvertTo-Json -Depth 10)
        }
        
        $response = Invoke-RestMethod @params -StatusCodeVariable statusCode
        Write-Debug "HTTP $statusCode $Method $Uri"
        
        return @{
            Success = $true
            StatusCode = $statusCode
            Data = $response
        }
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        Write-Debug "HTTP $statusCode $Method $Uri"
        
        # Try to get error details
        $errorBody = $null
        if ($_.ErrorDetails.Message) {
            try {
                $errorBody = $_.ErrorDetails.Message | ConvertFrom-Json
            } catch {
                $errorBody = $_.ErrorDetails.Message
            }
        }
        
        return @{
            Success = $false
            StatusCode = $statusCode
            Error = $_.Exception.Message
            ErrorBody = $errorBody
        }
    }
}

# Variables to store credentials and tokens
$script:sysAdminToken = $null
$script:restrictedClientKey = $null
$script:restrictedClientSecret = $null
$script:restrictedToken = $null
$script:sisClientKey = $null
$script:sisClientSecret = $null
$script:sisToken = $null

# Step 1: Setup System Administrator
function Setup-SystemAdmin {
    Write-Step "Setting up System Administrator"
    
    # Register system admin
    $registerBody = "ClientId=sys-admin&ClientSecret=SdfH%2998%26Jk&DisplayName=System Administrator"
    $registerResponse = Invoke-WebRequest -Uri "$configUrl/connect/register" `
        -Method POST `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $registerBody `
        -SkipHttpErrorCheck
    
    if ($registerResponse.StatusCode -eq 200 -or $registerResponse.StatusCode -eq 400) {
        Write-Success "System admin registration complete"
    } else {
        Write-Fail "Failed to register system admin: $($registerResponse.StatusCode)"
        return $false
    }
    
    # Get token
    $tokenBody = "client_id=sys-admin&client_secret=SdfH%2998%26Jk&grant_type=client_credentials&scope=edfi_admin_api/full_access"
    $tokenResponse = Invoke-RestMethod -Uri "$configUrl/connect/token" `
        -Method POST `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $tokenBody
    
    $script:sysAdminToken = $tokenResponse.access_token
    Write-Success "Got system admin token"
    Write-Debug "Token: $($script:sysAdminToken.Substring(0, 20))..."
    
    return $true
}

# Step 2: Create Vendors and Applications
function Setup-Clients {
    Write-Step "Creating Vendors and Applications"
    
    $headers = @{ Authorization = "Bearer $script:sysAdminToken" }
    
    # Create restricted vendor
    $vendorBody = @{
        company = "Restricted Vendor"
        contactName = "Test User"
        contactEmailAddress = "test@example.com"
        namespacePrefixes = "uri://ed-fi.org"
    }
    
    $vendorResponse = Invoke-HttpRequest -Uri "$configUrl/v2/vendors" -Method POST -Headers $headers -Body $vendorBody
    
    if ($vendorResponse.Success) {
        Write-Success "Created restricted vendor"
        $vendorLocation = $vendorResponse.Data.Headers.Location
        
        # Get vendor ID
        $vendorDetails = Invoke-HttpRequest -Uri $vendorLocation -Headers $headers
        $vendorId = $vendorDetails.Data.id
        
        # Create application with restricted claim set
        $appBody = @{
            vendorId = $vendorId
            applicationName = "Restricted App"
            claimSetName = "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
            educationOrganizationIds = @(255901001)
        }
        
        $appResponse = Invoke-HttpRequest -Uri "$configUrl/v2/applications" -Method POST -Headers $headers -Body $appBody
        
        if ($appResponse.Success) {
            $script:restrictedClientKey = $appResponse.Data.key
            $script:restrictedClientSecret = $appResponse.Data.secret
            Write-Success "Created restricted application"
            Write-Debug "Client Key: $script:restrictedClientKey"
        } else {
            Write-Fail "Failed to create restricted application"
            return $false
        }
    } else {
        Write-Fail "Failed to create vendor"
        return $false
    }
    
    # Create SIS vendor for comparison
    $sisVendorBody = @{
        company = "SIS Vendor"
        contactName = "SIS User"
        contactEmailAddress = "sis@example.com"  
        namespacePrefixes = "uri://ed-fi.org"
    }
    
    $sisVendorResponse = Invoke-HttpRequest -Uri "$configUrl/v2/vendors" -Method POST -Headers $headers -Body $sisVendorBody
    
    if ($sisVendorResponse.Success) {
        Write-Success "Created SIS vendor"
        $sisVendorLocation = $sisVendorResponse.Data.Headers.Location
        
        # Get vendor ID
        $sisVendorDetails = Invoke-HttpRequest -Uri $sisVendorLocation -Headers $headers
        $sisVendorId = $sisVendorDetails.Data.id
        
        # Create SIS application
        $sisAppBody = @{
            vendorId = $sisVendorId
            applicationName = "SIS App"
            claimSetName = "SIS-Vendor"
        }
        
        $sisAppResponse = Invoke-HttpRequest -Uri "$configUrl/v2/applications" -Method POST -Headers $headers -Body $sisAppBody
        
        if ($sisAppResponse.Success) {
            $script:sisClientKey = $sisAppResponse.Data.key
            $script:sisClientSecret = $sisAppResponse.Data.secret
            Write-Success "Created SIS application"
            Write-Debug "SIS Client Key: $script:sisClientKey"
        }
    }
    
    return $true
}

# Step 3: Get DMS Tokens
function Get-DmsTokens {
    Write-Step "Getting DMS Tokens"
    
    # Get token URL from discovery
    $discoveryResponse = Invoke-HttpRequest -Uri $dmsUrl
    $tokenUrl = $discoveryResponse.Data.urls.oauth
    $script:dataApi = $discoveryResponse.Data.urls.dataManagementApi
    
    # If discovery returns localhost:8080, use Keycloak directly
    if ($tokenUrl -like "*localhost:8080*") {
        $tokenUrl = "$keycloakUrl/realms/edfi/protocol/openid-connect/token"
        Write-Info "Using Keycloak token endpoint: $tokenUrl"
    }
    
    # Get restricted client token
    $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${script:restrictedClientKey}:${script:restrictedClientSecret}"))
    $tokenResponse = Invoke-HttpRequest -Uri $tokenUrl -Method POST `
        -Headers @{ Authorization = "Basic $authHeader" } `
        -Body @{ grant_type = "client_credentials" }
    
    if ($tokenResponse.Success) {
        $script:restrictedToken = $tokenResponse.Data.access_token
        Write-Success "Got restricted client token"
    } else {
        Write-Fail "Failed to get restricted token"
        return $false
    }
    
    # Get SIS client token  
    $sisAuthHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${script:sisClientKey}:${script:sisClientSecret}"))
    $sisTokenResponse = Invoke-HttpRequest -Uri $tokenUrl -Method POST `
        -Headers @{ Authorization = "Basic $sisAuthHeader" } `
        -Body @{ grant_type = "client_credentials" }
    
    if ($sisTokenResponse.Success) {
        $script:sisToken = $sisTokenResponse.Data.access_token
        Write-Success "Got SIS client token"
    }
    
    return $true
}

# Step 4: Test Initial Authorization
function Test-InitialAuthorization {
    Write-Step "Testing Initial Authorization (Before Claim Upload)"
    
    # Test restricted client - should NOT have student access
    Write-Info "Testing restricted client student access (expect 403)..."
    $restrictedStudentResponse = Invoke-HttpRequest -Uri "$script:dataApi/ed-fi/students" `
        -Headers @{ Authorization = "Bearer $script:restrictedToken" }
    
    if ($restrictedStudentResponse.StatusCode -eq 403) {
        Write-Success "Restricted client correctly denied student access (403)"
    } else {
        Write-Fail "Unexpected response for restricted client: $($restrictedStudentResponse.StatusCode)"
    }
    
    # Test restricted client - should have school access
    Write-Info "Testing restricted client school access (expect 200)..."
    $restrictedSchoolResponse = Invoke-HttpRequest -Uri "$script:dataApi/ed-fi/schools" `
        -Headers @{ Authorization = "Bearer $script:restrictedToken" }
    
    if ($restrictedSchoolResponse.StatusCode -eq 200) {
        Write-Success "Restricted client has school access (200)"
    } else {
        Write-Fail "Restricted client denied school access: $($restrictedSchoolResponse.StatusCode)"
    }
    
    # Test SIS client for comparison
    Write-Info "Testing SIS client student access (expect 200)..."
    $sisStudentResponse = Invoke-HttpRequest -Uri "$script:dataApi/ed-fi/students" `
        -Headers @{ Authorization = "Bearer $script:sisToken" }
    
    if ($sisStudentResponse.StatusCode -eq 200) {
        Write-Success "SIS client has student access (200)"
    } else {
        Write-Fail "SIS client denied student access: $($sisStudentResponse.StatusCode)"
    }
}

# Step 5: Check Current Claims
function Check-CurrentClaims {
    Write-Step "Checking Current Claims in CMS"
    
    $headers = @{ Authorization = "Bearer $script:sysAdminToken" }
    
    # Get authorization metadata for restricted claim set
    $metadataResponse = Invoke-HttpRequest `
        -Uri "$configUrl/config/authorizationMetadata?claimSetName=E2E-RelationshipsWithEdOrgsOnlyClaimSet" `
        -Headers $headers
    
    if ($metadataResponse.Success) {
        Write-Success "Retrieved authorization metadata"
        
        # Check if students claim exists
        $hasStudentClaim = $false
        foreach ($item in $metadataResponse.Data) {
            foreach ($claim in $item.claims) {
                if ($claim.name -like "*students*") {
                    $hasStudentClaim = $true
                    Write-Info "Found student claim: $($claim.name)"
                }
            }
        }
        
        if (-not $hasStudentClaim) {
            Write-Info "No student claims found for E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        }
    }
}

# Step 6: Upload New Claims
function Upload-NewClaims {
    Write-Step "Uploading New Claims to CMS"
    
    $headers = @{ Authorization = "Bearer $script:sysAdminToken" }
    
    # Create complete claim upload with student access
    $claimUpload = @{
        claims = @{
            claimSets = @(
                @{
                    claimSetName = "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
                    isSystemReserved = $false
                },
                @{
                    claimSetName = "SIS-Vendor"
                    isSystemReserved = $false
                }
            )
            claimsHierarchy = @(
                @{
                    name = "http://ed-fi.org/identity/claims/domains/edFiTypes"
                    claims = @(
                        @{
                            name = "http://ed-fi.org/identity/claims/ed-fi/schools"
                            claimSets = @(
                                @{
                                    name = "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
                                    actions = @(
                                        @{ name = "Create"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Read"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Update"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Delete"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) }
                                    )
                                },
                                @{
                                    name = "SIS-Vendor"
                                    actions = @(
                                        @{ name = "Create"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Read"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Update"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Delete"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) }
                                    )
                                }
                            )
                        },
                        @{
                            name = "http://ed-fi.org/identity/claims/ed-fi/students"
                            claimSets = @(
                                @{
                                    name = "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
                                    actions = @(
                                        @{ name = "Create"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Read"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Update"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Delete"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) }
                                    )
                                },
                                @{
                                    name = "SIS-Vendor"
                                    actions = @(
                                        @{ name = "Create"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Read"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Update"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) },
                                        @{ name = "Delete"; authorizationStrategyOverrides = @(@{ name = "NoFurtherAuthorizationRequired" }) }
                                    )
                                }
                            )
                        }
                    )
                }
            )
        }
    }
    
    $uploadResponse = Invoke-HttpRequest -Uri "$configUrl/config/management/upload-claims" `
        -Method POST -Headers $headers -Body $claimUpload
    
    if ($uploadResponse.Success) {
        Write-Success "Claims uploaded successfully"
        Write-Info "Reload ID: $($uploadResponse.Data.reloadId)"
        return $true
    } else {
        Write-Fail "Failed to upload claims"
        Write-Fail "Error: $($uploadResponse.ErrorBody | ConvertTo-Json -Depth 3)"
        return $false
    }
}

# Step 7: Verify Claims Were Updated
function Verify-ClaimsUpdated {
    Write-Step "Verifying Claims Were Updated in CMS"
    
    $headers = @{ Authorization = "Bearer $script:sysAdminToken" }
    
    # Check current claims
    $currentClaimsResponse = Invoke-HttpRequest -Uri "$configUrl/config/management/current-claims" -Headers $headers
    
    if ($currentClaimsResponse.Success) {
        Write-Success "Retrieved current claims"
        
        # Check if our claim sets are present
        $foundRestrictedSet = $false
        $foundSisSet = $false
        
        foreach ($claimSet in $currentClaimsResponse.Data.claimSets) {
            if ($claimSet.claimSetName -eq "E2E-RelationshipsWithEdOrgsOnlyClaimSet") {
                $foundRestrictedSet = $true
            }
            if ($claimSet.claimSetName -eq "SIS-Vendor") {
                $foundSisSet = $true
            }
        }
        
        if ($foundRestrictedSet -and $foundSisSet) {
            Write-Success "Both claim sets found in uploaded claims"
        } else {
            Write-Fail "Missing claim sets in uploaded claims"
        }
    }
    
    # Check authorization metadata again
    $metadataResponse = Invoke-HttpRequest `
        -Uri "$configUrl/config/authorizationMetadata?claimSetName=E2E-RelationshipsWithEdOrgsOnlyClaimSet" `
        -Headers $headers
    
    if ($metadataResponse.Success) {
        Write-Success "Retrieved updated authorization metadata"
        
        # Check if students claim exists now
        $hasStudentClaim = $false
        foreach ($item in $metadataResponse.Data) {
            foreach ($claim in $item.claims) {
                if ($claim.name -like "*students*") {
                    $hasStudentClaim = $true
                    Write-Success "Student claim now present: $($claim.name)"
                }
            }
        }
        
        if (-not $hasStudentClaim) {
            Write-Fail "Student claims still missing after upload!"
        }
    }
}

# Step 8: Test Authorization After Upload
function Test-AuthorizationAfterUpload {
    Write-Step "Testing Authorization After Claim Upload"
    
    Write-Info "Generating new token for restricted client..."
    
    # Get new token for restricted client
    $tokenUrl = "$keycloakUrl/realms/edfi/protocol/openid-connect/token"
    $authHeader = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${script:restrictedClientKey}:${script:restrictedClientSecret}"))
    $newTokenResponse = Invoke-HttpRequest -Uri $tokenUrl -Method POST `
        -Headers @{ Authorization = "Basic $authHeader" } `
        -Body @{ grant_type = "client_credentials" }
    
    if ($newTokenResponse.Success) {
        $newRestrictedToken = $newTokenResponse.Data.access_token
        Write-Success "Got new token for restricted client"
        
        # Start monitoring DMS logs if requested
        if ($MonitorLogs) {
            Write-Info "Starting DMS log monitoring (press Ctrl+C to stop)..."
            Start-DmsLogMonitor
        }
        
        # Test with new token
        Write-Info "Testing restricted client student access with new token..."
        $studentResponse = Invoke-HttpRequest -Uri "$script:dataApi/ed-fi/students" `
            -Headers @{ Authorization = "Bearer $newRestrictedToken" }
        
        if ($studentResponse.StatusCode -eq 200 -or $studentResponse.StatusCode -eq 201) {
            Write-Success "SUCCESS! Restricted client now has student access!"
        } elseif ($studentResponse.StatusCode -eq 403) {
            Write-Fail "Still getting 403 - DMS may need restart or doesn't reload claims"
            Write-Info "Try restarting DMS and running this script again with -SkipSetup"
            
            # Show recent DMS logs for debugging
            Show-RecentDmsLogs
        } else {
            Write-Fail "Unexpected response: $($studentResponse.StatusCode)"
        }
    }
}

# Helper function to show recent DMS logs
function Show-RecentDmsLogs {
    Write-Step "Recent DMS Logs (last 20 lines)"
    
    try {
        $logs = docker logs dms-local-dms-1 --tail 20 2>&1
        Write-Host $logs -ForegroundColor Gray
    } catch {
        Write-Info "Could not retrieve DMS logs. Check container name with: docker ps"
    }
}

# Helper function to monitor DMS logs
function Start-DmsLogMonitor {
    Write-Info "Monitoring DMS logs in background..."
    
    # Start log monitoring in background job
    $script:logJob = Start-Job -ScriptBlock {
        docker logs dms-local-dms-1 --follow --tail 10 2>&1
    }
    
    # Give it a moment to start
    Start-Sleep -Seconds 2
    
    # Show any output
    if ($script:logJob) {
        Receive-Job $script:logJob -Keep
    }
}

# Main execution
Write-Host "`nClaim Upload Authorization Debug Script" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

if (-not $SkipSetup) {
    if (-not (Setup-SystemAdmin)) { exit 1 }
    if (-not (Setup-Clients)) { exit 1 }
}

if (-not (Get-DmsTokens)) { exit 1 }
Test-InitialAuthorization
Check-CurrentClaims

if (-not $SkipSetup) {
    if (Upload-NewClaims) {
        Verify-ClaimsUpdated
    }
}

Test-AuthorizationAfterUpload

Write-Host "`n" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Debugging Complete" -ForegroundColor Cyan
Write-Host "" -ForegroundColor Cyan
Write-Host "If authorization still fails after claim upload:" -ForegroundColor Yellow
Write-Host "1. Restart DMS service to reload claims" -ForegroundColor Yellow
Write-Host "2. Run this script again with -SkipSetup flag" -ForegroundColor Yellow
Write-Host "3. Check DMS logs for authorization errors" -ForegroundColor Yellow