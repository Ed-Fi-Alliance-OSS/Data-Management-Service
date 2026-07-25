# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Get-DbLocalEndpointIdentity derives the structured, non-secret local `db`-service identity from
# Compose-resolved service objects (Iteration 3). These tests exercise the pure derivation directly with
# synthetic service objects - fail-closed port extraction, IPv4-loopback host_ip normalization, the
# deterministically-resolvable in-network-name set (uniqueness proven against the whole service model), and
# the engine-specific container port / admin user - without Docker.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:composeRoot "env-utility.psm1") -Force

    function script:New-DbService {
        param(
            [AllowNull()][object]$ContainerName = 'dms-postgresql',
            [AllowNull()][object]$Hostname = 'dms-postgresql',
            [object]$Networks = ([pscustomobject]@{ dms = $null }),
            [object[]]$Ports = @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 5432; published = '5435'; protocol = 'tcp' }),
            [object]$Environment = ([pscustomobject]@{ POSTGRES_DB_NAME = 'edfi_datamanagementservice'; POSTGRES_USER = 'postgres' })
        )
        $service = [pscustomobject]@{ ports = $Ports; networks = $Networks; environment = $Environment }
        # container_name / hostname are added only when non-null so a "missing" case can omit them entirely.
        if ($null -ne $ContainerName) { $service | Add-Member -NotePropertyName container_name -NotePropertyValue $ContainerName }
        if ($null -ne $Hostname) { $service | Add-Member -NotePropertyName hostname -NotePropertyValue $Hostname }
        return $service
    }

    $script:ConfigServiceShared = [pscustomobject]@{ networks = [pscustomobject]@{ dms = $null } }

    # The complete Compose service model a config-bearing resolution requires. The db service under test is
    # skipped by name during collision detection, so the model's db entry only needs to exist; extra services
    # (for collision cases) are merged in by the caller.
    function script:New-ServiceModel {
        param([object]$Db, [hashtable]$Extra = @{})
        $model = [pscustomobject]@{
            db     = $Db
            config = $script:ConfigServiceShared
        }
        foreach ($name in $Extra.Keys) { $model | Add-Member -NotePropertyName $name -NotePropertyValue $Extra[$name] }
        return $model
    }
}

Describe "Get-DbLocalEndpointIdentity - successful derivation" {
    It "derives a PostgreSQL single-host endpoint with the published port and admin user" {
        $db = New-DbService
        $endpoint = Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql
        $endpoint.ServiceName | Should -Be 'db'
        $endpoint.ContainerName | Should -Be 'dms-postgresql'
        $endpoint.ContainerPort | Should -Be 5432
        $endpoint.PublishedHost | Should -Be '127.0.0.1'
        $endpoint.PublishedPort | Should -Be 5435
        $endpoint.PostgresAdminUser | Should -Be 'postgres'
        $endpoint.InNetworkNames | Should -Contain 'db'
        $endpoint.InNetworkNames | Should -Contain 'dms-postgresql'
    }

    It "uses the SQL Server container port (1433) and does not read a PostgreSQL admin user" {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 1433; published = '1435'; protocol = 'tcp' }) -Environment ([pscustomobject]@{ MSSQL_DB_NAME = 'edfi_datamanagementservice' })
        $endpoint = Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine mssql
        $endpoint.ContainerPort | Should -Be 1433
        $endpoint.PublishedPort | Should -Be 1435
        $endpoint.PostgresAdminUser | Should -BeNullOrEmpty
    }

    It "includes only aliases on a network shared with the Configuration Service" {
        $db = New-DbService -Networks ([pscustomobject]@{
                dms      = [pscustomobject]@{ aliases = @('db-shared-alias') }
                internal = [pscustomobject]@{ aliases = @('db-private-alias') }
            })
        # The Configuration Service joins only 'dms'.
        $endpoint = Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql
        $endpoint.InNetworkNames | Should -Contain 'db-shared-alias'
        $endpoint.InNetworkNames | Should -Not -Contain 'db-private-alias' -Because "an alias on a network CMS does not join is not reachable"
    }

    It "normalizes an unspecified/0.0.0.0 host_ip to the IPv4 loopback dial address" {
        foreach ($hostIp in @('', '0.0.0.0', '127.0.0.1')) {
            $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = $hostIp; target = 5432; published = '5435'; protocol = 'tcp' })
            (Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql).PublishedHost |
                Should -Be '127.0.0.1' -Because "host_ip '$hostIp' publishes on the loopback"
        }
    }

    It "keeps the host-side coordinates but claims no CMS-reachable names for a database-only compose set" {
        # No Configuration Service in the compose set (e.g. configure-local-data-store composes db-only): the
        # host-side dial coordinates are still resolved, but InNetworkNames must be empty - there is no CMS to
        # claim reachability for, and no complete service model is required.
        $endpoint = Get-DbLocalEndpointIdentity -DbService (New-DbService) -ConfigService $null -InfrastructureEngine postgresql
        $endpoint.ContainerName | Should -Be 'dms-postgresql'
        $endpoint.PublishedPort | Should -Be 5435
        $endpoint.PublishedHost | Should -Be '127.0.0.1'
        @($endpoint.InNetworkNames).Count | Should -Be 0 -Because "no Configuration Service is composed, so no CMS-reachable names are claimed"
    }

    It "excludes the container's own hostname from the reachable names when it diverges" {
        # Docker peer-resolves service names, container names, and network aliases - NOT a container's own
        # `hostname`, which is not automatically a network alias. A divergent hostname must never be advertised.
        $db = New-DbService -Hostname 'divergent-host'
        $endpoint = Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql
        $endpoint.InNetworkNames | Should -Not -Contain 'divergent-host' -Because "the container hostname is not a peer-resolvable network alias"
        $endpoint.InNetworkNames | Should -Contain 'db'
        $endpoint.InNetworkNames | Should -Contain 'dms-postgresql'
        $endpoint.Hostname | Should -Be 'divergent-host' -Because "the resolved hostname is still recorded as metadata, just not claimed as reachable"
    }
}

Describe "Get-DbLocalEndpointIdentity - uniqueness against the whole service model" {
    # Docker permits an alias (and, across services, a name) to be answered by more than one container. Any
    # candidate name a DIFFERENT service also answers to on a Configuration-Service network resolves
    # nondeterministically, so it must not be returned. Each candidate class - the service name, the container
    # name, and shared-network aliases - is checked.
    It "drops a database <Class> that another service also claims on the shared network" -ForEach @(
        @{ Class = 'network alias'; Collide = 'db-shared-alias'; Survivor = 'db-unique-alias'; DbNetworks = ([pscustomobject]@{ dms = [pscustomobject]@{ aliases = @('db-unique-alias', 'db-shared-alias') } }) }
        @{ Class = 'service name'; Collide = 'db'; Survivor = 'dms-postgresql'; DbNetworks = ([pscustomobject]@{ dms = $null }) }
        @{ Class = 'container name'; Collide = 'dms-postgresql'; Survivor = 'db'; DbNetworks = ([pscustomobject]@{ dms = $null }) }
    ) {
        $db = New-DbService -Networks $DbNetworks
        $model = New-ServiceModel -Db $db -Extra @{
            sneaky = [pscustomobject]@{ networks = [pscustomobject]@{ dms = [pscustomobject]@{ aliases = @($Collide) } } }
        }
        $endpoint = Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices $model -InfrastructureEngine postgresql
        $endpoint.InNetworkNames | Should -Not -Contain $Collide -Because "another service answers to '$Collide' on the shared network"
        $endpoint.InNetworkNames | Should -Contain $Survivor -Because "'$Survivor' still resolves uniquely to the db"
    }
}

Describe "Get-DbLocalEndpointIdentity - fail-closed" {
    It "throws when no db service is present" {
        { Get-DbLocalEndpointIdentity -DbService $null -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $null) -InfrastructureEngine postgresql } |
            Should -Throw "*no 'db' database service*"
    }

    It "throws when the db service has no concrete container_name" {
        $db = New-DbService -ContainerName $null
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*no concrete container_name*"
    }

    It "throws when a Configuration Service is present but no complete service model is supplied" {
        # The whole-model uniqueness guarantee must not be silently bypassable by omitting -AllServices.
        { Get-DbLocalEndpointIdentity -DbService (New-DbService) -ConfigService $script:ConfigServiceShared -InfrastructureEngine postgresql } |
            Should -Throw "*requires the complete Compose service model*"
    }

    It "throws when no TCP mapping targets the container port" {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 9999; published = '5435'; protocol = 'tcp' })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*no TCP mapping for container port 5432*"
    }

    It "throws when the container port is published only over a non-TCP protocol" {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 5432; published = '5435'; protocol = 'udp' })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*no TCP mapping for container port 5432*"
    }

    It "throws when multiple TCP mappings target the container port (ambiguous)" {
        $db = New-DbService -Ports @(
            [pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 5432; published = '5435'; protocol = 'tcp' }
            [pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 5432; published = '5436'; protocol = 'tcp' }
        )
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*ambiguous*"
    }

    It "throws for a ranged or non-integer container-port target (controlled diagnostic, not a raw cast)" -ForEach @(
        @{ Target = '5432-5433' }
        @{ Target = 'abc' }
    ) {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = $Target; published = '5435'; protocol = 'tcp' })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*non-integer container-port target*"
    }

    It "throws for a ranged, non-numeric, or out-of-range published port" -ForEach @(
        @{ Published = 'abc' }
        @{ Published = '0' }
        @{ Published = '70000' }
        @{ Published = '5435-5436' }
    ) {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = '127.0.0.1'; target = 5432; published = $Published; protocol = 'tcp' })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*not a concrete port in 1-65535*"
    }

    It "throws for a non-loopback host_ip (including IPv6 loopback)" -ForEach @(
        @{ HostIp = '::1' }
        @{ HostIp = '::' }
        @{ HostIp = '10.0.0.5' }
    ) {
        $db = New-DbService -Ports @([pscustomobject]@{ mode = 'ingress'; host_ip = $HostIp; target = 5432; published = '5435'; protocol = 'tcp' })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*not an IPv4 loopback*"
    }

    It "throws when a composed Configuration Service shares no network with the db" {
        # CMS present but on disjoint networks: it cannot reach the db, so advertising any in-network name would
        # be a lie the runtime contract's endpoint-locality check could act on. Fail closed.
        $db = New-DbService -Networks ([pscustomobject]@{ internal = $null })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*share no docker network*"
    }

    It "treats network keys case-sensitively (a 'DMS' db network does not share with a 'dms' config network)" {
        # Compose network references are map identifiers; a case-only difference is a different network and must
        # not establish reachability.
        $db = New-DbService -Networks ([pscustomobject]@{ DMS = $null })
        { Get-DbLocalEndpointIdentity -DbService $db -ConfigService $script:ConfigServiceShared -AllServices (New-ServiceModel -Db $db) -InfrastructureEngine postgresql } |
            Should -Throw "*share no docker network*"
    }
}
