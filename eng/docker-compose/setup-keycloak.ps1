# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Local Keycloak bootstrap script passes a development password to HTTP requests and container setup where plaintext is required.')]
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
    $NewClientId = "DmsConfigurationService",

    # Client Name
    [string]
    $NewClientName = "DMS Configuration Service",

    # Client Secret -Client secret must contain at least one lowercase letter, one uppercase letter,
    # one number, and one special character, and must be 32 to 128 characters long.
    [string]
    $NewClientSecret = "ValidClientSecret1234567890!Abcd",

    [int]
    $ClientSecretMinimumLength = 32,

    [int]
    $ClientSecretMaximumLength = 128,

    # DMS specific client role
    [string]
    $DmsClientRole = "dms-client",

    # Role name for Config Service accounts
    [string]
    $ConfigServiceRole = "cms-client",

    # Admin API's client scope
    [string]
    $ClientScopeName = "edfi_admin_api/full_access",

    # Name of the hardcoded claim
    [string]
    $ClaimName = "namespacePrefixes",

    # Value of the hardcoded claim
    [string]
    $ClaimValue = "http://ed-fi.org",

    # Token life span
    [int]
    $TokenLifespan = 1800,

    # Skip granting the Keycloak realm-admin role to the new client. Use for clients that only
    # need the config-service role + scope to call the CMS Admin API and never call the Keycloak
    # Admin API themselves (e.g. a CMS bootstrap-admin client), so their credential is not also a
    # realm administrator. Read-only clients never get realm-admin regardless of this switch.
    [switch]
    $SkipRealmAdmin
)

$script:KeycloakServer = $KeycloakServer
$script:AdminRealm = $AdminRealm
$script:Realm = $Realm
$script:AdminUsername = $AdminUsername
$script:AdminPassword = $AdminPassword
$script:AdminClientId = $AdminClientId
$script:NewClientId = $NewClientId
$script:NewClientName = $NewClientName
$script:NewClientSecret = $NewClientSecret
$script:ClientSecretMinimumLength = $ClientSecretMinimumLength
$script:ClientSecretMaximumLength = $ClientSecretMaximumLength
$script:DmsClientRole = $DmsClientRole
$script:ConfigServiceRole = $ConfigServiceRole
$script:ClientScopeName = $ClientScopeName
$script:ClaimName = $ClaimName
$script:ClaimValue = $ClaimValue
$script:TokenLifespan = $TokenLifespan
$script:SkipRealmAdmin = [bool]$SkipRealmAdmin

function Test_ClientSecretLength() {
    param([string] $ClientSecret)

    if ($ClientSecret.Length -lt $ClientSecretMinimumLength -or $ClientSecret.Length -gt $ClientSecretMaximumLength) {
        throw "NewClientSecret must be between $ClientSecretMinimumLength and $ClientSecretMaximumLength characters long."
    }
}

function Test_ClientSecretComplexity() {
    param([string] $ClientSecret)

    $complexityPattern = '^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*()\-_=\+\[\]{}:;,.?]).{' + $ClientSecretMinimumLength + ',' + $ClientSecretMaximumLength + '}$'

    if ($ClientSecret -notmatch $complexityPattern) {
        throw "NewClientSecret must contain at least one lowercase letter, one uppercase letter, one number, and one special character from !@#$%^&*()-_=+[]{}:;,.? and must be between $ClientSecretMinimumLength and $ClientSecretMaximumLength characters long."
    }
}

function Get_Access_Token() {
    $TokenResponse = Invoke-RestMethod -Uri "$script:KeycloakServer/realms/$script:AdminRealm/protocol/openid-connect/token" `
        -Method Post `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{
        client_id  = $script:AdminClientId
        username   = $script:AdminUsername
        password   = $script:AdminPassword
        grant_type = "password"
    }
    return $TokenResponse.access_token
}

function Check_RealmExists () {
    try {
        # Check if the realm exists
        Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm" `
            -Method Get `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -ErrorAction Stop
        return $true
    }
    catch {
        # The realm does not exist
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $false
        }
        else {
            throw $_.Exception.Response
        }
    }
}

function Create_Realm() {
    # Define the new realm configuration
    $RealmData = @{
        id          = $script:Realm
        realm       = $script:Realm
        displayName = $script:Realm
        enabled     = $true
    }
    # Create the new realm
    try {
        Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -ContentType "application/json" `
            -Body ($RealmData | ConvertTo-Json -Depth 10)

        $realmSettingsPayload = @{
            accessTokenLifespan = $script:TokenLifespan
        } | ConvertTo-Json

        Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm" `
            -Method Put `
            -Headers @{ "Authorization" = "Bearer $access_token" } `
            -Body $realmSettingsPayload `
            -ContentType "application/json"

        Write-Output "Realm created successfully: $script:Realm"
    }
    catch {
        Write-Error $_.Exception.Response
    }
}

function Get_Client () {
    try {
        $existingClient = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients?clientId=$script:NewClientId" `
            -Method Get `
            -Headers  @{ Authorization = "Bearer $access_token" }
        return $existingClient
    }
    catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $null
        }
        else {
            Write-Error $_.Exception.Response
        }
    }
}

# The role names are configured values (CONFIGURATION_SERVICE_IDENTITY_SERVICE_ROLE / _CLIENT_ROLE, routed here
# by the start scripts) and travel as one path segment, so they are percent-encoded: a space, '#',
# '?' or '/' pasted raw would end the path, start a fragment or query, or add a segment, the lookup
# would answer 404 for a role that exists, and Create_Role would create it again under the raw name.
function Get_Role([string] $roleName) {
    try {
        $existingRole = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/roles/$([uri]::EscapeDataString($roleName))" `
            -Method Get `
            -Headers @{ Authorization = "Bearer $access_token" }
        return $existingRole
    }
    catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $null
        }
        else {
            Write-Error $_.Exception.Response
        }
    }
}

function Get_Realm_Management_ClientId() {
    $realmManagementClient = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients?clientId=realm-management" `
        -Method Get `
        -Headers @{ Authorization = "Bearer $access_token" }

    $RealmManagementClientId = $realmManagementClient[0].id

    return $RealmManagementClientId
}

function Get_Realm_Admin_Role([string] $roleName) {
    try {

        $RealmManagementClientId = Get_Realm_Management_ClientId
        $realmAdminRole = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$RealmManagementClientId/roles/$([uri]::EscapeDataString($roleName))" `
            -Method Get `
            -Headers @{ Authorization = "Bearer $access_token" }

        return $realmAdminRole

    }
    catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 404) {
            return $null
        }
        else {
            Write-Error $_.Exception.Response
        }
    }
}


function Create_Role([string] $roleName) {
    $existingRole = Get_Role $roleName
    if (-not $existingRole) {
        # Create the realm role
        $rolePayload = @{
            name = $roleName
        } | ConvertTo-Json

        Invoke-RestMethod -Method Post `
            -Uri "$script:KeycloakServer/admin/realms/$script:Realm/roles" `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $rolePayload `
            -ContentType "application/json"
        Write-Output "Role $roleName created successfully."
    }
    else {
        Write-Output "Role $roleName already exists."
    }
}

function Get_ClientScope([string] $scopeName) {
    $existingClientScopes = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/client-scopes" `
        -Method Get `
        -Headers @{ Authorization = "Bearer $access_token" }

    $clientScope = $existingClientScopes | Where-Object { $_.name -eq $scopeName }

    return $clientScope
}

function Create_ClientScope([string] $scopeName) {
    $existingClientScope = Get_ClientScope $scopeName

    if ($existingClientScope) {
        Write-Output "Client scope '$scopeName' already exists."
    }
    else {
        # Create the client scope
        $clientScopePayload = @{
            name            = $scopeName
            protocol        = "openid-connect"
            protocolMappers = @(@{
                    name            = $scopeName
                    protocol        = "openid-connect"
                    protocolMapper  = "oidc-audience-resolve-mapper"
                    consentRequired = "false"
                    config          = @{
                        "introspection.token.claim" = "true"
                        "access.token.claim"        = "true"
                    }
                })
        } | ConvertTo-Json -Depth 3

        Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/client-scopes" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $access_token" } `
            -Body $clientScopePayload `
            -ContentType "application/json"
        Write-Output "Client scope '$scopeName' created successfully."
    }
}

function Assign_RealmRole([object] $role, [string] $ClientId) {
    # Assign the realm role to the client’s service account
    # Get the service account user ID for the client
    $serviceAccountUser = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/service-account-user" `
        -Method Get `
        -Headers @{ Authorization = "Bearer $access_token" }

    $ServiceAccountUserId = $serviceAccountUser.id

    $roleAssignmentPayload = @($role) | ConvertTo-Json
    $rolesArray = "[ $roleAssignmentPayload ]"

    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/users/$ServiceAccountUserId/role-mappings/realm" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -Body $rolesArray `
        -ContentType "application/json"

    Write-Output "Role '$role' assigned as a service account role to clientId '$ClientId'."
}

function Assign_Realm_Admin_Role([object] $role, [string] $ClientId) {
    # Assign the realm role to the client’s service account
    # Get the service account user ID for the client
    $serviceAccountUser = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/service-account-user" `
        -Method Get `
        -Headers @{ Authorization = "Bearer $access_token" }

    $ServiceAccountUserId = $serviceAccountUser.id

    $roleAssignmentPayload = @($role) | ConvertTo-Json
    $rolesArray = "[ $roleAssignmentPayload ]"

    $rmClientId = Get_Realm_Management_ClientId

    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/users/$ServiceAccountUserId/role-mappings/clients/$rmClientId" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -Body $rolesArray `
        -ContentType "application/json"

    Write-Output "Role 'realm-admin' assigned as a service account role to clientId '$ClientId'."
}

function Add_Role_To_Token([string] $ClientId) {
    # Add ProtocolMapper to include the role in the token
    $protocolMapperPayload = @{
        name           = "Dms service role mapper"
        protocol       = "openid-connect"
        protocolMapper = "oidc-usermodel-realm-role-mapper"
        config         = @{
            "claim.name"           = "http://schemas\.microsoft\.com/ws/2008/06/identity/claims/role"
            "jsonType.label"       = "String"
            "userinfo.token.claim" = "true"
            "id.token.claim"       = "true"
            "access.token.claim"   = "true"
            "multivalued"          = "true"
        }
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/protocol-mappers/models" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -Body $protocolMapperPayload `
        -ContentType "application/json"

    Write-Output "ProtocolMapper added to client '$script:NewClientName' to map '$script:DmsClientRole' in tokens."
}

function Add_Scope([string] $scopeId) {
    # Assign the client scope to the client
    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/default-client-scopes/$scopeId" `
        -Method Put `
        -Headers @{ "Authorization" = "Bearer $access_token" } `
        -ContentType "application/json"

    Write-Output "Claim set scope added to client '$script:NewClientName'."
}

function Add_Custom_Claim([string] $ClientId) {
    # Add custom claim for "namespacePrefixes"
    $customClaimProtocolMapperPayload = @{
        name           = $script:ClaimName
        protocol       = "openid-connect"
        protocolMapper = "oidc-hardcoded-claim-mapper"
        config         = @{
            "claim.name"           = $script:ClaimName
            "claim.value"          = $script:ClaimValue
            "jsonType.label"       = "String"
            "id.token.claim"       = "true"
            "access.token.claim"   = "true"
            "userinfo.token.claim" = "true"
        }
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/protocol-mappers/models" `
        -Method Post `
        -Headers @{ "Authorization" = "Bearer $access_token" } `
        -Body $customClaimProtocolMapperPayload `
        -ContentType "application/json"

    Write-Output "Custom claim added to client '$script:NewClientName'."
}

function Add_Self_Audience([string] $ClientId) {
    # From Keycloak 26.6.2 token introspection answers active:false unless the introspecting client
    # is in the token's audience, so the client names itself there, as the Configuration Service does
    # for the clients it creates.
    $selfAudienceProtocolMapperPayload = @{
        name           = "Client's own audience"
        protocol       = "openid-connect"
        protocolMapper = "oidc-audience-mapper"
        config         = @{
            "included.client.audience"  = $script:NewClientId
            "id.token.claim"            = "false"
            "access.token.claim"        = "true"
            "introspection.token.claim" = "true"
            "lightweight.claim"         = "false"
        }
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/protocol-mappers/models" `
        -Method Post `
        -Headers @{ "Authorization" = "Bearer $access_token" } `
        -Body $selfAudienceProtocolMapperPayload `
        -ContentType "application/json"

    Write-Output "Own audience added to client '$script:NewClientName'."
}

function Test_GivesOwnAudience([object] $Mapper) {
    # The criteria the Configuration Service applies to the clients it manages: an OpenID Connect
    # audience mapper adding this client -- its included client audience, or its included custom
    # audience when it has none -- to access tokens satisfies it, whatever that mapper is called.
    if ($Mapper.protocol -cne "openid-connect" -or $Mapper.protocolMapper -cne "oidc-audience-mapper" -or $null -eq $Mapper.config) {
        return $false
    }

    $audience = $Mapper.config.'included.client.audience'
    if ($null -eq $audience) {
        $audience = $Mapper.config.'included.custom.audience'
    }

    return ($audience -ceq $script:NewClientId) -and ($Mapper.config.'access.token.claim' -ceq "true")
}

function Repair_Self_Audience([string] $ClientId) {
    # An existing client may predate its own-audience mapper. The mapper is added only when no mapper
    # already gives the client its own audience, and every other mapper is left as it is. Only tokens
    # issued after the mapper is added carry the audience; tokens issued before keep theirs.
    $protocolMappers = Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients/$ClientId/protocol-mappers/models" `
        -Method Get `
        -Headers @{ "Authorization" = "Bearer $access_token" }

    if ($protocolMappers | Where-Object { Test_GivesOwnAudience $_ }) {
        Write-Output "Client '$script:NewClientId' already puts itself into its access tokens' audience."
        return
    }

    # Keycloak allows one mapper per name and protocol, and overwriting a mapper changed by an operator
    # could remove an audience something else relies on, so a conflicting mapper is kept as it is.
    if ($protocolMappers | Where-Object { $_.protocol -ceq "openid-connect" -and $_.name -ceq "Client's own audience" }) {
        Write-Warning "Client '$script:NewClientId' was NOT repaired: its OpenID Connect protocol mapper named 'Client's own audience' does not put the client into its access tokens' audience and was left unchanged. On Keycloak 26.6.2 or later, introspecting the client's own tokens reports them inactive until an operator corrects or removes that mapper and reruns this script."
        return
    }

    Add_Self_Audience $ClientId
    Write-Output "Tokens issued to client '$script:NewClientId' before this keep their audience; request a new token."
}

function Create_Client() {
    # Define the new client configuration
    $ClientData = @{
        clientId               = $script:NewClientId
        name                   = $script:NewClientName
        secret                 = $script:NewClientSecret
        protocol               = "openid-connect"
        serviceAccountsEnabled = $true
        publicClient           = $false
    }
    # Create a new client
    Invoke-RestMethod -Uri "$script:KeycloakServer/admin/realms/$script:Realm/clients" `
        -Method Post `
        -Headers @{ Authorization = "Bearer $access_token" } `
        -ContentType "application/json" `
        -Body ($ClientData | ConvertTo-Json -Depth 10)

    Write-Output "Client created successfully: $script:NewClientName"

    $client = Get_Client
    return $client
}

# Keycloak health check
while ($true) {
    try {
        $response = Invoke-WebRequest -Uri $script:KeycloakServer/realms/master -Method Get -TimeoutSec 5 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            Write-Output "Keycloak is running."
            break
        }
    } catch {
        Write-Verbose "Keycloak health check failed: $($_.Exception.Message)"
    }
    Write-Output "Keycloak is not running. Retrying in 5 seconds..."
    Start-Sleep -Seconds 5
}

# Get access token
$access_token = Get_Access_Token

# Create Realm
if ( -not (Check_RealmExists)) {
    Create_Realm
}
else {
    Write-Output "Realm already exists: $script:Realm"
}

# Check and create client
$client = Get_Client
if ($client) {
    Write-Output "Client '$script:NewClientId' already exists; checking its own audience."
    # Its credentials, roles and scopes are left as they are; only its own audience is repaired.
    Repair_Self_Audience $client[0].id
}
else {
    Test_ClientSecretLength -ClientSecret $script:NewClientSecret
    Test_ClientSecretComplexity -ClientSecret $script:NewClientSecret
    $client = Create_Client
    $clientId = $client.id
    $realmAdminRole = Get_Realm_Admin_Role "realm-admin"


    # Create a required role
    Create_Role $script:DmsClientRole
    Create_role $script:ConfigServiceRole
    # Create custom scope
    Create_ClientScope $script:ClientScopeName
    $clientRole = Get_Role $script:ConfigServiceRole
    Assign_RealmRole $clientRole $clientId
    # Conditionally assign realm-admin role: never to read-only clients, and never when the
    # caller passed -SkipRealmAdmin (clients that only call the CMS Admin API, not Keycloak's).
    if (-not $script:SkipRealmAdmin -and $script:NewClientId -ne 'CMSAuthMetadataReadOnlyAccess' -and $script:NewClientId -ne 'CMSReadOnlyAccess') {
        Assign_Realm_Admin_Role $realmAdminRole $clientId
    }
    Add_Role_To_Token $clientId
    $clientScope = Get_ClientScope $script:ClientScopeName
    Add_Scope $clientScope.id
    Add_Custom_Claim $clientId
    Add_Self_Audience $clientId
}
