# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Keycloak server url
    [string]
    $KeycloakServer = "http://localhost:8045",

    # Admin realm for admin access
    [string]
    $AdminRealm = "master",

    # Realm name
    [string]
    $Realm = "edfi",

    # Admin username
    [string]
    $AdminUsername = "admin",

    # Admin password
    [string]
    $AdminPassword = "admin",

    # Client Id for accessing Keycloak admin API
    [string]
    $AdminClientId = "admin-cli",

    # Client Id
    [string]
    $NewClientId = "test-client",

    # Client Name
    [string]
    $NewClientName = "Test Client",

    # Client Secret -Client secret must contain at least one lowercase letter, one uppercase letter,
    # one number, and one special character, and must be 8 to 12 characters long.
    [string]
    $NewClientSecret = "s3creT@09",

    # DMS specific client role
    [string]
    $NewClientRole = "dms-client",

    # Scope name should match the ClaimSet name
    [string]
    $ClientScopeName = "sis-vendor",

    # Name of the hardcoded claim
    [string]
    $ClaimName = "namespacePrefixes",

    # Value of the hardcoded claim
    [string]
    $ClaimValue = "http://ed-fi.org",

    # Token life span
    [int]
    $TokenLifespan = 1800

)

function Get_Access_Token()
{
    $TokenResponse = Invoke-RestMethod -Uri "$KeycloakServer/realms/$AdminRealm/protocol/openid-connect/token" `
    -Method Post `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        client_id = $AdminClientId
        username = $AdminUsername
        password = $AdminPassword
        grant_type = "password"
    }
    return $TokenResponse.access_token
}

function Check_RealmExists () {
    try {
        # Check if the realm exists
        Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm" `
            -Method Get `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -ErrorAction Stop
        return $true
    } catch {
        # The realm does not exist
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $false
        } else {
            throw $_.Exception.Response
        }
    }
}

function Create_Realm() {
    # Define the new realm configuration
    $RealmData = @{
        id = $Realm
        realm = $Realm
        displayName = $Realm
        enabled = $true
    }
    # Create the new realm
    try
    {
        Invoke-RestMethod -Uri "$KeycloakServer/admin/realms" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -ContentType "application/json" `
            -Body ($RealmData | ConvertTo-Json -Depth 10)

            $realmSettingsPayload = @{
                accessTokenLifespan = $TokenLifespan
            } | ConvertTo-Json

            Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm" `
                -Method Put `
                -Headers @{ "Authorization" = "Bearer $access_token" } `
                -Body $realmSettingsPayload `
                -ContentType "application/json"

        Write-Output "Realm created successfully: $Realm"
    }
    catch
    {
        Write-Error $_.Exception.Response
    }
}

function Get_Client ()
{
    try{
        $existingClient = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients?clientId=$NewClientId" `
        -Method Get `
        -Headers  @{ Authorization = "Bearer $access_token" }
        return $existingClient
    } catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $null
        }
        else{
            Write-Error $_.Exception.Response
        }
    }
}

function Get_Role([string] $roleName)
{
    try {
        $existingRole = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/roles/$roleName" `
        -Method Get `
        -Headers @{ Authorization = "Bearer $access_token" }
        return $existingRole
    } catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $null
        }
        else{
            Write-Error $_.Exception.Response
        }
    }
}

function Create_Role([string] $roleName)
{
    $existingRole = Get_Role $roleName
    if (-not $existingRole) {
        # Create the realm role
        $rolePayload = @{
            name = $roleName
        } | ConvertTo-Json

        Invoke-RestMethod -Method Post `
            -Uri "$KeycloakServer/admin/realms/$Realm/roles" `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $rolePayload `
            -ContentType "application/json"
        Write-Output "Role $roleName crearted successfully."
    } else {
       Write-Output "Role $roleName already exists."
    }
}

function Get_ClientScope([string] $scopeName)
{
    $existingClientScopes = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/client-scopes" `
    -Method Get `
    -Headers @{ Authorization = "Bearer $access_token" }

    $clientScope = $existingClientScopes | Where-Object { $_.name -eq $scopeName }

    return $clientScope
}

function Create_ClientScope([string] $scopeName)
{
    $existingClientScope = Get_ClientScope $scopeName

    if ($existingClientScope) {
        Write-Output "Client scope '$scopeName' already exists."
    } else {
        # Create the client scope
        $clientScopePayload = @{
            name = $scopeName
            protocol = "openid-connect"
        } | ConvertTo-Json

        Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/client-scopes" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $clientScopePayload `
            -ContentType "application/json"
        Write-Output "Client scope '$scopeName' created successfully."
    }
}

function Assign_RealmRole([object] $role, [string] $ClientId)
{
    # Assign the realm role to the client’s service account
    # Get the service account user ID for the client
    $serviceAccountUser = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients/$ClientId/service-account-user" `
    -Method Get `
    -Headers @{ Authorization = "Bearer $access_token" }

    $ServiceAccountUserId = $serviceAccountUser.id

    $roleAssignmentPayload = @($role) | ConvertTo-Json
    $rolesArray = "[ $roleAssignmentPayload ]"

    Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/users/$ServiceAccountUserId/role-mappings/realm" `
    -Method Post `
    -Headers @{ Authorization = "Bearer $access_token" } `
    -Body $rolesArray `
    -ContentType "application/json"

    Write-Output "Role '$NewClientRole' assigned as a service account role to client '$NewClientName'."
}

function Add_Role_To_Token([string] $ClientId)
{
    # Add ProtocolMapper to include the role in the token
    $protocolMapperPayload = @{
        name = "Dms service role mapper"
        protocol = "openid-connect"
        protocolMapper = "oidc-usermodel-realm-role-mapper"
        config = @{
            "claim.name" = "http://schemas\.microsoft\.com/ws/2008/06/identity/claims/role"
            "jsonType.label" = "String"
            "userinfo.token.claim" = "true"
            "id.token.claim" = "true"
            "access.token.claim" = "true"
            "multivalued" = "true"
        }
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients/$ClientId/protocol-mappers/models" `
    -Method Post `
    -Headers @{ Authorization = "Bearer $access_token" } `
    -Body $protocolMapperPayload `
    -ContentType "application/json"

    Write-Output "ProtocolMapper added to client '$NewClientName' to map '$NewClientRole' in tokens."
}

function Add_Scope([string] $scopeId)
{
     # Assign the client scope to the client
     Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients/$ClientId/default-client-scopes/$scopeId" `
     -Method Put `
     -Headers @{ "Authorization" = "Bearer $access_token" } `
     -ContentType "application/json"

     Write-Output "Claim set scope added to client '$NewClientName'."
}

function Add_Cutom_Claim([string] $ClientId)
{
     # Add custom claim for "namespacePrefixes"
     $customClaimProtocolMapperPayload = @{
        name = $ClaimName
        protocol = "openid-connect"
        protocolMapper = "oidc-hardcoded-claim-mapper"
        config = @{
            "claim.name" = $ClaimName
            "claim.value" = $ClaimValue
            "jsonType.label" = "String"
            "id.token.claim" = "true"
            "access.token.claim" = "true"
            "userinfo.token.claim" = "true"
        }
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients/$ClientId/protocol-mappers/models" `
        -Method Post `
        -Headers @{ "Authorization" = "Bearer $access_token" } `
        -Body $customClaimProtocolMapperPayload `
        -ContentType "application/json"

    Write-Output "Custom claim added to client '$NewClientName'."
}

function Create_Client()
{
    # Define the new client configuration
    $ClientData = @{
        clientId     = $NewClientId
        name         = $NewClientName
        secret       = $NewClientSecret
        protocol     = "openid-connect"
        serviceAccountsEnabled = $true
        publicClient = $false
    }
    # Create a new client
    Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -ContentType "application/json" `
        -Body ($ClientData | ConvertTo-Json -Depth 10)

    Write-Output "Client created successfully: $NewClientName"

    $client = Get_Client
    return $client
}

# Keycloak health check
try {
    Invoke-RestMethod -Method Get `
        -Uri "$KeycloakServer/realms/master" `
        -TimeoutSec 5
    Write-Output "Keycloak is running."
} catch {
    Write-Error "Keycloak is not running. Please start Keycloak and try again."
    exit
}

# Get access token
$access_token = Get_Access_Token

# Create Realm
if( -not (Check_RealmExists)){
    Create_Realm
}
else{
    Write-Output "Realm already exists: $Realm"
}

# Create a required role
Create_Role $NewClientRole

# Create custom scope
Create_ClientScope $ClientScopeName

# Check and create client
$client = Get_Client
if($client){
     Write-Warning "Client '$NewClientId' already exists. Please provide new client id."
}
else{
    $client = Create_Client
    $clientId = $client.id
    $clientRole = Get_Role $NewClientRole
    Assign_RealmRole $clientRole $clientId
    Add_Role_To_Token $clientId
    $clientScope = Get_ClientScope $ClientScopeName
    Add_Scope $clientScope.id
    Add_Cutom_Claim $clientId
}
