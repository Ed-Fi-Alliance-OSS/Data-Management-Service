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
    $Username = "admin",

    # Admin password
    [string]
    $Password = "admin",

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
    $ClaimValue = "http://ed-fi.org"

)

function Get_Access_Token()
{
    $TokenResponse = Invoke-RestMethod -Uri "$KeycloakServer/realms/$AdminRealm/protocol/openid-connect/token" `
    -Method Post `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        client_id = $AdminClientId
        username = $Username
        password = $Password
        grant_type = "password"
    }
    return $TokenResponse.access_token
}

function Check_RealmExists ([string] $access_token) {
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
            throw
        }
    }
}

function Create_Realm([string] $access_token) {
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
        $CreateRealmResponse = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -ContentType "application/json" `
            -Body ($RealmData | ConvertTo-Json -Depth 10)

            $realmSettingsPayload = @{
                accessTokenLifespan = 1800
            } | ConvertTo-Json

            Write-Host $realmSettingsPayload

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

function Get_Client ([string] $access_token)
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

function Check_ClientExists ([string] $access_token) {
    $client = Get_Client $access_token
    if($client){
        return $true
    }
    else{
        return $false
    }
}

function Get_Role([string] $access_token)
{
    try {
        $existingRole = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/roles/$NewClientRole" `
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

function Create_Role([string] $access_token)
{
    $existingRole = Get_Role $access_token
    if (-not $existingRole) {
        # Create the realm role
        $rolePayload = @{
            name = $NewClientRole
        } | ConvertTo-Json

        Invoke-RestMethod -Method Post `
            -Uri "$KeycloakServer/admin/realms/$Realm/roles" `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $rolePayload `
            -ContentType "application/json"

        Write-Output "Realm role '$NewClientRole' created successfully."
    } else {
        Write-Output "Realm role '$NewClientRole' already exists."
    }
}

function Get_ClientScope([string] $access_token)
{
    $existingClientScopes = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/client-scopes" `
    -Method Get `
    -Headers @{ Authorization = "Bearer $access_token" }

    $clientScope = $existingClientScopes | Where-Object { $_.name -eq $ClientScopeName }

    return $clientScope
}

function Create_ClientScope([string] $access_token)
{
    $existingClientScope = Get_ClientScope $access_token

    if ($existingClientScope) {
        Write-Output "Client scope '$ClientScopeName' already exists."
    } else {
        # Create the client scope
        $clientScopePayload = @{
            name = $ClientScopeName
            protocol = "openid-connect"
        } | ConvertTo-Json

        $clientScopeCreationResponse = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/client-scopes" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $clientScopePayload `
            -ContentType "application/json"

        Write-Output "Client scope '$ClientScopeName' created successfully."

        # Retrieve the client scope ID
        $createdClientScope = Get_ClientScope $access_token
    }

    return $clientScopeId
}

function Create_Client([string] $access_token)
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

    # Retrieve the role ID
    $role = Get_Role $access_token

    # Create a new client
    $CreateClientResponse = Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -ContentType "application/json" `
        -Body ($ClientData | ConvertTo-Json -Depth 10)

    Write-Output "Client created successfully: $NewClientName"

    $client = Get_Client $access_token
    $ClientId = $client[0].id

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

    # Add Claim set scope
    $existingClientScope = Get_ClientScope $access_token
    $scopeId = $existingClientScope.id

    # Assign the client scope to the client
    Invoke-RestMethod -Uri "$KeycloakServer/admin/realms/$Realm/clients/$ClientId/default-client-scopes/$scopeId" `
    -Method Put `
    -Headers @{ "Authorization" = "Bearer $access_token" } `
    -ContentType "application/json"

    Write-Output "Claim set scope added to client '$NewClientName'."

    # Add custom claim for "namespacePrefixes"
    $customClaimProtocolMapperPayload = @{
        name = "namespacePrefixes"
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

    Write-Output "Client created and configured successfully: $NewClientName"
}

# Get access token
$token = Get_Access_Token

# Create Realm
if( -not (Check_RealmExists $token)){
    Create_Realm $token
}
else{
    Write-Output "Realm already exists: $Realm"
}

# Create a required role
Create_Role $token

# Create custom scope
Create_ClientScope $token

# Create client
if(Check_ClientExists $token){
     Write-Warning "Client '$NewClientId' already exists. Please provide new client id."
}
else{
    Create_Client $token
}
